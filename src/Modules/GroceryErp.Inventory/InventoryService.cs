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
                   l.qty_received AS Received,l.qty_on_hand AS OnHand,l.is_active AS IsActive
            FROM product_lot l JOIN product p ON p.id=l.product_id
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
              SELECT m.id,m.moved_at,m.move_date,m.type_code,m.qty,m.unit_cost,m.ref_type,m.ref_id,m.note,
                     l.lot_code,SUM(m.qty) OVER (PARTITION BY m.lot_id ORDER BY m.id) AS lot_balance
              FROM stock_movement m JOIN product_lot l ON l.id=m.lot_id
              WHERE m.product_id=@productId
            )
            SELECT * FROM history WHERE move_date BETWEEN @from AND @to ORDER BY id
            """, new { productId, from, to }).Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

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
        string filter = categoryIds is { Count: > 0 } ? "AND p.category_id IN @categoryIds" : string.Empty;
        int rows = transaction.Connection.Execute($"""
            INSERT INTO stock_count_line(stock_count_id,product_id,lot_id,expected_qty,unit_cost)
            SELECT @id,l.product_id,l.id,l.qty_on_hand,l.unit_cost
            FROM product_lot l JOIN product p ON p.id=l.product_id
            WHERE p.is_active=1 {filter}
            """, new { id, categoryIds }, transaction.Transaction);
        audit.Write(transaction, "stock_count", id, "CREATE", new { rows, snapshot });
        return id;
    }, cancellationToken);

    public Task SetCountedQuantityAsync(long countId, long lotId, Quantity counted, CancellationToken cancellationToken = default)
    {
        if (counted < Quantity.Zero) throw new DomainException(ErrorCode.Validation, "Counted quantity cannot be negative.");
        return database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute("""
                UPDATE stock_count_line SET counted_qty=@counted WHERE stock_count_id=@countId AND lot_id=@lotId
                """, new { countId, lotId, counted = counted.Scaled }, transaction.Transaction);
            if (changed == 0) throw new DomainException(ErrorCode.NotFound, "stock count line", lotId);
        }, cancellationToken);
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

    public async Task PostStockCountAsync(long countId, string operationId, CancellationToken cancellationToken = default)
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
            List<CountPostRow> lines = transaction.Connection.Query<CountPostRow>("""
                SELECT scl.product_id AS ProductId,scl.lot_id AS LotId,scl.expected_qty AS Expected,
                       scl.counted_qty AS Counted,scl.unit_cost AS UnitCost,
                       COALESCE((SELECT SUM(m.qty) FROM stock_movement m
                                 WHERE m.lot_id=scl.lot_id AND m.id>@snapshot),0) AS Movement
                FROM stock_count_line scl WHERE scl.stock_count_id=@countId
                """, new { countId, snapshot = header.Snapshot }, transaction.Transaction).ToList();
            if (lines.Count == 0 || lines.Any(static line => line.Counted is null))
                throw new DomainException(ErrorCode.Validation, "Every stock-count line must be counted.");
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
    private sealed class LotRow { public long Id { get; init; } public long ProductId { get; init; } public string ProductName { get; init; } = ""; public string LotCode { get; init; } = ""; public string? BatchNumber { get; init; } public DateOnly ReceivedDate { get; init; } public DateOnly? ExpiryDate { get; init; } public long UnitCost { get; init; } public long? Mrp { get; init; } public long SellingPrice { get; init; } public long Received { get; init; } public long OnHand { get; init; } public bool IsActive { get; init; } public LotItem ToModel() => new(Id, ProductId, ProductName, LotCode, BatchNumber, ReceivedDate, ExpiryDate, Money.FromScaled(UnitCost), Mrp is null ? null : Money.FromScaled(Mrp.Value), Money.FromScaled(SellingPrice), Quantity.FromScaled(Received), Quantity.FromScaled(OnHand), IsActive); }
    private sealed class LedgerRow { public long Id { get; init; } public DateTime MovedAt { get; init; } public DateOnly MoveDate { get; init; } public string TypeCode { get; init; } = ""; public long Qty { get; init; } public long UnitCost { get; init; } public string? RefType { get; init; } public long? RefId { get; init; } public string LotCode { get; init; } = ""; public long LotBalance { get; init; } public string? Note { get; init; } public LedgerItem ToModel() => new(Id, MovedAt, MoveDate, TypeCode, Quantity.FromScaled(Qty), Money.FromScaled(UnitCost), RefType, RefId, LotCode, Quantity.FromScaled(LotBalance), Note); }
    private sealed class ProductRule { public bool TrackExpiry { get; init; } public bool AllowFraction { get; init; } }
    private sealed class LotIdentity { public long ProductId { get; init; } public long UnitCost { get; init; } }
    private sealed class CountHeader { public long Id { get; init; } public DateOnly Date { get; init; } public string Status { get; init; } = ""; public string? Notes { get; init; } public long Snapshot { get; init; } }
    private sealed class CountPostRow { public long ProductId { get; init; } public long LotId { get; init; } public long Expected { get; init; } public long? Counted { get; init; } public long UnitCost { get; init; } public long Movement { get; init; } }
    private sealed class CountLineRow { public long Id { get; init; } public long ProductId { get; init; } public string ProductName { get; init; } = ""; public long LotId { get; init; } public string LotCode { get; init; } = ""; public long Expected { get; init; } public long? Counted { get; init; } public long UnitCost { get; init; } public long Movement { get; init; } public StockCountLine ToModel() { long difference = Counted is null ? 0 : Counted.Value - (Expected + Movement); return new(Id, ProductId, ProductName, LotId, LotCode, Quantity.FromScaled(Expected), Counted is null ? null : Quantity.FromScaled(Counted.Value), Money.FromScaled(UnitCost), Quantity.FromScaled(Movement), Quantity.FromScaled(difference), new Money(Fx3.FromScaled(difference) * Fx3.FromScaled(UnitCost))); } }
    private sealed class ValuationRow { public long CategoryId { get; init; } public string CategoryName { get; init; } = ""; public long Quantity { get; init; } public long CostValue { get; init; } public long SellingValue { get; init; } public long MrpValue { get; init; } public StockValuationItem ToModel() => new(CategoryId, CategoryName, GroceryErp.Core.Numerics.Quantity.FromScaled(Quantity), Money.FromScaled(CostValue), Money.FromScaled(SellingValue), Money.FromScaled(MrpValue)); }
}
