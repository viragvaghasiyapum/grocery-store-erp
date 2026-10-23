using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Messages;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;

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
        var products = new HashSet<long>();
        IReadOnlyList<long> result = await database.WriteAsync(tx =>
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
                    throw new DomainException(ErrorCode.Validation,
                        "Selected lot stock or details changed. Refresh the page and review again.");
                if (reason == "EXPIRED" && (lot.ExpiryDate is null || lot.ExpiryDate >= date))
                    throw new DomainException(ErrorCode.Validation, "Expired reason requires an expired lot.");
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
                stock.Post(tx, new(date, lot.ProductId, line.LotId, "DAMAGE",
                    new Quantity(-line.Quantity.Value), unitCost, "damage", id, reason));
                audit.Write(tx, "damage", id, "CREATE", new
                {
                    document,
                    operationId,
                    Quantity = line.Quantity.Scaled,
                    Cost = cost.Scaled
                });
                ids.Add(id);
                products.Add(lot.ProductId);
            }
            OperationGuard.Complete(tx, operationId, "damage", ids[0]);
            return (IReadOnlyList<long>)ids;
        }, cancellationToken);
        if (products.Count > 0) WeakReferenceMessenger.Default.Send(new StockChangedMessage(products.ToArray()));
        return result;
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
