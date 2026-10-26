using System.Globalization;

namespace GroceryErp.Core.Numerics;

public static class Fx3
{
    public const int Decimals = 3;
    public const long Factor = 1000;

    public static decimal Round(decimal value) =>
        decimal.Round(value, Decimals, MidpointRounding.AwayFromZero);

    public static long ToScaled(decimal value) => decimal.ToInt64(Round(value) * Factor);

    public static decimal FromScaled(long scaled) => scaled / (decimal)Factor;
}

/// <summary>
/// Screens show money with two decimals while every saved value keeps three. In a row that reads
/// A + B = C, B is shown as the shown C minus the shown A, so the row adds up on screen.
/// </summary>
public static class TwoDecimals
{
    public static decimal Round(Money value) => decimal.Round(value.Value, 2, MidpointRounding.AwayFromZero);

    /// <summary>The part shown between a shown whole and its other shown part.</summary>
    public static decimal Remainder(Money whole, Money other) => Round(whole) - Round(other);
}

public readonly record struct Money : IComparable<Money>
{
    public Money(decimal value) => Value = Fx3.Round(value);
    public decimal Value { get; }
    public long Scaled => Fx3.ToScaled(Value);
    public static Money Zero => default;
    public static Money FromScaled(long scaled) => new(Fx3.FromScaled(scaled));
    public static Money operator +(Money a, Money b) => new(a.Value + b.Value);
    public static Money operator -(Money a, Money b) => new(a.Value - b.Value);
    public static Money operator -(Money value) => new(-value.Value);
    public static Money operator *(Money price, Quantity quantity) => new(price.Value * quantity.Value);
    public static Money operator *(Quantity quantity, Money price) => price * quantity;
    public static bool operator <(Money a, Money b) => a.Value < b.Value;
    public static bool operator >(Money a, Money b) => a.Value > b.Value;
    public static bool operator <=(Money a, Money b) => a.Value <= b.Value;
    public static bool operator >=(Money a, Money b) => a.Value >= b.Value;
    public Money PercentOf(Percent percent) => new(Value * percent.Value / 100m);
    public Money DivideBy(Quantity quantity) => quantity.Value == 0
        ? throw new DivideByZeroException()
        : new(Value / quantity.Value);
    public int CompareTo(Money other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("0.000", CultureInfo.InvariantCulture);
}

public readonly record struct Quantity : IComparable<Quantity>
{
    public Quantity(decimal value) => Value = Fx3.Round(value);
    public decimal Value { get; }
    public long Scaled => Fx3.ToScaled(Value);
    public bool IsWhole => Value == decimal.Truncate(Value);
    public static Quantity Zero => default;
    public static Quantity One => new(1m);
    public static Quantity FromScaled(long scaled) => new(Fx3.FromScaled(scaled));
    public static Quantity operator +(Quantity a, Quantity b) => new(a.Value + b.Value);
    public static Quantity operator -(Quantity a, Quantity b) => new(a.Value - b.Value);
    public static bool operator <(Quantity a, Quantity b) => a.Value < b.Value;
    public static bool operator >(Quantity a, Quantity b) => a.Value > b.Value;
    public static bool operator <=(Quantity a, Quantity b) => a.Value <= b.Value;
    public static bool operator >=(Quantity a, Quantity b) => a.Value >= b.Value;
    public int CompareTo(Quantity other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("0.000", CultureInfo.InvariantCulture);
}

public readonly record struct Percent : IComparable<Percent>
{
    public Percent(decimal value) => Value = Fx3.Round(value);
    public decimal Value { get; }
    public long Scaled => Fx3.ToScaled(Value);
    public static Percent Zero => default;
    public static Percent FromScaled(long scaled) => new(Fx3.FromScaled(scaled));
    public static bool operator <(Percent a, Percent b) => a.Value < b.Value;
    public static bool operator >(Percent a, Percent b) => a.Value > b.Value;
    public static bool operator <=(Percent a, Percent b) => a.Value <= b.Value;
    public static bool operator >=(Percent a, Percent b) => a.Value >= b.Value;
    public int CompareTo(Percent other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("0.000", CultureInfo.InvariantCulture);
}
