using System.Globalization;
using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class ProductEditorWindow : Window
{
    private readonly CatalogService _catalog;
    private readonly long? _productId;

    public ProductEditorWindow(CatalogService catalog, long? productId = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _productId = productId;
        InitializeComponent();
        Loaded += LoadAsync;
    }

    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveButton.IsEnabled = false;
            IReadOnlyList<MasterItem> categories = await _catalog.GetCategoriesAsync();
            CategoryBox.ItemsSource = categories.Where(static item => item.IsActive);
            BrandBox.ItemsSource = (await _catalog.GetBrandsAsync()).Where(static item => item.IsActive);
            UnitBox.ItemsSource = await _catalog.GetUnitsAsync();
            TaxRateBox.ItemsSource = (await _catalog.GetTaxRatesAsync()).Where(static item => item.IsActive);
            if (_productId is null)
            {
                SkuBox.Text = await _catalog.SuggestSkuAsync();
                CategoryBox.SelectedIndex = 0;
                UnitBox.SelectedIndex = 0;
                TaxRateBox.SelectedIndex = 0;
                if (categories.Count == 0) ErrorText.Text = TranslationSource.Instance["Catalog_CategoryRequiredFirst"];
            }
            else
            {
                ProductInput product = (await _catalog.GetAsync(_productId.Value)).Product;
                SkuBox.Text = product.Sku;
                NameBox.Text = product.Name;
                LocalNameBox.Text = product.NameLocal;
                AliasesBox.Text = product.SearchTerms;
                CategoryBox.SelectedValue = product.CategoryId;
                BrandBox.SelectedValue = product.BrandId;
                UnitBox.SelectedValue = product.UomId;
                HsnBox.Text = product.HsnCode;
                TaxRateBox.SelectedValue = product.TaxRateId;
                PricesIncludeTaxBox.IsChecked = product.PriceIncludesTax;
                LooseBox.IsChecked = product.IsLoose;
                PluBox.Text = product.PluCode;
                TrackExpiryBox.IsChecked = product.TrackExpiry;
                ShelfLifeBox.Text = product.DefaultShelfLifeDays?.ToString(CultureInfo.InvariantCulture);
                ReorderBox.Text = product.ReorderLevel.ToString();
                PriceBox.Text = product.DefaultSellingPrice?.ToString();
                MrpBox.Text = product.DefaultMrp?.ToString();
                BarcodesBox.Text = string.Join(Environment.NewLine, product.Barcodes.Select(static item => item.Barcode));
            }
            SaveButton.IsEnabled = categories.Count > 0;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void LooseBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (PluBox is not null) PluBox.IsEnabled = LooseBox.IsChecked == true;
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorText.Text = string.Empty;
            SaveButton.IsEnabled = false;
            if (CategoryBox.SelectedValue is not long categoryId || UnitBox.SelectedValue is not long unitId)
                throw new DomainException(ErrorCode.Validation, "Category and unit are required.");
            IReadOnlyCollection<ProductBarcodeInput> barcodes = BarcodesBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Select(static (barcode, index) => new ProductBarcodeInput(barcode, Quantity.One,
                    barcode.Length == 8 ? "EAN8" : barcode.Length == 12 ? "UPCA" : barcode.Length == 13 ? "EAN13" : "CODE128",
                    index == 0)).ToList();
            var input = new ProductInput(
                SkuBox.Text, NameBox.Text, LocalNameBox.Text, AliasesBox.Text, categoryId,
                BrandBox.SelectedValue as long?, unitId, HsnBox.Text, TaxRateBox.SelectedValue as long?,
                PricesIncludeTaxBox.IsChecked == true, LooseBox.IsChecked == true, PluBox.Text,
                TrackExpiryBox.IsChecked == true, ParseNullableInt(ShelfLifeBox.Text), null,
                new Quantity(ParseDecimal(ReorderBox.Text, required: true) ?? 0),
                ParseMoney(PriceBox.Text), ParseMoney(MrpBox.Text), barcodes);
            if (_productId is null) await _catalog.CreateAsync(input);
            else await _catalog.UpdateAsync(_productId.Value, input);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception is DomainException domain
                ? TranslationSource.Instance[$"Error_{domain.Code}"]
                : exception.Message;
            SaveButton.IsEnabled = true;
        }
    }

    private static Money? ParseMoney(string text) => ParseDecimal(text, required: false) is { } value ? new Money(value) : null;

    private static decimal? ParseDecimal(string text, bool required)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (required) throw new DomainException(ErrorCode.Validation, "Enter a number.");
            return null;
        }
        if (!decimal.TryParse(text, NumberStyles.Number, TranslationSource.FormatCulture, out decimal value))
            throw new DomainException(ErrorCode.Validation, "Enter a valid number.");
        return value;
    }

    private static int? ParseNullableInt(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0)
            throw new DomainException(ErrorCode.Validation, "Enter a positive whole number.");
        return value;
    }
}
