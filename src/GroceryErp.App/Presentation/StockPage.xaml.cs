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
    private readonly Stack<(string? Name, long? Id)> _overviewPrevious = new();
    private readonly Stack<long?> _lotPrevious = new();
    private readonly Stack<long?> _ledgerPrevious = new();
    private (string? Name, long? Id) _overviewCursor;
    private long? _lotCursor;
    private long? _ledgerCursor;
    private StockOverviewPage? _overviewPage;
    private LotPage? _lotPage;
    private LedgerPage? _ledgerPage;
    private bool _overviewLoading;
    private bool _lotLoading;
    private bool _ledgerLoading;

    public StockPage() : this(null) { }

    public StockPage(InventoryService? inventory, bool loadOnShow = true)
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
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void OpeningTemplate_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = "opening-stock-template.xlsx" };
        if (picker.ShowDialog() != true) return;
        try { OpeningStockExcelImporter.WriteTemplate(picker.FileName); StatusText.Text = TranslationSource.Instance["Stock_TemplateSaved"]; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
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
            var picker = new DraftCountPickerWindow(_inventory) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() != true || picker.SelectedCountId is not long countId) return;
            var window = new StockCountWindow(_inventory, countId) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true) await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
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
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void ToggleLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot) return;
        try { await _inventory.SetLotActiveAsync(lot.Id, !lot.IsActive); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void OverviewGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ReloadLedgerAsync();
    }

    private async void ApplyLedgerFilter_OnClick(object sender, RoutedEventArgs e) => await ReloadLedgerAsync();

    private async Task ReloadLedgerAsync()
    {
        if (OverviewGrid.SelectedItem is not StockOverviewItem product) return;
        _ledgerPrevious.Clear();
        await LoadLedgerPageAsync(product, null);
    }

    private async Task LoadLedgerPageAsync(StockOverviewItem product, long? beforeId)
    {
        if (_ledgerLoading) return;
        _ledgerLoading = true;
        try
        {
            LedgerProductText.Text = product.Name;
            DateOnly from = DateOnly.FromDateTime(LedgerFromBox.SelectedDate ?? new DateTime(2000, 1, 1));
            DateOnly to = DateOnly.FromDateTime(LedgerToBox.SelectedDate ?? new DateTime(2100, 12, 31));
            string? previous = (LedgerTypeBox.SelectedItem as LedgerTypeOption)?.Code;
            if (LedgerTypeBox.ItemsSource is null)
            {
                var options = new List<LedgerTypeOption> { new(TranslationSource.Instance["Stock_AllMovements"], null) };
                options.AddRange((await _inventory.GetMovementTypesAsync()).Select(static code => new LedgerTypeOption(code, code)));
                LedgerTypeBox.ItemsSource = options;
                LedgerTypeBox.SelectedItem = options.FirstOrDefault(option => option.Code == previous) ?? options[0];
            }
            LedgerPage page = await _inventory.GetLedgerPageAsync(product.ProductId, from, to, beforeId,
                (LedgerTypeBox.SelectedItem as LedgerTypeOption)?.Code);
            _ledgerPage = page;
            _ledgerCursor = beforeId;
            LedgerGrid.ItemsSource = page.Items;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _ledgerLoading = false;
            PreviousLedgerButton.IsEnabled = _ledgerPrevious.Count > 0;
            NextLedgerButton.IsEnabled = _ledgerPage?.HasMore == true;
        }
    }

    private async void NextLedger_OnClick(object sender, RoutedEventArgs e)
    {
        if (_ledgerLoading || OverviewGrid.SelectedItem is not StockOverviewItem product ||
            _ledgerPage is not { HasMore: true, NextId: not null } page) return;
        long? previous = _ledgerCursor;
        await LoadLedgerPageAsync(product, page.NextId);
        if (_ledgerCursor == page.NextId) _ledgerPrevious.Push(previous);
        PreviousLedgerButton.IsEnabled = _ledgerPrevious.Count > 0;
    }

    private async void PreviousLedger_OnClick(object sender, RoutedEventArgs e)
    {
        if (_ledgerLoading || _ledgerPrevious.Count == 0 || OverviewGrid.SelectedItem is not StockOverviewItem product) return;
        long? previous = _ledgerPrevious.Peek();
        await LoadLedgerPageAsync(product, previous);
        if (_ledgerCursor == previous) _ledgerPrevious.Pop();
        PreviousLedgerButton.IsEnabled = _ledgerPrevious.Count > 0;
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
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private sealed record LedgerTypeOption(string Label, string? Code)
    {
        public override string ToString() => Label;
    }

    private async Task RefreshAsync()
    {
        using var timing = UiRefreshProbe.Track("stock", this);
        try
        {
            _overviewPrevious.Clear();
            _lotPrevious.Clear();
            await LoadOverviewPageAsync(null, null);
            await LoadLotPageAsync(null);
            var valuation = await _inventory.GetValuationAsync();
            timing.Bind(() => ValuationGrid.ItemsSource = valuation);
            StatusText.Text = $"{OverviewGrid.Items.Count} / {LotsGrid.Items.Count}";
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async Task LoadOverviewPageAsync(string? name, long? id)
    {
        if (_overviewLoading) return;
        _overviewLoading = true;
        try
        {
            StockOverviewPage page = await _inventory.GetOverviewPageAsync(OverviewSearchBox.Text, name, id);
            _overviewPage = page;
            _overviewCursor = (name, id);
            UiRefreshProbe.RecordBinding("stock", () => OverviewGrid.ItemsSource = page.Items);
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _overviewLoading = false;
            PreviousOverviewButton.IsEnabled = _overviewPrevious.Count > 0;
            NextOverviewButton.IsEnabled = _overviewPage?.HasMore == true;
        }
    }

    private async Task LoadLotPageAsync(long? beforeId)
    {
        if (_lotLoading) return;
        _lotLoading = true;
        try
        {
            LotPage page = await _inventory.GetLotPageAsync(query: LotSearchBox.Text, beforeId: beforeId);
            _lotPage = page;
            _lotCursor = beforeId;
            UiRefreshProbe.RecordBinding("stock", () => LotsGrid.ItemsSource = page.Items);
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _lotLoading = false;
            PreviousLotButton.IsEnabled = _lotPrevious.Count > 0;
            NextLotButton.IsEnabled = _lotPage?.HasMore == true;
        }
    }

    private async void OverviewSearch_OnClick(object sender, RoutedEventArgs e)
    {
        _overviewPrevious.Clear();
        await LoadOverviewPageAsync(null, null);
    }

    private async void LotSearch_OnClick(object sender, RoutedEventArgs e)
    {
        _lotPrevious.Clear();
        await LoadLotPageAsync(null);
    }

    private async void NextOverview_OnClick(object sender, RoutedEventArgs e)
    {
        if (_overviewLoading || _overviewPage is not { HasMore: true, NextName: not null, NextId: not null } page) return;
        (string? name, long? id) old = _overviewCursor;
        await LoadOverviewPageAsync(page.NextName, page.NextId);
        if (_overviewCursor == (page.NextName, page.NextId)) _overviewPrevious.Push(old);
        PreviousOverviewButton.IsEnabled = _overviewPrevious.Count > 0;
    }

    private async void PreviousOverview_OnClick(object sender, RoutedEventArgs e)
    {
        if (_overviewLoading || _overviewPrevious.Count == 0) return;
        (string? name, long? id) previous = _overviewPrevious.Peek();
        await LoadOverviewPageAsync(previous.name, previous.id);
        if (_overviewCursor == previous) _overviewPrevious.Pop();
        PreviousOverviewButton.IsEnabled = _overviewPrevious.Count > 0;
    }

    private async void NextLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (_lotLoading || _lotPage is not { HasMore: true, NextId: not null } page) return;
        long? old = _lotCursor;
        await LoadLotPageAsync(page.NextId);
        if (_lotCursor == page.NextId) _lotPrevious.Push(old);
        PreviousLotButton.IsEnabled = _lotPrevious.Count > 0;
    }

    private async void PreviousLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (_lotLoading || _lotPrevious.Count == 0) return;
        long? previous = _lotPrevious.Peek();
        await LoadLotPageAsync(previous);
        if (_lotCursor == previous) _lotPrevious.Pop();
        PreviousLotButton.IsEnabled = _lotPrevious.Count > 0;
    }
}
