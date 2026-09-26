using System.Data;
using System.Globalization;
using Dapper;
using GroceryErp.Core.Numerics;

namespace GroceryErp.Data.Infrastructure;

public static class DapperSetup
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        SqlMapper.AddTypeHandler(new MoneyHandler());
        SqlMapper.AddTypeHandler(new QuantityHandler());
        SqlMapper.AddTypeHandler(new PercentHandler());
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
    }
}

public sealed class MoneyHandler : SqlMapper.TypeHandler<Money>
{
    public override void SetValue(IDbDataParameter parameter, Money value) { parameter.DbType = DbType.Int64; parameter.Value = value.Scaled; }
    public override Money Parse(object value) => Money.FromScaled(Convert.ToInt64(value, CultureInfo.InvariantCulture));
}

public sealed class QuantityHandler : SqlMapper.TypeHandler<Quantity>
{
    public override void SetValue(IDbDataParameter parameter, Quantity value) { parameter.DbType = DbType.Int64; parameter.Value = value.Scaled; }
    public override Quantity Parse(object value) => Quantity.FromScaled(Convert.ToInt64(value, CultureInfo.InvariantCulture));
}

public sealed class PercentHandler : SqlMapper.TypeHandler<Percent>
{
    public override void SetValue(IDbDataParameter parameter, Percent value) { parameter.DbType = DbType.Int64; parameter.Value = value.Scaled; }
    public override Percent Parse(object value) => Percent.FromScaled(Convert.ToInt64(value, CultureInfo.InvariantCulture));
}

public sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    public override DateOnly Parse(object value) => DateOnly.ParseExact((string)value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
