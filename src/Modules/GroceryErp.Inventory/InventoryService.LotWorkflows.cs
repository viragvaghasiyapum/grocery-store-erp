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
                """, new
            {
                id = edit.LotId,
                version = edit.ExpectedVersion,
                price = edit.SellingPrice.Scaled,
                mrp = edit.Mrp?.Scaled,
                expiry = edit.ExpiryDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            },
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
            IsOnOpenBill(connection, lotId) ? "Stock_DeleteOpenBill" : null;
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
        if (IsOnOpenBill(tx.Connection, lotId, tx.Transaction))
            throw new DomainException(ErrorCode.Validation, "A held or draft bill still uses this lot.");
        int changed = tx.Connection.Execute("""
            DELETE FROM product_lot WHERE id=@lotId AND qty_on_hand=0 AND detail_version=@expectedVersion
            """, new { lotId, expectedVersion }, tx.Transaction);
        if (changed != 1) ThrowLotEditConflict(tx, lotId);
        audit.Write(tx, "product_lot", lotId, "DELETE_ZERO", new { productId });
    }, cancellationToken);

    // Posted documents keep their own snapshots, so history never blocks deleting a
    // zero lot. An unfinished bill still points at the live lot it will sell from.
    internal static bool IsOnOpenBill(System.Data.IDbConnection connection, long lotId,
        System.Data.IDbTransaction? transaction = null) => connection.ExecuteScalar<long>("""
            SELECT EXISTS(SELECT 1 FROM sale s CROSS JOIN sale_line sl
                          WHERE s.status IN ('HELD','DRAFT') AND sl.sale_id=s.id AND sl.lot_id=@lotId)
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
