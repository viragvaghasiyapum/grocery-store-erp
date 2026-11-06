namespace GroceryErp.Core.Numerics;

public static class MoneyMath
{
    public static IReadOnlyList<Money> Allocate(Money total, IReadOnlyList<Money> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentOutOfRangeException.ThrowIfLessThan(total, Money.Zero);
        if (weights.Any(static weight => weight < Money.Zero))
            throw new ArgumentOutOfRangeException(nameof(weights));

        long totalScaled = total.Scaled;
        long weightSum = weights.Sum(static weight => weight.Scaled);
        long[] shares = new long[weights.Count];
        if (weightSum == 0 || totalScaled == 0)
            return shares.Select(Money.FromScaled).ToArray();

        var remainders = new (int Index, decimal Value)[weights.Count];
        long allocated = 0;
        for (int index = 0; index < weights.Count; index++)
        {
            decimal exact = (decimal)totalScaled * weights[index].Scaled / weightSum;
            long floor = decimal.ToInt64(decimal.Floor(exact));
            shares[index] = floor;
            remainders[index] = (index, exact - floor);
            allocated += floor;
        }

        foreach ((int index, _) in remainders.OrderByDescending(static item => item.Value)
                     .ThenBy(static item => item.Index).Take(checked((int)(totalScaled - allocated))))
            shares[index]++;

        return shares.Select(Money.FromScaled).ToArray();
    }

    public static Money RoundOff(Money total, Money step)
    {
        if (step <= Money.Zero) return Money.Zero;
        decimal payable = decimal.Round(total.Value / step.Value, 0, MidpointRounding.AwayFromZero) * step.Value;
        return new Money(payable - total.Value);
    }
}

public static class Tax
{
    public static (Money Taxable, Money Tax) FromInclusive(Money gross, Percent rate)
    {
        if (rate.Value <= -100m) throw new ArgumentOutOfRangeException(nameof(rate));
        var taxable = new Money(gross.Value * 100m / (100m + rate.Value));
        return (taxable, gross - taxable);
    }

    public static (Money Taxable, Money Tax) FromExclusive(Money taxable, Percent rate) =>
        (taxable, taxable.PercentOf(rate));
}
