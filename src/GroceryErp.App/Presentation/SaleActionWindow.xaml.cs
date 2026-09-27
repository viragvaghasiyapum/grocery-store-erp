using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Numerics;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

public partial class SaleActionWindow : Window
{
    private readonly bool _isReturn;

    public SaleActionWindow(bool isReturn, IReadOnlyList<SaleLine>? lines = null)
    {
        InitializeComponent();
        _isReturn = isReturn;
        ReturnFields.Visibility = isReturn ? Visibility.Visible : Visibility.Collapsed;
        LineBox.ItemsSource = lines;
        LineBox.SelectedIndex = lines?.Count > 0 ? 0 : -1;
    }

    public string Reason => ReasonBox.Text.Trim();
    public SaleLine? SelectedLine => LineBox.SelectedItem as SaleLine;
    public Quantity Quantity { get; private set; }
    public bool Resellable => ResellableBox.IsChecked == true;

    private void LineBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedLine is { } line) QuantityBox.Text = line.Quantity.Value.ToString("0.###", CultureInfo.GetCultureInfo("en-IN"));
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (Reason.Length == 0) return;
        if (_isReturn && (!decimal.TryParse(QuantityBox.Text, NumberStyles.Number,
                CultureInfo.GetCultureInfo("en-IN"), out decimal value) || value <= 0 || SelectedLine is null)) return;
        if (_isReturn) Quantity = new Quantity(decimal.Parse(QuantityBox.Text, CultureInfo.GetCultureInfo("en-IN")));
        DialogResult = true;
    }
}
