using GroceryErp.Core.Errors;

namespace GroceryErp.Core.Numerics;

public sealed record CartLineInput(
    long ProductId,
    long LotId,
    Quantity Quantity,
    Money UnitPrice,
    Money? Mrp,
    Percent TaxRate,
    bool PriceIncludesTax,
    Money LineDiscount,
    Money UnitCost);

public sealed record PricedLine(
    CartLineInput Input,
    Money Gross,
    Money Discount,
    Money Taxable,
    Money Tax,
    Money Total,
    Money Cost);

public sealed record PricedBill(
    IReadOnlyList<PricedLine> Lines,
    Money Gross,
    Money Discount,
    Money Taxable,
    Money Tax,
    Money RoundOff,
    Money GrandTotal,
    Money Cost,
    Money MrpSavings)
{
    public static PricedBill Empty { get; } = new(
        [], Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero);
}

public static class PricingEngine
{
    public static PricedBill Price(IReadOnlyList<CartLineInput> lines, Money billDiscount, Money roundStep)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (billDiscount < Money.Zero) throw new DomainException(ErrorCode.Validation, nameof(billDiscount));
        if (lines.Count == 0)
        {
            if (billDiscount > Money.Zero) throw new DomainException(ErrorCode.DiscountExceedsLine);
            return PricedBill.Empty;
        }

        var gross = lines.Select(static line => line.UnitPrice * line.Quantity).ToArray();
        var afterLineDiscount = lines.Select((line, index) => gross[index] - line.LineDiscount).ToArray();
        if (lines.Any(static line => line.Quantity <= Quantity.Zero || line.UnitPrice < Money.Zero ||
                                     line.LineDiscount < Money.Zero || line.UnitCost < Money.Zero) ||
            afterLineDiscount.Any(static value => value < Money.Zero))
            throw new DomainException(ErrorCode.DiscountExceedsLine);

        Money subtotal = Sum(afterLineDiscount);
        if (billDiscount > subtotal) throw new DomainException(ErrorCode.DiscountExceedsLine);
        IReadOnlyList<Money> billShares = MoneyMath.Allocate(billDiscount, afterLineDiscount);

        var priced = new List<PricedLine>(lines.Count);
        for (int index = 0; index < lines.Count; index++)
        {
            CartLineInput line = lines[index];
            Money net = afterLineDiscount[index] - billShares[index];
            (Money taxable, Money tax) = line.PriceIncludesTax
                ? Tax.FromInclusive(net, line.TaxRate)
                : Tax.FromExclusive(net, line.TaxRate);
            priced.Add(new(line, gross[index], line.LineDiscount + billShares[index], taxable, tax,
                taxable + tax, line.UnitCost * line.Quantity));
        }

        Money total = Sum(priced.Select(static line => line.Total));
        Money roundOff = MoneyMath.RoundOff(total, roundStep);
        Money atMrp = Sum(priced.Select(static line => line.Input.Mrp is { } mrp
            ? mrp * line.Input.Quantity
            : line.Gross));
        Money savings = atMrp - (total + roundOff);
        return new(priced, Sum(gross), Sum(priced.Select(static line => line.Discount)),
            Sum(priced.Select(static line => line.Taxable)), Sum(priced.Select(static line => line.Tax)),
            roundOff, total + roundOff, Sum(priced.Select(static line => line.Cost)),
            savings > Money.Zero ? savings : Money.Zero);
    }

    private static Money Sum(IEnumerable<Money> values) =>
        values.Aggregate(Money.Zero, static (total, value) => total + value);
}
