using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;

namespace GroceryErp.Purchasing;

public sealed partial class PurchasingService
{
    /// <summary>Most rows one "all matching" purchasing bulk action may resolve.</summary>
    public const int MaxBulkRows = 2000;

    /// <summary>Reads one keyset page of purchases, newest received first, with every filter in SQL.</summary>
    public Task<PurchasePage> GetPurchasePageAsync(PurchaseFilter filter, DateOnly? beforeDate, long? beforeId,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (beforeDate is null != (beforeId is null) || limit is < 1 or > 200)
            throw new DomainException(ErrorCode.Validation, "Invalid purchase page filter.");
        _ = PurchaseConditions(filter); // rejects an invalid filter before it queues for a reader
        return database.ReadAsync(connection =>
        {
            (string conditions, DynamicParameters parameters) = PurchaseConditions(filter, BroadProductTerm(connection, filter));
            parameters.Add("beforeDate", beforeDate);
            parameters.Add("beforeId", beforeId);
            parameters.Add("take", limit + 1);
            // The cursor as a date bound the index can seek to, so a deep page costs what the first does.
            string after = beforeDate is null ? string.Empty
                : "AND p.received_date<=@beforeDate AND (p.received_date<@beforeDate OR p.id<@beforeId)";
            List<PurchaseListItem> rows = connection.Query<PurchaseListRow>($"""
                SELECT p.id AS Id,p.doc_no AS DocumentNumber,
                       COALESCE(p.vendor_name_snapshot,v.name,'Legacy unknown') AS VendorName,
                       p.vendor_invoice_no AS VendorInvoiceNumber,p.received_date AS ReceivedDate,
                       p.status AS Status,p.grand_total AS GrandTotal,p.vendor_bill_total AS VendorBillTotal
                FROM purchase p LEFT JOIN vendor v ON v.id=p.vendor_id
                WHERE {conditions} {after}
                ORDER BY p.received_date DESC,p.id DESC LIMIT @take
                """, parameters).Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            PurchaseListItem? last = rows.LastOrDefault();
            return new PurchasePage(rows, hasMore, last?.ReceivedDate, last?.Id);
        }, cancellationToken);
    }

    public Task<int> CountPurchasesAsync(PurchaseFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        (string conditions, DynamicParameters parameters) = PurchaseConditions(filter);
        return database.ReadAsync(connection =>
            connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM purchase p WHERE {conditions}", parameters),
            cancellationToken);
    }

    public Task<IReadOnlyList<long>> ResolvePurchaseIdsAsync(PurchaseFilter filter, int maximum = MaxBulkRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        int cap = Math.Clamp(maximum, 1, MaxBulkRows);
        (string conditions, DynamicParameters parameters) = PurchaseConditions(filter);
        parameters.Add("cap", cap + 1);
        return database.ReadAsync(connection =>
        {
            long[] ids = connection.Query<long>(
                $"SELECT p.id FROM purchase p WHERE {conditions} ORDER BY p.received_date DESC,p.id DESC LIMIT @cap",
                parameters).ToArray();
            if (ids.Length > cap) throw TooMany(cap);
            return (IReadOnlyList<long>)ids;
        }, cancellationToken);
    }

