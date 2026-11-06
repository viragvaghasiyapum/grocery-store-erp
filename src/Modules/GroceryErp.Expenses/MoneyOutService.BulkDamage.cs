using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Expenses;

public sealed partial class MoneyOutService
{
    public async Task<IReadOnlyList<long>> RecordBulkDamageAsync(IReadOnlyCollection<BulkDamageInput> lines,
        DateOnly date, string reason, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count is < 1 or > 50 || lines.Any(static line => line.LotId <= 0 ||
            line.Quantity <= Quantity.Zero || line.ExpectedDetailVersion <= 0) ||
            lines.Select(static line => line.LotId).Distinct().Count() != lines.Count ||
            reason is not ("EXPIRED" or "BROKEN" or "SPOILED" or "PEST" or "LOST" or "OTHER"))
            throw new DomainException(ErrorCode.Validation, "Select 1 to 50 valid lot quantities.");
        return await database.WriteAsync(tx =>
        {
            OperationRef? replay = OperationGuard.Claim(tx, operationId, OpType.DamageSelection);
            if (replay is not null)
                return (IReadOnlyList<long>)tx.Connection.Query<long>(
                    "SELECT id FROM damage WHERE bulk_operation_id=@operationId ORDER BY id",
                    new { operationId }, tx.Transaction).ToList();
            periods.EnsureOpen(tx, date);
            var ids = new List<long>(lines.Count);
            foreach (BulkDamageInput line in lines.OrderBy(static line => line.LotId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                BulkDamageLot? lot = tx.Connection.QuerySingleOrDefault<BulkDamageLot>("""
                    SELECT product_id AS ProductId,qty_on_hand AS OnHand,unit_cost AS UnitCost,
                           detail_version AS DetailVersion,expiry_date AS ExpiryDate
                    FROM product_lot WHERE id=@id
                    """, new { id = line.LotId }, tx.Transaction);
                if (lot is null || lot.OnHand != line.ExpectedOnHandScaled ||
                    lot.DetailVersion != line.ExpectedDetailVersion || line.Quantity.Scaled > lot.OnHand)
                    throw new DomainException(ErrorCode.EntryChanged, line.LotId);
                if (reason == "EXPIRED" && (lot.ExpiryDate is null || lot.ExpiryDate >= date))
                    throw new DomainException(ErrorCode.ExpiredReasonNeedsExpiredLot, line.LotId);
                Money unitCost = Money.FromScaled(lot.UnitCost);
                Money cost = unitCost * line.Quantity;
                string document = numbers.Next(tx, DocType.Damage, date);
                long id = tx.Connection.ExecuteScalar<long>("""
                    INSERT INTO damage(doc_no,damage_date,product_id,lot_id,qty,unit_cost,cost_value,
                        recovered_amount,reason,notes,bulk_operation_id,product_name_snapshot,
                        product_sku_snapshot,product_name_local_snapshot,uom_snapshot,
                        product_class_snapshot,lot_code_snapshot,batch_no_snapshot,expiry_date_snapshot,
                        snapshot_provenance)
                    SELECT @document,@date,p.id,l.id,@qty,@unitCost,@cost,0,@reason,
                        'Reviewed bulk damage',@operationId,p.name,p.sku,p.name_local,u.code,
                        p.product_class,l.lot_code,l.batch_no,l.expiry_date,'AT_POST'
                    FROM product_lot l JOIN product p ON p.id=l.product_id JOIN uom u ON u.id=p.uom_id
                    WHERE l.id=@lotId RETURNING id
                    """, new
                {
                    document,
                    date = Format(date),
                    qty = line.Quantity.Scaled,
                    unitCost = unitCost.Scaled,
                    cost = cost.Scaled,
                    reason,
                    operationId,
                    lotId = line.LotId
                }, tx.Transaction);
                stock.Change(tx, new(date, lot.ProductId, line.LotId, LotChangeKind.Damage,
                    new Quantity(-line.Quantity.Value)));
                audit.Write(tx, "damage", id, "CREATE", new
                {
                    document,
                    operationId,
                    Quantity = line.Quantity.Scaled,
                    Cost = cost.Scaled
                });
                ids.Add(id);
            }
            OperationGuard.Complete(tx, operationId, "damage", ids[0]);
            return (IReadOnlyList<long>)ids;
        }, cancellationToken);
    }

    /// <summary>Most expired lots one reviewed write-off may resolve; larger sets must be narrowed.</summary>
    public const int MaxExpiredWriteOffLots = 5000;

