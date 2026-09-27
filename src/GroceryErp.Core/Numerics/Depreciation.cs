using GroceryErp.Core.Time;

namespace GroceryErp.Core.Numerics;

public enum DepreciationMethod { Wdv, Slm }

public sealed record AssetDepreciationInfo(
    Money Cost,
    Money Salvage,
    Percent Rate,
    DepreciationMethod Method,
    DateOnly InUseFrom,
    DateOnly? DisposedDate);

public static class Depreciation
{
    public static Money MonthlyCharge(
        AssetDepreciationInfo asset,
        YearMonth month,
        Money opening,
        Money fiscalYearStartValue,
        Money postedThisFiscalYear,
        int fiscalYearStartMonth)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Cost <= Money.Zero || asset.Salvage < Money.Zero || asset.Salvage > asset.Cost ||
            asset.Rate < Percent.Zero || asset.Rate > new Percent(100))
            throw new ArgumentOutOfRangeException(nameof(asset));

        DateOnly start = Max(month.FirstDay, asset.InUseFrom);
        DateOnly end = asset.DisposedDate is { } disposed ? Min(month.LastDay, disposed) : month.LastDay;
        if (end < start || opening <= asset.Salvage) return Money.Zero;

        int fiscalYear = FiscalYear.StartYear(month.FirstDay, fiscalYearStartMonth);
        int daysInFiscalYear = FiscalYear.DaysIn(fiscalYear, fiscalYearStartMonth);
        decimal yearly = asset.Method == DepreciationMethod.Wdv
            ? fiscalYearStartValue.Value * asset.Rate.Value / 100m
            : (asset.Cost - asset.Salvage).Value * asset.Rate.Value / 100m;

        bool closesFiscalYear = month.IsLastMonthOfFiscalYear(fiscalYearStartMonth);
        bool closesOnDisposal = asset.DisposedDate is { } disposal && disposal >= month.FirstDay && disposal <= month.LastDay;
        Money charge;
        if (closesFiscalYear || closesOnDisposal)
        {
            (DateOnly fiscalFrom, _) = FiscalYear.Range(fiscalYear, fiscalYearStartMonth);
            DateOnly usedFrom = Max(fiscalFrom, asset.InUseFrom);
            int daysUsed = end.DayNumber - usedFrom.DayNumber + 1;
            charge = new Money(yearly * daysUsed / daysInFiscalYear) - postedThisFiscalYear;
        }
        else
        {
            int daysUsed = end.DayNumber - start.DayNumber + 1;
            charge = new Money(yearly * daysUsed / daysInFiscalYear);
        }

        Money cap = opening - asset.Salvage;
        if (charge > cap) charge = cap;
        return charge < Money.Zero ? Money.Zero : charge;
    }

    private static DateOnly Max(DateOnly left, DateOnly right) => left > right ? left : right;
    private static DateOnly Min(DateOnly left, DateOnly right) => left < right ? left : right;
}
