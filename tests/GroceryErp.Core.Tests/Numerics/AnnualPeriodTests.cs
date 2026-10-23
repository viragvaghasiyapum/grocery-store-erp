using GroceryErp.Core.Time;
using Shouldly;
using Xunit;

namespace GroceryErp.Core.Tests.Numerics;

public sealed class AnnualPeriodTests
{
    [Fact]
    public void CalendarAndFiscalYears_LockOnTheEighthDateAfterTheyEnd()
    {
        AnnualPeriod calendar = AnnualPeriod.Calendar(new DateOnly(2026, 6, 15));
        calendar.Start.ShouldBe(new DateOnly(2026, 1, 1));
        calendar.End.ShouldBe(new DateOnly(2026, 12, 31));
        calendar.EditCutoff.ShouldBe(new DateOnly(2027, 1, 7));
        calendar.IsLockedOn(new DateOnly(2027, 1, 7)).ShouldBeFalse();
        calendar.IsLockedOn(new DateOnly(2027, 1, 8)).ShouldBeTrue();
        calendar.Label.ShouldBe("2026");

        AnnualPeriod fiscal = AnnualPeriod.Fiscal(new DateOnly(2026, 2, 10), 4);
        fiscal.Start.ShouldBe(new DateOnly(2025, 4, 1));
        fiscal.End.ShouldBe(new DateOnly(2026, 3, 31));
        fiscal.IsLockedOn(new DateOnly(2026, 4, 7)).ShouldBeFalse();
        fiscal.IsLockedOn(new DateOnly(2026, 4, 8)).ShouldBeTrue();
        fiscal.Label.ShouldBe("2025-26");
        AnnualPeriod.Fiscal(new DateOnly(2026, 4, 1), 4).Label.ShouldBe("2026-27");
    }

    [Theory]
    // In October 2026 a February 2026 date is in a locked fiscal year and an open calendar year.
    [InlineData("2026-02-10", "2026-10-15", false)]
    // In February 2027 an October 2026 date is in a locked calendar year and an open fiscal year.
    [InlineData("2026-10-10", "2027-02-15", false)]
    // The seven buffer dates keep both years containing the date editable.
    [InlineData("2026-12-31", "2027-01-07", true)]
    [InlineData("2026-12-31", "2027-01-08", false)]
    [InlineData("2027-03-31", "2027-04-07", true)]
    [InlineData("2027-03-31", "2027-04-08", false)]
    [InlineData("2026-10-10", "2026-10-15", true)]
    public void DateIsOpen_OnlyWhileBothContainingYearsAreEditable(string date, string businessDate, bool open) =>
        AnnualPeriod.IsDateOpen(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
            DateOnly.Parse(businessDate, System.Globalization.CultureInfo.InvariantCulture), 4).ShouldBe(open);

    [Theory]
    [InlineData("2026-10-23", "2026-04")]
    [InlineData("2027-01-05", "2026-04")]
    [InlineData("2027-01-08", "2027-01")]
    [InlineData("2027-04-07", "2027-01")]
    [InlineData("2027-04-08", "2027-04")]
    [InlineData("2026-04-07", "2026-01")]
    public void FirstOpenMonth_IsTheEarliestMonthBothYearsStillAccept(string businessDate, string month) =>
        AnnualPeriod.FirstOpenMonth(DateOnly.Parse(businessDate, System.Globalization.CultureInfo.InvariantCulture), 4)
            .ShouldBe(YearMonth.Parse(month));

    [Fact]
    public void FromStart_RejectsADateThatDoesNotBeginTheYear()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            AnnualPeriod.FromStart(AnnualPeriodKind.Fiscal, new DateOnly(2026, 1, 1), 4));
        AnnualPeriod.FromStart(AnnualPeriodKind.Fiscal, new DateOnly(2026, 4, 1), 4).End
            .ShouldBe(new DateOnly(2027, 3, 31));
        AnnualPeriod.ParseKind("FISCAL").ShouldBe(AnnualPeriodKind.Fiscal);
    }
}
