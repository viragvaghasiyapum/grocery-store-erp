using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Catalog;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class UnknownBarcodeWindow : Window
{
    private readonly CatalogService _catalog;
    private readonly InventoryService _inventory;
    private readonly string _barcode;
    private readonly DateOnly _businessDate;
    private long? _createdProductId;
    private long? _barcodeOwnerId;
    private int _searchVersion;

    public long? ResolvedProductId { get; private set; }
    public bool CanAddToBill { get; private set; }

    public UnknownBarcodeWindow(CatalogService catalog, InventoryService inventory, string barcode, DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(inventory);
        _catalog = catalog;
        _inventory = inventory;
        _barcode = InputNormalizer.Normalize(barcode.Trim());
        _businessDate = businessDate;
        InitializeComponent();
        BarcodeText.Text = _barcode;
    }

    private async void Search_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        int version = ++_searchVersion;
        await Task.Delay(180);
        if (version != _searchVersion || !IsLoaded) return;
        try
        {
            IReadOnlyList<ProductListItem>? results = string.IsNullOrWhiteSpace(SearchBox.Text) ? null
                : await _catalog.SearchAsync(new ProductSearchFilter(SearchBox.Text, Limit: 50));
            if (version == _searchVersion && IsLoaded) ProductsGrid.ItemsSource = results;
        }
        catch (Exception exception) { StatusText.Text = Error(exception); }
    }

    private void ProductsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AttachButton is not null) AttachButton.IsEnabled = ProductsGrid.SelectedItem is ProductListItem;
    }

    private async void Attach_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductListItem selected) return;
        try
        {
            AttachButton.IsEnabled = false;
            BarcodeOwner? owner = await _catalog.AttachBarcodeAsync(selected.Id, _barcode);
            if (owner is not null)
            {
                _barcodeOwnerId = owner.ProductId;
                OpenOwnerButton.IsEnabled = true;
                StatusText.Text = TranslationSource.Instance.Format("Pos_BarcodeOwned", owner.ProductName);
                return;
            }
            ResolvedProductId = selected.Id;
            CanAddToBill = true;
            DialogResult = true;
        }
        catch (Exception exception) { StatusText.Text = Error(exception); }
        finally { AttachButton.IsEnabled = ProductsGrid.SelectedItem is ProductListItem; }
    }

    private void OpenOwner_OnClick(object sender, RoutedEventArgs e)
    {
        if (_barcodeOwnerId is not { } id) return;
        new ProductEditorWindow(_catalog, id) { Owner = this }.ShowDialog();
    }

    private async void Create_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CreateButton.IsEnabled = false;
            Quantity? openingQuantity = ParseQuantity(OpeningQuantityBox.Text);
            Money unitCost = ParseMoney(UnitCostBox.Text);
            DateOnly? expiry = ParseExpiry(ExpiryBox.Text);
            if (openingQuantity is null && (!string.IsNullOrWhiteSpace(UnitCostBox.Text) || expiry is not null))
                throw new InvalidOperationException(TranslationSource.Instance["Pos_OpeningQuantityRequired"]);
            if (_createdProductId is null)
            {
                var editor = new QuickProductWindow(_catalog, _barcode) { Owner = this };
                if (editor.ShowDialog() != true || editor.SavedProductId is null) return;
                _createdProductId = editor.SavedProductId.Value;
            }
            long productId = _createdProductId.Value;
            if (openingQuantity is not null)
            {
                ProductInput product = (await _catalog.GetAsync(productId)).Product;
                Money price = product.DefaultSellingPrice
                    ?? throw new InvalidOperationException(TranslationSource.Instance["Pos_QuickRequired"]);
                await _inventory.ImportOpeningStockAsync(
                    [new OpeningStockLine(productId, openingQuantity.Value, unitCost, price,
                        product.DefaultMrp, expiry, null, _businessDate)], OperationGuard.NewId());
            }
            ResolvedProductId = productId;
            CanAddToBill = openingQuantity is not null;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = Error(exception);
            if (_createdProductId is not null) CreateButton.Content = TranslationSource.Instance["Pos_SaveOpeningStock"];
        }
        finally { CreateButton.IsEnabled = true; }
    }

    private static Quantity? ParseQuantity(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!decimal.TryParse(InputNormalizer.Normalize(text), NumberStyles.Number, TranslationSource.FormatCulture, out decimal value) || value <= 0)
            throw new InvalidOperationException(TranslationSource.Instance["Pos_OpeningQuantityInvalid"]);
        return new Quantity(value);
    }

    private static Money ParseMoney(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Money.Zero;
        if (!decimal.TryParse(InputNormalizer.Normalize(text), NumberStyles.Number, TranslationSource.FormatCulture, out decimal value) || value < 0)
            throw new InvalidOperationException(TranslationSource.Instance["Pos_OpeningCostInvalid"]);
        return new Money(value);
    }

    private static DateOnly? ParseExpiry(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateOnly.TryParseExact(InputNormalizer.Normalize(text), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateOnly date))
            throw new InvalidOperationException(TranslationSource.Instance["Pos_OpeningExpiryInvalid"]);
        return date;
    }

    private static string Error(Exception exception) => UserError.Message(exception);
}