    /// <summary>Checks which selected purchases can still be voided: posted and never touched by any document.</summary>
    public Task<IReadOnlyList<PurchasingBulkRow>> PreviewVoidAsync(IReadOnlyCollection<long> purchaseIds,
        CancellationToken cancellationToken = default)
    {
        long[] ids = BulkBatch.ValidateIds(purchaseIds, MaxBulkRows);
        return database.ReadAsync(connection =>
        {
            Dictionary<long, PurchaseVoidRow> headers = [];
            foreach (long[] chunk in ids.Chunk(500))
                foreach (PurchaseVoidRow row in connection.Query<PurchaseVoidRow>(PurchaseVoidSql, new { ids = chunk }))
                    headers[row.Id] = row;
            var rows = new List<PurchasingBulkRow>(ids.Length);
            // Selected purchases share few received dates, so each date's period is read once.
            var openDates = new Dictionary<DateOnly, bool>();
            foreach (long id in ids)
            {
                if (!headers.TryGetValue(id, out PurchaseVoidRow? header))
                {
                    rows.Add(new PurchasingBulkRow(id, string.Empty, false, "Bulk_Missing", Money.Zero));
                    continue;
                }
                if (!openDates.TryGetValue(header.ReceivedDate, out bool open))
                    openDates[header.ReceivedDate] = open = periods.IsOpen(connection, null, header.ReceivedDate);
                string? reason = header.Status != "POSTED" ? "Bulk_Reason_NotPosted"
                    : !open ? "Bulk_Reason_PurchasePeriodLocked"
                    : IsUntouched(connection, id, VoidLots(connection, id)) ? null : "Bulk_Reason_PurchaseTouched";
                rows.Add(new PurchasingBulkRow(id, header.Label, reason is null, reason, Money.FromScaled(header.GrandTotal)));
            }
            return (IReadOnlyList<PurchasingBulkRow>)rows;
        }, cancellationToken);
    }

    /// <summary>
    /// Voids one batch of purchases in one transaction on <paramref name="date"/>. Each purchase is
    /// rechecked inside it; a purchase used since the review is reported and left posted.
    /// </summary>
    public async Task<IReadOnlyList<BulkItemResult>> RunVoidBatchAsync(IReadOnlyCollection<long> purchaseIds,
        DateOnly date, string batchOperationId, CancellationToken cancellationToken = default)
    {
        long[] ids = BulkBatch.ValidateIds(purchaseIds, 20);
        return await database.WriteAsync(tx =>
        {
            if (BulkBatch.Claim(tx, batchOperationId, ids) is { } replay) return replay;
            periods.EnsureOpen(tx, date);
            var outcome = new List<BulkItemResult>(ids.Length);
            foreach (long id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var header = tx.Connection.QuerySingleOrDefault<(string Status, DateOnly ReceivedDate)?>(
                    "SELECT status AS Status,received_date AS ReceivedDate FROM purchase WHERE id=@id",
                    new { id }, tx.Transaction);
                if (header is not { } purchase)
                {
                    outcome.Add(new BulkItemResult(id, BulkItemOutcome.Missing, "Bulk_Missing"));
                    continue;
                }
                if (purchase.Status != "POSTED" || !periods.IsOpen(tx.Connection, tx.Transaction, purchase.ReceivedDate))
                {
                    outcome.Add(new BulkItemResult(id, BulkItemOutcome.NotEligible,
                        purchase.Status != "POSTED" ? "Bulk_Reason_NotPosted" : "Bulk_Reason_PurchasePeriodLocked"));
                    continue;
                }
                List<CancelLotRow> lots = VoidLots(tx.Connection, id, tx.Transaction);
                if (!IsUntouched(tx.Connection, id, lots, tx.Transaction))
                {
                    outcome.Add(new BulkItemResult(id, BulkItemOutcome.NotEligible, "Bulk_Reason_PurchaseTouched"));
                    continue;
                }
                ApplyVoid(tx, id, date, lots);
                outcome.Add(new BulkItemResult(id, BulkItemOutcome.Changed));
            }
            return BulkBatch.Complete(tx, batchOperationId, outcome);
        }, cancellationToken);
    }

