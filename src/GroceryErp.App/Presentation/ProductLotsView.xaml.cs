using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GroceryErp.Catalog;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

/// <summary>
/// One product's lots: filters for status, expiry, source, batch, expiry dates and price; keyset
/// pages sized to the visible table; lot actions; and reviewed bulk block, damage and empty-lot delete.
/// Created only when the Lots tab is first opened.
/// </summary>
public partial class ProductLotsView : UserControl
{
    private readonly InventoryService _inventory;
    private readonly long _productId;
    private readonly bool _isInternal;
    private readonly KeysetPager<long?> _pager = new();
    private readonly LatestRequest _requests = new();
    private readonly TableSelection<LotItem> _selection = new(static lot => lot.Id);
    private readonly FitPageSize _fit;
    private readonly DispatcherTimer _searchDelay;
    private CheckBox? _pageCheck;

    public ProductLotsView(InventoryService inventory, long productId, bool isInternal, bool loadOnShow = true)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        _inventory = inventory;
        _productId = productId;
        _isInternal = isInternal;
        InitializeComponent();
        ConsumeButton.Visibility = isInternal ? Visibility.Visible : Visibility.Collapsed;
        _ = new ActionStrip(ActionBar, ActionButtons, MoreActionsMenu, MoreActions);
        _ = new FilterRow(FilterBar, SearchBox, MoreFiltersToggle, MoreFiltersPanel).Fold(ExpiryFilter).Fold(StatusFilter);
        // Source and expiry cells are converted in code, so a reload shows them in the new language.
        LanguageRefresh.Attach(this, () => _ = LoadAsync(PageMove.Same));
        _fit = new FitPageSize(LotsGrid, 12);
        _fit.RowsChanged += async (_, _) => await LoadAsync(PageMove.Same);
        _searchDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await ReloadAsync();
        };
        _selection.Changed += (_, _) => UpdateSelection();
        Selection.ClearRequested += (_, _) => _selection.Clear();
        Selection.AllMatchingRequested += async (_, _) => await SelectAllMatchingAsync();
        Pager.PreviousRequested += async (_, _) => await LoadAsync(PageMove.Previous);
        Pager.NextRequested += async (_, _) => await LoadAsync(PageMove.Next);
        Unloaded += (_, _) =>
        {
            _searchDelay.Stop();
            _requests.Cancel();
        };
        if (loadOnShow) Loaded += async (_, _) =>
        {
            if (_pager.PageNumber == 1 && LotsGrid.ItemsSource is null) await LoadAsync(PageMove.First);
        };
    }

    /// <summary>Raised after a lot write so the product row and panel header refresh.</summary>
    public event EventHandler? StockChanged;

    /// <summary>
    /// Removes the lot action row so a host can show it elsewhere, aligned to the right; the
    /// actions keep working and still fold behind More when the new place is narrow.
    /// </summary>
    internal FrameworkElement DetachActions()
    {
        ((Panel)ActionBar.Parent).Children.Remove(ActionBar);
        ActionBar.Margin = new Thickness(0);
        ActionButtons.HorizontalAlignment = HorizontalAlignment.Right;
        return ActionBar;
    }

    public Task ReloadAsync()
    {
        _selection.Clear();
        return LoadAsync(PageMove.First);
    }

    private static DateOnly BusinessDate =>
        (Application.Current as App)?.ServicesOrNull?.GetService<IClock>()?.BusinessDate ?? DateOnly.FromDateTime(DateTime.Today);

    private LotFilter CurrentFilter() => new()
    {
        ProductId = _productId,
        Query = InputNormalizer.Normalize(SearchBox.Text ?? string.Empty),
        Status = SelectedTag(StatusFilter) ?? "ALL",
        Expiry = SelectedTag(ExpiryFilter) ?? "ALL",
        Source = SelectedTag(SourceFilter) is { Length: > 0 } source ? source : null,
        ExpiryFrom = ExpiryFromBox.SelectedDate is { } from ? DateOnly.FromDateTime(from) : null,
        ExpiryTo = ExpiryToBox.SelectedDate is { } to ? DateOnly.FromDateTime(to) : null,
        MinPrice = Price(MinPriceBox.Text),
        MaxPrice = Price(MaxPriceBox.Text),
        AsOf = BusinessDate,
    };

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private static Money? Price(string text) =>
        decimal.TryParse(InputNormalizer.Normalize(text).Trim(), NumberStyles.Number, TranslationSource.FormatCulture,
            out decimal value) && value >= 0 ? new Money(value) : null;

    private async Task LoadAsync(PageMove move)
    {
        if (!_pager.CanMove(move)) return;
        using LatestRequest.Scope request = _requests.Begin();
        Pager.SetLoading(true);
        try
        {
            LotPage page = await _inventory.GetLotPageAsync(CurrentFilter(), _pager.CursorFor(move), _fit.Take(), request.Token);
            if (!request.IsCurrent) return;
            _pager.Commit(move, page.HasMore, page.NextId);
            _selection.Refresh(page.Items);
            LotsGrid.ItemsSource = page.Items;
            EmptyText.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Pager.Status = string.Empty;
            UpdateSelection();
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) Pager.Status = UserError.Message(exception);
        }
        finally
        {
            if (request.IsCurrent) Pager.Update(_pager.PageNumber, _pager.CanGoBack, _pager.HasMore);
        }
    }

    private void Search_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private async void Filter_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await ReloadAsync();
    }

    private async void Date_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await ReloadAsync();
    }

    private void MoreFilters_OnClick(object sender, RoutedEventArgs e) =>
        MoreFiltersPanel.Visibility = MoreFiltersToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void ClearFilters_OnClick(object sender, RoutedEventArgs e)
    {
        _searchDelay.Stop();
        SearchBox.Text = string.Empty;
        MinPriceBox.Text = string.Empty;
        MaxPriceBox.Text = string.Empty;
        StatusFilter.SelectedIndex = 0;
        ExpiryFilter.SelectedIndex = 0;
        SourceFilter.SelectedIndex = 0;
        ExpiryFromBox.SelectedDate = null;
        ExpiryToBox.SelectedDate = null;
        _searchDelay.Stop();
        await ReloadAsync();
    }

    private LotItem? Current => LotsGrid.SelectedItem as LotItem;

    private void LotsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LotItem? lot = Current;
        EditLotButton.IsEnabled = lot is not null;
        DamageLotButton.IsEnabled = lot is not null && lot.OnHand > Quantity.Zero;
        LabelButton.IsEnabled = lot is { IsActive: true } && lot.OnHand > Quantity.Zero;
        ConsumeButton.IsEnabled = _isInternal && lot is { IsActive: true } && lot.OnHand > Quantity.Zero;
    }

    private void LotsGrid_OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Current is not null) EditLot_OnClick(sender, e);
    }

    private void RowCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: LotItem lot } box) box.IsChecked = _selection.IsSelected(lot);
    }

    private void RowCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: LotItem lot } box) _selection.Set(lot, box.IsChecked == true);
    }

    private void PageCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        _pageCheck = sender as CheckBox;
        UpdateSelection();
    }

    private void PageCheck_OnClick(object sender, RoutedEventArgs e)
    {
        LotItem[] rows = LotsGrid.Items.OfType<LotItem>().ToArray();
        _selection.SetPage(rows, _selection.PageState(rows) != true);
        LotsGrid.Items.Refresh();
    }

    private void UpdateSelection()
    {
        LotItem[] rows = LotsGrid.Items.OfType<LotItem>().ToArray();
        bool? state = _selection.PageState(rows);
        if (_pageCheck is not null) _pageCheck.IsChecked = state;
        Selection.Show(_selection.Count, _selection.AllMatching, state == true, _pager.HasMore || _pager.CanGoBack,
            _selection.AllMatching ? FilterSummary() : null);
        BulkDamageItem.IsEnabled = !_selection.AllMatching && _selection.Count is > 0 and <= 50;
    }

    private async Task SelectAllMatchingAsync()
    {
        try
        {
            int count = await _inventory.CountLotsAsync(CurrentFilter());
            _selection.SelectAllMatching(count);
            LotsGrid.Items.Refresh();
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async Task<IReadOnlyList<long>> SelectedIdsAsync(CancellationToken cancellationToken) =>
        _selection.AllMatching
            ? await _inventory.ResolveLotIdsAsync(CurrentFilter(), cancellationToken: cancellationToken)
            : _selection.PickedIds;

    private string SelectionSummary() => _selection.AllMatching
        ? $"{TranslationSource.Instance.Format("Selection_AllMatchingCount", _selection.Count)} · {FilterSummary()}"
        : TranslationSource.Instance.Format("Selection_Count", _selection.Count);

    private string FilterSummary() => FilterText.Describe(SearchBox, StatusFilter, ExpiryFilter, SourceFilter,
        ExpiryFromBox, ExpiryToBox, MinPriceBox, MaxPriceBox);

    private async void BulkBlock_OnClick(object sender, RoutedEventArgs e) =>
        await RunLotBulkAsync(LotBulkAction.Block, "Stock_BlockSelected", destructive: false);

    private async void BulkUnblock_OnClick(object sender, RoutedEventArgs e) =>
        await RunLotBulkAsync(LotBulkAction.Unblock, "Stock_UnblockSelected", destructive: false);

    private async void BulkDeleteZero_OnClick(object sender, RoutedEventArgs e) =>
        await RunLotBulkAsync(LotBulkAction.DeleteZero, "Stock_DeleteZero", destructive: true);

    private async Task RunLotBulkAsync(LotBulkAction action, string titleKey, bool destructive)
    {
        if (_selection.IsEmpty) return;
        var bulk = new BulkAction
        {
            Title = TranslationSource.Instance[titleKey],
            RunLabel = TranslationSource.Instance[titleKey],
            SelectionSummary = SelectionSummary(),
            Destructive = destructive,
            Plan = async token =>
            {
                IReadOnlyList<LotBulkRow> rows = await _inventory.PreviewLotBulkAsync(await SelectedIdsAsync(token), action, token);
                LotBulkRow[] eligible = rows.Where(static row => row.Eligible).ToArray();
                return new BulkPlan(rows.Select(static row => new BulkCandidate(row.Id, row.Label, row.Eligible, row.ReasonKey)).ToArray(),
                    TranslationSource.Instance.Format("Stock_BulkLotEffect", eligible.Length,
                        Quantity.FromScaled(eligible.Sum(static row => row.OnHand.Scaled)),
                        Money.FromScaled(eligible.Sum(static row => row.CostValue.Scaled))));
            },
            Run = (ids, operation, token) => _inventory.RunLotBulkBatchAsync(action, ids, operation, token),
        };
        await ShowBulkAsync(bulk);
    }

    private async Task ShowBulkAsync(BulkAction bulk)
    {
        var window = new BulkActionWindow(bulk) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (!window.ChangedAny) return;
        _selection.Clear();
        await LoadAsync(PageMove.Same);
        StockChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void BulkDamage_OnClick(object sender, RoutedEventArgs e)
    {
        LotItem[] lots = _selection.Picked.Where(static lot => lot.OnHand > Quantity.Zero).ToArray();
        if (_selection.AllMatching || lots.Length is < 1 or > 50)
        {
            Pager.Status = TranslationSource.Instance["Stock_BulkDamageLimit"];
            return;
        }
        IServiceProvider services = ((App)Application.Current).Services;
        if (new LotBulkDamageWindow(services.GetRequiredService<MoneyOutService>(),
            services.GetRequiredService<IClock>(), lots)
        { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        _selection.Clear();
        await LoadAsync(PageMove.Same);
        StockChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void AddDirectLot_OnClick(object sender, RoutedEventArgs e)
    {
        IServiceProvider services = ((App)Application.Current).Services;
        if (new DirectLotWindow(_inventory, _productId, services.GetRequiredService<IClock>())
        { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await AfterWriteAsync();
    }

    private async void EditLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is not LotItem lot) return;
        if (new LotPriceWindow(_inventory, lot) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await AfterWriteAsync();
    }

    private async void DamageLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is not LotItem lot || lot.OnHand <= Quantity.Zero) return;
        IServiceProvider services = ((App)Application.Current).Services;
        if (new LotDamageWindow(services.GetRequiredService<MoneyOutService>(), services.GetRequiredService<IClock>(), lot)
        { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await AfterWriteAsync();
    }

    private async void Consume_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isInternal || Current is not LotItem { IsActive: true } lot) return;
        IServiceProvider services = ((App)Application.Current).Services;
        if (new InternalUseWindow(services.GetRequiredService<PurchasingService>(), lot.Id, services.GetRequiredService<IClock>())
        { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await AfterWriteAsync();
    }

    private void Label_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is not LotItem lot) return;
        IServiceProvider services = ((App)Application.Current).Services;
        new LabelBatchWindow(services.GetRequiredService<CatalogService>(), _inventory,
            services.GetRequiredService<ISettings>(), lotIds: [lot.Id])
        { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async Task AfterWriteAsync()
    {
        await LoadAsync(PageMove.Same);
        StockChanged?.Invoke(this, EventArgs.Empty);
    }
}
