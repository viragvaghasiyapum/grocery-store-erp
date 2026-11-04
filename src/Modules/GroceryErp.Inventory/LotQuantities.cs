using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Inventory;

// The one writer of lot quantity. Each change runs in its document's transaction as
// a conditional update; the document itself is the stock record. Only a damage
// correction, which has no other document, is saved as a stock_correction row.
public sealed class LotQuantities(ISettings settings) : ILotQuantities
{
    public void Change(DbTx transaction, LotChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        bool adds = change.Kind switch
        {
            LotChangeKind.Opening or LotChangeKind.DirectEntry or LotChangeKind.Purchase or
                LotChangeKind.VendorReturnCancel or LotChangeKind.SaleVoid or LotChangeKind.CustomerReturn => true,
            LotChangeKind.PurchaseVoid or LotChangeKind.VendorReturn or LotChangeKind.InternalUse or
                LotChangeKind.Sale or LotChangeKind.Damage => false,
            _ => throw new DomainException(ErrorCode.Validation, "Invalid stock change."),
        };
        if (change.Quantity == Quantity.Zero || change.Quantity > Quantity.Zero != adds)
            throw new DomainException(ErrorCode.Validation, "Invalid stock change.");
        Apply(transaction, change.Date, change.ProductId, change.LotId, change.Quantity, change.Kind == LotChangeKind.Sale);
    }

    public void CorrectDamage(DbTx transaction, DamageStockCorrection correction)
    {
        ArgumentNullException.ThrowIfNull(correction);
        if (correction.Quantity == Quantity.Zero || correction.UnitCost < Money.Zero)
            throw new DomainException(ErrorCode.Validation, "Invalid stock change.");
        Apply(transaction, correction.Date, correction.ProductId, correction.LotId, correction.Quantity, sale: false);
        transaction.Connection.Execute("""
            INSERT INTO stock_correction(correction_date,product_id,lot_id,quantity_delta,unit_cost,
                source_kind,source_id,reason)
            VALUES (@date,@ProductId,@LotId,@delta,@cost,'DAMAGE_CORRECTION',@DamageCorrectionId,@Reason)
            """, new
        {
            date = Format(correction.Date),
            correction.ProductId,
            correction.LotId,
            delta = correction.Quantity.Scaled,
            cost = correction.UnitCost.Scaled,
            correction.DamageCorrectionId,
            correction.Reason,
        }, transaction.Transaction);
    }

    private void Apply(DbTx transaction, DateOnly date, long productId, long lotId, Quantity quantity, bool sale)
    {
        if (productId <= 0 || lotId <= 0)
            throw new DomainException(ErrorCode.Validation, "Invalid stock change.");
        int updated = transaction.Connection.Execute("""
            UPDATE product_lot SET qty_on_hand=qty_on_hand+@delta
            WHERE id=@lotId AND product_id=@productId AND qty_on_hand+@delta>=0
              AND (@sale=0 OR (is_active=1 AND
                   (@blockExpired=0 OR expiry_date IS NULL OR expiry_date>=@date)))
            """, new
        {
            delta = quantity.Scaled,
            lotId,
            productId,
            sale = sale ? 1 : 0,
            blockExpired = sale && settings.Current.BlockExpiredLots ? 1 : 0,
            date = Format(date),
        }, transaction.Transaction);
        if (updated != 1) throw Rejected(transaction, productId, lotId, quantity, sale);
    }

    private static DomainException Rejected(DbTx transaction, long productId, long lotId, Quantity quantity, bool sale)
    {
        var lot = transaction.Connection.QuerySingleOrDefault<(long OnHand, long IsActive, string LotCode)?>("""
            SELECT qty_on_hand AS OnHand,is_active AS IsActive,lot_code AS LotCode FROM product_lot
            WHERE id=@lotId AND product_id=@productId
            """, new { lotId, productId }, transaction.Transaction);
        if (lot is null) return new DomainException(ErrorCode.LotProductMismatch, lotId);
        if (lot.Value.OnHand + quantity.Scaled < 0)
            return new DomainException(ErrorCode.InsufficientStock, lotId);
        return sale && lot.Value.IsActive == 1
            ? new DomainException(ErrorCode.ExpiredLot, lotId)
            : new DomainException(ErrorCode.LotBlocked, lot.Value.LotCode);
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
                   selling_price AS Price, mrp AS Mrp, unit_cost AS Cost,
                   unit_tax AS CostTax, unit_cost_incl_tax AS CostInclTax
            FROM product_lot
            WHERE product_id=@productId AND is_active=1 AND qty_on_hand>0
              AND (@blockExpired=0 OR expiry_date IS NULL OR expiry_date>=@asOf)
            ORDER BY expiry_date IS NULL, expiry_date, received_date, id
            """, new
        {
            productId,
            blockExpired = settings.Current.BlockExpiredLots ? 1 : 0,
            asOf = Format(asOf),
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

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class LotRow
    {
        public long LotId { get; init; }
        public long OnHand { get; init; }
        public DateOnly? Expiry { get; init; }
        public long Price { get; init; }
        public long? Mrp { get; init; }
        public long Cost { get; init; }
        public long? CostTax { get; init; }
        public long? CostInclTax { get; init; }
        public LotStock ToModel() => new(LotId, Quantity.FromScaled(OnHand), Expiry,
            Money.FromScaled(Price), Mrp is null ? null : Money.FromScaled(Mrp.Value), Money.FromScaled(Cost))
        {
            CostTax = CostTax is null ? null : Money.FromScaled(CostTax.Value),
            CostInclTax = CostInclTax is null ? null : Money.FromScaled(CostInclTax.Value),
        };
    }
}
