using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
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
    private bool? _isInternal;
    private int _lotLoadVersion;
    private int _ledgerLoadVersion;

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
        StockTabs.SelectionChanged += (_, _) =>
        {
            UpdatePageButtons();
            if (IsLoaded && StockTabs.SelectedIndex == 1 && _ledgerPage is null)
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await LoadLedgerAsync(null); }
                    catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
                }));
        };
        UpdatePageButtons();
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_isInternal is null)
            {
                IServiceProvider services = ((App)Application.Current).Services;
                _isInternal = (await services.GetRequiredService<CatalogService>().GetAsync(_productId))
                    .Product.ProductClass == "INTERNAL";
                ConsumeInternalButton.Visibility = _isInternal.Value ? Visibility.Visible : Visibility.Collapsed;
            }
            _lotPrevious.Clear();
            _ledgerPrevious.Clear();
            _lotPage = null;
            _ledgerPage = null;
            if (StockTabs.SelectedIndex == 0) await LoadLotsAsync(null);
            else await LoadLedgerAsync(null);
            StatusText.Text = string.Empty;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async Task LoadLotsAsync(long? beforeId)
    {
        int version = ++_lotLoadVersion;
        string filter = (ExpiryFilterBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "ALL";
        LotPage page = await _inventory.GetLotPageAsync(_productId, beforeId: beforeId,
            limit: PageSize, expiryFilter: filter);
        if (version != _lotLoadVersion) return;
        await Dispatcher.InvokeAsync(() =>
        {
            _lotPage = page;
            _lotCursor = beforeId;
            LotsGrid.ItemsSource = page.Items;
            UpdatePageButtons();
        });
    }

    private async Task LoadLedgerAsync(long? beforeId)
    {
        int version = ++_ledgerLoadVersion;
        LedgerPage page = await _inventory.GetLedgerPageAsync(_productId, DateOnly.MinValue, DateOnly.MaxValue, beforeId, limit: PageSize);
        if (version != _ledgerLoadVersion) return;
        await Dispatcher.InvokeAsync(() =>
        {
            _ledgerPage = page;
            _ledgerCursor = beforeId;
            LedgerGrid.ItemsSource = page.Items;
            UpdatePageButtons();
        });
    }

    private void UpdatePageButtons()
    {
        if (PreviousLotButton is null) return;
        bool lots = StockTabs.SelectedIndex == 0;
        PrintLotButton.Visibility = lots ? Visibility.Visible : Visibility.Collapsed;
        AddDirectLotButton.Visibility = lots ? Visibility.Visible : Visibility.Collapsed;
        ConsumeInternalButton.Visibility = lots && _isInternal == true ? Visibility.Visible : Visibility.Collapsed;
        CorrectLotButton.Visibility = lots ? Visibility.Visible : Visibility.Collapsed;
        DamageLotButton.Visibility = lots ? Visibility.Visible : Visibility.Collapsed;
        BulkDamageButton.Visibility = lots ? Visibility.Visible : Visibility.Collapsed;
        DeleteZeroLotButton.Visibility = lots ? Visibility.Visible : Visibility.Collapsed;
        ExpiryFilterBox.Visibility = lots ? Visibility.Visible : Visibility.Collapsed;
        OpenSourceButton.Visibility = lots ? Visibility.Collapsed : Visibility.Visible;
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
        if (DamageLotButton is not null)
            DamageLotButton.IsEnabled = LotsGrid.SelectedItem is LotItem { OnHand: var amount } && amount > Quantity.Zero;
        if (BulkDamageButton is not null)
            BulkDamageButton.IsEnabled = LotsGrid.SelectedItems.OfType<LotItem>().Count() is > 1 and <= 50 &&
                LotsGrid.SelectedItems.OfType<LotItem>().All(static lot => lot.OnHand > Quantity.Zero);
        if (DeleteZeroLotButton is not null)
            DeleteZeroLotButton.IsEnabled = LotsGrid.SelectedItems.OfType<LotItem>().Count() is > 0 and <= 50;
        if (ConsumeInternalButton is not null)
            ConsumeInternalButton.IsEnabled = _isInternal == true &&
                LotsGrid.SelectedItem is LotItem { IsActive: true } chosen && chosen.OnHand > Quantity.Zero;
    }

    private async void LotActive_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { DataContext: LotItem lot } box) return;
        bool active = box.IsChecked == true;
        if (active == lot.IsActive) return;
        box.IsEnabled = false;
        try
        {
            await _inventory.SetLotActiveAsync(lot.Id, active, lot.IsActive);
            await LoadLotsAsync(_lotCursor);
            StatusText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            box.IsChecked = lot.IsActive;
            StatusText.Text = UserError.Message(exception);
        }
        finally { box.IsEnabled = true; }
    }

    private async void CorrectLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot) return;
        if (new LotPriceWindow(_inventory, lot) { Owner = this }.ShowDialog() == true) await RefreshAsync();
    }

    private async void AddDirectLot_OnClick(object sender, RoutedEventArgs e)
    {
        IServiceProvider services = ((App)Application.Current).Services;
        if (new DirectLotWindow(_inventory, _productId, services.GetRequiredService<IClock>())
        { Owner = this }.ShowDialog() == true) await RefreshAsync();
    }

    private async void ConsumeInternal_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isInternal != true || LotsGrid.SelectedItem is not LotItem { IsActive: true } lot) return;
        IServiceProvider services = ((App)Application.Current).Services;
        if (new InternalUseWindow(services.GetRequiredService<PurchasingService>(), lot.Id,
            services.GetRequiredService<IClock>())
        { Owner = this }.ShowDialog() == true)
            await RefreshAsync();
    }

    private async void PageSize_OnChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) await RefreshAsync();
    }

    private async void DamageLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not LotItem lot || lot.OnHand <= Quantity.Zero) return;
        IServiceProvider services = ((App)Application.Current).Services;
        if (new LotDamageWindow(services.GetRequiredService<MoneyOutService>(),
            services.GetRequiredService<IClock>(), lot)
        { Owner = this }.ShowDialog() == true)
            await RefreshAsync();
    }

    private async void BulkDamage_OnClick(object sender, RoutedEventArgs e)
    {
        LotItem[] lots = LotsGrid.SelectedItems.OfType<LotItem>().ToArray();
        if (lots.Length is < 2 or > 50 || lots.Any(static lot => lot.OnHand <= Quantity.Zero)) return;
        IServiceProvider services = ((App)Application.Current).Services;
        if (new LotBulkDamageWindow(services.GetRequiredService<MoneyOutService>(),
            services.GetRequiredService<IClock>(), lots)
        { Owner = this }.ShowDialog() == true)
            await RefreshAsync();
    }

    private async void DeleteZeroLot_OnClick(object sender, RoutedEventArgs e)
    {
        LotItem[] selected = LotsGrid.SelectedItems.OfType<LotItem>().ToArray();
        if (selected.Length is < 1 or > 50) return;
        try
        {
            IReadOnlyList<LotDeletionPreview> previews = await _inventory.PreviewDeleteZeroLotsAsync(
                selected.Select(static lot => lot.Id).ToArray());
            long[] eligible = previews.Where(static preview => preview.CanDelete)
                .Select(static preview => preview.LotId).ToArray();
            if (eligible.Length == 0)
            {
                StatusText.Text = previews.Count == 1
                    ? TranslationSource.Instance[previews[0].BlockReason!]
                    : TranslationSource.Instance["Stock_DeleteNoneEligible"];
                return;
            }
            if (MessageBox.Show(TranslationSource.Instance.Format("Stock_DeleteSelectedPreview",
                    selected.Length, eligible.Length, selected.Length - eligible.Length),
                TranslationSource.Instance["Stock_DeleteZero"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _inventory.DeleteZeroLotsAsync(selected.Where(lot => eligible.Contains(lot.Id))
                .Select(static lot => new LotDeletionRequest(lot.Id, lot.DetailVersion)).ToArray());
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void ExpiryFilter_OnChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _lotPrevious.Clear();
        try { await LoadLotsAsync(null); }
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