    /// <summary>Reads one keyset page of vendors by name, with balance due.</summary>
    public Task<VendorPage> GetVendorPageAsync(VendorFilter filter, string? afterName, long? afterId,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (afterName is null != (afterId is null) || limit is < 1 or > 200)
            throw new DomainException(ErrorCode.Validation, "Invalid vendor page.");
        (string conditions, DynamicParameters parameters) = VendorConditions(filter);
        parameters.Add("afterName", afterName);
        parameters.Add("afterId", afterId);
        parameters.Add("take", limit + 1);
        return database.ReadAsync(connection =>
        {
            List<VendorItem> rows = connection.Query<VendorRow>($"""
                SELECT v.id AS Id,v.name AS Name,v.phone AS Phone,v.gstin AS Gstin,
                       b.balance_due AS BalanceDue,v.is_active AS IsActive
                FROM vendor v JOIN v_vendor_balance b ON b.vendor_id=v.id
                WHERE {conditions}
                  AND (@afterName IS NULL OR v.name COLLATE NOCASE>@afterName COLLATE NOCASE
                       OR (v.name COLLATE NOCASE=@afterName COLLATE NOCASE AND v.id>@afterId))
                ORDER BY v.name COLLATE NOCASE,v.id LIMIT @take
                """, parameters).Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            VendorItem? last = rows.LastOrDefault();
            return new VendorPage(rows, hasMore, last?.Name, last?.Id);
        }, cancellationToken);
    }

    public Task<int> CountVendorsAsync(VendorFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        (string conditions, DynamicParameters parameters) = VendorConditions(filter);
        return database.ReadAsync(connection => connection.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM vendor v JOIN v_vendor_balance b ON b.vendor_id=v.id WHERE {conditions}", parameters),
            cancellationToken);
    }

