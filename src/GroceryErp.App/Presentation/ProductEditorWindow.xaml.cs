using System.Globalization;
using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Core;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class ProductEditorWindow : Window
{
    private readonly CatalogService _catalog;
    private readonly long? _productId;
    private IReadOnlyCollection<ProductBarcodeInput> _originalBarcodes = [];

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
            FirstLotDateBox.SelectedDate = ((App)Application.Current).Services
                .GetRequiredService<IClock>().BusinessDate.ToDateTime(TimeOnly.MinValue);
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
                FirstLotTab.Visibility = Visibility.Collapsed;
                ProductInput product = (await _catalog.GetAsync(_productId.Value)).Product;
                _originalBarcodes = product.Barcodes;
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
                ExpiryAlertBox.Text = product.ExpiryAlertDays?.ToString(CultureInfo.InvariantCulture);
                ReorderBox.Text = product.ReorderLevel.Value.ToString("0.000", TranslationSource.FormatCulture);
                PriceBox.Text = product.DefaultSellingPrice?.Value.ToString("0.000", TranslationSource.FormatCulture);
                MrpBox.Text = product.DefaultMrp?.Value.ToString("0.000", TranslationSource.FormatCulture);
                ProductClassBox.SelectedValue = product.ProductClass;
                BarcodesBox.Text = string.Join(Environment.NewLine, product.Barcodes.Select(static item => item.Barcode));
            }
            SaveButton.IsEnabled = categories.Count > 0;
        }
        catch (Exception exception)
        {
            ErrorText.Text = UserError.Message(exception);
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
            List<ProductBarcodeInput> barcodes = InputNormalizer.Normalize(BarcodesBox.Text)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Select((barcode, index) => _originalBarcodes.FirstOrDefault(item => item.Barcode == barcode) ?? new ProductBarcodeInput(barcode, Quantity.One,
                    barcode.Length == 8 ? "EAN8" : barcode.Length == 12 ? "UPCA" : barcode.Length == 13 ? "EAN13" : "CODE128",
                    index == 0 && !_originalBarcodes.Any(static item => item.IsPrimary))).ToList();
            if (barcodes.Count > 0 && !barcodes.Any(static item => item.IsPrimary))
                barcodes[0] = barcodes[0] with { IsPrimary = true };
            var input = new ProductInput(
                SkuBox.Text, NameBox.Text, LocalNameBox.Text, AliasesBox.Text, categoryId,
                BrandBox.SelectedValue as long?, unitId, HsnBox.Text, TaxRateBox.SelectedValue as long?,
                PricesIncludeTaxBox.IsChecked == true, LooseBox.IsChecked == true, PluBox.Text,
                TrackExpiryBox.IsChecked == true, ParseNullableInt(ShelfLifeBox.Text), ParseNullableInt(ExpiryAlertBox.Text, allowZero: true),
                new Quantity(ParseDecimal(ReorderBox.Text, required: true) ?? 0),
                ParseMoney(PriceBox.Text), ParseMoney(MrpBox.Text), barcodes)
            { ProductClass = ProductClassBox.SelectedValue as string ?? "SALEABLE" };
            if (_productId is null && AddFirstLotBox.IsChecked == true)
            {
                DateOnly date = DateOnly.FromDateTime(FirstLotDateBox.SelectedDate ??
                    throw new DomainException(ErrorCode.Validation, "First lot received date is required."));
                var lot = new NewProductLotInput(
                    new Quantity(ParseDecimal(FirstLotQtyBox.Text, required: true) ?? 0),
                    new Money(ParseDecimal(FirstLotCostBox.Text, required: true) ?? 0),
                    new Money(ParseDecimal(FirstLotPriceBox.Text, required: true) ?? 0),
                    ParseMoney(FirstLotMrpBox.Text), FirstLotBatchBox.Text,
                    FirstLotExpiryBox.SelectedDate is { } expiry ? DateOnly.FromDateTime(expiry) : null,
                    date, FirstLotReasonBox.Text);
                IServiceProvider services = ((App)Application.Current).Services;
                await _catalog.CreateWithDirectLotAsync(input, lot,
                    services.GetRequiredService<IStockLedger>(),
                    services.GetRequiredService<DocNumberService>(),
                    services.GetRequiredService<PeriodGuard>(), OperationGuard.NewId());
            }
            else if (_productId is null) await _catalog.CreateAsync(input);
            else await _catalog.UpdateAsync(_productId.Value, input);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = UserError.Message(exception);
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
        if (!decimal.TryParse(InputNormalizer.Normalize(text), NumberStyles.Number, TranslationSource.FormatCulture, out decimal value))
            throw new DomainException(ErrorCode.Validation, "Enter a valid number.");
        return value;
    }

    private static int? ParseNullableInt(string text, bool allowZero = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!int.TryParse(InputNormalizer.Normalize(text), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < (allowZero ? 0 : 1))
            throw new DomainException(ErrorCode.Validation, "Enter a positive whole number.");
        return value;
    }
}
