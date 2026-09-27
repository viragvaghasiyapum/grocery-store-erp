namespace GroceryErp.Core.Numerics;

public sealed record PurchaseCostLine(Quantity Quantity, Quantity FreeQuantity, Money Rate, Money Discount, Money TaxAmount);

public static class LandedCost
{
    public static IReadOnlyList<Money> Compute(
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
            Money total = bases[index] + shares[index] + (taxIsCost ? line.TaxAmount : Money.Zero);
            return total.DivideBy(line.Quantity + line.FreeQuantity);
        }).ToArray();
    }
}
