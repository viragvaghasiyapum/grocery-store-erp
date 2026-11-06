using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Inventory;

public sealed partial class InventoryService(
    DbExecutor database,
    ILotQuantities stock,
    DocNumberService numbers,
    PeriodGuard periods,
    IAuditLog audit)
{
    /// <summary>The categories, units and tax rates a product created for restored units may use.</summary>
    public Task<RestoreOptions> GetRestoreOptionsAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => RestoreProducts.Options(connection), cancellationToken);

    /// <summary>Whether any product, archived ones included, already uses the SKU.</summary>
    public Task<bool> IsSkuInUseAsync(string sku, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => connection.ExecuteScalar<long>(
            "SELECT EXISTS(SELECT 1 FROM product WHERE sku=@sku COLLATE NOCASE)", new { sku = sku.Trim() }) != 0,
            cancellationToken);

    public Task<LotPage> GetLotPageAsync(long? productId = null, string? query = null, long? beforeId = null,
        int limit = 100, bool availableOnly = false, string expiryFilter = "ALL",
        DateOnly? asOfDate = null, CancellationToken cancellationToken = default) =>
        GetLotPageAsync(new LotFilter
        {
            ProductId = productId,
            Query = query,
            Status = availableOnly ? "AVAILABLE" : "ALL",
            Expiry = expiryFilter,
            AsOf = asOfDate,
        }, beforeId, limit, cancellationToken);

    /// <summary>Reads one keyset page of lots, newest first, with every filter applied in SQL.</summary>
    public Task<LotPage> GetLotPageAsync(LotFilter filter, long? beforeId, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (limit is < 1 or > 200 || beforeId <= 0)
            throw new DomainException(ErrorCode.Validation, "Invalid lot page.");
        (string conditions, DynamicParameters parameters) = LotConditions(filter);
        parameters.Add("beforeId", beforeId);
        parameters.Add("take", limit + 1);
        return database.ReadAsync(connection =>
        {
            List<LotItem> rows = connection.Query<LotRow>($$"""
                WITH visible AS MATERIALIZED (
                    SELECT l.id FROM product_lot l JOIN product p ON p.id=l.product_id
                    WHERE {{conditions}} {{(beforeId is null ? string.Empty : "AND l.id<@beforeId")}}
                    ORDER BY l.id DESC LIMIT @take
                )
                SELECT l.id AS Id,l.product_id AS ProductId,p.name AS ProductName,l.lot_code AS LotCode,
                       l.batch_no AS BatchNumber,l.received_date AS ReceivedDate,l.expiry_date AS ExpiryDate,
                       l.unit_cost AS UnitCost,l.mrp AS Mrp,l.selling_price AS SellingPrice,
                       l.qty_received AS Received,l.qty_on_hand AS OnHand,l.is_active AS IsActive,
                       l.origin_kind AS OriginKind,l.detail_version AS DetailVersion,
                       l.tax_label_snapshot AS TaxLabel,l.unit_tax AS UnitTax,l.unit_cost_incl_tax AS UnitCostInclTax,
                       {{ExpiryBucketSql}} AS ExpiryBucketCode
                FROM visible page JOIN product_lot l ON l.id=page.id JOIN product p ON p.id=l.product_id
                ORDER BY l.id DESC
                """, parameters).Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            return new LotPage(rows, hasMore, rows.LastOrDefault()?.Id);
        }, cancellationToken);
    }

    /// <summary>Counts every lot matching the filters, for an "all matching" selection.</summary>
    public Task<int> CountLotsAsync(LotFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        (string conditions, DynamicParameters parameters) = LotConditions(filter);
        return database.ReadAsync(connection => connection.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM product_lot l JOIN product p ON p.id=l.product_id WHERE {conditions}", parameters),
            cancellationToken);
    }

    /// <summary>Resolves every matching lot ID, newest first; fails rather than truncating past the cap.</summary>
    public Task<IReadOnlyList<long>> ResolveLotIdsAsync(LotFilter filter, int maximum = MaxBulkLots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        int cap = Math.Clamp(maximum, 1, MaxBulkLots);
        (string conditions, DynamicParameters parameters) = LotConditions(filter);
        parameters.Add("cap", cap + 1);
        return database.ReadAsync(connection =>
        {
            long[] ids = connection.Query<long>(
                $"SELECT l.id FROM product_lot l JOIN product p ON p.id=l.product_id WHERE {conditions} ORDER BY l.id DESC LIMIT @cap",
                parameters).ToArray();
            if (ids.Length > cap) throw new DomainException(ErrorCode.TooManyRows, cap);
            return (IReadOnlyList<long>)ids;
        }, cancellationToken);
    }

    /// <summary>Most lots one "all matching" bulk action may resolve; larger sets must be narrowed.</summary>
    public const int MaxBulkLots = 5000;

    // Labels follow the business date (@date), as the expiry filters do, not the machine clock, and
    // the dashboard's rule (ExpiryAlert): Critical and Soon mark exactly the lots the Expiring filter
    // lists, so a lot past its alert window is Fresh even when it expires within seven days.
    private static readonly string ExpiryBucketSql = $"""
        CASE WHEN l.is_active=0 THEN 'BLOCKED' WHEN l.qty_on_hand<=0 THEN 'EMPTY'
             WHEN l.expiry_date IS NULL THEN 'NONE'
             WHEN l.expiry_date<@date THEN 'EXPIRED'
             WHEN l.expiry_date>{ExpiryAlert.WindowEndSql("p", "@date")} THEN 'FRESH'
             WHEN l.expiry_date<={ExpiryAlert.CriticalEndSql("@date")} THEN 'CRITICAL'
             ELSE 'SOON' END
        """;

    // The dashboard's expiry rule (ExpiryAlert). The null-expiry term lets one product's expiring lots
    // be read as a range of the FEFO index (0.1 ms for the busiest product, against 5-8 ms walking
    // all 1,041 of its lots).
    private static readonly string ExpiredLots = $"AND {ExpiryAlert.ExpiredSql("l", "@date")}";
    private static readonly string ExpiringLots =
        $"AND {ExpiryAlert.ExpiringSql("l", "p", "@date")} AND (l.expiry_date IS NULL)=0";

    private (string Conditions, DynamicParameters Parameters) LotConditions(LotFilter filter)
    {
        if (filter.Status is not ("ALL" or "AVAILABLE" or "ZERO" or "BLOCKED") ||
            filter.Expiry is not ("ALL" or "EXPIRED" or "EXPIRING" or "NONE") ||
            filter.Source is not (null or "PURCHASE_LINE" or "DIRECT" or "CUSTOMER_RETURN"))
            throw new DomainException(ErrorCode.Validation, "Invalid lot filter.");
        if (filter.ExpiryFrom > filter.ExpiryTo || filter.MinPrice > filter.MaxPrice)
            throw new DomainException(ErrorCode.FilterRangeReversed);
        DateOnly today = filter.AsOf ?? periods.Today;
        var parameters = new DynamicParameters();
        parameters.Add("productId", filter.ProductId);
        parameters.Add("date", Format(today));
        parameters.Add("expiryFrom", filter.ExpiryFrom is { } from ? Format(from) : null);
        parameters.Add("expiryTo", filter.ExpiryTo is { } to ? Format(to) : null);
        parameters.Add("source", filter.Source);
        parameters.Add("minPrice", filter.MinPrice?.Scaled);
        parameters.Add("maxPrice", filter.MaxPrice?.Scaled);
        string status = filter.Status switch
        {
            "AVAILABLE" => "AND l.is_active=1 AND l.qty_on_hand>0",
            "ZERO" => "AND l.qty_on_hand=0",
            "BLOCKED" => "AND l.is_active=0",
            _ => string.Empty,
        };
        string expiry = filter.Expiry switch
        {
            "EXPIRED" => ExpiredLots,
            "EXPIRING" => ExpiringLots,
            "NONE" => "AND l.expiry_date IS NULL",
            _ => string.Empty,
        };
        string search = string.Empty;
        string query = (filter.Query ?? string.Empty).Trim();
        if (query.Length > 0)
        {
            parameters.Add("pattern", "%" + query.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
            search = filter.ProductId is null
                ? "AND (p.name LIKE @pattern ESCAPE '\\' OR l.lot_code LIKE @pattern ESCAPE '\\' OR l.batch_no LIKE @pattern ESCAPE '\\')"
                : "AND (l.lot_code LIKE @pattern ESCAPE '\\' OR l.batch_no LIKE @pattern ESCAPE '\\')";
        }
        // Only the filters in use appear, as plain comparisons: "(@x IS NULL OR column=@x)" stops
        // SQLite using an index, which turned one product's lots into a scan of every lot.
        var conditions = new List<string> { filter.ProductId is null ? "1=1" : "l.product_id=@productId" };
        if (filter.ExpiryFrom is not null) conditions.Add("l.expiry_date>=@expiryFrom");
        if (filter.ExpiryTo is not null) conditions.Add("l.expiry_date<=@expiryTo");
        if (filter.Source is not null) conditions.Add("l.origin_kind=@source");
        if (filter.MinPrice is not null) conditions.Add("l.selling_price>=@minPrice");
        if (filter.MaxPrice is not null) conditions.Add("l.selling_price<=@maxPrice");
        return ($"{string.Join(" AND ", conditions)} {status} {expiry} {search}", parameters);
    }

    public Task<IReadOnlyList<LotItem>> GetLotsByIdsAsync(IReadOnlyCollection<long> lotIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lotIds);
        long[] ids = lotIds.Where(static id => id > 0).Distinct().ToArray();
        if (ids.Length > 200) throw new DomainException(ErrorCode.Validation, "Select at most 200 lots per batch.");
        if (ids.Length == 0) return Task.FromResult<IReadOnlyList<LotItem>>([]);
        string date = Format(periods.Today);
        return database.ReadAsync(connection => (IReadOnlyList<LotItem>)connection.Query<LotRow>($$"""
            SELECT l.id AS Id,l.product_id AS ProductId,p.name AS ProductName,l.lot_code AS LotCode,
                   l.batch_no AS BatchNumber,l.received_date AS ReceivedDate,l.expiry_date AS ExpiryDate,
                   l.unit_cost AS UnitCost,l.mrp AS Mrp,l.selling_price AS SellingPrice,
                   l.qty_received AS Received,l.qty_on_hand AS OnHand,l.is_active AS IsActive,
                   l.detail_version AS DetailVersion,l.origin_kind AS OriginKind,
                   l.tax_label_snapshot AS TaxLabel,l.unit_tax AS UnitTax,l.unit_cost_incl_tax AS UnitCostInclTax,
                   {{ExpiryBucketSql}} AS ExpiryBucketCode
            FROM product_lot l JOIN product p ON p.id=l.product_id
            WHERE l.id IN @ids ORDER BY l.id
            """, new { ids, date }).Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    public async Task<long> CreateDirectLotAsync(OpeningStockLine line, string reason,
        string operationId, CancellationToken cancellationToken = default)
    {
        ValidateOpening(line);
        string note = Required(reason, 500, nameof(reason));
        string? batch = Optional(line.BatchNumber, 120);
        periods.EnsureOpen(line.ReceivedDate);
        return await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.DirectEntry);
            if (replay?.RefId is long previous) return previous;
            periods.EnsureOpen(transaction, line.ReceivedDate);
            ProductRule product = LoadProductRule(transaction, line.ProductId);
            ValidateProductQuantity(product, line.Quantity, line.ExpiryDate);
            if (line.Mrp is not null && line.SellingPrice > line.Mrp.Value)
                throw new DomainException(ErrorCode.PriceAboveMrp);
            LotTax tax = OpeningTax(transaction, line);
            string lotCode = numbers.Next(transaction, DocType.Lot, line.ReceivedDate);
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,
                    unit_cost,mrp,selling_price,qty_received,direct_reason,
                    tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,unit_tax,unit_cost_incl_tax)
                VALUES (@ProductId,@lotCode,@BatchNumber,@receivedDate,@expiryDate,
                    @unitCost,@mrp,@sellingPrice,@quantity,@note,
                    @taxRateId,@taxLabel,@taxRatePct,@unitTax,@unitCostInclTax) RETURNING id
                """, tax.AddTo(new DynamicParameters(new
            {
                line.ProductId,
                lotCode,
                BatchNumber = batch,
                receivedDate = Format(line.ReceivedDate),
                expiryDate = line.ExpiryDate is null ? null : Format(line.ExpiryDate.Value),
                unitCost = tax.Cost.Booked.Scaled,
                mrp = line.Mrp?.Scaled,
                sellingPrice = line.SellingPrice.Scaled,
                quantity = line.Quantity.Scaled,
                note
            })), transaction.Transaction);
            long originId = transaction.Connection.ExecuteScalar<long>(
                "SELECT id FROM direct_stock_origin WHERE lot_id=@id", new { id }, transaction.Transaction);
            stock.Change(transaction, new(line.ReceivedDate, line.ProductId, id, LotChangeKind.DirectEntry, line.Quantity));
            audit.Write(transaction, "direct_stock_origin", originId, "CREATE", new { lotId = id, note });
            OperationGuard.Complete(transaction, operationId, "product_lot", id);
            return id;
        }, cancellationToken);
    }

    public async Task<long> ImportOpeningStockAsync(
        IReadOnlyList<OpeningStockLine> lines,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) throw new DomainException(ErrorCode.Validation, "Opening stock is empty.");
        foreach (OpeningStockLine line in lines)
        {
            ValidateOpening(line);
            periods.EnsureOpen(line.ReceivedDate);
        }
        string fingerprint = ImportJobs.Fingerprint(lines);
        OpeningStockLine[][] batches = lines.Chunk(100).ToArray();
        ImportJobState state = await database.WriteAsync(tx => ImportJobs.Start(tx, operationId,
            "OPENING_STOCK", fingerprint, batches.Length), cancellationToken);
        if (state.Completed) return state.ResultId;
        for (int batchNo = 0; batchNo < batches.Length; batchNo++)
        {
            int number = batchNo;
            OpeningStockLine[] batch = batches[number];
            await database.WriteAsync(transaction =>
        {
            if (ImportJobs.HasBatch(transaction, operationId, number)) return;
            long firstLot = 0;
            foreach (OpeningStockLine line in batch)
            {
                ValidateOpening(line);
                periods.EnsureOpen(transaction, line.ReceivedDate);
                ProductRule product = LoadProductRule(transaction, line.ProductId);
                ValidateProductQuantity(product, line.Quantity, line.ExpiryDate);
                if (line.Mrp is not null && line.SellingPrice > line.Mrp.Value)
                    throw new DomainException(ErrorCode.PriceAboveMrp);
                LotTax tax = OpeningTax(transaction, line);
                string lotCode = numbers.Next(transaction, DocType.Lot, line.ReceivedDate);
                long lotId = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,unit_cost,mrp,
                                            selling_price,qty_received,tax_rate_id_snapshot,tax_label_snapshot,
                                            tax_rate_pct_snapshot,unit_tax,unit_cost_incl_tax)
                    VALUES (@ProductId,@lotCode,@BatchNumber,@ReceivedDate,@ExpiryDate,@UnitCost,@Mrp,@SellingPrice,@Quantity,
                            @taxRateId,@taxLabel,@taxRatePct,@unitTax,@unitCostInclTax)
                    RETURNING id
                    """, tax.AddTo(new DynamicParameters(new
                {
                    line.ProductId,
                    lotCode,
                    line.BatchNumber,
                    ReceivedDate = Format(line.ReceivedDate),
                    ExpiryDate = line.ExpiryDate is null ? null : Format(line.ExpiryDate.Value),
                    UnitCost = tax.Cost.Booked.Scaled,
                    Mrp = line.Mrp?.Scaled,
                    SellingPrice = line.SellingPrice.Scaled,
                    Quantity = line.Quantity.Scaled,
                })), transaction.Transaction);
                stock.Change(transaction, new(line.ReceivedDate, line.ProductId, lotId, LotChangeKind.Opening, line.Quantity));
                firstLot = firstLot == 0 ? lotId : firstLot;
            }
            audit.Write(transaction, "opening_stock", firstLot, "IMPORT_BATCH", new { rows = batch.Length, batchNo = number });
            ImportJobs.RecordBatch(transaction, operationId, number, created: batch.Length, firstLot: firstLot);
        }, cancellationToken);
        }
        return (await database.WriteAsync(tx => ImportJobs.Complete(tx, operationId,
            batches.Length, openingStock: true), cancellationToken)).ResultId;
    }

    private static void ThrowLotEditConflict(DbTx transaction, long lotId)
    {
        if (transaction.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot WHERE id=@lotId",
            new { lotId }, transaction.Transaction) == 0)
            throw new DomainException(ErrorCode.NotFound, "lot", lotId);
        throw new DomainException(ErrorCode.EntryChanged, lotId);
    }

    // The typed purchase price is before tax. A chosen rate must be active; without one the lot
    // takes the product's rate as it is now.
    private static LotTax OpeningTax(DbTx transaction, OpeningStockLine line) =>
        LotTaxes.FromBeforeTax(transaction, line.TaxRateId is { } chosen
            ? LotTaxes.Rate(transaction, chosen, activeOnly: true)
            : LotTaxes.ProductRate(transaction, line.ProductId), line.PurchasePrice);

    private static ProductRule LoadProductRule(DbTx transaction, long productId) =>
        transaction.Connection.QuerySingleOrDefault<ProductRule>("""
            SELECT p.track_expiry AS TrackExpiry,u.allow_fraction AS AllowFraction
            FROM product p JOIN uom u ON u.id=p.uom_id WHERE p.id=@productId AND p.is_active=1
            """, new { productId }, transaction.Transaction)
        ?? throw new DomainException(ErrorCode.NotFound, "product", productId);

    private static void ValidateOpening(OpeningStockLine line)
    {
        if (line.ProductId <= 0 || line.Quantity <= Quantity.Zero || line.PurchasePrice < Money.Zero ||
            line.SellingPrice < Money.Zero || line.ReceivedDate == default)
            throw new DomainException(ErrorCode.Validation, "Invalid opening-stock row.");
        if (line.ExpiryDate is not null && line.ExpiryDate < line.ReceivedDate)
            throw new DomainException(ErrorCode.InvalidDate);
    }

    private static void ValidateProductQuantity(ProductRule product, Quantity quantity, DateOnly? expiry)
    {
        if (!product.AllowFraction && !quantity.IsWhole) throw new DomainException(ErrorCode.FractionalQtyNotAllowed);
        if (product.TrackExpiry && expiry is null) throw new DomainException(ErrorCode.ExpiryRequired);
    }

    private static string Required(string? value, int maximum, string parameter)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maximum) throw new DomainException(ErrorCode.Validation, parameter);
        return normalized;
    }

    private static string? Optional(string? value, int maximum)
    {
        string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maximum) throw new DomainException(ErrorCode.Validation, nameof(value));
        return normalized;
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class LotRow { public long Id { get; init; } public long ProductId { get; init; } public string ProductName { get; init; } = ""; public string LotCode { get; init; } = ""; public string? BatchNumber { get; init; } public DateOnly ReceivedDate { get; init; } public DateOnly? ExpiryDate { get; init; } public long UnitCost { get; init; } public long? Mrp { get; init; } public long SellingPrice { get; init; } public long Received { get; init; } public long OnHand { get; init; } public bool IsActive { get; init; } public string ExpiryBucketCode { get; init; } = "NONE"; public string OriginKind { get; init; } = "DIRECT"; public long DetailVersion { get; init; } public string? TaxLabel { get; init; } public long? UnitTax { get; init; } public long? UnitCostInclTax { get; init; } public LotItem ToModel() => new(Id, ProductId, ProductName, LotCode, BatchNumber, ReceivedDate, ExpiryDate, Money.FromScaled(UnitCost), Mrp is null ? null : Money.FromScaled(Mrp.Value), Money.FromScaled(SellingPrice), Quantity.FromScaled(Received), Quantity.FromScaled(OnHand), IsActive, ExpiryBucketCode) { OriginKind = OriginKind, DetailVersion = DetailVersion, TaxLabel = TaxLabel, UnitTax = UnitTax is null ? null : Money.FromScaled(UnitTax.Value), UnitCostInclTax = UnitCostInclTax is null ? null : Money.FromScaled(UnitCostInclTax.Value) }; }
    private sealed class ProductRule { public bool TrackExpiry { get; init; } public bool AllowFraction { get; init; } }
}
