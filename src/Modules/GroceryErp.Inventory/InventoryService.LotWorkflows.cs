using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Inventory;

public sealed partial class InventoryService
{
    public Task EditLotDetailsAsync(LotDetailsEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.LotId <= 0 || edit.ExpectedVersion <= 0 || edit.SellingPrice < Money.Zero ||
            edit.Mrp < Money.Zero || edit.Mrp is { } mrp && edit.SellingPrice > mrp)
            throw new DomainException(ErrorCode.Validation, "Invalid lot price or version.");
        return database.WriteAsync(tx =>
        {
            LotEditRule? rule = tx.Connection.QuerySingleOrDefault<LotEditRule>("""
                SELECT l.received_date AS ReceivedDate,p.track_expiry AS TrackExpiry,l.product_id AS ProductId
                FROM product_lot l JOIN product p ON p.id=l.product_id WHERE l.id=@id
                """, new { id = edit.LotId }, tx.Transaction);
            if (rule is null) throw new DomainException(ErrorCode.NotFound, "lot", edit.LotId);
            if (edit.ExpiryDate < rule.ReceivedDate || rule.TrackExpiry && edit.ExpiryDate is null)
                throw new DomainException(ErrorCode.Validation, "Invalid lot expiry.");
            int changed = tx.Connection.Execute("""
                UPDATE product_lot SET selling_price=@price,mrp=@mrp,expiry_date=@expiry,
                    detail_version=detail_version+1,
                    updated_at=strftime('%Y-%m-%d %H:%M:%f','now','localtime')
                WHERE id=@id AND detail_version=@version
                """, new { id = edit.LotId, version = edit.ExpectedVersion,
                    price = edit.SellingPrice.Scaled, mrp = edit.Mrp?.Scaled,
                    expiry = edit.ExpiryDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) },
                tx.Transaction);
            if (changed != 1) ThrowLotEditConflict(tx, edit.LotId);
            audit.Write(tx, "product_lot", edit.LotId, "DETAIL_EDIT", new
            { edit.ExpectedVersion, Price = edit.SellingPrice.Scaled, Mrp = edit.Mrp?.Scaled, edit.ExpiryDate });
        }, cancellationToken);
    }

    public Task<LotDeletionPreview> PreviewDeleteZeroLotAsync(long lotId,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        LotDeleteRow? lot = connection.QuerySingleOrDefault<LotDeleteRow>("""
            SELECT lot_code AS LotCode,qty_on_hand AS Quantity FROM product_lot WHERE id=@lotId
            """, new { lotId });
        if (lot is null) throw new DomainException(ErrorCode.NotFound, "lot", lotId);
        string? reason = lot.Quantity != 0 ? "Stock_DeleteHasStock" :
            connection.ExecuteScalar<long>("""
                SELECT EXISTS(SELECT 1 FROM stock_movement WHERE lot_id=@lotId)
                    OR EXISTS(SELECT 1 FROM vendor_return_line WHERE lot_id=@lotId)
                    OR EXISTS(SELECT 1 FROM stock_count_line WHERE lot_id=@lotId)
                    OR EXISTS(SELECT 1 FROM sale_line WHERE lot_id=@lotId)
                    OR EXISTS(SELECT 1 FROM damage WHERE lot_id=@lotId)
                    OR EXISTS(SELECT 1 FROM direct_stock_origin WHERE lot_id=@lotId)
                """, new { lotId }) != 0 ? "Stock_DeleteHistory" : null;
        return new LotDeletionPreview(lotId, lot.LotCode, Quantity.FromScaled(lot.Quantity), reason is null, reason);
    }, cancellationToken);

    public Task DeleteZeroLotAsync(long lotId, long expectedVersion,
        CancellationToken cancellationToken = default) => database.WriteAsync(tx =>
    {
        if (lotId <= 0 || expectedVersion <= 0) throw new DomainException(ErrorCode.Validation, "Invalid lot.");
        long? productId = tx.Connection.QuerySingleOrDefault<long?>("""
            SELECT product_id FROM product_lot WHERE id=@lotId AND qty_on_hand=0 AND detail_version=@expectedVersion
            """, new { lotId, expectedVersion }, tx.Transaction);
        if (productId is null) ThrowLotEditConflict(tx, lotId);
        if (tx.Connection.ExecuteScalar<long>("""
            SELECT EXISTS(SELECT 1 FROM stock_movement WHERE lot_id=@lotId)
                OR EXISTS(SELECT 1 FROM vendor_return_line WHERE lot_id=@lotId)
                OR EXISTS(SELECT 1 FROM stock_count_line WHERE lot_id=@lotId)
                OR EXISTS(SELECT 1 FROM sale_line WHERE lot_id=@lotId)
                OR EXISTS(SELECT 1 FROM damage WHERE lot_id=@lotId)
                OR EXISTS(SELECT 1 FROM direct_stock_origin WHERE lot_id=@lotId)
            """, new { lotId }, tx.Transaction) != 0)
            throw new DomainException(ErrorCode.Validation, "Historical documents still reference this lot.");
        int changed = tx.Connection.Execute("""
            DELETE FROM product_lot WHERE id=@lotId AND qty_on_hand=0 AND detail_version=@expectedVersion
            """, new { lotId, expectedVersion }, tx.Transaction);
        if (changed != 1) ThrowLotEditConflict(tx, lotId);
        audit.Write(tx, "product_lot", lotId, "DELETE_ZERO", new { productId });
    }, cancellationToken);

    public Task<IReadOnlyList<LotDeletionPreview>> PreviewDeleteZeroLotsAsync(
        IReadOnlyCollection<long> lotIds, CancellationToken cancellationToken = default)
    {
        long[] ids = ValidateBulkLotIds(lotIds);
        return database.ReadAsync<IReadOnlyList<LotDeletionPreview>>(connection =>
        {
            var previews = new List<LotDeletionPreview>(ids.Length);
            foreach (long lotId in ids)
            {
                LotDeleteRow? lot = connection.QuerySingleOrDefault<LotDeleteRow>("""
                    SELECT lot_code AS LotCode,qty_on_hand AS Quantity FROM product_lot WHERE id=@lotId
                    """, new { lotId });
                if (lot is null) throw new DomainException(ErrorCode.NotFound, "lot", lotId);
                bool referenced = lot.Quantity == 0 && HasLotHistory(connection, lotId);
                string? reason = lot.Quantity != 0 ? "Stock_DeleteHasStock" :
                    referenced ? "Stock_DeleteHistory" : null;
                previews.Add(new(lotId, lot.LotCode, Quantity.FromScaled(lot.Quantity),
                    reason is null, reason));
            }
            return previews;
        }, cancellationToken);
    }

    public Task DeleteZeroLotsAsync(IReadOnlyCollection<LotDeletionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        long[] ids = ValidateBulkLotIds(requests.Select(static request => request.LotId).ToArray());
        if (ids.Length != requests.Count || requests.Any(static request => request.ExpectedVersion <= 0))
            throw new DomainException(ErrorCode.Validation, "Select distinct zero lots with current versions.");
        return database.WriteAsync(tx =>
        {
            foreach (LotDeletionRequest request in requests)
            {
                var key = new { lotId = request.LotId, expectedVersion = request.ExpectedVersion };
                long? productId = tx.Connection.QuerySingleOrDefault<long?>("""
                    SELECT product_id FROM product_lot
                    WHERE id=@lotId AND qty_on_hand=0 AND detail_version=@expectedVersion
                    """, key, tx.Transaction);
                if (productId is null) ThrowLotEditConflict(tx, request.LotId);
                if (HasLotHistory(tx.Connection, request.LotId, tx.Transaction))
                    throw new DomainException(ErrorCode.Validation, "Historical documents still reference this lot.");
                int changed = tx.Connection.Execute("""
                    DELETE FROM product_lot
                    WHERE id=@lotId AND qty_on_hand=0 AND detail_version=@expectedVersion
                    """, key, tx.Transaction);
                if (changed != 1) ThrowLotEditConflict(tx, request.LotId);
                audit.Write(tx, "product_lot", request.LotId, "DELETE_ZERO", new { productId });
            }
        }, cancellationToken);
    }

    private static long[] ValidateBulkLotIds(IReadOnlyCollection<long> lotIds)
    {
        ArgumentNullException.ThrowIfNull(lotIds);
        long[] ids = lotIds.ToArray();
        if (ids.Length is < 1 or > 50 || ids.Any(static id => id <= 0) || ids.Distinct().Count() != ids.Length)
            throw new DomainException(ErrorCode.Validation, "Select 1 to 50 distinct lots.");
        return ids;
    }

    private static bool HasLotHistory(System.Data.IDbConnection connection, long lotId,
        System.Data.IDbTransaction? transaction = null) => connection.ExecuteScalar<long>("""
            SELECT EXISTS(SELECT 1 FROM stock_movement WHERE lot_id=@lotId)
                OR EXISTS(SELECT 1 FROM vendor_return_line WHERE lot_id=@lotId)
                OR EXISTS(SELECT 1 FROM stock_count_line WHERE lot_id=@lotId)
                OR EXISTS(SELECT 1 FROM sale_line WHERE lot_id=@lotId)
                OR EXISTS(SELECT 1 FROM damage WHERE lot_id=@lotId)
                OR EXISTS(SELECT 1 FROM direct_stock_origin WHERE lot_id=@lotId)
            """, new { lotId }, transaction) != 0;

    private sealed class LotEditRule
    {
        public DateOnly ReceivedDate { get; init; }
        public bool TrackExpiry { get; init; }
        public long ProductId { get; init; }
    }

    private sealed class LotDeleteRow
    {
        public string LotCode { get; init; } = string.Empty;
        public long Quantity { get; init; }
    }
}
