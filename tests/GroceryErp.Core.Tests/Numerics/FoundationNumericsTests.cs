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

    [Fact]
    public void Property_Fx3RoundTripsAcrossTheFullScaledLongRange()
    {
        var random = new Random(7219);
        long[] edges = [long.MinValue, long.MinValue + 1, -1, 0, 1, long.MaxValue - 1, long.MaxValue];
        foreach (long scaled in edges.Concat(Enumerable.Range(0, 10_000).Select(_ => random.NextInt64(long.MinValue, long.MaxValue))))
        {
            Fx3.ToScaled(Fx3.FromScaled(scaled)).ShouldBe(scaled);
            Money.FromScaled(scaled).Scaled.ShouldBe(scaled);
            Quantity.FromScaled(scaled).Scaled.ShouldBe(scaled);
            Percent.FromScaled(scaled).Scaled.ShouldBe(scaled);
        }
    }

    [Fact]
    public void Property_AllocationAndTaxAlwaysReconcileToOneMilliunit()
    {
        var random = new Random(1947);
        for (int run = 0; run < 2_000; run++)
        {
            long total = random.NextInt64(0, 1_000_000_000);
            Money[] weights = Enumerable.Range(0, random.Next(1, 16))
                .Select(_ => Money.FromScaled(random.NextInt64(0, 1_000_000_000))).ToArray();
            IReadOnlyList<Money> allocated = MoneyMath.Allocate(Money.FromScaled(total), weights);
            allocated.Sum(static part => part.Scaled).ShouldBe(weights.Sum(static weight => weight.Scaled) == 0 ? 0 : total);
            allocated.All(static part => part >= Money.Zero).ShouldBeTrue();

            Money gross = Money.FromScaled(total);
            Percent rate = new(random.Next(0, 29));
            (Money taxable, Money tax) = Tax.FromInclusive(gross, rate);
            (taxable + tax).ShouldBe(gross);
        }
    }
}
