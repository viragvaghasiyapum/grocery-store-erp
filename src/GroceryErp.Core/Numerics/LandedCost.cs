namespace GroceryErp.Core.Numerics;

public sealed record PurchaseCostLine(Quantity Quantity, Quantity FreeQuantity, Money Rate, Money Discount, Money TaxAmount);

/// <summary>
/// One piece's purchase price before tax, its tax and its price including tax, plus the cost the
/// books carry: before tax in Regular mode (the tax is input credit), including tax otherwise.
/// </summary>
public sealed record PurchaseUnitCost(Money BeforeTax, Money Tax, Money InclTax, Money Booked);

public static class LandedCost
{
    // Tax is charged on the goods only; other charges are spread by goods value and never taxed.
    // Tax per piece is the difference of the two saved prices, so each row adds up exactly.
    public static IReadOnlyList<PurchaseUnitCost> Compute(
        IReadOnlyList<PurchaseCostLine> lines,
        Money otherCharges,
        bool taxIsCost)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return [];
        ArgumentOutOfRangeException.ThrowIfLessThan(otherCharges, Money.Zero);

        var bases = new List<Money>(lines.Count);
        foreach (PurchaseCostLine line in lines)
        {
            if (line.Quantity <= Quantity.Zero || line.FreeQuantity < Quantity.Zero || line.Rate < Money.Zero ||
                line.Discount < Money.Zero || line.TaxAmount < Money.Zero)
                throw new ArgumentOutOfRangeException(nameof(lines));
            Money basis = line.Rate * line.Quantity - line.Discount;
            if (basis < Money.Zero) throw new ArgumentOutOfRangeException(nameof(lines));
            bases.Add(basis);
        }

        IReadOnlyList<Money> shares = MoneyMath.Allocate(otherCharges, bases);
        return lines.Select((line, index) =>
        {
            Quantity units = line.Quantity + line.FreeQuantity;
            Money beforeTax = (bases[index] + shares[index]).DivideBy(units);
            Money inclTax = (bases[index] + shares[index] + line.TaxAmount).DivideBy(units);
            return new PurchaseUnitCost(beforeTax, inclTax - beforeTax, inclTax, taxIsCost ? inclTax : beforeTax);
        }).ToArray();
    }

    /// <summary>Derives a lot's values from a before-tax purchase price, as typed for a direct lot.</summary>
    public static PurchaseUnitCost FromBeforeTax(Money beforeTax, Percent rate, bool taxIsCost)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(beforeTax, Money.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(rate, Percent.Zero);
        Money inclTax = beforeTax + beforeTax.PercentOf(rate);
        return new PurchaseUnitCost(beforeTax, inclTax - beforeTax, inclTax, taxIsCost ? inclTax : beforeTax);
    }

    /// <summary>
    /// Derives a lot's values from a cost the books already carry, such as a recreated lot's saved
    /// cost, so <see cref="PurchaseUnitCost.Booked"/> is that cost exactly.
    /// </summary>
    public static PurchaseUnitCost FromBooked(Money booked, Percent rate, bool taxIsCost)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(booked, Money.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(rate, Percent.Zero);
        if (!taxIsCost) return FromBeforeTax(booked, rate, taxIsCost: false);
        Money beforeTax = Tax.FromInclusive(booked, rate).Taxable;
        return new PurchaseUnitCost(beforeTax, booked - beforeTax, booked, booked);
    }
}
