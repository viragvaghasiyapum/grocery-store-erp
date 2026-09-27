using System.Globalization;

namespace GroceryErp.Core.Time;

public readonly record struct YearMonth : IComparable<YearMonth>
{
    public YearMonth(int year, int month)
    {
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        Year = year;
        Month = month;
    }

    public int Year { get; }
    public int Month { get; }
    public DateOnly FirstDay => new(Year, Month, 1);
    public DateOnly LastDay => FirstDay.AddMonths(1).AddDays(-1);

    public YearMonth AddMonths(int months)
    {
        DateOnly result = FirstDay.AddMonths(months);
        return new(result.Year, result.Month);
    }

    public bool IsLastMonthOfFiscalYear(int fiscalYearStartMonth) =>
        Month == (fiscalYearStartMonth == 1 ? 12 : fiscalYearStartMonth - 1);

    public int CompareTo(YearMonth other) => FirstDay.CompareTo(other.FirstDay);
    public override string ToString() => $"{Year:D4}-{Month:D2}";

    public static YearMonth From(DateOnly date) => new(date.Year, date.Month);

    public static YearMonth Parse(string value)
    {
        if (!DateOnly.TryParseExact($"{value}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly date))
            throw new FormatException("A year-month must use YYYY-MM format.");
        return From(date);
    }

    public static bool operator <(YearMonth left, YearMonth right) => left.CompareTo(right) < 0;
    public static bool operator >(YearMonth left, YearMonth right) => left.CompareTo(right) > 0;
    public static bool operator <=(YearMonth left, YearMonth right) => left.CompareTo(right) <= 0;
    public static bool operator >=(YearMonth left, YearMonth right) => left.CompareTo(right) >= 0;
}
