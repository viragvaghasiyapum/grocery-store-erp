using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Messages;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Inventory;

public sealed partial class InventoryService(
    DbExecutor database,
    IStockLedger ledger,
    DocNumberService numbers,
    PeriodGuard periods,
    IAuditLog audit)
{
    public Task<StockOverviewPage> GetOverviewPageAsync(string? query = null, string? afterName = null,
        long? afterId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (afterName is null != (afterId is null) || limit is < 1 or > 200)
            throw new DomainException(ErrorCode.Validation, "Invalid stock overview page.");
        string pattern = "%" + (query ?? string.Empty).Trim().Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return database.ReadAsync(connection =>
        {
            List<StockOverviewItem> rows = connection.Query<OverviewRow>("""
                SELECT p.id AS ProductId,p.sku AS Sku,p.name AS Name,
                       COALESCE(t.quantity,0) AS Quantity,COALESCE(t.cost_value,0) AS CostValue,
                       p.reorder_level AS ReorderLevel,
                       COALESCE(t.is_low,0) AS IsLow
                FROM product p LEFT JOIN product_stock_total t ON t.product_id=p.id
                WHERE p.is_active=1 AND (p.name LIKE @pattern ESCAPE '\' OR p.sku LIKE @pattern ESCAPE '\')
                  AND (@afterName IS NULL OR p.name COLLATE NOCASE>@afterName COLLATE NOCASE
                       OR (p.name COLLATE NOCASE=@afterName COLLATE NOCASE AND p.id>@afterId))
                ORDER BY p.name COLLATE NOCASE,p.id LIMIT @take
                """, new { pattern, afterName, afterId, take = limit + 1 })
                .Select(static row => new StockOverviewItem(row.ProductId, row.Sku, row.Name,
                    Quantity.FromScaled(row.Quantity), Money.FromScaled(row.CostValue),
                    Quantity.FromScaled(row.ReorderLevel), row.IsLow)).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            StockOverviewItem? last = rows.LastOrDefault();
            return new StockOverviewPage(rows, hasMore, last?.Name, last?.ProductId);
        }, cancellationToken);
    }

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
                       {{ExpiryBucketSql}} AS ExpiryBucketCode
                FROM visible page JOIN product_lot l ON l.id=page.id JOIN product p ON p.id=l.product_id
                JOIN category c ON c.id=p.category_id LEFT JOIN category pc ON pc.id=c.parent_id
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
            if (ids.Length > cap)
                throw new DomainException(ErrorCode.Validation,
                    $"More than {cap.ToString(System.Globalization.CultureInfo.InvariantCulture)} lots match. Narrow the filters and try again.");
            return (IReadOnlyList<long>)ids;
        }, cancellationToken);
    }

    /// <summary>Most lots one "all matching" bulk action may resolve; larger sets must be narrowed.</summary>
    public const int MaxBulkLots = 5000;

    private const string ExpiryBucketSql = """
        CASE WHEN l.is_active=0 THEN 'BLOCKED' WHEN l.qty_on_hand<=0 THEN 'EMPTY'
             WHEN l.expiry_date IS NULL THEN 'NONE'
             WHEN l.expiry_date<date('now','localtime') THEN 'EXPIRED'
             WHEN l.expiry_date<=date('now','localtime','+7 days') THEN 'CRITICAL'
             WHEN julianday(l.expiry_date)-julianday('now','localtime','start of day')<=
                  COALESCE(p.expiry_alert_days,c.expiry_alert_days,pc.expiry_alert_days,
                           CAST((SELECT value FROM setting WHERE key='expiry.default_alert_days') AS INTEGER),30)
                  THEN 'SOON' ELSE 'FRESH' END
        """;

    private static (string Conditions, DynamicParameters Parameters) LotConditions(LotFilter filter)
    {
        if (filter.Status is not ("ALL" or "AVAILABLE" or "ZERO" or "BLOCKED") ||
            filter.Expiry is not ("ALL" or "EXPIRED" or "EXPIRING" or "NONE") ||
            filter.Source is not (null or "PURCHASE_LINE" or "DIRECT" or "CUSTOMER_RETURN"))
            throw new DomainException(ErrorCode.Validation, "Invalid lot filter.");
        if (filter.ExpiryFrom > filter.ExpiryTo || filter.MinPrice > filter.MaxPrice)
            throw new DomainException(ErrorCode.Validation, "The filter range starts after it ends.");
        DateOnly today = filter.AsOf ?? DateOnly.FromDateTime(DateTime.Today);
        var parameters = new DynamicParameters();
        parameters.Add("productId", filter.ProductId);
        parameters.Add("date", Format(today));
        parameters.Add("soon", Format(today.AddDays(30)));
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
            "EXPIRED" => "AND l.qty_on_hand>0 AND l.expiry_date<@date",
            "EXPIRING" => "AND l.qty_on_hand>0 AND l.expiry_date>=@date AND l.expiry_date<=@soon",
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
        return database.ReadAsync(connection => (IReadOnlyList<LotItem>)connection.Query<LotRow>("""
            SELECT l.id AS Id,l.product_id AS ProductId,p.name AS ProductName,l.lot_code AS LotCode,
                   l.batch_no AS BatchNumber,l.received_date AS ReceivedDate,l.expiry_date AS ExpiryDate,
                   l.unit_cost AS UnitCost,l.mrp AS Mrp,l.selling_price AS SellingPrice,
                   l.qty_received AS Received,l.qty_on_hand AS OnHand,l.is_active AS IsActive,
                   l.detail_version AS DetailVersion,
                   CASE WHEN l.is_active=0 THEN 'BLOCKED' WHEN l.qty_on_hand<=0 THEN 'EMPTY'
                        WHEN l.expiry_date IS NULL THEN 'NONE'
                        WHEN l.expiry_date<date('now','localtime') THEN 'EXPIRED'
                        WHEN l.expiry_date<=date('now','localtime','+7 days') THEN 'CRITICAL'
                        WHEN julianday(l.expiry_date)-julianday('now','localtime','start of day')<=
                             COALESCE(p.expiry_alert_days,c.expiry_alert_days,pc.expiry_alert_days,
                                      CAST((SELECT value FROM setting WHERE key='expiry.default_alert_days') AS INTEGER),30)
                             THEN 'SOON' ELSE 'FRESH' END AS ExpiryBucketCode
            FROM product_lot l JOIN product p ON p.id=l.product_id
            JOIN category c ON c.id=p.category_id LEFT JOIN category pc ON pc.id=c.parent_id
            WHERE l.id IN @ids ORDER BY l.id
            """, new { ids }).Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    public Task<IReadOnlyList<StockOverviewItem>> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<StockOverviewItem>)connection.Query<OverviewRow>("""
            SELECT product_id AS ProductId,sku AS Sku,name AS Name,stock_qty AS Quantity,
                   stock_value AS CostValue,reorder_level AS ReorderLevel,is_low AS IsLow
            FROM v_product_stock ORDER BY is_low DESC,name COLLATE NOCASE
            """).Select(static row => new StockOverviewItem(row.ProductId, row.Sku, row.Name,
                Quantity.FromScaled(row.Quantity), Money.FromScaled(row.CostValue),
                Quantity.FromScaled(row.ReorderLevel), row.IsLow)).ToList(), cancellationToken);

    public Task<IReadOnlyList<LotItem>> GetLotsAsync(long? productId = null, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<LotItem>)connection.Query<LotRow>("""
            SELECT l.id AS Id,l.product_id AS ProductId,p.name AS ProductName,l.lot_code AS LotCode,
                   l.batch_no AS BatchNumber,l.received_date AS ReceivedDate,l.expiry_date AS ExpiryDate,
                   l.unit_cost AS UnitCost,l.mrp AS Mrp,l.selling_price AS SellingPrice,
                   l.qty_received AS Received,l.qty_on_hand AS OnHand,l.is_active AS IsActive,
                   CASE WHEN l.is_active=0 THEN 'BLOCKED'
                        WHEN l.qty_on_hand<=0 THEN 'EMPTY'
                        WHEN l.expiry_date IS NULL THEN 'NONE'
                        ELSE COALESCE(e.bucket,'FRESH') END AS ExpiryBucketCode
            FROM product_lot l JOIN product p ON p.id=l.product_id
            LEFT JOIN v_expiry_alert e ON e.lot_id=l.id
            WHERE @productId IS NULL OR l.product_id=@productId
            ORDER BY p.name COLLATE NOCASE,l.expiry_date IS NULL,l.expiry_date,l.id
            """, new { productId }).Select(static row => row.ToModel()).ToList(), cancellationToken);

    public Task<IReadOnlyList<StockValuationItem>> GetValuationAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<StockValuationItem>)connection.Query<ValuationRow>("""
            SELECT c.id AS CategoryId,c.name AS CategoryName,COALESCE(SUM(t.quantity),0) AS Quantity,
                   COALESCE(SUM(t.cost_value),0) AS CostValue,
                   COALESCE(SUM(t.selling_value),0) AS SellingValue,
                   COALESCE(SUM(t.mrp_value),0) AS MrpValue
            FROM category c JOIN product p ON p.category_id=c.id
            JOIN product_stock_total t ON t.product_id=p.id
            WHERE t.quantity<>0 OR t.cost_value<>0 OR t.selling_value<>0 OR t.mrp_value<>0
            GROUP BY c.id,c.name ORDER BY c.name COLLATE NOCASE
            """).Select(static row => row.ToModel()).ToList(), cancellationToken);

    // The ledger readers below show history from before the direct-stock upgrade.
    public Task<IReadOnlyList<LedgerItem>> GetLedgerAsync(long productId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (productId <= 0 || from > to) throw new DomainException(ErrorCode.Validation, "Invalid ledger filter.");
        return database.ReadAsync(connection => (IReadOnlyList<LedgerItem>)connection.Query<LedgerRow>("""
            WITH history AS (
              SELECT m.id AS Id,m.moved_at AS MovedAt,m.move_date AS MoveDate,
                     m.type_code AS TypeCode,m.qty AS Qty,m.unit_cost AS UnitCost,
                     m.ref_type AS RefType,m.ref_id AS RefId,m.note AS Note,
                     COALESCE(l.lot_code,o.lot_code_snapshot,pl.lot_code_snapshot,'Legacy unknown') AS LotCode,
                     SUM(m.qty) OVER (PARTITION BY m.lot_id ORDER BY m.id) AS LotBalance
              FROM stock_movement m LEFT JOIN product_lot l ON l.id=m.lot_id
              LEFT JOIN direct_stock_origin o ON o.lot_id=m.lot_id
              LEFT JOIN purchase_line pl ON pl.lot_id_snapshot=m.lot_id
              WHERE m.product_id=@productId
            )
            SELECT Id,MovedAt,MoveDate,TypeCode,Qty,UnitCost,RefType,RefId,Note,
                   LotCode,LotBalance
            FROM history WHERE MoveDate BETWEEN @from AND @to ORDER BY Id
            """, new { productId, from, to }).Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    public Task<LedgerPage> GetLedgerPageAsync(long productId, DateOnly from, DateOnly to,
        long? beforeId = null, string? type = null, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (productId <= 0 || from > to || limit is < 1 or > 200 || beforeId <= 0)
            throw new DomainException(ErrorCode.Validation, "Invalid ledger page filter.");
        return database.ReadAsync(connection =>
        {
            List<LedgerItem> rows = connection.Query<LedgerRow>("""
                WITH page AS MATERIALIZED (
                    SELECT m.id,m.moved_at,m.move_date,m.type_code,m.qty,m.unit_cost,
                           m.ref_type,m.ref_id,m.note,m.lot_id
                    FROM stock_movement m INDEXED BY ix_move_product_id
                    WHERE m.product_id=@productId AND m.move_date BETWEEN @from AND @to
                      AND (@beforeId IS NULL OR m.id<@beforeId)
                      AND (@type IS NULL OR m.type_code=@type)
                    ORDER BY m.id DESC LIMIT @take
                )
                SELECT m.id AS Id,m.moved_at AS MovedAt,m.move_date AS MoveDate,
                       m.type_code AS TypeCode,m.qty AS Qty,m.unit_cost AS UnitCost,
                       m.ref_type AS RefType,m.ref_id AS RefId,m.note AS Note,
                       COALESCE(l.lot_code,o.lot_code_snapshot,pl.lot_code_snapshot,'Legacy unknown') AS LotCode,
                       (SELECT COALESCE(SUM(b.qty),0) FROM stock_movement b
                        WHERE b.lot_id=m.lot_id AND b.id<=m.id) AS LotBalance
                FROM page m LEFT JOIN product_lot l ON l.id=m.lot_id
                LEFT JOIN direct_stock_origin o ON o.lot_id=m.lot_id
                LEFT JOIN purchase_line pl ON pl.lot_id_snapshot=m.lot_id
                ORDER BY m.id DESC
                """, new { productId, from, to, beforeId, type, take = limit + 1 })
                .Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            return new LedgerPage(rows, hasMore, rows.LastOrDefault()?.Id);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetMovementTypesAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<string>)connection.Query<string>(
            "SELECT code FROM stock_movement_type ORDER BY code").ToList(), cancellationToken);

    public Task<StockSourceDocument> GetSourceDocumentAsync(LedgerItem movement,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        ArgumentNullException.ThrowIfNull(movement);
        SourceHeaderRow? header;
        SourceLineRow[] lines;
        string type;
        switch (movement.RefType)
        {
            case "direct_stock_origin" when movement.RefId is { } originId:
                type = "Direct entry";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT lot_code_snapshot AS Number,origin_date AS Date,
                           provenance AS Status,reason AS Notes
                    FROM direct_stock_origin WHERE id=@originId
                    """, new { originId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT COALESCE(product_name_snapshot,'Legacy unknown') AS ProductName,
                           COALESCE(lot_code_snapshot,'Legacy unknown') AS LotCode,
                           quantity AS Quantity,
                           CAST(ROUND(quantity*unit_cost/1000.0) AS INTEGER) AS Amount
                    FROM direct_stock_origin WHERE id=@originId
                    """, new { originId }).ToArray();
                break;
            case "internal_use_record" when movement.RefId is { } internalId:
                type = "Internal use";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT CAST(id AS TEXT) AS Number,used_date AS Date,reason AS Notes
                    FROM internal_use_record WHERE id=@internalId
                    """, new { internalId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT product_name_snapshot AS ProductName,lot_code_snapshot AS LotCode,
                           quantity AS Quantity,
                           CAST(ROUND(quantity*unit_cost/1000.0) AS INTEGER) AS Amount
                    FROM internal_use_record WHERE id=@internalId
                    """, new { internalId }).ToArray();
                break;
            case "purchase_line" when movement.RefId is { } purchaseLineId:
                type = "Purchase";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT po.doc_no AS Number,po.received_date AS Date,
                           COALESCE(po.vendor_name_snapshot,'Legacy unknown') AS Party,
                           po.status AS Status,po.notes AS Notes
                    FROM purchase_line pl JOIN purchase po ON po.id=pl.purchase_id
                    WHERE pl.id=@purchaseLineId
                    """, new { purchaseLineId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT COALESCE(pl.product_name_snapshot,'Legacy unknown') AS ProductName,
                           COALESCE(pl.lot_code_snapshot,'Legacy unknown') AS LotCode,
                           pl.qty+pl.free_qty AS Quantity,pl.line_total AS Amount
                    FROM purchase_line source JOIN purchase_line pl ON pl.purchase_id=source.purchase_id
                    WHERE source.id=@purchaseLineId ORDER BY pl.line_no
                    """, new { purchaseLineId }).ToArray();
                break;
            case "sale_line" when movement.RefId is { } saleLineId:
                type = "Sale";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT s.invoice_no AS Number,s.sale_date AS Date,c.name AS Party,
                           s.status AS Status,s.void_reason AS Notes
                    FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                    LEFT JOIN customer c ON c.id=s.customer_id WHERE sl.id=@saleLineId
                    """, new { saleLineId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT sl.product_name AS ProductName,
                           COALESCE(sl.lot_code_snapshot,'Legacy unknown') AS LotCode,
                           sl.qty AS Quantity,sl.line_total AS Amount
                    FROM sale_line source JOIN sale_line sl ON sl.sale_id=source.sale_id
                    WHERE source.id=@saleLineId
                    ORDER BY sl.line_no
                    """, new { saleLineId }).ToArray();
                break;
            case "sale_return" when movement.RefId is { } returnId:
                type = "Return";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT r.return_no AS Number,r.return_date AS Date,s.invoice_no AS Party,
                           r.refund_mode AS Status,r.reason AS Notes
                    FROM sale_return r JOIN sale s ON s.id=r.sale_id WHERE r.id=@returnId
                    """, new { returnId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT COALESCE(rl.product_name_snapshot,'Legacy unknown') AS ProductName,
                           COALESCE(rl.lot_code_snapshot,'Legacy unknown') AS LotCode,
                           rl.qty AS Quantity,rl.refund_amount AS Amount
                    FROM sale_return_line rl
                    WHERE rl.sale_return_id=@returnId ORDER BY rl.id
                    """, new { returnId }).ToArray();
                break;
            case "vendor_return" when movement.RefId is { } vendorReturnId:
                type = "VendorReturn";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT vr.doc_no AS Number,vr.return_date AS Date,
                           COALESCE(po.vendor_name_snapshot,'Legacy unknown') AS Party,
                           vr.vendor_credit_no AS Status,vr.reason AS Notes
                    FROM vendor_return vr JOIN purchase po ON po.id=vr.purchase_id
                    WHERE vr.id=@vendorReturnId
                    """, new { vendorReturnId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT COALESCE(vrl.product_name_snapshot,'Legacy unknown') AS ProductName,
                           COALESCE(vrl.lot_code_snapshot,'Legacy unknown') AS LotCode,
                           vrl.qty AS Quantity,
                           vrl.credit_before_tax+vrl.tax_credit AS Amount
                    FROM vendor_return_line vrl
                    WHERE vrl.vendor_return_id=@vendorReturnId ORDER BY vrl.id
                    """, new { vendorReturnId }).ToArray();
                break;
            case "damage" when movement.RefId is { } damageId:
                type = "Damage";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT doc_no AS Number,damage_date AS Date,reason AS Status,notes AS Notes
                    FROM damage WHERE id=@damageId
                    """, new { damageId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT COALESCE(d.product_name_snapshot,'Legacy unknown') AS ProductName,
                           COALESCE(d.lot_code_snapshot,'Legacy unknown') AS LotCode,
                           d.qty AS Quantity,d.cost_value AS Amount
                    FROM damage d WHERE d.id=@damageId
                    """, new { damageId }).ToArray();
                break;
            case "stock_count" when movement.RefId is { } countId:
                type = "Count";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT CAST(id AS TEXT) AS Number,count_date AS Date,status AS Status,notes AS Notes
                    FROM stock_count WHERE id=@countId
                    """, new { countId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT COALESCE(o.product_name_snapshot,pl.product_name_snapshot,'Legacy unknown') AS ProductName,
                           COALESCE(o.lot_code_snapshot,pl.lot_code_snapshot,'Legacy unknown') AS LotCode,
                           m.qty AS Quantity,
                           CAST(ROUND(m.qty*m.unit_cost/1000.0) AS INTEGER) AS Amount
                    FROM stock_movement m
                    LEFT JOIN direct_stock_origin o ON o.lot_id=m.lot_id
                    LEFT JOIN purchase_line pl ON pl.lot_id_snapshot=m.lot_id
                    WHERE m.ref_type='stock_count' AND m.ref_id=@countId ORDER BY m.id
                    """, new { countId }).ToArray();
                break;
            default:
                type = movement.RefType is "opening_import" ? "Opening" : "Adjustment";
                header = new SourceHeaderRow
                {
                    Number = movement.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Date = movement.MoveDate,
                    Status = movement.Type,
                    Notes = movement.Note,
                };
                lines = connection.Query<SourceLineRow>("""
                    SELECT COALESCE(o.product_name_snapshot,pl.product_name_snapshot,'Legacy unknown') AS ProductName,
                           COALESCE(o.lot_code_snapshot,pl.lot_code_snapshot,'Legacy unknown') AS LotCode,
                           m.qty AS Quantity,
                           CAST(ROUND(m.qty*m.unit_cost/1000.0) AS INTEGER) AS Amount
                    FROM stock_movement m
                    LEFT JOIN direct_stock_origin o ON o.lot_id=m.lot_id
                    LEFT JOIN purchase_line pl ON pl.lot_id_snapshot=m.lot_id
                    WHERE m.id=@id
                    """, new { movement.Id }).ToArray();
                break;
        }
        if (header is null) throw new DomainException(ErrorCode.NotFound,
            movement.RefType ?? "stock_movement", movement.RefId ?? movement.Id);
        return new StockSourceDocument(type, header.Number ?? movement.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            header.Date, header.Party, header.Status, header.Notes,
            lines.Select(static row => new StockSourceLine(row.ProductName, row.LotCode,
                Quantity.FromScaled(row.Quantity), Money.FromScaled(row.Amount))).ToArray());
    }, cancellationToken);

    public async Task<long> CreateDirectLotAsync(OpeningStockLine line, string reason,
        string operationId, CancellationToken cancellationToken = default)
    {
        ValidateOpening(line);
        string note = Required(reason, 500, nameof(reason));
        string? batch = Optional(line.BatchNumber, 120);
        periods.EnsureOpen(line.ReceivedDate);
        long lotId = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.DirectEntry);
            if (replay?.RefId is long previous) return previous;
            periods.EnsureOpen(transaction, line.ReceivedDate);
            ProductRule product = LoadProductRule(transaction, line.ProductId);
            ValidateProductQuantity(product, line.Quantity, line.ExpiryDate);
            if (line.Mrp is not null && line.SellingPrice > line.Mrp.Value)
                throw new DomainException(ErrorCode.PriceAboveMrp);
            string lotCode = numbers.Next(transaction, DocType.Lot, line.ReceivedDate);
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,
                    unit_cost,mrp,selling_price,qty_received,direct_reason)
                VALUES (@ProductId,@lotCode,@BatchNumber,@receivedDate,@expiryDate,
                    @unitCost,@mrp,@sellingPrice,@quantity,@note) RETURNING id
                """, new
            {
                line.ProductId,
                lotCode,
                BatchNumber = batch,
                receivedDate = Format(line.ReceivedDate),
                expiryDate = line.ExpiryDate is null ? null : Format(line.ExpiryDate.Value),
                unitCost = line.UnitCost.Scaled,
                mrp = line.Mrp?.Scaled,
                sellingPrice = line.SellingPrice.Scaled,
                quantity = line.Quantity.Scaled,
                note
            }, transaction.Transaction);
            long originId = transaction.Connection.ExecuteScalar<long>(
                "SELECT id FROM direct_stock_origin WHERE lot_id=@id", new { id }, transaction.Transaction);
            ledger.Post(transaction, new(line.ReceivedDate, line.ProductId, id, "DIRECT_ENTRY",
                line.Quantity, line.UnitCost, "direct_stock_origin", originId, note));
            audit.Write(transaction, "direct_stock_origin", originId, "CREATE", new { lotId = id, note });
            OperationGuard.Complete(transaction, operationId, "product_lot", id);
            return id;
        }, cancellationToken);
        WeakReferenceMessenger.Default.Send(new StockChangedMessage([line.ProductId]));
        return lotId;
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
        var changed = new HashSet<long>();
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
                string lotCode = numbers.Next(transaction, DocType.Lot, line.ReceivedDate);
                long lotId = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,unit_cost,mrp,
                                            selling_price,qty_received)
                    VALUES (@ProductId,@lotCode,@BatchNumber,@ReceivedDate,@ExpiryDate,@UnitCost,@Mrp,@SellingPrice,@Quantity)
                    RETURNING id
                    """, new
                {
                    line.ProductId,
                    lotCode,
                    line.BatchNumber,
                    ReceivedDate = Format(line.ReceivedDate),
                    ExpiryDate = line.ExpiryDate is null ? null : Format(line.ExpiryDate.Value),
                    UnitCost = line.UnitCost.Scaled,
                    Mrp = line.Mrp?.Scaled,
                    SellingPrice = line.SellingPrice.Scaled,
                    Quantity = line.Quantity.Scaled,
                }, transaction.Transaction);
                ledger.Post(transaction, new(line.ReceivedDate, line.ProductId, lotId, "OPENING", line.Quantity,
                    line.UnitCost, "opening_import", null, "Opening stock"));
                firstLot = firstLot == 0 ? lotId : firstLot;
                changed.Add(line.ProductId);
            }
            audit.Write(transaction, "opening_stock", firstLot, "IMPORT_BATCH", new { rows = batch.Length, batchNo = number });
            ImportJobs.RecordBatch(transaction, operationId, number, created: batch.Length, firstLot: firstLot);
        }, cancellationToken);
        }
        long result = (await database.WriteAsync(tx => ImportJobs.Complete(tx, operationId,
            batches.Length, openingStock: true), cancellationToken)).ResultId;
        if (changed.Count > 0) WeakReferenceMessenger.Default.Send(new StockChangedMessage(changed.ToArray()));
        return result;
    }

    public async Task AdjustAsync(long lotId, DateOnly date, Quantity change, string reason, string operationId,
        CancellationToken cancellationToken = default)
    {
        string note = Required(reason, 240, nameof(reason));
        if (lotId <= 0 || change == Quantity.Zero) throw new DomainException(ErrorCode.Validation, "A lot and non-zero change are required.");
        long productId = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.StockAdjustment);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(transaction, date);
            LotIdentity? lot = transaction.Connection.QuerySingleOrDefault<LotIdentity>(
                "SELECT product_id AS ProductId,unit_cost AS UnitCost FROM product_lot WHERE id=@lotId",
                new { lotId }, transaction.Transaction);
            if (lot is null) throw new DomainException(ErrorCode.NotFound, "lot", lotId);
            ledger.Post(transaction, new(date, lot.ProductId, lotId, "ADJUSTMENT", change,
                Money.FromScaled(lot.UnitCost), "manual_adjustment", lotId, note));
            audit.Write(transaction, "product_lot", lotId, "ADJUST", new { quantity = change.Scaled, reason = note });
            OperationGuard.Complete(transaction, operationId, "product_lot", lotId);
            return lot.ProductId;
        }, cancellationToken);
        if (productId > 0) WeakReferenceMessenger.Default.Send(new StockChangedMessage([productId]));
    }

    public Task ChangeLotPriceAsync(long lotId, Money sellingPrice, Money? mrp, CancellationToken cancellationToken = default)
        => ChangeLotPriceCoreAsync(lotId, sellingPrice, mrp, null, null, false, cancellationToken);

    public Task ChangeLotPriceAsync(long lotId, Money sellingPrice, Money? mrp,
        Money expectedPrice, Money? expectedMrp, CancellationToken cancellationToken = default)
        => ChangeLotPriceCoreAsync(lotId, sellingPrice, mrp, expectedPrice, expectedMrp, true, cancellationToken);

    private Task ChangeLotPriceCoreAsync(long lotId, Money sellingPrice, Money? mrp,
        Money? expectedPrice, Money? expectedMrp, bool checkExpected, CancellationToken cancellationToken)
    {
        if (sellingPrice < Money.Zero || (mrp is not null && sellingPrice > mrp.Value))
            throw new DomainException(ErrorCode.PriceAboveMrp);
        return database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute(
                """
                UPDATE product_lot SET selling_price=@price,mrp=@mrp
                WHERE id=@lotId AND (@checkExpected=0 OR (selling_price=@expectedPrice AND mrp IS @expectedMrp))
                """,
                new
                {
                    lotId,
                    price = sellingPrice.Scaled,
                    mrp = mrp?.Scaled,
                    expectedPrice = expectedPrice?.Scaled,
                    expectedMrp = expectedMrp?.Scaled,
                    checkExpected = checkExpected ? 1 : 0
                }, transaction.Transaction);
            if (changed == 0) ThrowLotEditConflict(transaction, lotId);
            audit.Write(transaction, "product_lot", lotId, "PRICE_CHANGE", new { price = sellingPrice.Scaled, mrp = mrp?.Scaled });
        }, cancellationToken);
    }

    public async Task CorrectLotAsync(LotCorrection correction, string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correction);
        LotItem original = correction.Original;
        string reason = Required(correction.Reason, 240, nameof(correction.Reason));
        string? batch = Optional(correction.BatchNumber, 100);
        if (correction.UnitCost < Money.Zero || correction.SellingPrice < Money.Zero ||
            correction.Mrp is { } mrp && correction.SellingPrice > mrp ||
            correction.ExpiryDate < original.ReceivedDate)
            throw new DomainException(ErrorCode.Validation, "Invalid lot correction.");
        long productId = await database.WriteAsync(transaction =>
        {
            periods.EnsureOpen(transaction, correction.Date);
            LotRow? current = transaction.Connection.QuerySingleOrDefault<LotRow>("""
                SELECT id AS Id,product_id AS ProductId,batch_no AS BatchNumber,
                       received_date AS ReceivedDate,expiry_date AS ExpiryDate,
                       unit_cost AS UnitCost,mrp AS Mrp,selling_price AS SellingPrice,
                       qty_received AS Received,qty_on_hand AS OnHand,is_active AS IsActive
                FROM product_lot WHERE id=@id
                """, new { id = original.Id }, transaction.Transaction);
            if (current is null) throw new DomainException(ErrorCode.NotFound, "lot", original.Id);
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.StockAdjustment);
            if (replay is not null) return current.ProductId;
            if (current.ProductId != original.ProductId || current.BatchNumber != original.BatchNumber ||
                current.ExpiryDate != original.ExpiryDate || current.UnitCost != original.UnitCost.Scaled ||
                current.Mrp != original.Mrp?.Scaled || current.SellingPrice != original.SellingPrice.Scaled ||
                current.OnHand != original.OnHand.Scaled)
                throw new DomainException(ErrorCode.Validation, "This lot changed since it was loaded. Refresh and try again.");
            if (correction.QuantityChange != Quantity.Zero || correction.ExpiryDate != original.ExpiryDate)
            {
                ProductRule rule = LoadProductRule(transaction, current.ProductId);
                ValidateProductQuantity(rule, correction.QuantityChange, correction.ExpiryDate);
            }
            if (correction.QuantityChange != Quantity.Zero)
                ledger.Post(transaction, new(correction.Date, current.ProductId, original.Id, "ADJUSTMENT",
                    correction.QuantityChange, original.UnitCost, "lot_correction", original.Id, reason));
            long balance = current.OnHand + correction.QuantityChange.Scaled;
            // Two opposite ledger entries revalue the remaining stock without changing its quantity.
            // Their net value is visible in the stock-adjustment P&L and offsets inventory valuation.
            if (current.UnitCost != correction.UnitCost.Scaled && balance != 0)
            {
                Quantity reverse = Quantity.FromScaled(-balance);
                Quantity restore = Quantity.FromScaled(balance);
                ledger.Post(transaction, new(correction.Date, current.ProductId, original.Id, "ADJUSTMENT",
                    reverse, original.UnitCost, "lot_cost_correction", original.Id, reason));
                ledger.Post(transaction, new(correction.Date, current.ProductId, original.Id, "ADJUSTMENT",
                    restore, correction.UnitCost, "lot_cost_correction", original.Id, reason));
            }
            transaction.Connection.Execute("""
                UPDATE product_lot SET batch_no=@batch,expiry_date=@expiry,
                    unit_cost=@cost,selling_price=@price,mrp=@mrp WHERE id=@id
                """, new
            {
                id = original.Id,
                batch,
                expiry = correction.ExpiryDate,
                cost = correction.UnitCost.Scaled,
                price = correction.SellingPrice.Scaled,
                mrp = correction.Mrp?.Scaled
            }, transaction.Transaction);
            audit.Write(transaction, "product_lot", original.Id, "CORRECT", new
            {
                reason,
                before = new
                {
                    original.BatchNumber,
                    original.ExpiryDate,
                    cost = original.UnitCost.Scaled,
                    price = original.SellingPrice.Scaled,
                    mrp = original.Mrp?.Scaled,
                    quantity = original.OnHand.Scaled
                },
                after = new
                {
                    batch,
                    correction.ExpiryDate,
                    cost = correction.UnitCost.Scaled,
                    price = correction.SellingPrice.Scaled,
                    mrp = correction.Mrp?.Scaled,
                    quantity = balance
                }
            });
            OperationGuard.Complete(transaction, operationId, "product_lot", original.Id);
            return current.ProductId;
        }, cancellationToken);
        WeakReferenceMessenger.Default.Send(new StockChangedMessage([productId]));
    }

    public Task SetLotActiveAsync(long lotId, bool active, CancellationToken cancellationToken = default) =>
        SetLotActiveCoreAsync(lotId, active, null, cancellationToken);

    public Task SetLotActiveAsync(long lotId, bool active, bool expectedActive,
        CancellationToken cancellationToken = default) =>
        SetLotActiveCoreAsync(lotId, active, expectedActive, cancellationToken);

    private Task SetLotActiveCoreAsync(long lotId, bool active, bool? expectedActive,
        CancellationToken cancellationToken) =>
        database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute("""
                UPDATE product_lot SET is_active=@active WHERE id=@lotId
                  AND (@expectedActive IS NULL OR is_active=@expectedActive)
                """, new
            {
                lotId,
                active = active ? 1 : 0,
                expectedActive = expectedActive is null ? (int?)null : expectedActive.Value ? 1 : 0
            },
                transaction.Transaction);
            if (changed == 0) ThrowLotEditConflict(transaction, lotId);
            audit.Write(transaction, "product_lot", lotId, active ? "UNBLOCK" : "BLOCK", null);
        }, cancellationToken);

    private static void ThrowLotEditConflict(DbTx transaction, long lotId)
    {
        if (transaction.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot WHERE id=@lotId",
            new { lotId }, transaction.Transaction) == 0)
            throw new DomainException(ErrorCode.NotFound, "lot", lotId);
        throw new DomainException(ErrorCode.Validation, "This lot changed since it was loaded. Refresh it and try again.");
    }

    private static ProductRule LoadProductRule(DbTx transaction, long productId) =>
        transaction.Connection.QuerySingleOrDefault<ProductRule>("""
            SELECT p.track_expiry AS TrackExpiry,u.allow_fraction AS AllowFraction
            FROM product p JOIN uom u ON u.id=p.uom_id WHERE p.id=@productId AND p.is_active=1
            """, new { productId }, transaction.Transaction)
        ?? throw new DomainException(ErrorCode.NotFound, "product", productId);

    private static void ValidateOpening(OpeningStockLine line)
    {
        if (line.ProductId <= 0 || line.Quantity <= Quantity.Zero || line.UnitCost < Money.Zero ||
            line.SellingPrice < Money.Zero || line.ReceivedDate == default)
            throw new DomainException(ErrorCode.Validation, "Invalid opening-stock row.");
        if (line.ExpiryDate is not null && line.ExpiryDate < line.ReceivedDate)
            throw new DomainException(ErrorCode.InvalidDate);
    }

    private static void ValidateProductQuantity(ProductRule product, Quantity quantity, DateOnly? expiry)
    {
        if (!product.AllowFraction && !quantity.IsWhole) throw new DomainException(ErrorCode.FractionalQtyNotAllowed);
        if (product.TrackExpiry && expiry is null) throw new DomainException(ErrorCode.Validation, "Expiry is required.");
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

    private sealed class OverviewRow { public long ProductId { get; init; } public string Sku { get; init; } = ""; public string Name { get; init; } = ""; public long Quantity { get; init; } public long CostValue { get; init; } public long ReorderLevel { get; init; } public bool IsLow { get; init; } }
    private sealed class LotRow { public long Id { get; init; } public long ProductId { get; init; } public string ProductName { get; init; } = ""; public string LotCode { get; init; } = ""; public string? BatchNumber { get; init; } public DateOnly ReceivedDate { get; init; } public DateOnly? ExpiryDate { get; init; } public long UnitCost { get; init; } public long? Mrp { get; init; } public long SellingPrice { get; init; } public long Received { get; init; } public long OnHand { get; init; } public bool IsActive { get; init; } public string ExpiryBucketCode { get; init; } = "NONE"; public string OriginKind { get; init; } = "DIRECT"; public long DetailVersion { get; init; } public LotItem ToModel() => new(Id, ProductId, ProductName, LotCode, BatchNumber, ReceivedDate, ExpiryDate, Money.FromScaled(UnitCost), Mrp is null ? null : Money.FromScaled(Mrp.Value), Money.FromScaled(SellingPrice), Quantity.FromScaled(Received), Quantity.FromScaled(OnHand), IsActive, ExpiryBucketCode) { OriginKind = OriginKind, DetailVersion = DetailVersion }; }
    private sealed class LedgerRow { public long Id { get; init; } public DateTime MovedAt { get; init; } public DateOnly MoveDate { get; init; } public string TypeCode { get; init; } = ""; public long Qty { get; init; } public long UnitCost { get; init; } public string? RefType { get; init; } public long? RefId { get; init; } public string LotCode { get; init; } = ""; public long LotBalance { get; init; } public string? Note { get; init; } public LedgerItem ToModel() => new(Id, MovedAt, MoveDate, TypeCode, Quantity.FromScaled(Qty), Money.FromScaled(UnitCost), RefType, RefId, LotCode, Quantity.FromScaled(LotBalance), Note); }
    private sealed class SourceHeaderRow { public string? Number { get; init; } public DateOnly Date { get; init; } public string? Party { get; init; } public string? Status { get; init; } public string? Notes { get; init; } }
    private sealed class SourceLineRow { public string ProductName { get; init; } = ""; public string LotCode { get; init; } = ""; public long Quantity { get; init; } public long Amount { get; init; } }
    private sealed class ProductRule { public bool TrackExpiry { get; init; } public bool AllowFraction { get; init; } }
    private sealed class LotIdentity { public long ProductId { get; init; } public long UnitCost { get; init; } }
    private sealed class ValuationRow { public long CategoryId { get; init; } public string CategoryName { get; init; } = ""; public long Quantity { get; init; } public long CostValue { get; init; } public long SellingValue { get; init; } public long MrpValue { get; init; } public StockValuationItem ToModel() => new(CategoryId, CategoryName, GroceryErp.Core.Numerics.Quantity.FromScaled(Quantity), Money.FromScaled(CostValue), Money.FromScaled(SellingValue), Money.FromScaled(MrpValue)); }
}
