using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Inventory.Contracts;

public sealed record StockMovementInput(
    DateOnly Date,
    long ProductId,
    long LotId,
    string Type,
    Quantity Quantity,
    Money UnitCost,
    string? RefType = null,
    long? RefId = null,
    string? Note = null);

public sealed record LotStock(long LotId, Quantity OnHand, DateOnly? Expiry, Money Price, Money? Mrp, Money Cost);

public sealed record LotAllocation(LotStock Lot, Quantity Quantity);

public interface IStockLedger
{
    void Post(DbTx transaction, StockMovementInput movement);

    IReadOnlyList<LotAllocation> AllocateFefo(
        DbTx transaction,
        long productId,
        Quantity requested,
        DateOnly asOf,
        IReadOnlyDictionary<long, Quantity> alreadyReserved);
}
