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

public sealed record AssetPage(IReadOnlyList<AssetItem> Items, bool HasMore,
    bool? NextDisposed, string? NextName, long? NextId);

/// A posted month. ChargedOn is set when the month was posted after its year locked,
/// so its charge belongs to the open period of that date.
public sealed record DepreciationEntry(
    long Id, long AssetId, YearMonth Period, Money OpeningValue, Money Amount, Money ClosingValue, DateTime PostedAt,
    DateOnly? ChargedOn = null);

/// Months posted, and the closed-year months among them that were charged to the business date.
public sealed record DepreciationPostResult(int PostedCount, IReadOnlyList<YearMonth> CurrentDatedPeriods);

/// A dated set of depreciation terms. Reason is INITIAL, CHANGE or RECALCULATION.
public sealed record AssetTermItem(long Id, YearMonth EffectiveMonth, Money Cost, DateOnly InUseFrom,
    DepreciationMethod Method, Percent Rate, Money SalvageValue, string Reason, DateTime CreatedAt);

/// Treatment is DETAILS (no financial change), TERMS (method, rate or salvage from a
/// month), RECALCULATION (cost or in-use date while every posted month is open) or
/// BLOCKED (cost or in-use date after a finalized month was posted).
public sealed record AssetChangePreview(string Treatment, YearMonth? EffectiveMonth, YearMonth FirstOpenMonth,
    int MonthsRecalculated, Money DepreciationBefore, Money DepreciationAfter, Money BookValueBefore,
    Money BookValueAfter);
