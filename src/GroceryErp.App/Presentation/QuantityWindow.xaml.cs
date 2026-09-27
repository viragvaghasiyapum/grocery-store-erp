using System.Globalization;
using System.Windows;
using GroceryErp.Core.Numerics;

namespace GroceryErp.App.Presentation;

public partial class QuantityWindow : Window
{
    public QuantityWindow(Quantity quantity)
    {
        InitializeComponent();
        Quantity = quantity;
        ValueBox.Text = quantity.Value.ToString("0.###", CultureInfo.GetCultureInfo("en-IN"));
        Loaded += (_, _) => { ValueBox.SelectAll(); ValueBox.Focus(); };
    }

    public Quantity Quantity { get; private set; }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(ValueBox.Text, NumberStyles.Number, CultureInfo.GetCultureInfo("en-IN"),
                out decimal value) || value <= 0) return;
        Quantity = new Quantity(value);
        DialogResult = true;
    }
}
