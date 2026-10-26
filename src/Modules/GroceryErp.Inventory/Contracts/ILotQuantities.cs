using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Inventory.Contracts;

/// <summary>
/// The business document behind a lot quantity change. That document records the quantity
/// itself, so the kind also fixes the direction of the change.
/// </summary>
public enum LotChangeKind
{
    /// <summary>Opening stock import; adds stock.</summary>
    Opening,
    /// <summary>Direct-origin lot; adds stock.</summary>
    DirectEntry,
    /// <summary>Posted purchase line; adds stock.</summary>
    Purchase,
    /// <summary>Voided purchase line; removes stock.</summary>
    PurchaseVoid,
    /// <summary>Return to the vendor; removes stock.</summary>
    VendorReturn,
    /// <summary>Cancelled vendor return; adds stock back.</summary>
    VendorReturnCancel,
    /// <summary>Internal-use consumption; removes stock.</summary>
    InternalUse,
    /// <summary>Completed sale line; removes stock from an active, unexpired lot.</summary>
    Sale,
    /// <summary>Voided sale line; adds stock back.</summary>
    SaleVoid,
    /// <summary>Resellable customer return; adds stock.</summary>
    CustomerReturn,
    /// <summary>Damage document; removes stock.</summary>
    Damage,
}

/// <summary>One document's change to one lot, applied inside that document's transaction.</summary>
public sealed record LotChange(DateOnly Date, long ProductId, long LotId, LotChangeKind Kind, Quantity Quantity);

/// <summary>
/// A damage correction's change to its lot. It has no document of its own, so it is also
/// saved as an immutable dated stock correction.
/// </summary>
public sealed record DamageStockCorrection(DateOnly Date, long ProductId, long LotId, Quantity Quantity,
    Money UnitCost, long DamageCorrectionId, string? Reason);

public sealed record LotStock(long LotId, Quantity OnHand, DateOnly? Expiry, Money Price, Money? Mrp, Money Cost)
{
    /// <summary>Gets one piece's purchase tax saved on the lot; null for a lot saved before tax snapshots.</summary>
    public Money? CostTax { get; init; }

    /// <summary>Gets one piece's purchase price including tax saved on the lot.</summary>
    public Money? CostInclTax { get; init; }
}

public sealed record LotAllocation(LotStock Lot, Quantity Quantity);

/// <summary>
/// The single writer of lot quantity. Each change is one conditional update in the caller's
/// transaction that never leaves a lot negative.
/// </summary>
public interface ILotQuantities
{
    /// <summary>Applies a document's change; a sale also requires an active lot that expiry blocking allows.</summary>
    void Change(DbTx transaction, LotChange change);

    /// <summary>Applies a damage correction and saves its stock correction.</summary>
    void CorrectDamage(DbTx transaction, DamageStockCorrection correction);

    /// <summary>Allocates a quantity across active lots, first expiring first.</summary>
    IReadOnlyList<LotAllocation> AllocateFefo(
        DbTx transaction,
        long productId,
        Quantity requested,
        DateOnly asOf,
        IReadOnlyDictionary<long, Quantity> alreadyReserved);
}
