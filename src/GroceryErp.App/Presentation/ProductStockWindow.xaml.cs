using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class ProductStockWindow : Window
{
    private readonly InventoryService _inventory;
    private readonly long _productId;

    public ProductStockWindow(InventoryService inventory, ProductListItem product, bool showLedger, bool loadOnShow = true)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(product);
        _inventory = inventory;
        _productId = product.Id;
        InitializeComponent();
        ProductNameText.Text = product.Name;
        SkuText.Text = product.Sku;
        StockTabs.SelectedIndex = showLedger ? 1 : 0;
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            LotsGrid.ItemsSource = await _inventory.GetLotsAsync(_productId);
            LedgerGrid.ItemsSource = await _inventory.GetLedgerAsync(_productId, DateOnly.MinValue, DateOnly.MaxValue);
            StatusText.Text = string.Empty;
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private void LotsGrid_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PrintLotButton is not null)
            PrintLotButton.IsEnabled = LotsGrid.SelectedItem is LotItem { IsActive: true } lot && lot.OnHand.Value > 0;
    }

    private void PrintLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot) return;
        IServiceProvider services = ((App)Application.Current).Services;
        var dialog = new LabelBatchWindow(services.GetRequiredService<CatalogService>(), _inventory,
            services.GetRequiredService<ISettings>(), lotIds: [lot.Id])
        { Owner = this };
        dialog.ShowDialog();
    }
}
