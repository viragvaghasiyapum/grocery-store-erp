using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class CatalogBulkEditWindow : Window
{
    private readonly CatalogService _catalog;
    private readonly IReadOnlyCollection<long> _productIds;

    public CatalogBulkEditWindow(CatalogService catalog, IReadOnlyCollection<long> productIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(productIds);
        _catalog = catalog;
        _productIds = productIds;
        InitializeComponent();
        CountText.Text = TranslationSource.Instance.Format("Catalog_SelectedCount", productIds.Count);
        Loaded += LoadAsync;
    }

    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            CategoryBox.ItemsSource = (await _catalog.GetCategoriesAsync()).Where(static item => item.IsActive);
            TaxBox.ItemsSource = (await _catalog.GetTaxRatesAsync()).Where(static item => item.IsActive);
        }
        catch (Exception exception)
        {
            StatusText.Text = UserError.Message(exception);
        }
    }

    private async void ApplyCategory_OnClick(object sender, RoutedEventArgs e)
    {
        if (CategoryBox.SelectedValue is not long categoryId) return;
        await ApplyAsync(() => _catalog.BulkChangeCategoryAsync(_productIds, categoryId));
    }

    private async void ApplyTax_OnClick(object sender, RoutedEventArgs e)
    {
        if (TaxBox.SelectedValue is not long taxId) return;
        await ApplyAsync(() => _catalog.BulkChangeTaxRateAsync(_productIds, taxId));
    }

    private async Task ApplyAsync(Func<Task<int>> operation)
    {
        try
        {
            int changed = await operation();
            MessageBox.Show(TranslationSource.Instance.Format("Catalog_BulkComplete", changed),
                TranslationSource.Instance["Catalog_BulkEdit"], MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = UserError.Message(exception);
        }
    }
}
