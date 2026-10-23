using System.Data;
using System.Globalization;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Data.Services;

public enum PeriodTreatment { OpenPeriod, CurrentDated }

/// Where a correction's P&L difference belongs: the original date while both of its
/// annual periods are open, otherwise the business date of the correction.
public sealed record CorrectionDating(PeriodTreatment Treatment, DateOnly OriginalDate, DateOnly EffectiveDate,
    AnnualPeriod Calendar, AnnualPeriod Fiscal, bool CalendarLocked, bool FiscalLocked);

// Calendar and fiscal years lock independently from the eighth date after they end.
// The lock follows the business date; a finalized snapshot also keeps its year locked
// if the clock is ever moved back.
public sealed class PeriodGuard(ISettings settings, IClock clock)
{
    public DateOnly Today => clock.BusinessDate;
    public int FiscalStartMonth => settings.Current.FyStartMonth;

    // Date-only check for validation before a transaction opens.
    public void EnsureOpen(DateOnly date)
    {
        if (!AnnualPeriod.IsDateOpen(date, Today, FiscalStartMonth)) throw Locked(date);
    }

    public void EnsureOpen(DbTx transaction, DateOnly date)
    {
        if (!IsOpen(transaction.Connection, transaction.Transaction, date)) throw Locked(date);
    }

    public bool IsOpen(IDbConnection connection, IDbTransaction? transaction, DateOnly date) =>
        AnnualPeriod.IsDateOpen(date, Today, FiscalStartMonth) && !IsFinalized(connection, transaction, date);

    public CorrectionDating Preview(IDbConnection connection, IDbTransaction? transaction, DateOnly originalDate)
    {
        ArgumentNullException.ThrowIfNull(connection);
        AnnualPeriod calendar = AnnualPeriod.Calendar(originalDate);
        AnnualPeriod fiscal = AnnualPeriod.Fiscal(originalDate, FiscalStartMonth);
        bool calendarLocked = calendar.IsLockedOn(Today) || IsFinalized(connection, transaction, calendar);
        bool fiscalLocked = fiscal.IsLockedOn(Today) || IsFinalized(connection, transaction, fiscal);
        bool open = !calendarLocked && !fiscalLocked;
        return new(open ? PeriodTreatment.OpenPeriod : PeriodTreatment.CurrentDated, originalDate,
            open ? originalDate : Today, calendar, fiscal, calendarLocked, fiscalLocked);
    }

    // The correction itself is recorded today, so today must be open too.
    public CorrectionDating DateCorrection(DbTx transaction, DateOnly originalDate)
    {
        EnsureOpen(transaction, Today);
        return Preview(transaction.Connection, transaction.Transaction, originalDate);
    }

    public YearMonth FirstOpenMonth(IDbConnection connection, IDbTransaction? transaction)
    {
        YearMonth month = AnnualPeriod.FirstOpenMonth(Today, FiscalStartMonth);
        while (IsFinalized(connection, transaction, month.FirstDay) && month < YearMonth.From(Today))
            month = month.AddMonths(1);
        return month;
    }

    private static bool IsFinalized(IDbConnection connection, IDbTransaction? transaction, DateOnly date) =>
        connection.ExecuteScalar<long>("""
            SELECT EXISTS(SELECT 1 FROM annual_pnl_close WHERE @date BETWEEN year_start AND year_end)
            """, new { date = Iso(date) }, transaction) != 0;

    private static bool IsFinalized(IDbConnection connection, IDbTransaction? transaction, AnnualPeriod period) =>
        connection.ExecuteScalar<long>("""
            SELECT EXISTS(SELECT 1 FROM annual_pnl_close WHERE period_kind=@kind AND year_start=@start)
            """, new { kind = period.Code, start = Iso(period.Start) }, transaction) != 0;

    private DomainException Locked(DateOnly date)
    {
        AnnualPeriod calendar = AnnualPeriod.Calendar(date);
        AnnualPeriod locked = calendar.IsLockedOn(Today) ? calendar : AnnualPeriod.Fiscal(date, FiscalStartMonth);
        return new DomainException(ErrorCode.PeriodLocked, Iso(date, "dd-MM-yyyy"),
            locked.Kind == AnnualPeriodKind.Calendar ? $"CY {locked.Label}" : $"FY {locked.Label}");
    }

    private static string Iso(DateOnly date, string format = "yyyy-MM-dd") =>
        date.ToString(format, CultureInfo.InvariantCulture);
}
