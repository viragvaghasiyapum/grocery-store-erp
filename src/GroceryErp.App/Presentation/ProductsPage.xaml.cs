using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GroceryErp.Catalog;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Products: server-side search (exact SKU/barcode first) and filters, keyset pages sized to the
/// window, page or all-matching selection with reviewed bulk actions, and a slide-in panel with
/// the product's Basics, Lots and History. Filters are kept for the session.
/// </summary>
public partial class ProductsPage : Page
{
    private const string StateKey = nameof(ProductsPage);
    private const int SearchPageSize = 8;
    private const int MaxLabelProducts = 200;
    private const int MaxCountProducts = 100;
    private readonly CatalogService _catalog;
    private readonly ISettings _settings;
    private readonly ProductExcelImporter _importer;
    private readonly KeysetPager<NameCursor> _pager = new();
    private readonly LatestRequest _requests = new();
    private readonly TableSelection<ProductListItem> _selection = new(static product => product.Id);
    private readonly FitPageSize _fit;
    private readonly DetailSplit _details;
    private readonly DispatcherTimer _searchDelay;
    private readonly bool _loadOnShow;
    private CheckBox? _pageSelectionCheck;
    private State? _pendingState;
    private bool _restoring;
    private long _searchStarted;

    public ProductsPage() : this(
        ((App)Application.Current).Services.GetRequiredService<CatalogService>(),
        ((App)Application.Current).Services.GetRequiredService<ISettings>(),
        ((App)Application.Current).Services.GetRequiredService<ProductExcelImporter>())
    {
    }

