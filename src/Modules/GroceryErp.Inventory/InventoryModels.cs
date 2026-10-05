using GroceryErp.Core.Numerics;

namespace GroceryErp.Inventory;

public sealed record StockOverviewItem(
    long ProductId,
    string Sku,
    string Name,
    Quantity Quantity,
    Money CostValue,
    Quantity ReorderLevel,
    bool IsLow);

public sealed record LotItem(
    long Id,
    long ProductId,
    string ProductName,
    string LotCode,
    string? BatchNumber,
    DateOnly ReceivedDate,
    DateOnly? ExpiryDate,
    Money UnitCost,
    Money? Mrp,
    Money SellingPrice,
    Quantity Received,
    Quantity OnHand,
    bool IsActive,
    string ExpiryBucketCode = "NONE");

public sealed record LedgerItem(
    long Id,
    DateTime MovedAt,
    DateOnly MoveDate,
    string Type,
    Quantity Quantity,
    Money UnitCost,
    string? RefType,
    long? RefId,
    string LotCode,
    Quantity LotBalance,
    string? Note);

public sealed record StockSourceLine(string ProductName, string LotCode, Quantity Quantity, Money Amount);

public sealed record StockSourceDocument(string Type, string Number, DateOnly Date,
    string? Party, string? Status, string? Notes, IReadOnlyList<StockSourceLine> Lines);

public sealed record OpeningStockLine(
    long ProductId,
    Quantity Quantity,
    Money UnitCost,
    Money SellingPrice,
    Money? Mrp,
    DateOnly? ExpiryDate,
    string? BatchNumber,
    DateOnly ReceivedDate);

public sealed record StockCountLine(
    long Id,
    long ProductId,
    string ProductName,
    long LotId,
    string LotCode,
    Quantity Expected,
    Quantity? Counted,
    Money UnitCost,
    Quantity MovementSinceSnapshot,
    Quantity Difference,
    Money DifferenceValue);

public sealed record StockCountDetails(long Id, DateOnly Date, string Status, string? Notes, IReadOnlyList<StockCountLine> Lines);

public sealed class StockCountSummary
{
    public long Id { get; init; }
    public DateOnly Date { get; init; }
    public long LotCount { get; init; }
    public long CountedCount { get; init; }
}

public sealed record StockValuationItem(
    long CategoryId,
    string CategoryName,
    Quantity Quantity,
    Money CostValue,
    Money SellingValue,
    Money MrpValue);
