using System.Globalization;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using Shouldly;
using Xunit;

namespace GroceryErp.Core.Tests.Numerics;

public sealed class FoundationNumericsTests
{
    [Fact]
    public void InclusiveTax_DerivesTaxBySubtraction()
    {
        (Money taxable, Money tax) = Tax.FromInclusive(new Money(580m), new Percent(5m));

        taxable.ShouldBe(new Money(552.381m));
        tax.ShouldBe(new Money(27.619m));
        (taxable + tax).ShouldBe(new Money(580m));
    }

    [Fact]
    public void Allocate_LargestRemainderPreservesTotal()
    {
        IReadOnlyList<Money> shares = MoneyMath.Allocate(new Money(1m),
            [new Money(1m), new Money(1m), new Money(1m)]);

        shares.Sum(static share => share.Scaled).ShouldBe(new Money(1m).Scaled);
        shares.Select(static share => share.Scaled).ShouldBe([334L, 333L, 333L]);
    }

    [Fact]
    public void PricingEngine_AllocatesDiscountAndReconcilesInclusiveTax()
    {
        PricedBill bill = PricingEngine.Price(
        [
            new(1, 11, new Quantity(2), new Money(118), new Money(120), new Percent(18),
                true, new Money(10), new Money(70)),
            new(2, 12, Quantity.One, new Money(50), null, Percent.Zero,
                true, Money.Zero, new Money(25)),
        ], new Money(12), new Money(1));

        bill.Gross.ShouldBe(new Money(286));
        bill.Discount.ShouldBe(new Money(22));
        bill.GrandTotal.ShouldBe(new Money(264));
        (bill.Taxable + bill.Tax + bill.RoundOff).ShouldBe(bill.GrandTotal);
        bill.Lines.Sum(static line => line.Discount.Scaled).ShouldBe(22000L);
    }

    [Theory]
    [InlineData("2026-03-31", 4, "2025-26", "2526")]
    [InlineData("2026-04-01", 4, "2026-27", "2627")]
    public void FiscalYear_UsesConfiguredStartMonth(string dateText, int startMonth, string label, string shortCode)
    {
        var date = DateOnly.Parse(dateText, CultureInfo.InvariantCulture);

        FiscalYear.Label(date, startMonth).ShouldBe(label);
        FiscalYear.ShortCode(date, startMonth).ShouldBe(shortCode);
    }
}