    public ProductsPage(CatalogService catalog, ISettings settings, ProductExcelImporter importer, bool loadOnShow = true)
    {
        InitializeComponent();
        _catalog = catalog;
        _settings = settings;
        _importer = importer;
        _loadOnShow = loadOnShow;
        _ = new FilterRow(FilterBar, SearchBox, MoreFiltersToggle, MoreFiltersPanel)
            .Fold(CategoryFilter).Fold(StatusFilter).Fold(StockFilter).Fold(ExpiryFilter).Fold(ClassFilter);
        _fit = new FitPageSize(ProductsGrid);
        _fit.RowsChanged += async (_, _) => await LoadAsync(PageMove.Same);
        _searchDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await ReloadAsync(_searchStarted);
        };
        _selection.Changed += (_, _) => UpdateSelection();
        Selection.ClearRequested += (_, _) =>
        {
            _selection.Clear();
            ProductsGrid.Items.Refresh();
        };
        Selection.AllMatchingRequested += async (_, _) => await SelectAllMatchingAsync();
        Pager.PreviousRequested += async (_, _) => await LoadAsync(PageMove.Previous);
        Pager.NextRequested += async (_, _) => await LoadAsync(PageMove.Next);
        _details = new DetailSplit(Layout, Details, Details.Panel, ProductsGrid, "Products");
        Details.CloseRequested += (_, _) => CloseDetails();
        Details.ProductChanged += async (_, _) => await LoadAsync(PageMove.Same);
        IServiceProvider? services = (Application.Current as App)?.ServicesOrNull;
        Details.Initialize(catalog, services?.GetService<InventoryService>(), BusinessDate,
            settings.Current.TaxMode == "REGULAR");
        _pendingState = PageStateStore.Current?.Get<State>(StateKey);
        ApplyState(_pendingState);
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += async (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged += TranslationChanged;
            if (!_loadOnShow) return;
            try
            {
                await LoadMastersAsync();
                await LoadAsync(PageMove.First);
            }
            catch (Exception exception) { Pager.Status = UserError.Message(exception); }
        };
        Unloaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= TranslationChanged;
            _searchDelay.Stop();
            _requests.Cancel();
            PageStateStore.Current?.Set(StateKey, CaptureState());
        };
    }

    private static DateOnly BusinessDate => App.BusinessDate;

    private sealed record State(string Search, int Class, int Status, long CategoryId, int Stock, int Expiry,
        long BrandId, long VendorId, bool NoBarcode, bool MissingLocal, bool Loose, bool MoreFilters);

    /// <summary>Makes the next Products page open on active products with expired, or else expiring, stock.</summary>
    internal static void RequestExpiryFilter(PageStateStore? store, bool expired) =>
        store?.Set(StateKey, new State(string.Empty, 0, 0, 0, 0, expired ? 1 : 2, 0, 0, false, false, false, false));

    /// <summary>Makes the next Products page open on active products below their reorder level.</summary>
    internal static void RequestLowStockFilter(PageStateStore? store) =>
        store?.Set(StateKey, new State(string.Empty, 0, 0, 0, 2, 0, 0, 0, false, false, false, false));

    private State CaptureState() => new(SearchBox.Text ?? string.Empty, ClassFilter.SelectedIndex, StatusFilter.SelectedIndex,
        CategoryFilter.SelectedValue as long? ?? 0, StockFilter.SelectedIndex, ExpiryFilter.SelectedIndex,
        BrandFilter.SelectedValue as long? ?? 0, VendorFilter.SelectedValue as long? ?? 0,
        NoBarcodeFilter.IsChecked == true, MissingLocalFilter.IsChecked == true, LooseFilter.IsChecked == true,
        MoreFiltersToggle.IsChecked == true);

    private void ApplyState(State? state)
    {
        if (state is null) return;
        _restoring = true;
        SearchBox.Text = state.Search;
        ClassFilter.SelectedIndex = state.Class;
        StatusFilter.SelectedIndex = state.Status;
        StockFilter.SelectedIndex = state.Stock;
        ExpiryFilter.SelectedIndex = state.Expiry;
        NoBarcodeFilter.IsChecked = state.NoBarcode;
        MissingLocalFilter.IsChecked = state.MissingLocal;
        LooseFilter.IsChecked = state.Loose;
        MoreFiltersToggle.IsChecked = state.MoreFilters;
        MoreFiltersPanel.Visibility = state.MoreFilters ? Visibility.Visible : Visibility.Collapsed;
        _restoring = false;
    }

    private void TranslationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]" || !IsLoaded) return;
        UpdateSelection();
        _ = RefreshForLanguageAsync();
    }

    // The "All …" filter items and the row count are set in code, so they follow a reload.
    private async Task RefreshForLanguageAsync()
    {
        try
        {
            await LoadMastersAsync();
            await LoadAsync(PageMove.Same);
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async Task LoadMastersAsync()
    {
        long? category = CategoryFilter.SelectedValue as long? ?? _pendingState?.CategoryId;
        long? brand = BrandFilter.SelectedValue as long? ?? _pendingState?.BrandId;
        long? vendor = VendorFilter.SelectedValue as long? ?? _pendingState?.VendorId;
        _pendingState = null;
        IReadOnlyList<MasterItem> categories = await _catalog.GetCategoriesAsync();
        IReadOnlyList<MasterItem> brands = await _catalog.GetBrandsAsync();
        PurchasingService? purchasing = (Application.Current as App)?.ServicesOrNull?.GetService<PurchasingService>();
        IReadOnlyList<VendorItem> vendors = purchasing is null ? [] : await purchasing.GetVendorsAsync(includeInactive: true);
        _restoring = true;
        try
        {
            CategoryFilter.ItemsSource = new[] { new MasterItem { Id = 0, Name = TranslationSource.Instance["Catalog_AllCategories"] } }
                .Concat(categories.Where(static item => item.IsActive)).ToList();
            BrandFilter.ItemsSource = new[] { new MasterItem { Id = 0, Name = TranslationSource.Instance["Catalog_AllBrands"] } }
                .Concat(brands.Where(static item => item.IsActive)).ToList();
            VendorFilter.ItemsSource = new[] { new VendorItem(0, TranslationSource.Instance["Purchasing_AllVendors"], null, null, Money.Zero, true) }
                .Concat(vendors).ToList();
            CategoryFilter.SelectedValue = category ?? 0;
            BrandFilter.SelectedValue = brand ?? 0;
            VendorFilter.SelectedValue = vendor ?? 0;
            if (CategoryFilter.SelectedIndex < 0) CategoryFilter.SelectedIndex = 0;
            if (BrandFilter.SelectedIndex < 0) BrandFilter.SelectedIndex = 0;
            if (VendorFilter.SelectedIndex < 0) VendorFilter.SelectedIndex = 0;
        }
        finally { _restoring = false; }
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private static long? NonZero(object? value) => value is long id and > 0 ? id : null;

    private ProductSearchFilter CurrentFilter(int limit) => new(
        SearchBox.Text,
        CategoryId: NonZero(CategoryFilter.SelectedValue),
        BrandId: NonZero(BrandFilter.SelectedValue),
        LowStockOnly: SelectedTag(StockFilter) == "LOW",
        NoBarcodeOnly: NoBarcodeFilter.IsChecked == true,
        MissingLocalNameOnly: MissingLocalFilter.IsChecked == true,
        IncludeArchived: SelectedTag(StatusFilter) == "ALL",
        LooseOnly: LooseFilter.IsChecked == true,
        Limit: limit,
        ExpiryFilter: SelectedTag(ExpiryFilter) ?? "ALL",
        ExpiryAsOf: BusinessDate)
    {
        ArchivedOnly = SelectedTag(StatusFilter) == "ARCHIVED",
        ProductClass = SelectedTag(ClassFilter) is { Length: > 0 } productClass ? productClass : null,
        HasStockOnly = SelectedTag(StockFilter) == "HAS",
        VendorId = NonZero(VendorFilter.SelectedValue),
        ExactFirst = true,
    };

    private Task ReloadAsync(long? searchStarted = null)
    {
        _selection.Clear();
        return LoadAsync(PageMove.First, searchStarted);
    }

    private async Task LoadAsync(PageMove move, long? searchStarted = null)
    {
        if (!_pager.CanMove(move)) return;
        using var timing = UiRefreshProbe.Track("products", this, searchStarted);
        using LatestRequest.Scope request = _requests.Begin();
        Pager.SetLoading(true);
        try
        {
            int rows = _fit.Take();
            int limit = string.IsNullOrWhiteSpace(SearchBox.Text) ? rows : Math.Min(SearchPageSize, rows);
            NameCursor? cursor = _pager.CursorFor(move);
            ProductSearchPage page = await _catalog.SearchPageAsync(CurrentFilter(limit), cursor?.Name, cursor?.Id, request.Token);
            if (!request.IsCurrent || !IsLoaded && _loadOnShow) return;
            _pager.Commit(move, page.HasMore, page.NextName is null || page.NextId is null ? null : new NameCursor(page.NextName, page.NextId.Value));
            _selection.Refresh(page.Items);
            if (searchStarted is not null) timing.SearchShape = page.Items.Count == 0 ? "empty" : "populated";
            timing.Bind(() => ProductsGrid.ItemsSource = page.Items);
            EmptyText.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Pager.Status = TranslationSource.Instance.Format("Catalog_Count", page.Items.Count);
            if (_details.IsOpen &&
                page.Items.FirstOrDefault(item => item.Id == Details.ProductId) is { } open)
                Details.Refresh(open);
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

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _restoring) return;
        // Invalidate the shown result at once; the debounce only delays the query.
        _requests.Cancel();
        _searchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        Pager.SetLoading(true);
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private async void Filter_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_restoring) await ReloadAsync();
    }

    private async void CheckFilter_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) await ReloadAsync();
    }

    private void MoreFilters_OnClick(object sender, RoutedEventArgs e) =>
        MoreFiltersPanel.Visibility = MoreFiltersToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void ClearFilters_OnClick(object sender, RoutedEventArgs e)
    {
        _restoring = true;
        SearchBox.Text = string.Empty;
        ClassFilter.SelectedIndex = 0;
        StatusFilter.SelectedIndex = 0;
        CategoryFilter.SelectedIndex = 0;
        StockFilter.SelectedIndex = 0;
        ExpiryFilter.SelectedIndex = 0;
        BrandFilter.SelectedIndex = 0;
        VendorFilter.SelectedIndex = 0;
        NoBarcodeFilter.IsChecked = false;
        MissingLocalFilter.IsChecked = false;
        LooseFilter.IsChecked = false;
        _restoring = false;
        _searchDelay.Stop();
        await ReloadAsync();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            New_OnClick(this, e);
            e.Handled = true;
        }
    }

    private void ProductsGrid_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductListItem product) return;
        if (e.Key == Key.Enter)
        {
            _ = OpenDetailsAsync(product);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            _selection.Set(product, !_selection.IsSelected(product));
            ProductsGrid.Items.Refresh();
            e.Handled = true;
        }
    }

    private async void ProductsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_details.IsOpen && ProductsGrid.SelectedItem is ProductListItem product &&
            product.Id != Details.ProductId)
            await OpenDetailsAsync(product);
    }

    private async void ProductsGrid_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProductsGrid.SelectedItem is ProductListItem product) await OpenDetailsAsync(product);
    }

    private async Task OpenDetailsAsync(ProductListItem product, bool lots = false)
    {
        if (_details.IsOpen && Details.ProductId != product.Id && Details.HasUnsavedChanges &&
            MessageBox.Show(TranslationSource.Instance.Format("Catalog_DiscardChanges", Details.ProductName),
                TranslationSource.Instance["Catalog_Title"], MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (!_details.IsOpen)
        {
            SetPanelOpen(true);
            _details.Open();
        }
        await Details.OpenAsync(product, lots);
    }

    private void CloseDetails()
    {
        if (Details.HasUnsavedChanges &&
            MessageBox.Show(TranslationSource.Instance.Format("Catalog_DiscardChanges", Details.ProductName),
                TranslationSource.Instance["Catalog_Title"], MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _details.Close();
        SetPanelOpen(false);
        ProductsGrid.Focus();
    }

    // Beside the open panel the list keeps only its title and New product; import and masters return on close.
    private void SetPanelOpen(bool open)
    {
        Visibility secondary = open ? Visibility.Collapsed : Visibility.Visible;
        ImportMenu.Visibility = secondary;
        MastersButton.Visibility = secondary;
    }

    private void ProductCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: ProductListItem item } box) box.IsChecked = _selection.IsSelected(item);
    }

    private void ProductCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: ProductListItem item } box) _selection.Set(item, box.IsChecked == true);
    }

    private void PageSelectionCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        _pageSelectionCheck = sender as CheckBox;
        UpdateSelection();
    }

    private void PageSelectionCheck_OnClick(object sender, RoutedEventArgs e)
    {
        ProductListItem[] rows = ProductsGrid.Items.OfType<ProductListItem>().ToArray();
        _selection.SetPage(rows, _selection.PageState(rows) != true);
        ProductsGrid.Items.Refresh();
    }

    private void UpdateSelection()
    {
        ProductListItem[] rows = ProductsGrid.Items.OfType<ProductListItem>().ToArray();
        bool? state = _selection.PageState(rows);
        if (_pageSelectionCheck is not null) _pageSelectionCheck.IsChecked = state;
        Selection.Show(_selection.Count, _selection.AllMatching, state == true, _pager.HasMore || _pager.CanGoBack,
            _selection.AllMatching ? FilterSummary() : null);
    }

    private async Task SelectAllMatchingAsync()
    {
        try
        {
            int count = await _catalog.CountAsync(CurrentFilter(1));
            _selection.SelectAllMatching(count);
            ProductsGrid.Items.Refresh();
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async Task<IReadOnlyList<long>> SelectedIdsAsync(int maximum, CancellationToken cancellationToken) =>
        _selection.AllMatching
            ? await _catalog.ResolveIdsAsync(CurrentFilter(1), maximum, cancellationToken)
            : _selection.PickedIds.Length <= maximum ? _selection.PickedIds
                : throw new Core.Errors.DomainException(Core.Errors.ErrorCode.Validation, "Too many products are selected.");

    private string SelectionSummary()
    {
        string count = _selection.AllMatching
            ? TranslationSource.Instance.Format("Selection_AllMatchingCount", _selection.Count)
            : TranslationSource.Instance.Format("Selection_Count", _selection.Count);
        return _selection.AllMatching ? $"{count} · {FilterSummary()}" : count;
    }

    // States the applied filters in words, for the selection bar, the review dialog and the result list.
    private string FilterSummary() => FilterText.Describe(SearchBox, ClassFilter, StatusFilter, CategoryFilter, StockFilter,
        ExpiryFilter, BrandFilter, VendorFilter, NoBarcodeFilter, MissingLocalFilter, LooseFilter);

    private async Task RunCatalogBulkAsync(CatalogBulkCommand command, string titleKey, bool destructive = false)
    {
        if (_selection.IsEmpty) return;
        await ShowBulkAsync(CatalogBulk(command, titleKey, destructive));
    }

    /// <summary>Builds the reviewed bulk action for the current selection and filters.</summary>
    internal BulkAction CatalogBulk(CatalogBulkCommand command, string titleKey, bool destructive = false) =>
        new()
        {
            Title = TranslationSource.Instance[titleKey],
            RunLabel = TranslationSource.Instance["Catalog_Apply"],
            SelectionSummary = SelectionSummary(),
            Destructive = destructive,
            // A category change re-files each product's older sale lines, so its batches are cut by line count.
            WeightBudget = command.Action == CatalogBulkAction.Category ? CatalogService.CategoryBatchSaleLines : null,
            Plan = async token =>
            {
                CatalogBulkPreview preview = await _catalog.PreviewBulkAsync(
                    await SelectedIdsAsync(CatalogService.MaxBulkProducts, token), command, token);
                return new BulkPlan(preview.Rows.Select(static row => new BulkCandidate(row.Id,
                        string.IsNullOrEmpty(row.Sku) ? row.Name : $"{row.Sku} · {row.Name}", row.Eligible, row.ReasonKey,
                        row.SaleLines)).ToArray(),
                    TranslationSource.Instance.Format("Catalog_BulkEffect", preview.EligibleIds.Count,
                        preview.AffectedQuantity, preview.AffectedCost));
            },
            Run = (ids, operation, token) => _catalog.RunBulkBatchAsync(command, ids, operation, token),
        };

    private async Task ShowBulkAsync(BulkAction bulk)
    {
        var window = new BulkActionWindow(bulk) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (!window.ChangedAny) return;
        _selection.Clear();
        await LoadAsync(PageMove.Same);
    }

    private async void BulkRemove_OnClick(object sender, RoutedEventArgs e) =>
        await RunCatalogBulkAsync(new(CatalogBulkAction.Archive), "Catalog_RemoveSelected", destructive: true);

    private async void BulkRestore_OnClick(object sender, RoutedEventArgs e) =>
        await RunCatalogBulkAsync(new(CatalogBulkAction.Restore), "Catalog_RestoreSelected");

    private async void BulkBarcode_OnClick(object sender, RoutedEventArgs e) =>
        await RunCatalogBulkAsync(new(CatalogBulkAction.Barcode, TargetCode: _settings.Current.ProductPrefix),
            "Catalog_GenerateBarcodeSelected");

    private async void BulkCategory_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<MasterItem> categories = await _catalog.GetCategoriesAsync();
            if (Choose("Catalog_SetCategory", "Catalog_Category", categories.Where(static item => item.IsActive).ToList(),
                    nameof(MasterItem.Name), nameof(MasterItem.Id)) is long categoryId)
                await RunCatalogBulkAsync(new(CatalogBulkAction.Category, categoryId), "Catalog_SetCategory");
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async void BulkTax_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<TaxRateItem> rates = await _catalog.GetTaxRatesAsync();
            if (Choose("Catalog_SetTaxRate", "Catalog_TaxRate", rates.Where(static item => item.IsActive).ToList(),
                    nameof(TaxRateItem.Name), nameof(TaxRateItem.Id)) is long taxId)
                await RunCatalogBulkAsync(new(CatalogBulkAction.Tax, taxId), "Catalog_SetTaxRate");
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async void BulkClass_OnClick(object sender, RoutedEventArgs e)
    {
        var choices = new[]
        {
            new { Code = "SALEABLE", Name = TranslationSource.Instance["Catalog_Saleable"] },
            new { Code = "INTERNAL", Name = TranslationSource.Instance["Catalog_InternalUse"] },
        };
        if (Choose("Catalog_SetProductClass", "Catalog_ProductClass", choices, "Name", "Code") is string productClass)
            await RunCatalogBulkAsync(new(CatalogBulkAction.Classification, TargetCode: productClass), "Catalog_SetProductClass");
    }

    private object? Choose(string titleKey, string labelKey, System.Collections.IEnumerable items, string display, string value)
    {
        var window = new ChoiceWindow(TranslationSource.Instance[titleKey], SelectionSummary(),
            TranslationSource.Instance[labelKey], items, display, value)
        { Owner = Window.GetWindow(this) };
        return window.ShowDialog() == true ? window.SelectedValue : null;
    }

    private async void BulkLabels_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<long> ids = await SelectedIdsAsync(MaxLabelProducts, CancellationToken.None);
            CatalogBulkPreview preview = await _catalog.PreviewBulkAsync(ids, new(CatalogBulkAction.Labels));
            if (preview.EligibleIds.Count == 0)
            {
                Pager.Status = TranslationSource.Instance["Catalog_BulkNoneEligible"];
                return;
            }
            var inventory = ((App)Application.Current).Services.GetRequiredService<InventoryService>();
            new LabelBatchWindow(_catalog, inventory, _settings, productIds: preview.EligibleIds.ToArray())
            { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception exception)
        {
            Pager.Status = TranslationSource.Instance.Format("Catalog_LabelsLimit", MaxLabelProducts) + " " + UserError.Message(exception);
        }
    }

    private async void Reconcile_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            long[] ids = [.. await SelectedIdsAsync(MaxCountProducts, CancellationToken.None)];
            IServiceProvider services = ((App)Application.Current).Services;
            var window = new DirectCountReviewWindow(services.GetRequiredService<DirectStockCountService>(), ids,
                services.GetRequiredService<IClock>())
            { Owner = Window.GetWindow(this) };
            window.ShowDialog();
            if (!window.HasPosted) return;
            _selection.Clear();
            await LoadAsync(PageMove.Same);
        }
        catch (Exception exception)
        {
            Pager.Status = TranslationSource.Instance.Format("Stock_CountLimit", MaxCountProducts) + " " + UserError.Message(exception);
        }
    }

    private async void WriteOffExpired_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selection.IsEmpty) return;
        IServiceProvider services = ((App)Application.Current).Services;
        var moneyOut = services.GetRequiredService<MoneyOutService>();
        DateOnly date = services.GetRequiredService<IClock>().BusinessDate;
        var bulk = new BulkAction
        {
            Title = TranslationSource.Instance["Catalog_WriteOffExpiredSelected"],
            RunLabel = TranslationSource.Instance["MoneyOut_WriteOff"],
            SelectionSummary = SelectionSummary(),
            Destructive = true,
            // Each lot becomes a damage document: about 3 ms a lot on a full store.
            BatchSize = 20,
            Plan = async token =>
            {
                IReadOnlyList<ExpiredWriteOffLot> lots = await moneyOut.PreviewExpiredWriteOffAsync(
                    await SelectedIdsAsync(CatalogService.MaxBulkProducts, token), date, token);
                return new BulkPlan(lots.Select(static lot => new BulkCandidate(lot.LotId,
                        $"{lot.ProductName} · {lot.LotCode} · {lot.ExpiryDate:yyyy-MM-dd}", true)).ToArray(),
                    TranslationSource.Instance.Format("Catalog_WriteOffEffect", lots.Count,
                        Quantity.FromScaled(lots.Sum(static lot => lot.Quantity.Scaled)),
                        Money.FromScaled(lots.Sum(static lot => lot.CostValue.Scaled)), date.ToString("dd MMM yyyy", TranslationSource.FormatCulture)));
            },
            Run = (ids, operation, token) => moneyOut.WriteOffExpiredBatchAsync(ids, date, operation, token),
        };
        await ShowBulkAsync(bulk);
    }

    private async void ProductActive_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ProductListItem item } box) return;
        bool active = box.IsChecked == true;
        if (active == item.IsActive) return;
        box.IsEnabled = false;
        try
        {
            await _catalog.SetActiveAsync(item.Id, active, item.IsActive);
            await LoadAsync(PageMove.Same);
        }
        catch (Exception exception)
        {
            box.IsChecked = item.IsActive;
            Pager.Status = UserError.Message(exception);
        }
        finally { box.IsEnabled = true; }
    }

    private async void New_OnClick(object sender, RoutedEventArgs e)
    {
        var editor = new ProductEditorWindow(_catalog) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true) await LoadAsync(PageMove.Same);
    }

    private async void Masters_OnClick(object sender, RoutedEventArgs e)
    {
        new CatalogMastersWindow(_catalog) { Owner = Window.GetWindow(this) }.ShowDialog();
        try
        {
            await LoadMastersAsync();
            await LoadAsync(PageMove.Same);
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async void Import_OnClick(object sender, RoutedEventArgs e)
    {
        var import = new ProductImportWindow(_importer) { Owner = Window.GetWindow(this) };
        if (import.ShowDialog() == true) await ReloadAsync();
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
            await ReloadAsync();
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private void OpeningTemplate_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = "opening-stock-template.xlsx" };
        if (picker.ShowDialog() != true) return;
        try
        {
            OpeningStockExcelImporter.WriteTemplate(picker.FileName);
            Pager.Status = TranslationSource.Instance["Stock_TemplateSaved"];
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }
}
