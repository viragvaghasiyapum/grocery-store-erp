using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;

namespace GroceryErp.Inventory;

public sealed record DirectCountLot(long LotId, long ProductId, string ProductName,
    string LotCode, long OnHand, long Version, long UnitCost, long AllowFraction);

public sealed record DirectCountLotPage(IReadOnlyList<DirectCountLot> Items, bool HasMore,
    long? NextLotId, long MaxLotId);

// This service is opt-in for the isolated direct-stock schema. The production app
// continues to use its ledger-backed stock count until the document cutover.
public sealed class DirectStockCountService(DbExecutor database, IDirectStockEngine engine, PeriodGuard periods)
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => connection.ExecuteScalar<long>("PRAGMA user_version") is 31 or 32 &&
            connection.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM sqlite_master
                WHERE type='table' AND name='direct_stock_sandbox'
                """) == 1, cancellationToken);

    public Task<DirectCountLotPage> GetPageAsync(IReadOnlyCollection<long> productIds,
        long? afterLotId = null, long? maxLotId = null, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        if (productIds.Count is < 1 or > 100 || productIds.Any(static id => id <= 0) ||
            afterLotId < 0 || maxLotId < 0 || limit is < 1 or > 100)
            throw new DomainException(ErrorCode.Validation, "Select up to 100 products and a page of up to 100 lots.");
        long[] ids = productIds.Distinct().ToArray();
        return database.ReadAsync(connection =>
        {
            if (connection.ExecuteScalar<long>("PRAGMA user_version") is not (31 or 32))
                throw new InvalidOperationException("Direct stock is inactive on this database.");
            long ceiling = maxLotId ?? connection.ExecuteScalar<long>("SELECT COALESCE(MAX(id),0) FROM product_lot");
            List<DirectCountLot> rows = connection.Query<DirectCountLot>("""
                SELECT l.id AS LotId,l.product_id AS ProductId,p.name AS ProductName,
                       l.lot_code AS LotCode,l.qty_on_hand AS OnHand,
                       l.quantity_version AS Version,l.unit_cost AS UnitCost,
                       u.allow_fraction AS AllowFraction
                FROM product_lot l INDEXED BY ix_product_lot_product
                JOIN product p ON p.id=l.product_id
                JOIN uom u ON u.id=p.uom_id
                WHERE l.product_id IN @ids AND l.id<=@ceiling
                  AND (@afterLotId IS NULL OR l.id>@afterLotId)
                ORDER BY l.id LIMIT @take
                """, new { ids, afterLotId, ceiling, take = limit + 1 }).ToList();
            bool more = rows.Count > limit;
            if (more) rows.RemoveAt(limit);
            return new DirectCountLotPage(rows, more, rows.LastOrDefault()?.LotId, ceiling);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<long>> PostReviewedAsync(IReadOnlyList<DirectCountReview> reviews,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        if (reviews.Count is < 1 or > 100 || reviews.Select(static row => row.LotId).Distinct().Count() != reviews.Count ||
            reviews.Select(static row => row.BusinessDate).Distinct().Count() != 1)
            throw new DomainException(ErrorCode.Validation, "Review one count page at a time.");
        return database.WriteAsync(tx =>
        {
            periods.EnsureOpen(reviews[0].BusinessDate);
            return engine.ReviewCountBatch(tx, reviews);
        }, cancellationToken);
    }
}
