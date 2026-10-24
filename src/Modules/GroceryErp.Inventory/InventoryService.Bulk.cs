using Dapper;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;

namespace GroceryErp.Inventory;

public sealed partial class InventoryService
{
    /// <summary>Resolves each lot's eligibility for a bulk lot action without changing anything.</summary>
    public Task<IReadOnlyList<LotBulkRow>> PreviewLotBulkAsync(IReadOnlyCollection<long> lotIds, LotBulkAction action,
        CancellationToken cancellationToken = default)
    {
        long[] ids = BulkBatch.ValidateIds(lotIds, MaxBulkLots);
        return database.ReadAsync(connection =>
        {
            Dictionary<long, BulkLotRow> rows = [];
            foreach (long[] chunk in ids.Chunk(500))
                foreach (BulkLotRow row in connection.Query<BulkLotRow>(BulkLotRowsSql, new { ids = chunk }))
                    rows[row.Id] = row;
            return (IReadOnlyList<LotBulkRow>)ids.Select(id => rows.TryGetValue(id, out BulkLotRow? row)
                ? new LotBulkRow(id, row.Label, SkipReason(row, action) is null, SkipReason(row, action),
                    Quantity.FromScaled(row.OnHand), Money.FromScaled(row.CostValue))
                : new LotBulkRow(id, string.Empty, false, "Bulk_Missing", Quantity.Zero, Money.Zero)).ToList();
        }, cancellationToken);
    }

    /// <summary>
    /// Applies one batch of a bulk lot action in one transaction. Each lot is rechecked inside the
    /// transaction (a lot that gained stock or joined an open bill is not deleted), and a batch
    /// replayed under the same operation ID changes nothing.
    /// </summary>
    public Task<IReadOnlyList<BulkItemResult>> RunLotBulkBatchAsync(LotBulkAction action,
        IReadOnlyCollection<long> lotIds, string batchOperationId, CancellationToken cancellationToken = default)
    {
        long[] ids = BulkBatch.ValidateIds(lotIds);
        return database.WriteAsync(tx =>
        {
            if (BulkBatch.Claim(tx, batchOperationId, ids) is { } replay) return replay;
            Dictionary<long, BulkLotRow> rows = tx.Connection.Query<BulkLotRow>(BulkLotRowsSql, new { ids }, tx.Transaction)
                .ToDictionary(static row => row.Id);
            var results = new List<BulkItemResult>(ids.Length);
            foreach (long id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!rows.TryGetValue(id, out BulkLotRow? row))
                {
                    results.Add(new BulkItemResult(id, BulkItemOutcome.Missing, "Bulk_Missing"));
                    continue;
                }
                if (SkipReason(row, action) is { } reason ||
                    (action == LotBulkAction.DeleteZero && IsOnOpenBill(tx.Connection, id, tx.Transaction)))
                {
                    results.Add(new BulkItemResult(id, BulkItemOutcome.NotEligible,
                        SkipReason(row, action) ?? "Stock_DeleteOpenBill"));
                    continue;
                }
                int changed = action switch
                {
                    LotBulkAction.Block or LotBulkAction.Unblock => tx.Connection.Execute(
                        "UPDATE product_lot SET is_active=@active WHERE id=@id AND is_active<>@active",
                        new { id, active = action == LotBulkAction.Unblock ? 1 : 0 }, tx.Transaction),
                    _ => tx.Connection.Execute("DELETE FROM product_lot WHERE id=@id AND qty_on_hand=0",
                        new { id }, tx.Transaction),
                };
                if (changed != 1)
                {
                    results.Add(new BulkItemResult(id, BulkItemOutcome.NotEligible, "Bulk_ChangedSincePreview"));
                    continue;
                }
                audit.Write(tx, "product_lot", id, action switch
                {
                    LotBulkAction.Block => "BLOCK",
                    LotBulkAction.Unblock => "UNBLOCK",
                    _ => "DELETE_ZERO",
                }, new { productId = row.ProductId, bulk = batchOperationId });
                results.Add(new BulkItemResult(id, BulkItemOutcome.Changed));
            }
            return BulkBatch.Complete(tx, batchOperationId, results);
        }, cancellationToken);
    }

    private static string? SkipReason(BulkLotRow row, LotBulkAction action) => action switch
    {
        LotBulkAction.Block => row.IsActive ? null : "Bulk_Reason_AlreadyBlocked",
        LotBulkAction.Unblock => row.IsActive ? "Bulk_Reason_AlreadyActive" : null,
        _ => row.OnHand != 0 ? "Stock_DeleteHasStock" : null,
    };

    private const string BulkLotRowsSql = """
        SELECT l.id AS Id,l.product_id AS ProductId,p.name || ' · ' || l.lot_code AS Label,
               l.is_active AS IsActive,l.qty_on_hand AS OnHand,
               CAST(ROUND(l.qty_on_hand * l.unit_cost / 1000.0) AS INTEGER) AS CostValue
        FROM product_lot l JOIN product p ON p.id=l.product_id
        WHERE l.id IN @ids
        """;

    private sealed class BulkLotRow
    {
        public long Id { get; init; }
        public long ProductId { get; init; }
        public string Label { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public long OnHand { get; init; }
        public long CostValue { get; init; }
    }
}
