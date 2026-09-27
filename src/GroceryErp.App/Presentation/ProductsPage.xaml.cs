using System.Windows;
using System.Windows.Controls;
using GroceryErp.Catalog;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class ProductsPage : Page
{
    private readonly CatalogService _catalog;
    private readonly ISettings _settings;
    private readonly ProductExcelImporter _importer;
    private int _searchVersion;

    public ProductsPage()
    {
        InitializeComponent();
        IServiceProvider services = ((App)Application.Current).Services;
        _catalog = services.GetRequiredService<CatalogService>();
        _settings = services.GetRequiredService<ISettings>();
        _importer = services.GetRequiredService<ProductExcelImporter>();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        int version = ++_searchVersion;
        await Task.Delay(180);
        if (version == _searchVersion && IsLoaded) await RefreshAsync();
    }

    private async void Filter_OnChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) await RefreshAsync();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ProductsGrid.ItemsSource = await _catalog.SearchAsync(new ProductSearchFilter(
                SearchBox.Text,
                NoBarcodeOnly: NoBarcodeFilter.IsChecked == true,
                MissingLocalNameOnly: MissingLocalFilter.IsChecked == true,
                IncludeArchived: ArchivedFilter.IsChecked == true), cancellationToken);
            StatusText.Text = TranslationSource.Instance.Format("Catalog_Count", ProductsGrid.Items.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusText.Text = exception.Message;
        }
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

    private async void Masters_OnClick(object sender, RoutedEventArgs e)
    {
        var masters = new CatalogMastersWindow(_catalog) { Owner = Window.GetWindow(this) };
        masters.ShowDialog();
        await RefreshAsync();
    }

    private async void Import_OnClick(object sender, RoutedEventArgs e)
    {
        var import = new ProductImportWindow(_importer) { Owner = Window.GetWindow(this) };
        if (import.ShowDialog() == true) await RefreshAsync();
    }

    private async void Labels_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductListItem product) return;
        try
        {
            ProductDetails details = await _catalog.GetAsync(product.Id);
            var labels = new ProductLabelWindow(details, _settings.Current.LabelPrinterName, _settings.Current.LabelSize)
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
        long[] ids = ProductsGrid.SelectedItems.Cast<ProductListItem>().Select(static item => item.Id).ToArray();
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
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private static void ShowError(Exception exception)
    {
        string message = exception is DomainException domain
            ? TranslationSource.Instance[$"Error_{domain.Code}"]
            : exception.Message;
        MessageBox.Show(message, TranslationSource.Instance["Catalog_Title"], MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
