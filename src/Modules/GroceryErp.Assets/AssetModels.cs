using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;

namespace GroceryErp.Assets;

public sealed record AssetInput(
    string Name, string? Category, DateOnly PurchaseDate, DateOnly InUseFrom, Money Cost,
    long? VendorId, DepreciationMethod Method, Percent Rate, Money SalvageValue, string? Notes);

public sealed record AssetItem(
    long Id, string Name, string? Category, DateOnly PurchaseDate, DateOnly InUseFrom, Money Cost,
    long? VendorId, string? VendorName, DepreciationMethod Method, Percent Rate, Money SalvageValue,
    DateOnly? DisposedDate, Money? DisposalValue, Money AccumulatedDepreciation, Money BookValue, string? Notes);

public sealed record DepreciationEntry(
    long Id, long AssetId, YearMonth Period, Money OpeningValue, Money Amount, Money ClosingValue, DateTime PostedAt);

public sealed record DepreciationPostResult(int PostedCount, IReadOnlyList<YearMonth> LockedPeriods);
