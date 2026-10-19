using System.Windows;
using System.Windows.Controls;
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
        await ApplyAsync(CatalogBulkAction.Category,
            ids => _catalog.BulkChangeCategoryAsync(ids, categoryId));
    }

    private async void ApplyTax_OnClick(object sender, RoutedEventArgs e)
    {
        if (TaxBox.SelectedValue is not long taxId) return;
        await ApplyAsync(CatalogBulkAction.Tax,
            ids => _catalog.BulkChangeTaxRateAsync(ids, taxId));
    }

    private async void ApplyClass_OnClick(object sender, RoutedEventArgs e)
    {
        if (ClassBox.SelectedItem is not ComboBoxItem item || item.Tag is not string productClass) return;
        await ApplyAsync(CatalogBulkAction.Classification,
            ids => _catalog.BulkChangeClassificationAsync(ids, productClass), productClass);
    }

    private async Task ApplyAsync(CatalogBulkAction action,
        Func<IReadOnlyCollection<long>, Task<int>> operation, string? targetClass = null)
    {
        try
        {
            CatalogBulkPreview preview = await _catalog.PreviewBulkAsync(_productIds, action, targetClass);
            if (preview.EligibleIds.Count == 0)
            {
                StatusText.Text = TranslationSource.Instance["Catalog_BulkNoneEligible"];
                return;
            }
            if (MessageBox.Show(TranslationSource.Instance.Format("Catalog_BulkPreview",
                    preview.Selected, preview.EligibleIds.Count, preview.Skipped,
                    preview.AffectedQuantity, preview.AffectedCost),
                TranslationSource.Instance["Catalog_BulkEdit"], MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            int changed = await operation(preview.EligibleIds);
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
