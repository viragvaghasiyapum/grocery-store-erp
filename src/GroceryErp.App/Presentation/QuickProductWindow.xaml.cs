using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Core;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Creates a product from an unknown scanned barcode with the essential fields only.</summary>
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
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        string? sku = form.Text(SkuField, SkuBox.Text, required: true);
        string? name = form.Text(NameField, NameBox.Text, required: true, maxLength: 120);
        long? categoryId = form.Choice(CategoryField, CategoryBox.SelectedValue);
        long? unitId = form.Choice(UnitField, UnitBox.SelectedValue);
        Money? price = form.Money(PriceField, PriceBox.Text, required: true);
        Money? mrp = form.Money(MrpField, MrpBox.Text);
        long? taxId = form.Choice(TaxField, TaxBox.SelectedValue);
        string? hsn = form.Text(HsnField, HsnBox.Text, maxLength: 8);
        if (!form.Apply(Fields, Summary)) return;
        try
        {
            SaveButton.IsEnabled = false;
            string symbology = _barcode.Length switch { 8 => "EAN8", 12 => "UPCA", 13 => "EAN13", _ => "CODE128" };
            SavedProductId = await _catalog.CreateAsync(new ProductInput(
                sku!, name!, null, null, categoryId!.Value, null, unitId!.Value,
                hsn, taxId, true, false, null, false, null, null,
                Quantity.Zero, price, mrp,
                [new ProductBarcodeInput(_barcode, Quantity.One, symbology, true)]));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            // Keep the typed values; a duplicate SKU or barcode is reported for the whole form.
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }
}
