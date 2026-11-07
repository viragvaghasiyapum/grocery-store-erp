using System.Globalization;
using System.Windows.Data;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Shows a money-out payment mode code (CASH, UPI, BANK, CARD, OTHER) in the UI language.</summary>
public sealed class MoneyModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string code ? Text(code) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static string Text(string? code) => code switch
    {
        null or "" => string.Empty,
        "UPI" => "UPI",
        "CASH" => TranslationSource.Instance["MoneyOut_ModeCash"],
        "BANK" => TranslationSource.Instance["MoneyOut_ModeBank"],
        "CARD" => TranslationSource.Instance["MoneyOut_ModeCard"],
        "OTHER" => TranslationSource.Instance["MoneyOut_ReasonOther"],
        _ => code,
    };
}

/// <summary>Shows a salary payment's type code (SALARY, BONUS, OVERTIME, OTHER) in the UI language.</summary>
public sealed class SalaryKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            "SALARY" => TranslationSource.Instance["MoneyOut_Salary"],
            "BONUS" => TranslationSource.Instance["MoneyOut_Bonus"],
            "OVERTIME" => TranslationSource.Instance["MoneyOut_Overtime"],
            "OTHER" => TranslationSource.Instance["MoneyOut_ReasonOther"],
            string code => code,
            _ => string.Empty,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows a vendor ledger entry's kind (PURCHASE, PAYMENT, ...) in the UI language.</summary>
public sealed class VendorLedgerKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string code ? TranslationSource.Instance[$"Purchasing_LedgerKind_{code}"] : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
