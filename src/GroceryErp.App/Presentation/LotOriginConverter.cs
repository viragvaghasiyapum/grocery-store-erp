using System.Globalization;
using System.Windows.Data;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Shows a lot's origin code (PURCHASE_LINE, DIRECT, CUSTOMER_RETURN) in the UI language.</summary>
public sealed class LotOriginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string code ? TranslationSource.Instance[$"Stock_Origin_{code}"] : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
