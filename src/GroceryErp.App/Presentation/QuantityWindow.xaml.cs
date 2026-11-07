using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class QuantityWindow : Window
{
    private readonly bool _allowZero;

    public QuantityWindow(Quantity quantity, bool allowZero = false, string? titleKey = null)
    {
        InitializeComponent();
        _allowZero = allowZero;
        if (titleKey is not null) Title = TranslationSource.Instance[titleKey];
        Quantity = quantity;
        ValueBox.Text = quantity.Value.ToString("0.###", CultureInfo.GetCultureInfo("en-IN"));
        Loaded += (_, _) => { ValueBox.SelectAll(); ValueBox.Focus(); };
    }

    /// <summary>The same box for Change price: a line's price, tax included, with its own help.</summary>
    public static QuantityWindow ForPrice(Money price)
    {
        var window = new QuantityWindow(new Quantity(price.Value), titleKey: "Sales_PriceOverride");
        AutomationProperties.SetName(window.ValueBox, TranslationSource.Instance["Pos_Price"]);
        ScreenHelp.SetTopic(window, "price-change");
        return window;
    }

    public Quantity Quantity { get; private set; }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(InputNormalizer.Normalize(ValueBox.Text), NumberStyles.Number,
                CultureInfo.GetCultureInfo("en-IN"), out decimal value) || value < 0) return;
        Quantity parsed = new(value);
        if (!_allowZero && parsed <= Quantity.Zero) return;
        Quantity = parsed;
        DialogResult = true;
    }
}
