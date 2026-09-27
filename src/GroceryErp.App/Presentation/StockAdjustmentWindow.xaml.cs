using System.Globalization;
using System.Windows;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class StockAdjustmentWindow : Window
{
    private readonly InventoryService _inventory;
    private readonly LotItem _lot;
    public StockAdjustmentWindow(InventoryService inventory, LotItem lot)
    {
        InitializeComponent();
        _inventory = inventory;
        _lot = lot;
        LotText.Text = $"{lot.ProductName} — {lot.LotCode} ({lot.OnHand})";
    }
    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DateOnly date = ((App)Application.Current).Services.GetRequiredService<IClock>().BusinessDate;
            await _inventory.AdjustAsync(_lot.Id, date,
                new Quantity(decimal.Parse(QuantityBox.Text, CultureInfo.InvariantCulture)), ReasonBox.Text, OperationGuard.NewId());
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
