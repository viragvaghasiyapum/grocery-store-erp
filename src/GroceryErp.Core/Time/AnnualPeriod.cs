using System.Globalization;

namespace GroceryErp.Core.Time;

/// <summary>The two independent annual reporting periods.</summary>
public enum AnnualPeriodKind { Calendar, Fiscal }

/// <summary>
/// A calendar or fiscal year. Each year stays editable for the seven dates after it
/// ends and is locked from the eighth, judged only from the business date.
/// </summary>
public readonly record struct AnnualPeriod
{
    /// <summary>Dates after the year end that remain editable.</summary>
    public const int EditableDaysAfterEnd = 7;

    private AnnualPeriod(AnnualPeriodKind kind, DateOnly start)
    {
        Kind = kind;
        Start = start;
    }

    /// <summary>Calendar or fiscal.</summary>
    public AnnualPeriodKind Kind { get; }

    /// <summary>First date of the year.</summary>
    public DateOnly Start { get; }

    /// <summary>Last date of the year.</summary>
    public DateOnly End => Start.AddYears(1).AddDays(-1);

    /// <summary>Last date on which entries dated in this year may still change.</summary>
    public DateOnly EditCutoff => End.AddDays(EditableDaysAfterEnd);

    /// <summary>First business date on which the year is locked.</summary>
    public DateOnly LockDate => EditCutoff.AddDays(1);

    /// <summary>Stable storage code.</summary>
    public string Code => Kind == AnnualPeriodKind.Calendar ? "CALENDAR" : "FISCAL";

    /// <summary>English-digit label: "2026" or "2026-27".</summary>
    public string Label => Kind == AnnualPeriodKind.Calendar || Start.Month == 1
        ? Start.Year.ToString(CultureInfo.InvariantCulture)
        : $"{Start.Year.ToString(CultureInfo.InvariantCulture)}-{((Start.Year + 1) % 100).ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>Whether the date falls within this year.</summary>
    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>Whether the year is locked on the given business date.</summary>
    public bool IsLockedOn(DateOnly businessDate) => businessDate >= LockDate;

    /// <summary>The year of the given kind that starts on <paramref name="start"/>.</summary>
    public static AnnualPeriod FromStart(AnnualPeriodKind kind, DateOnly start, int fiscalStartMonth)
    {
        int month = kind == AnnualPeriodKind.Calendar ? 1 : ValidMonth(fiscalStartMonth);
        if (start.Day != 1 || start.Month != month)
            throw new ArgumentOutOfRangeException(nameof(start), "An annual period starts on the first day of its first month.");
        return new(kind, start);
    }

    /// <summary>The calendar year containing the date.</summary>
    public static AnnualPeriod Calendar(DateOnly date) => new(AnnualPeriodKind.Calendar, new DateOnly(date.Year, 1, 1));

    /// <summary>The fiscal year containing the date.</summary>
    public static AnnualPeriod Fiscal(DateOnly date, int fiscalStartMonth) =>
        new(AnnualPeriodKind.Fiscal, FiscalYear.Range(FiscalYear.StartYear(date, fiscalStartMonth), fiscalStartMonth).From);

    /// <summary>The year of the given kind containing the date.</summary>
    public static AnnualPeriod Containing(AnnualPeriodKind kind, DateOnly date, int fiscalStartMonth) =>
        kind == AnnualPeriodKind.Calendar ? Calendar(date) : Fiscal(date, fiscalStartMonth);

    /// <summary>Parses a storage code.</summary>
    public static AnnualPeriodKind ParseKind(string code) => code switch
    {
        "CALENDAR" => AnnualPeriodKind.Calendar,
        "FISCAL" => AnnualPeriodKind.Fiscal,
        _ => throw new FormatException("Unknown annual period kind."),
    };

    /// <summary>
    /// Whether both annual periods containing the date are still editable on the
    /// business date. A date in either locked year must be corrected in an open period.
    /// </summary>
    public static bool IsDateOpen(DateOnly date, DateOnly businessDate, int fiscalStartMonth) =>
        !Calendar(date).IsLockedOn(businessDate) && !Fiscal(date, fiscalStartMonth).IsLockedOn(businessDate);

    /// <summary>The first month whose calendar and fiscal years are both still open.</summary>
    public static YearMonth FirstOpenMonth(DateOnly businessDate, int fiscalStartMonth)
    {
        YearMonth month = YearMonth.From(businessDate);
        while (IsDateOpen(month.AddMonths(-1).FirstDay, businessDate, fiscalStartMonth))
            month = month.AddMonths(-1);
        return month;
    }

    private static int ValidMonth(int month) => month is >= 1 and <= 12
        ? month : throw new ArgumentOutOfRangeException(nameof(month));
}
