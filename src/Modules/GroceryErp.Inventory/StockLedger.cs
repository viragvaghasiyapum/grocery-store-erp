using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Inventory;

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

        transaction.Connection.Execute("""
            INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost,ref_type,ref_id,note)
            VALUES (@Date,@ProductId,@LotId,@Type,@Quantity,@UnitCost,@RefType,@RefId,@Note)
            """, new
        {
            Date = movement.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            movement.ProductId,
            movement.LotId,
            movement.Type,
            Quantity = movement.Quantity.Scaled,
            UnitCost = movement.UnitCost.Scaled,
            movement.RefType,
            movement.RefId,
            movement.Note,
        }, transaction.Transaction);
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

        if (!settings.Current.AllowNegativeStock)
            throw new DomainException(ErrorCode.InsufficientStock, requested - remaining);

        LotRow? newest = transaction.Connection.QuerySingleOrDefault<LotRow>("""
            SELECT id AS LotId, qty_on_hand AS OnHand, expiry_date AS Expiry,
                   selling_price AS Price, mrp AS Mrp, unit_cost AS Cost
            FROM product_lot WHERE product_id=@productId AND is_active=1
              AND (@blockExpired=0 OR expiry_date IS NULL OR expiry_date>=@asOf)
            ORDER BY received_date DESC,id DESC LIMIT 1
            """, new
        {
            productId,
            blockExpired = settings.Current.BlockExpiredLots ? 1 : 0,
            asOf = asOf.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        }, transaction.Transaction);
        if (newest is null) throw new DomainException(ErrorCode.NoLotForProduct, productId);
        result.Add(new(newest.ToModel(), remaining));
        return result;
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
