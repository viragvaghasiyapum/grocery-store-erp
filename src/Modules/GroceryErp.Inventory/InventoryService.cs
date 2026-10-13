using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Messages;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Inventory;

public sealed class InventoryService(
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
        int limit = 100, bool availableOnly = false, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200 || beforeId <= 0)
            throw new DomainException(ErrorCode.Validation, "Invalid lot page.");
        string pattern = "%" + (query ?? string.Empty).Trim().Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return database.ReadAsync(connection =>
        {
            List<LotItem> rows = connection.Query<LotRow>("""
                WITH visible AS MATERIALIZED (
                    SELECT l.id FROM product_lot l JOIN product p ON p.id=l.product_id
                    WHERE (@productId IS NULL OR l.product_id=@productId)
                      AND (@availableOnly=0 OR (l.is_active=1 AND l.qty_on_hand>0))
                      AND (@beforeId IS NULL OR l.id<@beforeId)
                      AND (p.name LIKE @pattern ESCAPE '\' OR l.lot_code LIKE @pattern ESCAPE '\'
                           OR l.batch_no LIKE @pattern ESCAPE '\')
                    ORDER BY l.id DESC LIMIT @take
                )
                SELECT l.id AS Id,l.product_id AS ProductId,p.name AS ProductName,l.lot_code AS LotCode,
                       l.batch_no AS BatchNumber,l.received_date AS ReceivedDate,l.expiry_date AS ExpiryDate,
                       l.unit_cost AS UnitCost,l.mrp AS Mrp,l.selling_price AS SellingPrice,
                       l.qty_received AS Received,l.qty_on_hand AS OnHand,l.is_active AS IsActive,
                       CASE WHEN l.is_active=0 THEN 'BLOCKED' WHEN l.qty_on_hand<=0 THEN 'EMPTY'
                            WHEN l.expiry_date IS NULL THEN 'NONE'
                            WHEN l.expiry_date<date('now','localtime') THEN 'EXPIRED'
                            WHEN l.expiry_date<=date('now','localtime','+7 days') THEN 'CRITICAL'
                            WHEN julianday(l.expiry_date)-julianday('now','localtime','start of day')<=
                                 COALESCE(p.expiry_alert_days,c.expiry_alert_days,pc.expiry_alert_days,
                                          CAST((SELECT value FROM setting WHERE key='expiry.default_alert_days') AS INTEGER),30)
                                 THEN 'SOON' ELSE 'FRESH' END AS ExpiryBucketCode
                FROM visible page JOIN product_lot l ON l.id=page.id JOIN product p ON p.id=l.product_id
                JOIN category c ON c.id=p.category_id LEFT JOIN category pc ON pc.id=c.parent_id
                ORDER BY l.id DESC
                """, new { productId, beforeId, availableOnly, pattern, take = limit + 1 })
                .Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            return new LotPage(rows, hasMore, rows.LastOrDefault()?.Id);
        }, cancellationToken);
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
            SELECT c.id AS CategoryId,c.name AS CategoryName,COALESCE(SUM(l.qty_on_hand),0) AS Quantity,
                   COALESCE(SUM(CAST(ROUND(l.qty_on_hand*l.unit_cost/1000.0) AS INTEGER)),0) AS CostValue,
                   COALESCE(SUM(CAST(ROUND(l.qty_on_hand*l.selling_price/1000.0) AS INTEGER)),0) AS SellingValue,
                   COALESCE(SUM(CAST(ROUND(l.qty_on_hand*COALESCE(l.mrp,l.selling_price)/1000.0) AS INTEGER)),0) AS MrpValue
            FROM category c JOIN product p ON p.category_id=c.id
            JOIN product_lot l ON l.product_id=p.id AND l.qty_on_hand<>0
            GROUP BY c.id,c.name ORDER BY c.name COLLATE NOCASE
            """).Select(static row => row.ToModel()).ToList(), cancellationToken);

    public Task<int> RebuildStockCacheAsync(CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute("""
                UPDATE product_lot SET qty_on_hand=COALESCE(
                    (SELECT SUM(m.qty) FROM stock_movement m WHERE m.lot_id=product_lot.id),0)
                WHERE qty_on_hand<>COALESCE(
                    (SELECT SUM(m.qty) FROM stock_movement m WHERE m.lot_id=product_lot.id),0)
                """, transaction: transaction.Transaction);
            audit.Write(transaction, "stock_cache", 0, "REBUILD", new { lots = changed });
            return changed;
        }, cancellationToken);

    public Task<IReadOnlyList<LedgerItem>> GetLedgerAsync(long productId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (productId <= 0 || from > to) throw new DomainException(ErrorCode.Validation, "Invalid ledger filter.");
        return database.ReadAsync(connection => (IReadOnlyList<LedgerItem>)connection.Query<LedgerRow>("""
            WITH history AS (
              SELECT m.id AS Id,m.moved_at AS MovedAt,m.move_date AS MoveDate,
                     m.type_code AS TypeCode,m.qty AS Qty,m.unit_cost AS UnitCost,
                     m.ref_type AS RefType,m.ref_id AS RefId,m.note AS Note,
                     l.lot_code AS LotCode,
                     SUM(m.qty) OVER (PARTITION BY m.lot_id ORDER BY m.id) AS LotBalance
              FROM stock_movement m JOIN product_lot l ON l.id=m.lot_id
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
                    FROM stock_movement m
                    WHERE m.product_id=@productId AND m.move_date BETWEEN @from AND @to
                      AND (@beforeId IS NULL OR m.id<@beforeId)
                      AND (@type IS NULL OR m.type_code=@type)
                    ORDER BY m.id DESC LIMIT @take
                )
                SELECT m.id AS Id,m.moved_at AS MovedAt,m.move_date AS MoveDate,
                       m.type_code AS TypeCode,m.qty AS Qty,m.unit_cost AS UnitCost,
                       m.ref_type AS RefType,m.ref_id AS RefId,m.note AS Note,
                       l.lot_code AS LotCode,
                       COALESCE((SELECT SUM(b.qty) FROM stock_movement b
                                 WHERE b.lot_id=m.lot_id AND b.id<=m.id),0) AS LotBalance
                FROM page m JOIN product_lot l ON l.id=m.lot_id ORDER BY m.id DESC
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
            case "purchase_line" when movement.RefId is { } purchaseLineId:
                type = "Purchase";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT po.doc_no AS Number,po.received_date AS Date,v.name AS Party,
                           po.status AS Status,po.notes AS Notes
                    FROM purchase_line pl JOIN purchase po ON po.id=pl.purchase_id
                    JOIN vendor v ON v.id=po.vendor_id WHERE pl.id=@purchaseLineId
                    """, new { purchaseLineId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT p.name AS ProductName,COALESCE(l.lot_code,'') AS LotCode,
                           pl.qty+pl.free_qty AS Quantity,pl.line_total AS Amount
                    FROM purchase_line source JOIN purchase_line pl ON pl.purchase_id=source.purchase_id
                    JOIN product p ON p.id=pl.product_id
                    LEFT JOIN product_lot l ON l.purchase_line_id=pl.id
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
                    SELECT sl.product_name AS ProductName,l.lot_code AS LotCode,
                           sl.qty AS Quantity,sl.line_total AS Amount
                    FROM sale_line source JOIN sale_line sl ON sl.sale_id=source.sale_id
                    JOIN product_lot l ON l.id=sl.lot_id WHERE source.id=@saleLineId
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
                    SELECT sl.product_name AS ProductName,l.lot_code AS LotCode,
                           rl.qty AS Quantity,rl.refund_amount AS Amount
                    FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
                    JOIN product_lot l ON l.id=sl.lot_id
                    WHERE rl.sale_return_id=@returnId ORDER BY rl.id
                    """, new { returnId }).ToArray();
                break;
            case "vendor_return" when movement.RefId is { } vendorReturnId:
                type = "VendorReturn";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT vr.doc_no AS Number,vr.return_date AS Date,v.name AS Party,
                           vr.vendor_credit_no AS Status,vr.reason AS Notes
                    FROM vendor_return vr JOIN vendor v ON v.id=vr.vendor_id WHERE vr.id=@vendorReturnId
                    """, new { vendorReturnId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT p.name AS ProductName,l.lot_code AS LotCode,vrl.qty AS Quantity,
                           vrl.credit_before_tax+vrl.tax_credit AS Amount
                    FROM vendor_return_line vrl JOIN product p ON p.id=vrl.product_id
                    JOIN product_lot l ON l.id=vrl.lot_id
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
                    SELECT p.name AS ProductName,l.lot_code AS LotCode,
                           d.qty AS Quantity,d.cost_value AS Amount
                    FROM damage d JOIN product p ON p.id=d.product_id
                    JOIN product_lot l ON l.id=d.lot_id WHERE d.id=@damageId
                    """, new { damageId }).ToArray();
                break;
            case "stock_count" when movement.RefId is { } countId:
                type = "Count";
                header = connection.QuerySingleOrDefault<SourceHeaderRow>("""
                    SELECT CAST(id AS TEXT) AS Number,count_date AS Date,status AS Status,notes AS Notes
                    FROM stock_count WHERE id=@countId
                    """, new { countId });
                lines = connection.Query<SourceLineRow>("""
                    SELECT p.name AS ProductName,l.lot_code AS LotCode,m.qty AS Quantity,
                           CAST(ROUND(m.qty*m.unit_cost/1000.0) AS INTEGER) AS Amount
                    FROM stock_movement m JOIN product p ON p.id=m.product_id
                    JOIN product_lot l ON l.id=m.lot_id
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
                    SELECT p.name AS ProductName,l.lot_code AS LotCode,m.qty AS Quantity,
                           CAST(ROUND(m.qty*m.unit_cost/1000.0) AS INTEGER) AS Amount
                    FROM stock_movement m JOIN product p ON p.id=m.product_id
                    JOIN product_lot l ON l.id=m.lot_id WHERE m.id=@id
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

    public async Task<long> ImportOpeningStockAsync(
        IReadOnlyList<OpeningStockLine> lines,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) throw new DomainException(ErrorCode.Validation, "Opening stock is empty.");
        var changed = new HashSet<long>();
        long result = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.OpeningStockImport);
            if (replay is not null) return replay.RefId ?? 0;
            long firstLot = 0;
            foreach (OpeningStockLine line in lines)
            {
                ValidateOpening(line);
                periods.EnsureOpen(line.ReceivedDate);
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
            audit.Write(transaction, "opening_stock", firstLot, "IMPORT", new { rows = lines.Count });
            OperationGuard.Complete(transaction, operationId, "product_lot", firstLot);
            return firstLot;
        }, cancellationToken);
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
            periods.EnsureOpen(date);
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
    {
        if (sellingPrice < Money.Zero || (mrp is not null && sellingPrice > mrp.Value))
            throw new DomainException(ErrorCode.PriceAboveMrp);
        return database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute(
                "UPDATE product_lot SET selling_price=@price,mrp=@mrp WHERE id=@lotId",
                new { lotId, price = sellingPrice.Scaled, mrp = mrp?.Scaled }, transaction.Transaction);
            if (changed == 0) throw new DomainException(ErrorCode.NotFound, "lot", lotId);
            audit.Write(transaction, "product_lot", lotId, "PRICE_CHANGE", new { price = sellingPrice.Scaled, mrp = mrp?.Scaled });
        }, cancellationToken);
    }

    public Task SetLotActiveAsync(long lotId, bool active, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute("UPDATE product_lot SET is_active=@active WHERE id=@lotId",
                new { lotId, active = active ? 1 : 0 }, transaction.Transaction);
            if (changed == 0) throw new DomainException(ErrorCode.NotFound, "lot", lotId);
            audit.Write(transaction, "product_lot", lotId, active ? "UNBLOCK" : "BLOCK", null);
        }, cancellationToken);

    public Task<long> CreateStockCountAsync(DateOnly date, IReadOnlyCollection<long>? categoryIds, string? notes,
        CancellationToken cancellationToken = default) => database.WriteAsync(transaction =>
    {
        periods.EnsureOpen(date);
        long snapshot = transaction.Connection.ExecuteScalar<long>("SELECT COALESCE(MAX(id),0) FROM stock_movement", transaction: transaction.Transaction);
        long id = transaction.Connection.ExecuteScalar<long>("""
            INSERT INTO stock_count(count_date,snapshot_movement_id,notes) VALUES (@date,@snapshot,@notes) RETURNING id
            """, new { date = Format(date), snapshot, notes = Optional(notes, 500) }, transaction.Transaction);
        if (categoryIds is { Count: > 0 })
            transaction.Connection.Execute("""
                INSERT INTO stock_count_scope(stock_count_id,category_id) VALUES (@id,@categoryId)
                """, categoryIds.Distinct().Select(categoryId => new { id, categoryId }), transaction.Transaction);
        string filter = categoryIds is { Count: > 0 } ? "AND p.category_id IN @categoryIds" : string.Empty;
        int rows = transaction.Connection.Execute($"""
            INSERT INTO stock_count_line(stock_count_id,product_id,lot_id,expected_qty,unit_cost)
            SELECT @id,l.product_id,l.id,l.qty_on_hand,l.unit_cost
            FROM product_lot l JOIN product p ON p.id=l.product_id
            WHERE p.is_active=1 {filter}
            """, new { id, categoryIds }, transaction.Transaction);
        if (rows == 0) throw new DomainException(ErrorCode.Validation, "No lots are available to count.");
        audit.Write(transaction, "stock_count", id, "CREATE", new { rows, snapshot });
        return id;
    }, cancellationToken);

    public Task<int> RefreshStockCountLotsAsync(long countId, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            string? status = transaction.Connection.ExecuteScalar<string?>(
                "SELECT status FROM stock_count WHERE id=@countId", new { countId }, transaction.Transaction);
            if (status != "DRAFT") throw new DomainException(ErrorCode.Validation, "Stock count is not an active draft.");
            int added = transaction.Connection.Execute("""
                INSERT INTO stock_count_line(stock_count_id,product_id,lot_id,expected_qty,unit_cost)
                SELECT @countId,l.product_id,l.id,
                       l.qty_on_hand-COALESCE((SELECT SUM(m.qty) FROM stock_movement m
                           WHERE m.lot_id=l.id AND m.id>sc.snapshot_movement_id),0),l.unit_cost
                FROM stock_count sc CROSS JOIN product_lot l JOIN product p ON p.id=l.product_id
                WHERE sc.id=@countId AND p.is_active=1
                  AND (NOT EXISTS (SELECT 1 FROM stock_count_scope s WHERE s.stock_count_id=sc.id)
                       OR EXISTS (SELECT 1 FROM stock_count_scope s
                                  WHERE s.stock_count_id=sc.id AND s.category_id=p.category_id))
                  AND NOT EXISTS (SELECT 1 FROM stock_count_line scl
                                  WHERE scl.stock_count_id=sc.id AND scl.lot_id=l.id)
                """, new { countId }, transaction.Transaction);
            if (added > 0) audit.Write(transaction, "stock_count", countId, "REFRESH_LOTS", new { added });
            return added;
        }, cancellationToken);

    public Task<IReadOnlyList<StockCountSummary>> GetDraftCountsAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<StockCountSummary>)connection.Query<StockCountSummary>("""
            SELECT sc.id AS Id,sc.count_date AS Date,COUNT(scl.id) AS LotCount,
                   SUM(CASE WHEN scl.counted_qty IS NOT NULL THEN 1 ELSE 0 END) AS CountedCount
            FROM stock_count sc JOIN stock_count_line scl ON scl.stock_count_id=sc.id
            WHERE sc.status='DRAFT' GROUP BY sc.id ORDER BY sc.id DESC
            """).ToList(), cancellationToken);

    public Task<DraftCountPage> GetDraftCountPageAsync(long? beforeId = null, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (beforeId <= 0 || limit is < 1 or > 100)
            throw new DomainException(ErrorCode.Validation, "Invalid draft count page.");
        return database.ReadAsync(connection =>
        {
            List<StockCountSummary> rows = connection.Query<StockCountSummary>("""
                WITH visible AS MATERIALIZED (
                    SELECT id,count_date FROM stock_count
                    WHERE status='DRAFT' AND (@beforeId IS NULL OR id<@beforeId)
                    ORDER BY id DESC LIMIT @take
                )
                SELECT v.id AS Id,v.count_date AS Date,
                       (SELECT COUNT(*) FROM stock_count_line scl WHERE scl.stock_count_id=v.id) AS LotCount,
                       (SELECT COUNT(*) FROM stock_count_line scl WHERE scl.stock_count_id=v.id
                            AND scl.counted_qty IS NOT NULL) AS CountedCount
                FROM visible v ORDER BY v.id DESC
                """, new { beforeId, take = limit + 1 }).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            return new DraftCountPage(rows, hasMore, rows.LastOrDefault()?.Id);
        }, cancellationToken);
    }

    public Task SetCountedQuantityAsync(long countId, long lotId, Quantity counted, CancellationToken cancellationToken = default) =>
        SetCountedQuantitiesAsync(countId, new Dictionary<long, Quantity> { [lotId] = counted }, cancellationToken);

    public async Task SetCountedQuantitiesAsync(long countId, IReadOnlyDictionary<long, Quantity> quantities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quantities);
        if (countId <= 0 || quantities.Count == 0 || quantities.Values.Any(static quantity => quantity < Quantity.Zero))
            throw new DomainException(ErrorCode.Validation, "Counted quantities must be non-negative.");
        foreach (KeyValuePair<long, Quantity>[] batch in quantities.Chunk(200))
            await database.WriteAsync(transaction =>
        {
            string? status = transaction.Connection.QuerySingleOrDefault<string>(
                "SELECT status FROM stock_count WHERE id=@countId", new { countId }, transaction.Transaction);
            if (status != "DRAFT") throw new DomainException(ErrorCode.Validation, "Stock count is not an active draft.");
            var rules = transaction.Connection.Query<CountUnitRule>("""
                SELECT scl.lot_id AS LotId,u.allow_fraction AS AllowFraction
                FROM stock_count_line scl JOIN product p ON p.id=scl.product_id JOIN uom u ON u.id=p.uom_id
                WHERE scl.stock_count_id=@countId AND scl.lot_id IN @lotIds
                """, new { countId, lotIds = batch.Select(static item => item.Key).ToArray() },
                transaction.Transaction).ToDictionary(static rule => rule.LotId);
            foreach ((long lotId, Quantity quantity) in batch)
            {
                if (!rules.TryGetValue(lotId, out CountUnitRule? rule))
                    throw new DomainException(ErrorCode.NotFound, "stock count line", lotId);
                if (!rule.AllowFraction && !quantity.IsWhole)
                    throw new DomainException(ErrorCode.FractionalQtyNotAllowed);
            }
            foreach ((long lotId, Quantity quantity) in batch)
                transaction.Connection.Execute("""
                    UPDATE stock_count_line SET counted_qty=@counted WHERE stock_count_id=@countId AND lot_id=@lotId
                    """, new { countId, lotId, counted = quantity.Scaled }, transaction.Transaction);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<StockCountDetails> GetStockCountAsync(long countId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection =>
        {
            CountHeader? header = connection.QuerySingleOrDefault<CountHeader>("""
                SELECT id AS Id,count_date AS Date,status AS Status,notes AS Notes,snapshot_movement_id AS Snapshot
                FROM stock_count WHERE id=@countId
                """, new { countId });
            if (header is null) throw new DomainException(ErrorCode.NotFound, "stock count", countId);
            IReadOnlyList<StockCountLine> lines = connection.Query<CountLineRow>("""
                SELECT scl.id AS Id,scl.product_id AS ProductId,p.name AS ProductName,scl.lot_id AS LotId,
                       l.lot_code AS LotCode,scl.expected_qty AS Expected,scl.counted_qty AS Counted,
                       scl.unit_cost AS UnitCost,
                       COALESCE((SELECT SUM(m.qty) FROM stock_movement m
                                 WHERE m.lot_id=scl.lot_id AND m.id>@snapshot),0) AS Movement
                FROM stock_count_line scl JOIN product p ON p.id=scl.product_id JOIN product_lot l ON l.id=scl.lot_id
                WHERE scl.stock_count_id=@countId ORDER BY p.name COLLATE NOCASE,l.lot_code
                """, new { countId, snapshot = header.Snapshot }).Select(static row => row.ToModel()).ToList();
            return new StockCountDetails(header.Id, header.Date, header.Status, header.Notes, lines);
        }, cancellationToken);

    public Task<StockCountPage> GetStockCountPageAsync(long countId, long? afterLineId = null,
        int pageSize = 100, CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        int take = Math.Clamp(pageSize, 1, 200);
        CountHeader? header = connection.QuerySingleOrDefault<CountHeader>("""
            SELECT id AS Id,count_date AS Date,status AS Status,notes AS Notes,snapshot_movement_id AS Snapshot
            FROM stock_count WHERE id=@countId
            """, new { countId });
        if (header is null) throw new DomainException(ErrorCode.NotFound, "stock count", countId);
        CountProgress progress = connection.QuerySingle<CountProgress>("""
            SELECT COUNT(*) AS Total,COUNT(counted_qty) AS Counted FROM stock_count_line WHERE stock_count_id=@countId
            """, new { countId });
        List<CountLineRow> rows = connection.Query<CountLineRow>("""
            SELECT scl.id AS Id,scl.product_id AS ProductId,p.name AS ProductName,scl.lot_id AS LotId,
                   l.lot_code AS LotCode,scl.expected_qty AS Expected,scl.counted_qty AS Counted,
                   scl.unit_cost AS UnitCost,
                   COALESCE((SELECT SUM(m.qty) FROM stock_movement m
                             WHERE m.lot_id=scl.lot_id AND m.id>@snapshot),0) AS Movement
            FROM stock_count_line scl JOIN product p ON p.id=scl.product_id
                 JOIN product_lot l ON l.id=scl.lot_id
            WHERE scl.stock_count_id=@countId AND (@afterLineId IS NULL OR scl.id>@afterLineId)
            ORDER BY scl.id LIMIT @limit
            """, new { countId, afterLineId, snapshot = header.Snapshot, limit = take + 1 }).ToList();
        bool more = rows.Count > take;
        if (more) rows.RemoveAt(take);
        return new StockCountPage(header.Id, header.Date, header.Status, header.Notes,
            rows.Select(static row => row.ToModel()).ToList(), more,
            more ? rows[^1].Id : null, progress.Total, progress.Counted);
    }, cancellationToken);

    public Task<StockCountReview> GetStockCountReviewAsync(long countId,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        string? status = connection.QuerySingleOrDefault<string>(
            "SELECT status FROM stock_count WHERE id=@countId", new { countId });
        if (status != "DRAFT") throw new DomainException(ErrorCode.Validation, "Stock count is not an active draft.");
        CountReviewRow row = connection.QuerySingle<CountReviewRow>("""
            WITH balances AS MATERIALIZED (
                SELECT scl.expected_qty AS Expected,scl.counted_qty AS Counted,
                       scl.unit_cost AS UnitCost,
                       COALESCE((SELECT SUM(m.qty) FROM stock_movement m
                                 WHERE m.lot_id=scl.lot_id AND m.id>sc.snapshot_movement_id),0) AS Movement
                FROM stock_count_line scl JOIN stock_count sc ON sc.id=scl.stock_count_id
                WHERE scl.stock_count_id=@countId
            )
            SELECT COUNT(*) AS Total,COUNT(Counted) AS Counted,
                   COALESCE(SUM(CASE WHEN Counted IS NOT NULL AND Counted<>Expected+Movement
                                     THEN 1 ELSE 0 END),0) AS Differences,
                   COALESCE(SUM(CASE WHEN Counted IS NOT NULL THEN
                       CAST(ROUND((Counted-Expected-Movement)*UnitCost/1000.0) AS INTEGER)
                       ELSE 0 END),0) AS NetValue,
                   (SELECT COALESCE(MAX(id),0) FROM stock_movement) AS MovementHighWaterMark
            FROM balances
            """, new { countId });
        return new StockCountReview(row.Total, row.Counted, row.Differences,
            Money.FromScaled(row.NetValue), row.MovementHighWaterMark);
    }, cancellationToken);

    public Task<long?> FindStockCountLineAsync(long countId, string query, long? productId = null,
        long? lotId = null, CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        string normalized = query.Trim();
        if (normalized.Length == 0 && productId is null && lotId is null) return (long?)null;
        if (lotId is not null)
            return connection.ExecuteScalar<long?>("""
                SELECT id FROM stock_count_line WHERE stock_count_id=@countId AND lot_id=@lotId
                """, new { countId, lotId });
        if (productId is not null)
            return connection.ExecuteScalar<long?>("""
                SELECT id FROM stock_count_line WHERE stock_count_id=@countId AND product_id=@productId
                ORDER BY id LIMIT 1
                """, new { countId, productId });
        long? exact = connection.ExecuteScalar<long?>("""
            SELECT scl.id FROM product_lot l JOIN stock_count_line scl ON scl.lot_id=l.id
            WHERE scl.stock_count_id=@countId AND l.lot_code=@query COLLATE NOCASE LIMIT 1
            """, new { countId, query = normalized });
        return exact ?? connection.ExecuteScalar<long?>("""
            SELECT scl.id FROM product p JOIN stock_count_line scl ON scl.product_id=p.id
            WHERE scl.stock_count_id=@countId AND p.name LIKE @pattern ESCAPE '\'
            ORDER BY scl.id LIMIT 1
            """, new { countId, pattern = "%" + normalized.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%" });
    }, cancellationToken);

    public Task PostStockCountAsync(long countId, string operationId,
        IReadOnlyDictionary<long, long>? reviewedMovements = null, CancellationToken cancellationToken = default) =>
        PostStockCountCoreAsync(countId, operationId, reviewedMovements, null, cancellationToken);

    public Task PostReviewedStockCountAsync(long countId, string operationId,
        long reviewedMovementHighWaterMark, CancellationToken cancellationToken = default) =>
        PostStockCountCoreAsync(countId, operationId, null, reviewedMovementHighWaterMark, cancellationToken);

    private async Task PostStockCountCoreAsync(long countId, string operationId,
        IReadOnlyDictionary<long, long>? reviewedMovements, long? reviewedMovementHighWaterMark,
        CancellationToken cancellationToken)
    {
        var products = new HashSet<long>();
        await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.StockCountPost);
            if (replay is not null) return;
            CountHeader? header = transaction.Connection.QuerySingleOrDefault<CountHeader>("""
                SELECT id AS Id,count_date AS Date,status AS Status,notes AS Notes,snapshot_movement_id AS Snapshot
                FROM stock_count WHERE id=@countId
                """, new { countId }, transaction.Transaction);
            if (header is null) throw new DomainException(ErrorCode.NotFound, "stock count", countId);
            if (header.Status != "DRAFT") throw new DomainException(ErrorCode.Validation, "Stock count is already finalized.");
            periods.EnsureOpen(header.Date);
            if (transaction.Connection.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM product_lot l JOIN product p ON p.id=l.product_id
                WHERE p.is_active=1
                  AND (NOT EXISTS (SELECT 1 FROM stock_count_scope s WHERE s.stock_count_id=@countId)
                       OR EXISTS (SELECT 1 FROM stock_count_scope s
                                  WHERE s.stock_count_id=@countId AND s.category_id=p.category_id))
                  AND NOT EXISTS (SELECT 1 FROM stock_count_line scl
                                  WHERE scl.stock_count_id=@countId AND scl.lot_id=l.id)
                """, new { countId }, transaction.Transaction) > 0)
                throw new DomainException(ErrorCode.StockCountReviewChanged);
            if (reviewedMovementHighWaterMark is long watermark &&
                transaction.Connection.ExecuteScalar<long>("""
                    SELECT COUNT(*) FROM stock_movement m
                    JOIN stock_count_line scl ON scl.lot_id=m.lot_id
                    WHERE scl.stock_count_id=@countId AND m.id>@watermark
                    """, new { countId, watermark }, transaction.Transaction) > 0)
                throw new DomainException(ErrorCode.StockCountReviewChanged);
            List<CountPostRow> lines = transaction.Connection.Query<CountPostRow>("""
                SELECT scl.product_id AS ProductId,scl.lot_id AS LotId,scl.expected_qty AS Expected,
                       scl.counted_qty AS Counted,scl.unit_cost AS UnitCost,
                       COALESCE((SELECT SUM(m.qty) FROM stock_movement m
                                 WHERE m.lot_id=scl.lot_id AND m.id>@snapshot),0) AS Movement
                FROM stock_count_line scl WHERE scl.stock_count_id=@countId
                """, new { countId, snapshot = header.Snapshot }, transaction.Transaction).ToList();
            if (lines.Count == 0 || lines.Any(static line => line.Counted is null))
                throw new DomainException(ErrorCode.Validation, "Every stock-count line must be counted.");
            if (reviewedMovements is not null && (reviewedMovements.Count != lines.Count ||
                lines.Any(line => !reviewedMovements.TryGetValue(line.LotId, out long movement) ||
                                  movement != line.Movement)))
                throw new DomainException(ErrorCode.StockCountReviewChanged);
            foreach (CountPostRow line in lines)
            {
                long difference = line.Counted!.Value - (line.Expected + line.Movement);
                if (difference == 0) continue;
                ledger.Post(transaction, new(header.Date, line.ProductId, line.LotId, "ADJUSTMENT",
                    Quantity.FromScaled(difference), Money.FromScaled(line.UnitCost), "stock_count", countId,
                    $"Stock count {Format(header.Date)}"));
                products.Add(line.ProductId);
            }
            transaction.Connection.Execute("""
                UPDATE stock_count SET status='POSTED',posted_at=datetime('now','localtime') WHERE id=@countId
                """, new { countId }, transaction.Transaction);
            audit.Write(transaction, "stock_count", countId, "POST", new { adjustedProducts = products.Count });
            OperationGuard.Complete(transaction, operationId, "stock_count", countId);
        }, cancellationToken);
        if (products.Count > 0) WeakReferenceMessenger.Default.Send(new StockChangedMessage(products.ToArray()));
    }

    public Task CancelStockCountAsync(long countId, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute(
                "UPDATE stock_count SET status='CANCELLED' WHERE id=@countId AND status='DRAFT'",
                new { countId }, transaction.Transaction);
            if (changed == 0) throw new DomainException(ErrorCode.Validation, "Stock count is not an active draft.");
            audit.Write(transaction, "stock_count", countId, "CANCEL", null);
        }, cancellationToken);

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
    private sealed class LotRow { public long Id { get; init; } public long ProductId { get; init; } public string ProductName { get; init; } = ""; public string LotCode { get; init; } = ""; public string? BatchNumber { get; init; } public DateOnly ReceivedDate { get; init; } public DateOnly? ExpiryDate { get; init; } public long UnitCost { get; init; } public long? Mrp { get; init; } public long SellingPrice { get; init; } public long Received { get; init; } public long OnHand { get; init; } public bool IsActive { get; init; } public string ExpiryBucketCode { get; init; } = "NONE"; public LotItem ToModel() => new(Id, ProductId, ProductName, LotCode, BatchNumber, ReceivedDate, ExpiryDate, Money.FromScaled(UnitCost), Mrp is null ? null : Money.FromScaled(Mrp.Value), Money.FromScaled(SellingPrice), Quantity.FromScaled(Received), Quantity.FromScaled(OnHand), IsActive, ExpiryBucketCode); }
    private sealed class LedgerRow { public long Id { get; init; } public DateTime MovedAt { get; init; } public DateOnly MoveDate { get; init; } public string TypeCode { get; init; } = ""; public long Qty { get; init; } public long UnitCost { get; init; } public string? RefType { get; init; } public long? RefId { get; init; } public string LotCode { get; init; } = ""; public long LotBalance { get; init; } public string? Note { get; init; } public LedgerItem ToModel() => new(Id, MovedAt, MoveDate, TypeCode, Quantity.FromScaled(Qty), Money.FromScaled(UnitCost), RefType, RefId, LotCode, Quantity.FromScaled(LotBalance), Note); }
    private sealed class SourceHeaderRow { public string? Number { get; init; } public DateOnly Date { get; init; } public string? Party { get; init; } public string? Status { get; init; } public string? Notes { get; init; } }
    private sealed class SourceLineRow { public string ProductName { get; init; } = ""; public string LotCode { get; init; } = ""; public long Quantity { get; init; } public long Amount { get; init; } }
    private sealed class ProductRule { public bool TrackExpiry { get; init; } public bool AllowFraction { get; init; } }
    private sealed class LotIdentity { public long ProductId { get; init; } public long UnitCost { get; init; } }
    private sealed class CountHeader { public long Id { get; init; } public DateOnly Date { get; init; } public string Status { get; init; } = ""; public string? Notes { get; init; } public long Snapshot { get; init; } }
    private sealed class CountProgress { public long Total { get; init; } public long Counted { get; init; } }
    private sealed class CountReviewRow { public long Total { get; init; } public long Counted { get; init; } public long Differences { get; init; } public long NetValue { get; init; } public long MovementHighWaterMark { get; init; } }
    private sealed class CountPostRow { public long ProductId { get; init; } public long LotId { get; init; } public long Expected { get; init; } public long? Counted { get; init; } public long UnitCost { get; init; } public long Movement { get; init; } }
    private sealed class CountUnitRule { public long LotId { get; init; } public bool AllowFraction { get; init; } }
    private sealed class CountLineRow { public long Id { get; init; } public long ProductId { get; init; } public string ProductName { get; init; } = ""; public long LotId { get; init; } public string LotCode { get; init; } = ""; public long Expected { get; init; } public long? Counted { get; init; } public long UnitCost { get; init; } public long Movement { get; init; } public StockCountLine ToModel() { long difference = Counted is null ? 0 : Counted.Value - (Expected + Movement); return new(Id, ProductId, ProductName, LotId, LotCode, Quantity.FromScaled(Expected), Counted is null ? null : Quantity.FromScaled(Counted.Value), Money.FromScaled(UnitCost), Quantity.FromScaled(Movement), Quantity.FromScaled(difference), new Money(Fx3.FromScaled(difference) * Fx3.FromScaled(UnitCost))); } }
    private sealed class ValuationRow { public long CategoryId { get; init; } public string CategoryName { get; init; } = ""; public long Quantity { get; init; } public long CostValue { get; init; } public long SellingValue { get; init; } public long MrpValue { get; init; } public StockValuationItem ToModel() => new(CategoryId, CategoryName, GroceryErp.Core.Numerics.Quantity.FromScaled(Quantity), Money.FromScaled(CostValue), Money.FromScaled(SellingValue), Money.FromScaled(MrpValue)); }
}
