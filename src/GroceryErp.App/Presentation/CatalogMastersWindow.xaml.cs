using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Catalog;
using GroceryErp.Core.Errors;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class CatalogMastersWindow : Window
{
    private readonly CatalogService _catalog;

    public CatalogMastersWindow(CatalogService catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IReadOnlyList<MasterItem> categories = await _catalog.GetCategoriesAsync();
        CategoryList.ItemsSource = categories;
        ParentCategoryBox.ItemsSource = categories.Where(static item => item.IsActive).ToList();
        BrandList.ItemsSource = await _catalog.GetBrandsAsync();
        UnitList.ItemsSource = await _catalog.GetUnitDetailsAsync();
        TaxList.ItemsSource = await _catalog.GetTaxRatesAsync();
    }

    private void CategoryList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not MasterItem item) return;
        CategoryNameBox.Text = item.Name;
        CategoryLocalBox.Text = item.NameLocal;
        ParentCategoryBox.SelectedValue = item.ParentId;
    }

    private void BrandList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BrandList.SelectedItem is MasterItem item) BrandNameBox.Text = item.Name;
    }

    private void UnitList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UnitList.SelectedItem is not UnitItem item) return;
        UnitCodeBox.Text = item.Code;
        UnitNameBox.Text = item.Name;
        UnitLocalBox.Text = item.NameLocal;
        UnitFractionBox.IsChecked = item.AllowFraction;
    }

    private void TaxList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaxList.SelectedItem is not TaxRateItem item) return;
        TaxNameBox.Text = item.Name;
        TaxRateValueBox.Text = item.Rate.ToString("0.###", CultureInfo.InvariantCulture);
        TaxActiveBox.IsChecked = item.IsActive;
    }

    private void NewCategory_OnClick(object sender, RoutedEventArgs e)
    {
        CategoryList.SelectedIndex = -1;
        CategoryNameBox.Clear();
        CategoryLocalBox.Clear();
        CategoryExpiryBox.Clear();
        ParentCategoryBox.SelectedIndex = -1;
        StatusText.Text = string.Empty;
        CategoryNameBox.Focus();
    }

    private void NewBrand_OnClick(object sender, RoutedEventArgs e)
    {
        BrandList.SelectedIndex = -1;
        BrandNameBox.Clear();
        StatusText.Text = string.Empty;
        BrandNameBox.Focus();
    }

    private void NewUnit_OnClick(object sender, RoutedEventArgs e)
    {
        UnitList.SelectedIndex = -1;
        UnitCodeBox.Clear();
        UnitNameBox.Clear();
        UnitLocalBox.Clear();
        UnitFractionBox.IsChecked = false;
        StatusText.Text = string.Empty;
        UnitCodeBox.Focus();
    }

    private void NewTax_OnClick(object sender, RoutedEventArgs e)
    {
        TaxList.SelectedIndex = -1;
        TaxNameBox.Clear();
        TaxRateValueBox.Clear();
        TaxActiveBox.IsChecked = true;
        StatusText.Text = string.Empty;
        TaxNameBox.Focus();
    }

    private async void SaveCategory_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            int? expiryDays = null;
            if (!string.IsNullOrWhiteSpace(CategoryExpiryBox.Text))
            {
                if (!int.TryParse(CategoryExpiryBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
                    throw new DomainException(ErrorCode.Validation, "Expiry days must be zero or greater.");
                expiryDays = parsed;
            }
            var input = new CategoryInput(CategoryNameBox.Text, CategoryLocalBox.Text,
                ParentCategoryBox.SelectedValue as long?, expiryDays);
            if (CategoryList.SelectedItem is MasterItem selected)
                await _catalog.UpdateCategoryAsync(selected.Id, input);
            else
                await _catalog.CreateCategoryAsync(input);
            NewCategory_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void SaveBrand_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (BrandList.SelectedItem is MasterItem selected)
                await _catalog.UpdateBrandAsync(selected.Id, BrandNameBox.Text);
            else
                await _catalog.CreateBrandAsync(BrandNameBox.Text);
            NewBrand_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void SaveUnit_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (UnitList.SelectedItem is UnitItem selected)
                await _catalog.UpdateUnitAsync(selected.Id, UnitCodeBox.Text, UnitNameBox.Text, UnitLocalBox.Text, UnitFractionBox.IsChecked == true);
            else
                await _catalog.CreateUnitAsync(UnitCodeBox.Text, UnitNameBox.Text, UnitLocalBox.Text, UnitFractionBox.IsChecked == true);
            NewUnit_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void SaveTax_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!decimal.TryParse(TaxRateValueBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal rate))
                throw new DomainException(ErrorCode.Validation, "Enter a valid tax percentage.");
            if (TaxList.SelectedItem is TaxRateItem selected)
                await _catalog.UpdateTaxRateAsync(selected.Id, TaxNameBox.Text, rate, TaxActiveBox.IsChecked == true);
            else
                await _catalog.CreateTaxRateAsync(TaxNameBox.Text, rate);
            NewTax_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ShowError(Exception exception) => StatusText.Text = exception is DomainException domain
        ? TranslationSource.Instance[$"Error_{domain.Code}"]
        : exception.Message;
}
