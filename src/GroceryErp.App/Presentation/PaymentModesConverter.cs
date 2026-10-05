using System.Globalization;
using System.Windows.Data;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public sealed class PaymentModesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string modes) return string.Empty;
        return string.Join(", ", modes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(mode =>
            TranslationSource.Instance[mode switch
            {
                "CASH" => "Receipt_RefundModeCash",
                "UPI" => "Receipt_RefundModeUpi",
                "CARD" => "Receipt_RefundModeCard",
                _ => "Receipt_RefundModeOther",
            }]));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
