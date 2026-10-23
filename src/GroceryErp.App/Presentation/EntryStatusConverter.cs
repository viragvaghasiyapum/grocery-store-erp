using System.Globalization;
using System.Windows.Data;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

// ACTIVE, CORRECTED, VOID and PURCHASE money-out codes as localized text.
public sealed class EntryStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string code ? TranslationSource.Instance[$"MoneyOut_Status_{code}"] : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EmployeeStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        TranslationSource.Instance[value is true ? "MoneyOut_EmployeeWorking" : "MoneyOut_EmployeeRelieved"];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
