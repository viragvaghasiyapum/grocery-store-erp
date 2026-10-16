using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class ProductStockWindow : Window
{
    private int PageSize => int.TryParse((PageSizeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString(), out int size) ? size : 50;
    private readonly InventoryService _inventory;
    private readonly long _productId;
    private readonly Stack<long?> _lotPrevious = new();
    private readonly Stack<long?> _ledgerPrevious = new();
    private long? _lotCursor;
    private long? _ledgerCursor;
    private LotPage? _lotPage;
    private LedgerPage? _ledgerPage;

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
        StockTabs.SelectionChanged += (_, _) => UpdatePageButtons();
        UpdatePageButtons();
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            _lotPrevious.Clear();
            _ledgerPrevious.Clear();
            await LoadLotsAsync(null);
            await LoadLedgerAsync(null);
            StatusText.Text = string.Empty;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async Task LoadLotsAsync(long? beforeId)
    {
        LotPage page = await _inventory.GetLotPageAsync(_productId, beforeId: beforeId, limit: PageSize);
        _lotPage = page;
        _lotCursor = beforeId;
        LotsGrid.ItemsSource = page.Items;
        UpdatePageButtons();
    }

    private async Task LoadLedgerAsync(long? beforeId)
    {
        LedgerPage page = await _inventory.GetLedgerPageAsync(_productId, DateOnly.MinValue, DateOnly.MaxValue, beforeId, limit: PageSize);
        _ledgerPage = page;
        _ledgerCursor = beforeId;
        LedgerGrid.ItemsSource = page.Items;
        UpdatePageButtons();
    }

    private void UpdatePageButtons()
    {
        if (PreviousLotButton is null) return;
        bool lots = StockTabs.SelectedIndex == 0;
        PreviousLotButton.Visibility = NextLotButton.Visibility = lots ? Visibility.Visible : Visibility.Collapsed;
        PreviousLedgerButton.Visibility = NextLedgerButton.Visibility = lots ? Visibility.Collapsed : Visibility.Visible;
        PreviousLotButton.IsEnabled = _lotPrevious.Count > 0;
        NextLotButton.IsEnabled = _lotPage?.HasMore == true;
        PreviousLedgerButton.IsEnabled = _ledgerPrevious.Count > 0;
        NextLedgerButton.IsEnabled = _ledgerPage?.HasMore == true;
    }

    private async void NextLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (_lotPage is not { HasMore: true, NextId: not null } page) return;
        long? previous = _lotCursor;
        await LoadLotsAsync(page.NextId);
        _lotPrevious.Push(previous);
        UpdatePageButtons();
    }

    private async void PreviousLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (_lotPrevious.Count == 0) return;
        await LoadLotsAsync(_lotPrevious.Pop());
        UpdatePageButtons();
    }

    private async void NextLedger_OnClick(object sender, RoutedEventArgs e)
    {
        if (_ledgerPage is not { HasMore: true, NextId: not null } page) return;
        long? previous = _ledgerCursor;
        await LoadLedgerAsync(page.NextId);
        _ledgerPrevious.Push(previous);
        UpdatePageButtons();
    }

    private async void PreviousLedger_OnClick(object sender, RoutedEventArgs e)
    {
        if (_ledgerPrevious.Count == 0) return;
        await LoadLedgerAsync(_ledgerPrevious.Pop());
        UpdatePageButtons();
    }

    private void LotsGrid_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CorrectLotButton is not null) CorrectLotButton.IsEnabled = LotsGrid.SelectedItem is LotItem;
        if (PrintLotButton is not null)
            PrintLotButton.IsEnabled = LotsGrid.SelectedItem is LotItem { IsActive: true } lot && lot.OnHand.Value > 0;
        if (WriteOffButton is not null)
            WriteOffButton.IsEnabled = LotsGrid.SelectedItem is LotItem { ExpiryBucketCode: "EXPIRED", IsActive: true } expired && expired.OnHand > Quantity.Zero;
    }

    private async void CorrectLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot) return;
        if (new LotPriceWindow(_inventory, lot) { Owner = this }.ShowDialog() == true) await RefreshAsync();
    }

    private async void PageSize_OnChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) await RefreshAsync();
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

    private void PrintLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot) return;
        IServiceProvider services = ((App)Application.Current).Services;
        var dialog = new LabelBatchWindow(services.GetRequiredService<CatalogService>(), _inventory,
            services.GetRequiredService<ISettings>(), lotIds: [lot.Id])
        { Owner = this };
        dialog.ShowDialog();
    }

    private void LedgerGrid_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (OpenSourceButton is not null) OpenSourceButton.IsEnabled = LedgerGrid.SelectedItem is LedgerItem;
    }

    private async void OpenSource_OnClick(object sender, RoutedEventArgs e)
    {
        if (LedgerGrid.SelectedItem is not LedgerItem movement) return;
        try
        {
            StockSourceDocument source = await _inventory.GetSourceDocumentAsync(movement);
            new StockSourceWindow(source) { Owner = this }.ShowDialog();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }
}
