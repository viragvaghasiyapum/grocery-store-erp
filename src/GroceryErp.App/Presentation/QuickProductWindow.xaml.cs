using System.Globalization;
using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Core;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class QuickProductWindow : Window
{
    private readonly CatalogService _catalog;
    private readonly string _barcode;

    public long? SavedProductId { get; private set; }

    public QuickProductWindow(CatalogService catalog, string barcode, bool loadOnShow = true)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _barcode = InputNormalizer.Normalize(barcode.Trim());
        InitializeComponent();
        BarcodeBox.Text = _barcode;
        if (loadOnShow) Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            SkuBox.Text = await _catalog.SuggestSkuAsync();
            CategoryBox.ItemsSource = (await _catalog.GetCategoriesAsync()).Where(static item => item.IsActive);
            UnitBox.ItemsSource = await _catalog.GetUnitsAsync();
            TaxBox.ItemsSource = (await _catalog.GetTaxRatesAsync()).Where(static item => item.IsActive);
            CategoryBox.SelectedIndex = UnitBox.SelectedIndex = TaxBox.SelectedIndex = 0;
            SaveButton.IsEnabled = CategoryBox.Items.Count > 0 && UnitBox.Items.Count > 0 && TaxBox.Items.Count > 0;
            NameBox.Focus();
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveButton.IsEnabled = false;
            ErrorText.Text = string.Empty;
            if (CategoryBox.SelectedValue is not long categoryId || UnitBox.SelectedValue is not long unitId ||
                TaxBox.SelectedValue is not long taxId)
                throw new InvalidOperationException(TranslationSource.Instance["Pos_QuickRequired"]);
            Money price = ParseMoney(PriceBox.Text);
            Money mrp = ParseMoney(MrpBox.Text);
            string symbology = _barcode.Length switch { 8 => "EAN8", 12 => "UPCA", 13 => "EAN13", _ => "CODE128" };
            SavedProductId = await _catalog.CreateAsync(new ProductInput(
                SkuBox.Text, NameBox.Text, null, null, categoryId, null, unitId,
                HsnBox.Text, taxId, true, false, null, false, null, null,
                Quantity.Zero, price, mrp,
                [new ProductBarcodeInput(_barcode, Quantity.One, symbology, true)]));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = UserError.Message(exception);
            SaveButton.IsEnabled = true;
        }
    }

    private static Money ParseMoney(string text)
    {
        if (!decimal.TryParse(InputNormalizer.Normalize(text), NumberStyles.Number, TranslationSource.FormatCulture, out decimal value) || value < 0)
            throw new InvalidOperationException(TranslationSource.Instance["Pos_QuickRequired"]);
        return new Money(value);
    }
}
