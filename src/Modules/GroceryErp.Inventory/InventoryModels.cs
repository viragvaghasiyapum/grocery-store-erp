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
    string ExpiryBucketCode = "NONE")
{
    public string OriginKind { get; init; } = "DIRECT";
    public long DetailVersion { get; init; }
}

public sealed record LotDetailsEdit(long LotId, long ExpectedVersion, Money SellingPrice,
    Money? Mrp, DateOnly? ExpiryDate);

public sealed record LotDeletionPreview(long LotId, string LotCode, Quantity OnHand,
    bool CanDelete, string? BlockReason);
public sealed record LotDeletionRequest(long LotId, long ExpectedVersion);

public sealed record StockOverviewPage(IReadOnlyList<StockOverviewItem> Items, bool HasMore, string? NextName, long? NextId);
public sealed record LotPage(IReadOnlyList<LotItem> Items, bool HasMore, long? NextId);
public sealed record LotCorrection(LotItem Original, string? BatchNumber, DateOnly? ExpiryDate,
    Money UnitCost, Money SellingPrice, Money? Mrp, Quantity QuantityChange, DateOnly Date, string Reason);
public sealed record LedgerPage(IReadOnlyList<LedgerItem> Items, bool HasMore, long? NextId);

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

public sealed record StockValuationItem(
    long CategoryId,
    string CategoryName,
    Quantity Quantity,
    Money CostValue,
    Money SellingValue,
    Money MrpValue);
