using System.Globalization;
using System.Windows.Data;
using GroceryErp.Core.Numerics;

namespace GroceryErp.App.Presentation;

/// <summary>Shows a depreciation method as its accounting abbreviation, WDV or SLM, as the asset timeline does.</summary>
public sealed class DepreciationMethodConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DepreciationMethod method ? method == DepreciationMethod.Wdv ? "WDV" : "SLM" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
