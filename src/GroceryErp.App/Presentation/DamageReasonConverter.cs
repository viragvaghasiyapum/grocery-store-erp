using System.Globalization;
using System.Windows.Data;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Shows a damage reason code (EXPIRED, BROKEN, ...) in the UI language.</summary>
public sealed class DamageReasonConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string code ? Text(code) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static string Text(string code) => TranslationSource.Instance[code switch
    {
        "EXPIRED" => "MoneyOut_ReasonExpired",
        "BROKEN" => "MoneyOut_ReasonBroken",
        "SPOILED" => "MoneyOut_ReasonSpoiled",
        "PEST" => "MoneyOut_ReasonPest",
        "LOST" => "MoneyOut_ReasonLost",
        "CUSTOMER_RETURN" => "MoneyOut_ReasonReturn",
        _ => "MoneyOut_ReasonOther",
    }];
}
