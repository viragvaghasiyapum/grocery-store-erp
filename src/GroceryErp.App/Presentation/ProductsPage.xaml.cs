using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Catalog;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class ProductsPage : Page
{
    private int PageSize => int.TryParse((PageSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int size) ? size : 50;
    private readonly CatalogService _catalog;
    private readonly ISettings _settings;
    private readonly ProductExcelImporter _importer;
    private int _searchVersion;
    private int _refreshVersion;
    private bool _loadingMasters;
    private readonly Stack<(string? Name, long? Id)> _previousPages = new();
    private string? _afterName;
    private long? _afterId;
    private ProductSearchPage? _currentPage;
    private readonly Dictionary<long, bool> _selectedProducts = new();
    private int _selectionVersion;
    private bool _pageLoading;
    private CancellationTokenSource? _pageLoadCancellation;

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
        UpdateSelectionActions();
        Loaded += async (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged += TranslationChanged;
            if (!loadOnShow) return;
            try
            {
                await LoadMastersAsync();
                await RefreshAsync();
            }
            catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        };
        Unloaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= TranslationChanged;
            ++_searchVersion;
            ++_refreshVersion;
            _pageLoadCancellation?.Cancel();
            _pageLoading = false;
        };
    }

    private void TranslationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Item[]" && IsLoaded)
        {
            StatusText.Text = TranslationSource.Instance.Format("Catalog_Count", ProductsGrid.Items.Count);
            UpdateSelectionActions();
        }
    }

    private async void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ClearSelection();
        int version = ++_searchVersion;
        long searchStarted = Stopwatch.GetTimestamp();
        // Invalidate the current result as soon as the text changes. Waiting for
        // the debounce would let an older query replace rows for the new search.
        ++_refreshVersion;
        _pageLoadCancellation?.Cancel();
        _pageLoading = true;
        PreviousPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
        await Task.Delay(20);
        if (version == _searchVersion && IsLoaded) await RefreshAsync(searchStarted: searchStarted);
    }

    private async void Filter_OnChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) { ClearSelection(); await RefreshAsync(); }
    }

    private async void MasterFilter_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_loadingMasters) { ClearSelection(); await RefreshAsync(); }
    }

    private async Task LoadMastersAsync()
    {
        _loadingMasters = true;
        try
        {
            long? categoryId = CategoryFilter.SelectedValue as long?;
            long? brandId = BrandFilter.SelectedValue as long?;
            CategoryFilter.ItemsSource = (await _catalog.GetCategoriesAsync()).Where(static item => item.IsActive).ToList();
            BrandFilter.ItemsSource = (await _catalog.GetBrandsAsync()).Where(static item => item.IsActive).ToList();
            CategoryFilter.SelectedValue = categoryId;
            BrandFilter.SelectedValue = brandId;
        }
        finally { _loadingMasters = false; }
    }

    private Task RefreshAsync(long? searchStarted = null, CancellationToken cancellationToken = default) =>
        LoadPageAsync(null, null, reset: true, searchStarted, cancellationToken);

    private async Task LoadPageAsync(string? afterName, long? afterId, bool reset,
        long? searchStarted = null, CancellationToken cancellationToken = default)
    {
        using var timing = UiRefreshProbe.Track("products", this, searchStarted);
        _pageLoadCancellation?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pageLoadCancellation = request;
        int version = ++_refreshVersion;
        try
        {
            _pageLoading = true;
            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            ProductSearchPage page = await _catalog.SearchPageAsync(CurrentFilter(PageSize),
                afterName, afterId, request.Token);
            if (version != _refreshVersion || !IsLoaded) return;
            if (reset) _previousPages.Clear();
            _afterName = afterName;
            _afterId = afterId;
            _currentPage = page;
            if (searchStarted is not null)
                timing.SearchShape = page.Items.Count == 0 ? "empty" : "populated";
            timing.Bind(() => ProductsGrid.ItemsSource = page.Items);
            EmptyText.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = TranslationSource.Instance.Format("Catalog_Count", ProductsGrid.Items.Count);
            PreviousPageButton.IsEnabled = _previousPages.Count > 0;
            NextPageButton.IsEnabled = page.HasMore;
            UpdateSelectionActions();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (version == _refreshVersion && IsLoaded)
                StatusText.Text = UserError.Message(exception);
        }
        finally
        {
            if (ReferenceEquals(_pageLoadCancellation, request))
                _pageLoadCancellation = null;
            if (_refreshVersion == version)
            {
                _pageLoading = false;
            }
            request.Dispose();
        }
    }

    private ProductSearchFilter CurrentFilter(int limit) => new(
                SearchBox.Text,
                CategoryId: CategoryFilter.SelectedValue as long?,
                BrandId: BrandFilter.SelectedValue as long?,
                LowStockOnly: LowStockFilter.IsChecked == true,
                NoBarcodeOnly: NoBarcodeFilter.IsChecked == true,
                MissingLocalNameOnly: MissingLocalFilter.IsChecked == true,
                IncludeArchived: ArchivedFilter.IsChecked == true,
                LooseOnly: LooseFilter.IsChecked == true,
                Limit: limit);

    private async void NextPage_OnClick(object sender, RoutedEventArgs e)
    {
        if (_pageLoading || _currentPage is not { HasMore: true, NextName: not null, NextId: not null } page) return;
        string? previousName = _afterName;
        long? previousId = _afterId;
        await LoadPageAsync(page.NextName, page.NextId, reset: false);
        if (_afterName == page.NextName && _afterId == page.NextId)
        {
            _previousPages.Push((previousName, previousId));
            PreviousPageButton.IsEnabled = true;
        }
    }

    private async void PageSize_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await RefreshAsync();
    }

    private async void PreviousPage_OnClick(object sender, RoutedEventArgs e)
    {
        if (_pageLoading || _previousPages.Count == 0) return;
        (string? name, long? id) = _previousPages.Peek();
        await LoadPageAsync(name, id, reset: false);
        if (_afterName == name && _afterId == id)
        {
            _previousPages.Pop();
            PreviousPageButton.IsEnabled = _previousPages.Count > 0;
        }
    }

    private void ProductsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionActions();
    }

    private void ProductCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: ProductListItem item } box)
            box.IsChecked = _selectedProducts.ContainsKey(item.Id);
    }

    private void ProductCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ProductListItem item } box) return;
        if (box.IsChecked == true) _selectedProducts[item.Id] = item.IsActive;
        else _selectedProducts.Remove(item.Id);
        UpdateSelectionActions();
    }

    private void SelectPage_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (ProductListItem item in _currentPage?.Items ?? []) _selectedProducts[item.Id] = item.IsActive;
        ProductsGrid.Items.Refresh();
        UpdateSelectionActions();
    }

    private async void SelectMatching_OnClick(object sender, RoutedEventArgs e)
    {
        int version = _refreshVersion;
        int selectionVersion = _selectionVersion;
        var selected = new Dictionary<long, bool>();
        ProductSearchFilter filter = CurrentFilter(100);
        string? name = null;
        long? id = null;
        SelectMatchingButton.IsEnabled = false;
        try
        {
            while (true)
            {
                ProductSearchPage page = await _catalog.SearchPageAsync(filter, name, id);
                if (version != _refreshVersion || selectionVersion != _selectionVersion) return;
                foreach (ProductListItem item in page.Items) selected[item.Id] = item.IsActive;
                if (!page.HasMore || page.NextName is null || page.NextId is null) break;
                name = page.NextName;
                id = page.NextId;
            }
            foreach ((long productId, bool active) in selected) _selectedProducts[productId] = active;
            ProductsGrid.Items.Refresh();
            UpdateSelectionActions();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally { SelectMatchingButton.IsEnabled = true; }
    }

    private void ClearSelection_OnClick(object sender, RoutedEventArgs e) => ClearSelection();

    private void ClearSelection()
    {
        ++_selectionVersion;
        _selectedProducts.Clear();
        ProductsGrid?.UnselectAll();
        if (ProductsGrid is not null) ProductsGrid.Items.Refresh();
        UpdateSelectionActions();
    }

    private void UpdateSelectionActions()
    {
        if (EditButton is null) return;
        bool hasSelection = _selectedProducts.Count > 0 || ProductsGrid.SelectedItems.Count > 0;
        bool single = ProductsGrid.SelectedItems.Count == 1;
        bool active = single && ProductsGrid.SelectedItem is ProductListItem { IsActive: true };
        bool archived = single && ProductsGrid.SelectedItem is ProductListItem { IsActive: false };
        EditButton.IsEnabled = single;
        LotsButton.IsEnabled = single;
        LedgerButton.IsEnabled = single;
        LabelsButton.IsEnabled = hasSelection;
        BarcodeButton.IsEnabled = active;
        BulkButton.IsEnabled = hasSelection;
        RestoreButton.IsEnabled = archived;
        RemoveButton.IsEnabled = active;
        BulkRemoveButton.IsEnabled = _selectedProducts.Count > 0 && _selectedProducts.Values.All(static active => active);
        BulkRestoreButton.IsEnabled = _selectedProducts.Count > 0 && _selectedProducts.Values.All(static active => !active);
        ClearSelectionButton.IsEnabled = _selectedProducts.Count > 0;
        SelectedCountText.Text = TranslationSource.Instance.Format("Selection_Count", _selectedProducts.Count);
    }

    private async void BulkRemove_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProducts.Count == 0 || _selectedProducts.Values.Any(static active => !active)) return;
        if (MessageBox.Show(TranslationSource.Instance.Format("Catalog_BulkRemoveConfirm", _selectedProducts.Count),
            TranslationSource.Instance["Catalog_Title"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            int changed = await _catalog.BulkRemoveAsync(_selectedProducts.Keys.ToArray());
            ClearSelection();
            await RefreshAsync();
            StatusText.Text = TranslationSource.Instance.Format("Catalog_BulkRemoved", changed);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void BulkRestore_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProducts.Count == 0 || _selectedProducts.Values.Any(static active => active)) return;
        try
        {
            int changed = await _catalog.BulkRestoreAsync(_selectedProducts.Keys.ToArray());
            ClearSelection();
            await RefreshAsync();
            StatusText.Text = TranslationSource.Instance.Format("Catalog_BulkRestored", changed);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ProductsGrid_OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ProductsGrid.SelectedItem is ProductListItem) Edit_OnClick(sender, e);
    }

    private async void GenerateBarcode_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductListItem product) return;
        try
        {
            string barcode = await _catalog.GenerateInternalBarcodeAsync(product.Id, _settings.Current.ProductPrefix);
            MessageBox.Show(barcode, TranslationSource.Instance["Catalog_GenerateBarcode"], MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void New_OnClick(object sender, RoutedEventArgs e)
    {
        var editor = new ProductEditorWindow(_catalog) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true) await RefreshAsync();
    }

    private async void Edit_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductListItem product) return;
        var editor = new ProductEditorWindow(_catalog, product.Id) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true) await RefreshAsync();
    }

    private void Lots_OnClick(object sender, RoutedEventArgs e) => OpenStock(showLedger: false);

    private void Ledger_OnClick(object sender, RoutedEventArgs e) => OpenStock(showLedger: true);

    private void OpenStock(bool showLedger)
    {
        if (ProductsGrid.SelectedItem is not ProductListItem product) return;
        var inventory = ((App)Application.Current).Services.GetRequiredService<InventoryService>();
        new ProductStockWindow(inventory, product, showLedger) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async void Masters_OnClick(object sender, RoutedEventArgs e)
    {
        var masters = new CatalogMastersWindow(_catalog) { Owner = Window.GetWindow(this) };
        masters.ShowDialog();
        await LoadMastersAsync();
        await RefreshAsync();
    }

    private async void Restore_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductListItem { IsActive: false } product) return;
        try
        {
            await _catalog.RestoreAsync(product.Id);
            ClearSelection();
            await RefreshAsync();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void Import_OnClick(object sender, RoutedEventArgs e)
    {
        var import = new ProductImportWindow(_importer) { Owner = Window.GetWindow(this) };
        if (import.ShowDialog() == true) { ClearSelection(); await RefreshAsync(); }
    }

    private void Labels_OnClick(object sender, RoutedEventArgs e)
    {
        long[] ids = _selectedProducts.Keys.Concat(ProductsGrid.SelectedItems.Cast<ProductListItem>()
            .Select(static item => item.Id)).Distinct().ToArray();
        if (ids.Length == 0) return;
        try
        {
            var inventory = ((App)Application.Current).Services.GetRequiredService<InventoryService>();
            var labels = new LabelBatchWindow(_catalog, inventory, _settings, productIds: ids)
            {
                Owner = Window.GetWindow(this),
            };
            labels.ShowDialog();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void Bulk_OnClick(object sender, RoutedEventArgs e)
    {
        long[] ids = _selectedProducts.Keys.Concat(ProductsGrid.SelectedItems.Cast<ProductListItem>()
            .Select(static item => item.Id)).Distinct().ToArray();
        if (ids.Length == 0) return;
        var bulk = new CatalogBulkEditWindow(_catalog, ids) { Owner = Window.GetWindow(this) };
        if (bulk.ShowDialog() == true) await RefreshAsync();
    }

    private async void Remove_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductListItem product) return;
        MessageBoxResult choice = MessageBox.Show(
            TranslationSource.Instance.Format("Catalog_RemoveConfirm", product.Name),
            TranslationSource.Instance["Catalog_Title"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes) return;
        try
        {
            ProductRemoval removal = await _catalog.RemoveAsync(product.Id);
            StatusText.Text = TranslationSource.Instance[removal == ProductRemoval.Deleted ? "Catalog_Deleted" : "Catalog_ArchivedResult"];
            ClearSelection();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private static void ShowError(Exception exception)
    {
        string message = UserError.Message(exception);
        MessageBox.Show(message, TranslationSource.Instance["Catalog_Title"], MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