    /// <summary>
    /// Resolves the expired lots with stock of the selected products as of <paramref name="date"/>,
    /// with quantity and cost, so a reviewed write-off shows exactly what becomes damage.
    /// </summary>
    public Task<IReadOnlyList<ExpiredWriteOffLot>> PreviewExpiredWriteOffAsync(IReadOnlyCollection<long> productIds,
        DateOnly date, CancellationToken cancellationToken = default)
    {
        long[] ids = BulkBatch.ValidateIds(productIds, 5000);
        return database.ReadAsync(connection =>
        {
            var lots = new List<ExpiredWriteOffLot>();
            void Add(IEnumerable<ExpiredLotRow> rows)
            {
                lots.AddRange(rows.Select(static row => row.ToModel()));
                if (lots.Count > MaxExpiredWriteOffLots)
                    throw new DomainException(ErrorCode.TooManyRows, MaxExpiredWriteOffLots);
            }
            // Sellable lots are found per product through the FEFO index; left to itself SQLite
            // walked every expired lot in the store for each chunk (1.5 s per 500 products).
            foreach (long[] chunk in ids.Chunk(500))
                Add(connection.Query<ExpiredLotRow>("""
                    SELECT l.id AS LotId,p.name AS ProductName,l.lot_code AS LotCode,l.expiry_date AS ExpiryDate,
                           l.qty_on_hand AS OnHand,l.unit_cost AS UnitCost
                    FROM product_lot l INDEXED BY ix_lot_fefo JOIN product p ON p.id=l.product_id
                    WHERE l.product_id IN @chunk AND l.qty_on_hand>0 AND l.is_active=1
                      AND (l.expiry_date IS NULL)=0 AND l.expiry_date<@date
                    """, new { chunk, date = Format(date) }));
            // Blocked lots are few, so one read of all blocked expired stock covers the selection.
            var selected = ids.ToHashSet();
            Add(connection.Query<ExpiredLotRow>("""
                SELECT l.id AS LotId,l.product_id AS ProductId,p.name AS ProductName,l.lot_code AS LotCode,
                       l.expiry_date AS ExpiryDate,l.qty_on_hand AS OnHand,l.unit_cost AS UnitCost
                FROM product_lot l JOIN product p ON p.id=l.product_id
                WHERE l.is_active=0 AND l.qty_on_hand>0 AND l.expiry_date<@date
                """, new { date = Format(date) }).Where(row => selected.Contains(row.ProductId)));
            return (IReadOnlyList<ExpiredWriteOffLot>)lots.OrderBy(static lot => lot.ProductName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static lot => lot.LotId).ToList();
        }, cancellationToken);
    }

    /// <summary>
    /// Writes off one batch of expired lots in full as EXPIRED damage, in one transaction. A lot that
    /// lost its stock or is no longer expired is reported, not written off; a replayed batch changes nothing.
    /// </summary>
    public async Task<IReadOnlyList<BulkItemResult>> WriteOffExpiredBatchAsync(IReadOnlyCollection<long> lotIds,
        DateOnly date, string batchOperationId, CancellationToken cancellationToken = default)
    {
        long[] ids = BulkBatch.ValidateIds(lotIds);
        return await database.WriteAsync(tx =>
        {
            if (BulkBatch.Claim(tx, batchOperationId, ids) is { } replay) return replay;
            periods.EnsureOpen(tx, date);
            Dictionary<long, BulkLotRow> lots = tx.Connection.Query<BulkLotRow>("""
                SELECT id AS LotId,product_id AS ProductId,qty_on_hand AS OnHand,unit_cost AS UnitCost,expiry_date AS ExpiryDate
                FROM product_lot WHERE id IN @ids
                """, new { ids }, tx.Transaction).ToDictionary(static lot => lot.LotId);
            var outcome = new List<BulkItemResult>(ids.Length);
            foreach (long lotId in ids.Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lots.TryGetValue(lotId, out BulkLotRow? lot))
                {
                    outcome.Add(new BulkItemResult(lotId, BulkItemOutcome.Missing, "Bulk_Missing"));
                    continue;
                }
                if (lot.OnHand <= 0 || lot.ExpiryDate is null || lot.ExpiryDate >= date)
                {
                    outcome.Add(new BulkItemResult(lotId, BulkItemOutcome.NotEligible, "Bulk_Reason_NotExpired"));
                    continue;
                }
                Quantity quantity = Quantity.FromScaled(lot.OnHand);
                Money unitCost = Money.FromScaled(lot.UnitCost);
                Money cost = unitCost * quantity;
                string document = numbers.Next(tx, DocType.Damage, date);
                long id = tx.Connection.ExecuteScalar<long>("""
                    INSERT INTO damage(doc_no,damage_date,product_id,lot_id,qty,unit_cost,cost_value,recovered_amount,
                                       reason,notes,bulk_operation_id,product_name_snapshot,product_sku_snapshot,
                                       product_name_local_snapshot,uom_snapshot,product_class_snapshot,lot_code_snapshot,
                                       batch_no_snapshot,expiry_date_snapshot,snapshot_provenance)
                    SELECT @document,@date,p.id,l.id,@qty,@unitCost,@cost,0,'EXPIRED','Bulk expiry write-off',@operationId,
                           p.name,p.sku,p.name_local,u.code,p.product_class,l.lot_code,l.batch_no,
                           l.expiry_date,'AT_POST'
                    FROM product_lot l JOIN product p ON p.id=l.product_id JOIN uom u ON u.id=p.uom_id
                    WHERE l.id=@lotId
                    RETURNING id
                    """, new
                {
                    document,
                    date = Format(date),
                    qty = quantity.Scaled,
                    unitCost = unitCost.Scaled,
                    cost = cost.Scaled,
                    operationId = batchOperationId,
                    lotId
                }, tx.Transaction);
                stock.Change(tx, new(date, lot.ProductId, lotId, LotChangeKind.Damage, new Quantity(-quantity.Value)));
                audit.Write(tx, "damage", id, "CREATE", new { document, bulk = batchOperationId, quantity = quantity.Scaled });
                outcome.Add(new BulkItemResult(lotId, BulkItemOutcome.Changed));
            }
            return BulkBatch.Complete(tx, batchOperationId, outcome);
        }, cancellationToken);
    }

    private sealed class ExpiredLotRow
    {
        public long LotId { get; init; }
        public long ProductId { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string LotCode { get; init; } = string.Empty;
        public DateOnly ExpiryDate { get; init; }
        public long OnHand { get; init; }
        public long UnitCost { get; init; }

        public ExpiredWriteOffLot ToModel() => new(LotId, ProductName, LotCode, ExpiryDate,
            Quantity.FromScaled(OnHand), Money.FromScaled(UnitCost) * Quantity.FromScaled(OnHand));
    }

    private sealed class BulkDamageLot
    {
        public long ProductId { get; init; }
        public long OnHand { get; init; }
        public long UnitCost { get; init; }
        public long DetailVersion { get; init; }
        public DateOnly? ExpiryDate { get; init; }
    }
}
