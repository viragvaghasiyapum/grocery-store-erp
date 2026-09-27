using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using Shouldly;
using Xunit;

namespace GroceryErp.Core.Tests.Numerics;

public sealed class DepreciationTests
{
    [Fact]
    public void Wdv_WorkedExample_ClosesFiscalYearExactly()
    {
        var asset = new AssetDepreciationInfo(
            new Money(45000), Money.Zero, new Percent(15), DepreciationMethod.Wdv,
            new DateOnly(2026, 4, 1), null);
        Money opening = asset.Cost;
        Money posted = Money.Zero;
        var charges = new List<Money>();

        for (int offset = 0; offset < 12; offset++)
        {
            YearMonth month = new YearMonth(2026, 4).AddMonths(offset);
            Money charge = Depreciation.MonthlyCharge(asset, month, opening, asset.Cost, posted, 4);
            charges.Add(charge);
            opening -= charge;
            posted += charge;
        }

        charges[0].ShouldBe(new Money(554.795m));
        charges[1].ShouldBe(new Money(573.288m));
        charges[11].ShouldBe(new Money(573.284m));
        posted.ShouldBe(new Money(6750));
        opening.ShouldBe(new Money(38250));
    }

    [Fact]
    public void Slm_DisposalProratesAndNeverCrossesSalvage()
    {
        var asset = new AssetDepreciationInfo(
            new Money(1200), new Money(200), new Percent(100), DepreciationMethod.Slm,
            new DateOnly(2026, 4, 16), new DateOnly(2026, 4, 20));

        Money charge = Depreciation.MonthlyCharge(asset, new YearMonth(2026, 4),
            new Money(205), new Money(1200), Money.Zero, 4);

        charge.ShouldBe(new Money(5));
    }
}
