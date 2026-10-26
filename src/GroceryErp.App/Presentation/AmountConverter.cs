using System.Globalization;
using System.Windows.Data;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Shows money with two decimals and English digits; a missing value shows "—". Saved values keep
/// three decimals. A column that completes A + B = C binds the model's already-rounded remainder.
/// </summary>
public sealed class AmountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Money money => Text(TwoDecimals.Round(money)),
        decimal amount => Text(amount),
        _ => "—",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static string Text(decimal amount) => amount.ToString("N2", TranslationSource.FormatCulture);
}
