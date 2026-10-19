using System.Globalization;
using System.Windows.Data;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public sealed class PurchaseLineKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        TranslationSource.Instance[(value as string) switch
        {
            "INTERNAL" => "Purchasing_TypeInternal",
            "EXPENSE" => "Purchasing_TypeExpense",
            _ => "Purchasing_TypeProduct",
        }];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
