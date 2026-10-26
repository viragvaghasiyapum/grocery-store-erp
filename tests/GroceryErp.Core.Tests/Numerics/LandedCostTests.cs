using GroceryErp.Core.Numerics;
using Shouldly;
using Xunit;

namespace GroceryErp.Core.Tests.Numerics;

public sealed class LandedCostTests
{
    // §15.2: 10 pieces at 100 less 50 discount, 5% GST on 950, half of 60 freight, 2 free pieces.
    private static readonly PurchaseCostLine WorkedLine =
        new(new Quantity(10), new Quantity(2), new Money(100), new Money(50), new Money(47.5m));

    // A second line of the same value, so the freight splits evenly: 30 to each.
    private static readonly PurchaseCostLine EvenLine =
        new(Quantity.One, Quantity.Zero, new Money(950), Money.Zero, new Money(47.5m));

    [Fact]
    public void WorkedExample_GivesPurchasePriceTaxAndInclusivePriceExactly()
    {
        PurchaseUnitCost unit = LandedCost.Compute([WorkedLine, EvenLine], new Money(60), taxIsCost: false)[0];

        unit.BeforeTax.ShouldBe(new Money(81.667m));
        unit.InclTax.ShouldBe(new Money(85.625m));
        unit.Tax.ShouldBe(new Money(3.958m));
        unit.Booked.ShouldBe(unit.BeforeTax);
        (unit.BeforeTax + unit.Tax).ShouldBe(unit.InclTax);
        // Shown at two decimals the row reads 81.67 + 3.96 = 85.63; taxing the freight as well
        // (81.67 × 1.05 = 85.75) would be wrong.
        TwoDecimals.Round(unit.BeforeTax).ShouldBe(81.67m);
        TwoDecimals.Remainder(unit.InclTax, unit.BeforeTax).ShouldBe(3.96m);
        TwoDecimals.Round(unit.InclTax).ShouldBe(85.63m);

        LandedCost.Compute([WorkedLine, EvenLine], new Money(60), taxIsCost: true)[0].Booked
            .ShouldBe(new Money(85.625m));
    }

    [Fact]
    public void Booked_IsTheLandedCostTheBooksCarry_InEveryTaxMode()
    {
        PurchaseCostLine[] lines =
        [
            new(new Quantity(10), new Quantity(1), new Money(250), Money.Zero, new Money(125)),
            new(new Quantity(5), Quantity.Zero, new Money(100), Money.Zero, new Money(25)),
            new(new Quantity(3), new Quantity(3), new Money(33.333m), new Money(1.5m), new Money(17.774m)),
        ];
        foreach (bool taxIsCost in new[] { false, true })
        {
            IReadOnlyList<PurchaseUnitCost> units = LandedCost.Compute(lines, new Money(300), taxIsCost);
            IReadOnlyList<Money> shares = MoneyMath.Allocate(new Money(300),
                lines.Select(static line => line.Rate * line.Quantity - line.Discount).ToArray());
            for (int index = 0; index < lines.Length; index++)
            {
                PurchaseCostLine line = lines[index];
                // The formula the purchase posted before tax snapshots existed.
                Money expected = (line.Rate * line.Quantity - line.Discount + shares[index] +
                    (taxIsCost ? line.TaxAmount : Money.Zero)).DivideBy(line.Quantity + line.FreeQuantity);
                units[index].Booked.ShouldBe(expected);
            }
        }
    }

    [Fact]
    public void Property_TaxIsTheDifferenceOfTheTwoPrices()
    {
        var random = new Random(3915);
        for (int run = 0; run < 2_000; run++)
        {
            PurchaseCostLine[] lines = Enumerable.Range(0, random.Next(1, 6)).Select(_ =>
            {
                Quantity quantity = Quantity.FromScaled(random.NextInt64(1, 50_000));
                Money rate = Money.FromScaled(random.NextInt64(0, 1_000_000));
                Money gross = rate * quantity;
                Money discount = Money.FromScaled(random.NextInt64(0, gross.Scaled + 1));
                Percent taxRate = new(random.Next(0, 4) switch { 0 => 0, 1 => 5, 2 => 12, _ => 18 });
                return new PurchaseCostLine(quantity, random.Next(3) == 0 ? Quantity.FromScaled(random.NextInt64(0, 5_000)) : Quantity.Zero,
                    rate, discount, (gross - discount).PercentOf(taxRate));
            }).ToArray();
            // Uneven shares, or no other charges at all.
            Money charges = random.Next(4) == 0 ? Money.Zero : Money.FromScaled(random.NextInt64(1, 10_000_000));
            bool taxIsCost = random.Next(2) == 0;
            IReadOnlyList<PurchaseUnitCost> units = LandedCost.Compute(lines, charges, taxIsCost);
            for (int index = 0; index < lines.Length; index++)
            {
                PurchaseUnitCost unit = units[index];
                (unit.InclTax - unit.BeforeTax).ShouldBe(unit.Tax);
                unit.Tax.ShouldBeGreaterThanOrEqualTo(Money.Zero);
                unit.Booked.ShouldBe(taxIsCost ? unit.InclTax : unit.BeforeTax);
                if (lines[index].TaxAmount == Money.Zero) unit.Tax.ShouldBe(Money.Zero);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectLot_FromBeforeTax_AddsTheRateAndBooksByMode(bool taxIsCost)
    {
        PurchaseUnitCost unit = LandedCost.FromBeforeTax(new Money(81.667m), new Percent(5), taxIsCost);

        unit.BeforeTax.ShouldBe(new Money(81.667m));
        unit.Tax.ShouldBe(new Money(4.083m));
        unit.InclTax.ShouldBe(new Money(85.75m));
        unit.Booked.ShouldBe(taxIsCost ? unit.InclTax : unit.BeforeTax);
        LandedCost.FromBeforeTax(new Money(40), Percent.Zero, taxIsCost)
            .ShouldBe(new PurchaseUnitCost(new Money(40), Money.Zero, new Money(40), new Money(40)));
    }

    [Fact]
    public void RecreatedLot_FromBooked_KeepsTheBookedCostExactly()
    {
        // Regular books the price before tax, so the tax is added to it.
        PurchaseUnitCost regular = LandedCost.FromBooked(new Money(100), new Percent(18), taxIsCost: false);
        regular.ShouldBe(new PurchaseUnitCost(new Money(100), new Money(18), new Money(118), new Money(100)));

        // Without input credit the booked cost already includes the tax, which is taken out of it.
        PurchaseUnitCost composition = LandedCost.FromBooked(new Money(105), new Percent(5), taxIsCost: true);
        composition.ShouldBe(new PurchaseUnitCost(new Money(100), new Money(5), new Money(105), new Money(105)));
        PurchaseUnitCost uneven = LandedCost.FromBooked(new Money(85.625m), new Percent(5), taxIsCost: true);
        uneven.Booked.ShouldBe(new Money(85.625m));
        (uneven.BeforeTax + uneven.Tax).ShouldBe(uneven.InclTax);
    }
}
