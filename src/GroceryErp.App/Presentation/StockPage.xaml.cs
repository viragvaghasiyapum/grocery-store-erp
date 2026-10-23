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
    private int PageSize => int.TryParse((PageSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int size) ? size : 50;
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
    private readonly Dictionary<long, LotItem> _selectedLots = new();
    private CheckBox? _lotPageSelectionCheck;
    private bool _lotCheckboxSelectionUsed;

    public StockPage() : this(null) { }

    public StockPage(InventoryService? inventory, bool loadOnShow = true)
    {
        InitializeComponent();
        _inventory = inventory ?? ((App)Application.Current).Services.GetRequiredService<InventoryService>();
        UpdateLotSelectionActions();
        Loaded += (_, _) => TranslationSource.Instance.PropertyChanged += TranslationChanged;
        Unloaded += (_, _) => TranslationSource.Instance.PropertyChanged -= TranslationChanged;
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
    }

    private void TranslationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]" && IsLoaded) UpdateLotSelectionActions();
    }

    private void StockTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, StockTabs)) UpdatePagerVisibility();
    }

    private void UpdatePagerVisibility()
    {
        if (OverviewPager is null) return;
        OverviewPager.Visibility = StockTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        LotsPager.Visibility = StockTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        LedgerPager.Visibility = StockTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageSizePanel.Visibility = StockTabs.SelectedIndex == 3 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        switch (StockTabs.SelectedIndex)
        {
            case 0:
                _overviewPrevious.Clear();
                await LoadOverviewPageAsync(null, null);
                break;
            case 1:
                _lotPrevious.Clear();
                ClearLotSelection();
                await LoadLotPageAsync(null);
                break;
            case 2:
                await ReloadLedgerAsync();
                break;
            case 3:
                try { ValuationGrid.ItemsSource = await _inventory.GetValuationAsync(); }
                catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
                break;
        }
    }

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
            OpeningStockLine[] selectedLines = preview.Rows.Where(static row => row.IsValid)
                .Select(static row => row.Line!).ToArray();
            await importer.ImportAsync(preview, ImportJobs.OperationIdFor("OPENING_STOCK", selectedLines));
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
        if (SelectedSingleLot() is not LotItem lot) return;
        var window = new StockAdjustmentWindow(_inventory, lot) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private async void Price_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSingleLot() is not LotItem lot) return;
        var window = new LotPriceWindow(_inventory, lot) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private void LotsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedLots.Count == 0 && e.AddedItems.Count > 0) _lotCheckboxSelectionUsed = false;
        UpdateLotSelectionActions();
    }

    private void LotCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: LotItem lot } box)
            box.IsChecked = _selectedLots.ContainsKey(lot.Id);
    }

    private void LotCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: LotItem lot } box) return;
        _lotCheckboxSelectionUsed = true;
        if (box.IsChecked == true) _selectedLots[lot.Id] = lot;
        else _selectedLots.Remove(lot.Id);
        UpdateLotSelectionActions();
    }

    private void LotPageCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        _lotPageSelectionCheck = sender as CheckBox;
        UpdateLotPageSelectionCheck();
    }

    private void LotPageCheck_OnClick(object sender, RoutedEventArgs e)
    {
        LotItem[] pageItems = LotsGrid.Items.OfType<LotItem>().ToArray();
        if (pageItems.Length == 0) return;
        bool allSelected = pageItems.All(item => _selectedLots.ContainsKey(item.Id));
        _lotCheckboxSelectionUsed = true;
        foreach (LotItem item in pageItems)
        {
            if (allSelected) _selectedLots.Remove(item.Id);
            else _selectedLots[item.Id] = item;
        }
        if (allSelected) LotsGrid.UnselectAll();
        LotsGrid.Items.Refresh();
        UpdateLotSelectionActions();
    }

    private LotItem? SelectedSingleLot() => _selectedLots.Count switch
    {
        > 1 => null,
        1 => _selectedLots.Values.Single(),
        _ => _lotCheckboxSelectionUsed ? null : LotsGrid.SelectedItem as LotItem,
    };

    private void UpdateLotPageSelectionCheck()
    {
        if (_lotPageSelectionCheck is null) return;
        LotItem[] pageItems = LotsGrid.Items.OfType<LotItem>().ToArray();
        int selected = pageItems.Count(item => _selectedLots.ContainsKey(item.Id));
        _lotPageSelectionCheck.IsChecked = selected == 0 ? false : selected == pageItems.Length ? true : null;
    }

    private void UpdateLotSelectionActions()
    {
        if (AdjustButton is null) return;
        LotItem? lot = SelectedSingleLot();
        AdjustButton.IsEnabled = lot is not null;
        PriceButton.IsEnabled = lot is not null;
        ToggleLotButton.IsEnabled = lot is not null;
        WriteOffButton.IsEnabled = lot is not null && CanWriteOff(lot);
        bool multiple = _selectedLots.Count > 1;
        BulkBlockLotsButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        BulkUnblockLotsButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        BulkWriteOffLotsButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        BulkBlockLotsButton.IsEnabled = multiple && _selectedLots.Values.Any(static item => item.IsActive);
        BulkUnblockLotsButton.IsEnabled = multiple && _selectedLots.Values.Any(static item => !item.IsActive);
        BulkWriteOffLotsButton.IsEnabled = multiple && _selectedLots.Values.All(CanWriteOff);
        int count = _selectedLots.Count > 0 || _lotCheckboxSelectionUsed
            ? _selectedLots.Count : lot is null ? 0 : 1;
        LotSelectedCountText.Text = TranslationSource.Instance.Format("Selection_Count", count);
        UpdateLotPageSelectionCheck();
    }

    private static bool CanWriteOff(LotItem lot) =>
        lot is { ExpiryBucketCode: "EXPIRED", IsActive: true } &&
        lot.OnHand > GroceryErp.Core.Numerics.Quantity.Zero;

    private void ClearLotSelection()
    {
        _selectedLots.Clear();
        _lotCheckboxSelectionUsed = false;
        LotsGrid.UnselectAll();
        LotsGrid.Items.Refresh();
        UpdateLotSelectionActions();
    }

    private async void BulkBlockLots_OnClick(object sender, RoutedEventArgs e) => await ChangeLotsActiveAsync(false);

    private async void BulkUnblockLots_OnClick(object sender, RoutedEventArgs e) => await ChangeLotsActiveAsync(true);

    private async Task ChangeLotsActiveAsync(bool active)
    {
        LotItem[] lots = _selectedLots.Values.ToArray();
        if (lots.Length < 2 || lots.All(item => item.IsActive == active)) return;
        if (!active && MessageBox.Show(TranslationSource.Instance.Format("Stock_BlockSelectedConfirm", lots.Length),
            TranslationSource.Instance["Stock_BlockSelected"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        BulkBlockLotsButton.IsEnabled = false;
        BulkUnblockLotsButton.IsEnabled = false;
        try
        {
            int changed = await _inventory.BulkSetLotsActiveAsync(lots.Select(static item => item.Id).ToArray(), active);
            await RefreshAsync();
            StatusText.Text = TranslationSource.Instance.Format("Stock_LotStatusChanged", changed);
        }
        catch (Exception exception)
        {
            StatusText.Text = UserError.Message(exception);
            UpdateLotSelectionActions();
        }
    }

    private async void BulkWriteOffLots_OnClick(object sender, RoutedEventArgs e)
    {
        LotItem[] lots = _selectedLots.Values.ToArray();
        if (lots.Length < 2 || lots.Any(static item => !CanWriteOff(item))) return;
        if (MessageBox.Show(TranslationSource.Instance.Format("Stock_BulkWriteOffConfirm", lots.Length),
            TranslationSource.Instance["Stock_WriteOffSelectedExpired"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        BulkWriteOffLotsButton.IsEnabled = false;
        try
        {
            IServiceProvider services = ((App)Application.Current).Services;
            IReadOnlyList<long> writtenOff = await services.GetRequiredService<MoneyOutService>()
                .WriteOffExpiredAsync(lots.Select(static item => item.Id).ToArray(),
                    services.GetRequiredService<IClock>().BusinessDate, OperationGuard.NewId());
            await RefreshAsync();
            StatusText.Text = TranslationSource.Instance.Format("Stock_BulkWriteOffResult", writtenOff.Count);
        }
        catch (Exception exception)
        {
            StatusText.Text = UserError.Message(exception);
            UpdateLotSelectionActions();
        }
    }

    private async void WriteOff_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSingleLot() is not LotItem { ExpiryBucketCode: "EXPIRED", IsActive: true } lot) return;
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
        if (SelectedSingleLot() is not LotItem lot) return;
        try { await _inventory.SetLotActiveAsync(lot.Id, !lot.IsActive, lot.IsActive); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void LotActive_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: LotItem lot } box) return;
        bool active = box.IsChecked == true;
        if (active == lot.IsActive) return;
        box.IsEnabled = false;
        try
        {
            await _inventory.SetLotActiveAsync(lot.Id, active, lot.IsActive);
            ClearLotSelection();
            await LoadLotPageAsync(_lotCursor);
            StatusText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            box.IsChecked = lot.IsActive;
            StatusText.Text = UserError.Message(exception);
        }
        finally { box.IsEnabled = true; }
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
                (LedgerTypeBox.SelectedItem as LedgerTypeOption)?.Code, PageSize);
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
            ClearLotSelection();
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
            StockOverviewPage page = await _inventory.GetOverviewPageAsync(OverviewSearchBox.Text, name, id, PageSize);
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
            LotPage page = await _inventory.GetLotPageAsync(query: LotSearchBox.Text, beforeId: beforeId, limit: PageSize);
            _lotPage = page;
            _lotCursor = beforeId;
            UiRefreshProbe.RecordBinding("stock", () => LotsGrid.ItemsSource = page.Items);
            UpdateLotSelectionActions();
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
        ClearLotSelection();
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

    private async void PageSize_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        long? selectedProductId = (OverviewGrid.SelectedItem as StockOverviewItem)?.ProductId;
        _ledgerPrevious.Clear();
        _ledgerPage = null;
        _ledgerCursor = null;
        LedgerGrid.ItemsSource = null;
        await RefreshAsync();
        if (selectedProductId is long id && _overviewPage is { } page)
            OverviewGrid.SelectedItem = page.Items.FirstOrDefault(item => item.ProductId == id);
    }
}
