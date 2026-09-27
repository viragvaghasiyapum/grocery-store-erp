using System.Windows;
using System.Windows.Controls;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public partial class StockPage : Page
{
    private readonly InventoryService _inventory;

    public StockPage()
    {
        InitializeComponent();
        _inventory = ((App)Application.Current).Services.GetRequiredService<InventoryService>();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OpeningImport_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx" };
        if (picker.ShowDialog() != true) return;
        try
        {
            OpeningStockExcelImporter importer = ((App)Application.Current).Services.GetRequiredService<OpeningStockExcelImporter>();
            OpeningStockImportPreview preview = await importer.DryRunAsync(picker.FileName);
            if (preview.ErrorCount > 0)
            {
                StatusText.Text = $"{preview.ValidCount} valid; {preview.ErrorCount} errors";
                return;
            }
            await importer.ImportAsync(preview, OperationGuard.NewId());
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private void OpeningTemplate_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = "opening-stock-template.xlsx" };
        if (picker.ShowDialog() != true) return;
        try { OpeningStockExcelImporter.WriteTemplate(picker.FileName); StatusText.Text = TranslationSource.Instance["Stock_TemplateSaved"]; }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void Adjust_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot) return;
        var window = new StockAdjustmentWindow(_inventory, lot) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private async void Count_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new StockCountWindow(_inventory) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private async void Price_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot) return;
        var window = new LotPriceWindow(_inventory, lot) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private async void ToggleLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot) return;
        try { await _inventory.SetLotActiveAsync(lot.Id, !lot.IsActive); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void OverviewGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverviewGrid.SelectedItem is not StockOverviewItem product) return;
        try { LedgerGrid.ItemsSource = await _inventory.GetLedgerAsync(product.ProductId, new DateOnly(2000, 1, 1), new DateOnly(2100, 12, 31)); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async Task RefreshAsync()
    {
        try
        {
            OverviewGrid.ItemsSource = await _inventory.GetOverviewAsync();
            LotsGrid.ItemsSource = await _inventory.GetLotsAsync();
            ValuationGrid.ItemsSource = await _inventory.GetValuationAsync();
            StatusText.Text = $"{OverviewGrid.Items.Count} / {LotsGrid.Items.Count}";
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }
}
