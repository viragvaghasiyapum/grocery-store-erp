using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Inventory;

// The one writer of lot quantity. Each change runs in its document's transaction as
// a conditional update; the document itself is the stock record. Only corrections,
// which have no other document, are saved as stock_correction rows.
public sealed class StockLedger(ISettings settings) : IStockLedger
{
    private static readonly HashSet<string> MovementTypes =
        ["OPENING", "DIRECT_ENTRY", "PURCHASE", "PURCHASE_CANCEL", "VENDOR_RETURN", "VENDOR_RETURN_CANCEL",
         "INTERNAL_USE", "SALE", "SALE_VOID", "SALE_RETURN", "DAMAGE", "ADJUSTMENT"];

    public void Post(DbTx transaction, StockMovementInput movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        if (movement.ProductId <= 0 || movement.LotId <= 0 || movement.Quantity == Quantity.Zero ||
            movement.UnitCost < Money.Zero || !MovementTypes.Contains(movement.Type))
            throw new DomainException(ErrorCode.Validation, "Invalid stock movement.");
        string date = movement.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        bool sale = movement.Type == "SALE";
        int updated = transaction.Connection.Execute("""
            UPDATE product_lot SET qty_on_hand=qty_on_hand+@delta
            WHERE id=@lotId AND product_id=@productId AND qty_on_hand+@delta>=0
              AND (@sale=0 OR (is_active=1 AND
                   (@blockExpired=0 OR expiry_date IS NULL OR expiry_date>=@date)))
            """, new
        {
            delta = movement.Quantity.Scaled,
            lotId = movement.LotId,
            productId = movement.ProductId,
            sale = sale ? 1 : 0,
            blockExpired = sale && settings.Current.BlockExpiredLots ? 1 : 0,
            date,
        }, transaction.Transaction);
        if (updated != 1) throw Rejected(transaction, movement, sale);
        if (movement.Type != "ADJUSTMENT") return;
        transaction.Connection.Execute("""
            INSERT INTO stock_correction(correction_date,product_id,lot_id,quantity_delta,unit_cost,
                source_kind,source_id,reason)
            VALUES (@date,@ProductId,@LotId,@delta,@cost,@kind,@RefId,@Note)
            """, new
        {
            date,
            movement.ProductId,
            movement.LotId,
            delta = movement.Quantity.Scaled,
            cost = movement.UnitCost.Scaled,
            kind = movement.RefType switch
            {
                "damage_correction" => "DAMAGE_CORRECTION",
                "lot_correction" or "lot_cost_correction" => "LOT_CORRECTION",
                _ => "MANUAL",
            },
            movement.RefId,
            movement.Note,
        }, transaction.Transaction);
    }

    private static DomainException Rejected(DbTx transaction, StockMovementInput movement, bool sale)
    {
        var lot = transaction.Connection.QuerySingleOrDefault<(long OnHand, long IsActive)?>("""
            SELECT qty_on_hand AS OnHand,is_active AS IsActive FROM product_lot
            WHERE id=@LotId AND product_id=@ProductId
            """, new { movement.LotId, movement.ProductId }, transaction.Transaction);
        if (lot is null) return new DomainException(ErrorCode.LotProductMismatch, movement.LotId);
        if (lot.Value.OnHand + movement.Quantity.Scaled < 0)
            return new DomainException(ErrorCode.InsufficientStock, movement.LotId);
        return sale && lot.Value.IsActive == 1
            ? new DomainException(ErrorCode.ExpiredLot, movement.LotId)
            : new DomainException(ErrorCode.Validation, "This lot is blocked for sale.");
    }

    public IReadOnlyList<LotAllocation> AllocateFefo(
        DbTx transaction,
        long productId,
        Quantity requested,
        DateOnly asOf,
        IReadOnlyDictionary<long, Quantity> alreadyReserved)
    {
        ArgumentNullException.ThrowIfNull(alreadyReserved);
        if (productId <= 0 || requested <= Quantity.Zero)
            throw new DomainException(ErrorCode.Validation, "A product and positive quantity are required.");

        List<LotStock> lots = transaction.Connection.Query<LotRow>("""
            SELECT id AS LotId, qty_on_hand AS OnHand, expiry_date AS Expiry,
                   selling_price AS Price, mrp AS Mrp, unit_cost AS Cost
            FROM product_lot
            WHERE product_id=@productId AND is_active=1 AND qty_on_hand>0
              AND (@blockExpired=0 OR expiry_date IS NULL OR expiry_date>=@asOf)
            ORDER BY expiry_date IS NULL, expiry_date, received_date, id
            """, new
        {
            productId,
            blockExpired = settings.Current.BlockExpiredLots ? 1 : 0,
            asOf = asOf.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        }, transaction.Transaction).Select(static row => row.ToModel()).ToList();

        var result = new List<LotAllocation>();
        Quantity remaining = requested;
        foreach (LotStock lot in lots)
        {
            Quantity available = lot.OnHand - alreadyReserved.GetValueOrDefault(lot.LotId);
            if (available <= Quantity.Zero) continue;
            Quantity take = remaining < available ? remaining : available;
            result.Add(new(lot, take));
            remaining -= take;
            if (remaining == Quantity.Zero) return result;
        }
        throw new DomainException(ErrorCode.InsufficientStock, requested - remaining);
    }

    private sealed class LotRow
    {
        public long LotId { get; init; }
        public long OnHand { get; init; }
        public DateOnly? Expiry { get; init; }
        public long Price { get; init; }
        public long? Mrp { get; init; }
        public long Cost { get; init; }
        public LotStock ToModel() => new(LotId, Quantity.FromScaled(OnHand), Expiry,
            Money.FromScaled(Price), Mrp is null ? null : Money.FromScaled(Mrp.Value), Money.FromScaled(Cost));
    }
}
