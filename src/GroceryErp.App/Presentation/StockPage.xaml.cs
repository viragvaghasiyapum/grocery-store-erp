using System.Windows;
using System.Windows.Controls;
using GroceryErp.Catalog;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public partial class StockPage : Page
{
    private readonly InventoryService _inventory;
    private IReadOnlyList<LedgerItem> _ledgerItems = [];

    public StockPage(InventoryService? inventory = null, bool loadOnShow = true)
    {
        InitializeComponent();
        _inventory = inventory ?? ((App)Application.Current).Services.GetRequiredService<InventoryService>();
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
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
            var review = new OpeningStockPreviewWindow(preview) { Owner = Window.GetWindow(this) };
            if (review.ShowDialog() != true) return;
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
        try
        {
            IReadOnlyList<StockCountSummary> drafts = await _inventory.GetDraftCountsAsync();
            StockCountSummary? draft = drafts.Count == 0 ? null : drafts[0];
            if (draft is null) { StatusText.Text = TranslationSource.Instance["Stock_NoDraftCount"]; return; }
            var window = new StockCountWindow(_inventory, draft.Id) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true) await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void NewCount_OnClick(object sender, RoutedEventArgs e)
    {
        IServiceProvider services = ((App)Application.Current).Services;
        var scope = new StockCountScopeWindow(services.GetRequiredService<CatalogService>())
        { Owner = Window.GetWindow(this) };
        if (scope.ShowDialog() != true) return;
        var window = new StockCountWindow(_inventory, categoryIds: scope.CategoryIds, notes: scope.Notes)
        { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private async void Price_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot) return;
        var window = new LotPriceWindow(_inventory, lot) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private void LotsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool selected = LotsGrid.SelectedItem is LotItem;
        if (AdjustButton is not null) AdjustButton.IsEnabled = selected;
        if (PriceButton is not null) PriceButton.IsEnabled = selected;
        if (ToggleLotButton is not null) ToggleLotButton.IsEnabled = selected;
        if (WriteOffButton is not null) WriteOffButton.IsEnabled =
            LotsGrid.SelectedItem is LotItem { ExpiryBucketCode: "EXPIRED", IsActive: true } lot && lot.OnHand > GroceryErp.Core.Numerics.Quantity.Zero;
    }

    private async void WriteOff_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem { ExpiryBucketCode: "EXPIRED", IsActive: true } lot) return;
        if (MessageBox.Show(TranslationSource.Instance.Format("Stock_WriteOffConfirm", lot.LotCode, lot.OnHand),
            TranslationSource.Instance["Stock_WriteOffExpired"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            IServiceProvider services = ((App)Application.Current).Services;
            await services.GetRequiredService<MoneyOutService>().WriteOffExpiredAsync([lot.Id],
                services.GetRequiredService<IClock>().BusinessDate, OperationGuard.NewId());
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void ToggleLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot) return;
        try { await _inventory.SetLotActiveAsync(lot.Id, !lot.IsActive); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void OverviewGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ReloadLedgerAsync();
    }

    private async void ApplyLedgerFilter_OnClick(object sender, RoutedEventArgs e) => await ReloadLedgerAsync();

    private async Task ReloadLedgerAsync()
    {
        if (OverviewGrid.SelectedItem is not StockOverviewItem product) return;
        try
        {
            LedgerProductText.Text = product.Name;
            DateOnly from = DateOnly.FromDateTime(LedgerFromBox.SelectedDate ?? new DateTime(2000, 1, 1));
            DateOnly to = DateOnly.FromDateTime(LedgerToBox.SelectedDate ?? new DateTime(2100, 12, 31));
            _ledgerItems = await _inventory.GetLedgerAsync(product.ProductId, from, to);
            string? previous = (LedgerTypeBox.SelectedItem as LedgerTypeOption)?.Code;
            var options = new List<LedgerTypeOption> { new(TranslationSource.Instance["Stock_AllMovements"], null) };
            options.AddRange(_ledgerItems.Select(static movement => movement.Type).Distinct().Order()
                .Select(static code => new LedgerTypeOption(code, code)));
            LedgerTypeBox.ItemsSource = options;
            LedgerTypeBox.SelectedItem = options.FirstOrDefault(option => option.Code == previous) ?? options[0];
            ApplyLedgerType();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private void ApplyLedgerType()
    {
        string? type = (LedgerTypeBox.SelectedItem as LedgerTypeOption)?.Code;
        LedgerGrid.ItemsSource = type is null ? _ledgerItems : _ledgerItems.Where(item => item.Type == type).ToList();
    }

    private void LedgerGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpenSourceButton is not null) OpenSourceButton.IsEnabled = LedgerGrid.SelectedItem is LedgerItem;
    }

    private async void OpenSource_OnClick(object sender, RoutedEventArgs e)
    {
        if (LedgerGrid.SelectedItem is not LedgerItem movement) return;
        try
        {
            StockSourceDocument source = await _inventory.GetSourceDocumentAsync(movement);
            new StockSourceWindow(source) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private sealed record LedgerTypeOption(string Label, string? Code)
    {
        public override string ToString() => Label;
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
