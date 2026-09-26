namespace GroceryErp.Core.Time;

public interface IClock
{
    DateTime Now { get; }
    DateOnly BusinessDate { get; }
}

public interface IBusinessClockSettings
{
    int BusinessDayStartHour { get; }
}

public sealed class SystemClock(IBusinessClockSettings settings) : IClock
{
#pragma warning disable RS0030 // This is the single time boundary; callers depend on IClock.
    public DateTime Now => DateTime.Now;
    public DateOnly BusinessDate => DateOnly.FromDateTime(DateTime.Now.AddHours(-settings.BusinessDayStartHour));
#pragma warning restore RS0030
}

public static class FiscalYear
{
    public static int StartYear(DateOnly date, int startMonth)
    {
        ValidateMonth(startMonth);
        return date.Month >= startMonth ? date.Year : date.Year - 1;
    }

    public static string Label(DateOnly date, int startMonth)
    {
        int year = StartYear(date, startMonth);
        return startMonth == 1 ? $"{year}" : $"{year}-{(year + 1) % 100:D2}";
    }

    public static string ShortCode(DateOnly date, int startMonth)
    {
        int year = StartYear(date, startMonth);
        return $"{year % 100:D2}{(year + 1) % 100:D2}";
    }

    public static (DateOnly From, DateOnly To) Range(int startYear, int startMonth)
    {
        ValidateMonth(startMonth);
        var from = new DateOnly(startYear, startMonth, 1);
        return (from, from.AddYears(1).AddDays(-1));
    }

    public static int DaysIn(int startYear, int startMonth)
    {
        (DateOnly from, DateOnly to) = Range(startYear, startMonth);
        return to.DayNumber - from.DayNumber + 1;
    }

    private static void ValidateMonth(int month)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
    }
}