    public Task<IReadOnlyList<long>> ResolveVendorIdsAsync(VendorFilter filter, int maximum = MaxBulkRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        int cap = Math.Clamp(maximum, 1, MaxBulkRows);
        (string conditions, DynamicParameters parameters) = VendorConditions(filter);
        parameters.Add("cap", cap + 1);
        return database.ReadAsync(connection =>
        {
            long[] ids = connection.Query<long>($"""
                SELECT v.id FROM vendor v JOIN v_vendor_balance b ON b.vendor_id=v.id
                WHERE {conditions} ORDER BY v.name COLLATE NOCASE,v.id LIMIT @cap
                """, parameters).ToArray();
            if (ids.Length > cap) throw TooMany(cap);
            return (IReadOnlyList<long>)ids;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PurchasingBulkRow>> PreviewVendorBulkAsync(IReadOnlyCollection<long> vendorIds, bool active,
        CancellationToken cancellationToken = default)
    {
        long[] ids = BulkBatch.ValidateIds(vendorIds, MaxBulkRows);
        return database.ReadAsync(connection =>
        {
            Dictionary<long, VendorRow> rows = [];
            foreach (long[] chunk in ids.Chunk(500))
                foreach (VendorRow row in connection.Query<VendorRow>("""
                    SELECT v.id AS Id,v.name AS Name,v.phone AS Phone,v.gstin AS Gstin,
                           b.balance_due AS BalanceDue,v.is_active AS IsActive
                    FROM vendor v JOIN v_vendor_balance b ON b.vendor_id=v.id WHERE v.id IN @chunk
                    """, new { chunk }))
                    rows[row.Id] = row;
            return (IReadOnlyList<PurchasingBulkRow>)ids.Select(id => rows.TryGetValue(id, out VendorRow? row)
                ? new PurchasingBulkRow(id, row.Name, row.IsActive != active,
                    row.IsActive == active ? (active ? "Bulk_Reason_AlreadyActive" : "Bulk_Reason_AlreadyInactive") : null,
                    Money.FromScaled(row.BalanceDue))
                : new PurchasingBulkRow(id, string.Empty, false, "Bulk_Missing", Money.Zero)).ToList();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<BulkItemResult>> RunVendorBulkBatchAsync(IReadOnlyCollection<long> vendorIds, bool active,
        string batchOperationId, CancellationToken cancellationToken = default)
    {
        long[] ids = BulkBatch.ValidateIds(vendorIds);
        return database.WriteAsync(tx =>
        {
            if (BulkBatch.Claim(tx, batchOperationId, ids) is { } replay) return replay;
            var outcome = new List<BulkItemResult>(ids.Length);
            foreach (long id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool? current = tx.Connection.QuerySingleOrDefault<bool?>(
                    "SELECT is_active FROM vendor WHERE id=@id", new { id }, tx.Transaction);
                if (current is null)
                {
                    outcome.Add(new BulkItemResult(id, BulkItemOutcome.Missing, "Bulk_Missing"));
                    continue;
                }
                if (current == active)
                {
                    outcome.Add(new BulkItemResult(id, BulkItemOutcome.NotEligible,
                        active ? "Bulk_Reason_AlreadyActive" : "Bulk_Reason_AlreadyInactive"));
                    continue;
                }
                tx.Connection.Execute("UPDATE vendor SET is_active=@active WHERE id=@id",
                    new { id, active = active ? 1 : 0 }, tx.Transaction);
                audit.Write(tx, "vendor", id, active ? "ACTIVATE" : "DEACTIVATE", new { bulk = batchOperationId });
                outcome.Add(new BulkItemResult(id, BulkItemOutcome.Changed));
            }
            return BulkBatch.Complete(tx, batchOperationId, outcome);
        }, cancellationToken);
    }

    private static DomainException TooMany(int cap) => new(ErrorCode.TooManyRows, cap);

    /// <summary>
    /// Builds the purchase filter from the filters in use only, as plain comparisons the indexes
    /// can serve; "(@x IS NULL OR column=@x)" made every list a walk of the date index. Pass
    /// <paramref name="broadProducts"/> when the product term matches many products.
    /// </summary>
    private static (string Conditions, DynamicParameters Parameters) PurchaseConditions(PurchaseFilter filter,
        bool broadProducts = false)
    {
        if (filter.From > filter.To ||
            filter.Status is not (null or "DRAFT" or "POSTED" or "CANCELLED") ||
            filter.LineKind is not (null or "PRODUCT" or "INTERNAL" or "EXPENSE"))
            throw new DomainException(ErrorCode.Validation, "Invalid purchase page filter.");
        string? productPattern = LikePattern(filter.ProductQuery, contains: true);
        string? document = string.IsNullOrWhiteSpace(filter.DocumentQuery) ? null : filter.DocumentQuery.Trim();
        if (document?.Length > 60) throw new DomainException(ErrorCode.Validation, "Search term is too long.");
        var parameters = new DynamicParameters();
        parameters.Add("from", filter.From);
        parameters.Add("to", filter.To);
        parameters.Add("vendorId", filter.VendorId);
        parameters.Add("lineKind", filter.LineKind);
        parameters.Add("productPattern", productPattern);
        parameters.Add("productPrefix", LikePattern(filter.ProductQuery, contains: false));
        parameters.Add("productMatch", ProductMatch(filter.ProductQuery));
        parameters.Add("document", document?.ToUpperInvariant());
        parameters.Add("documentPrefix", LikePattern(document, contains: false));
        var conditions = new List<string> { "1=1" };
        if (filter.From is not null) conditions.Add("p.received_date>=@from");
        if (filter.To is not null) conditions.Add("p.received_date<=@to");
        if (filter.VendorId is not null) conditions.Add("p.vendor_id=@vendorId");
        // A validated literal, so the posted-by-vendor partial index can serve it.
        if (filter.Status is { } status) conditions.Add($"p.status='{status}'");
        if (document is not null)
            conditions.Add("(p.doc_no=@document OR p.vendor_invoice_no LIKE @documentPrefix ESCAPE '\\')");
        // Most purchases have product lines, so that kind is checked per purchase in date order;
        // the rare kinds are looked up first.
        if (filter.LineKind == "PRODUCT")
            conditions.Add("EXISTS (SELECT 1 FROM purchase_line typed WHERE typed.purchase_id=p.id AND typed.line_kind=@lineKind)");
        else if (filter.LineKind is not null)
            conditions.Add("p.id IN (SELECT typed.purchase_id FROM purchase_line typed WHERE typed.line_kind=@lineKind)");
        if (filter.ReturnableOnly)
            conditions.Add("""
                p.status='POSTED' AND EXISTS (
                    SELECT 1 FROM purchase_line rl JOIN product_lot l ON l.id=rl.lot_id_snapshot
                    WHERE rl.purchase_id=p.id AND l.qty_on_hand>0)
                """);
        if (productPattern is not null)
        {
            // A product's current SKU, names and aliases through the trigram index (terms of three
            // letters or more) or a name/SKU prefix for shorter terms, and the saved name of lines
            // whose product was deleted. Scanning every line's snapshot took 1.8 s for no match.
            string products = ProductMatch(filter.ProductQuery) is null
                ? "SELECT pp.id FROM product pp WHERE pp.name LIKE @productPrefix ESCAPE '\\' OR pp.sku LIKE @productPrefix ESCAPE '\\'"
                : "SELECT rowid FROM product_fts WHERE product_fts MATCH @productMatch";
            const string deleted = """
                pl.product_id IS NULL AND (pl.product_name_snapshot LIKE @productPattern ESCAPE '\'
                    OR pl.product_sku_snapshot LIKE @productPattern ESCAPE '\')
                """;
            conditions.Add(broadProducts || ProductMatch(filter.ProductQuery) is null
                ? $"EXISTS (SELECT 1 FROM purchase_line pl WHERE pl.purchase_id=p.id AND (pl.product_id IN ({products}) OR ({deleted})))"
                : $"p.id IN (SELECT pl.purchase_id FROM purchase_line pl WHERE pl.product_id IN ({products}) UNION SELECT pl.purchase_id FROM purchase_line pl WHERE {deleted})");
        }
        return (string.Join("\n  AND ", conditions), parameters);
    }

    // The trigram index matches substrings of three letters or more; shorter terms use a prefix.
    private static string? ProductMatch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        string[] terms = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return terms.All(static term => term.Length >= 3)
            ? string.Join(' ', terms.Select(static term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\""))
            : null;
    }

    // Whether a product term matches so many products that the page is found faster by walking
    // purchases newest first than by collecting and sorting every matching purchase.
    private static bool BroadProductTerm(System.Data.IDbConnection connection, PurchaseFilter filter) =>
        ProductMatch(filter.ProductQuery) is { } match && connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM (SELECT 1 FROM product_fts WHERE product_fts MATCH @match LIMIT 200)", new { match }) >= 200;

    private static (string Conditions, DynamicParameters Parameters) VendorConditions(VendorFilter filter)
    {
        if (filter.Status is not ("ACTIVE" or "INACTIVE" or "ALL"))
            throw new DomainException(ErrorCode.Validation, "Invalid vendor filter.");
        var parameters = new DynamicParameters();
        parameters.Add("pattern", LikePattern(filter.Query, contains: true));
        parameters.Add("status", filter.Status);
        parameters.Add("withBalance", filter.WithBalanceOnly);
        return ("""
            (@status='ALL' OR (@status='ACTIVE' AND v.is_active=1) OR (@status='INACTIVE' AND v.is_active=0))
              AND (@withBalance=0 OR b.balance_due<>0)
              AND (@pattern IS NULL OR v.name LIKE @pattern ESCAPE '\' COLLATE NOCASE
                   OR v.phone LIKE @pattern ESCAPE '\' OR v.gstin LIKE @pattern ESCAPE '\' COLLATE NOCASE)
            """, parameters);
    }

    private static string? LikePattern(string? text, bool contains)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string escaped = text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        if (escaped.Length > 244) throw new DomainException(ErrorCode.Validation, "Search term is too long.");
        return (contains ? "%" : string.Empty) + escaped + "%";
    }

    private const string PurchaseVoidSql = """
        SELECT p.id AS Id,p.status AS Status,p.grand_total AS GrandTotal,p.received_date AS ReceivedDate,
               COALESCE(p.doc_no,'-') || ' · ' || COALESCE(p.vendor_name_snapshot,v.name,'') AS Label
        FROM purchase p LEFT JOIN vendor v ON v.id=p.vendor_id WHERE p.id IN @ids
        """;

    private sealed class PurchaseVoidRow
    {
        public long Id { get; init; }
        public string Status { get; init; } = string.Empty;
        public long GrandTotal { get; init; }
        public DateOnly ReceivedDate { get; init; }
        public string Label { get; init; } = string.Empty;
    }
}
