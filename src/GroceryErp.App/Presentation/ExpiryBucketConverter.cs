using System.Globalization;
using System.Windows.Data;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public sealed class ExpiryBucketConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string code ? TranslationSource.Instance[$"Stock_Expiry_{code}"] : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
