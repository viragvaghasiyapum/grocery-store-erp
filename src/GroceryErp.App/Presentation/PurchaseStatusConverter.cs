using System.Globalization;
using System.Windows.Data;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Shows a purchase status code in the UI language; CANCELLED is shown as Void.</summary>
public sealed class PurchaseStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Text(value);

    public static string Text(object? value) => value switch
    {
        "DRAFT" => TranslationSource.Instance["Purchasing_StatusDraft"],
        "POSTED" => TranslationSource.Instance["Purchasing_StatusPosted"],
        "CANCELLED" => TranslationSource.Instance["Purchasing_StatusVoided"],
        string other => other,
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
