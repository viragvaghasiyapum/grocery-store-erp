using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Catalog;
using GroceryErp.Core;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

/// <summary>
/// The product form shared by the Products panel (Basics) and the New product dialog: grouped
/// fields with required markers, inline errors and a summary; a new product may add its first lot.
/// Entered values stay in place after a failed save.
/// </summary>
public partial class ProductForm : UserControl
{
    private CatalogService? _catalog;
    private IReadOnlyCollection<ProductBarcodeInput> _originalBarcodes = [];
    private bool _taxRequired;
    private LotCostPreview? _firstLotCost;
    // The first lot's tax follows the product's rate until the operator picks another one.
    private bool _firstLotTaxChosen;
    private bool _followingProductTax;

    private bool _tracking;

    public ProductForm()
    {
        InitializeComponent();
        // Any edit after loading marks the form dirty, so the panel never drops unsaved input silently.
        Fields.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new RoutedEventHandler(OnEdited));
        Fields.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new RoutedEventHandler(OnEdited));
        Fields.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(OnEdited));
        Fields.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(OnEdited));
    }

    /// <summary>Gets whether the operator changed a field since the form was loaded or saved.</summary>
    public bool IsDirty { get; private set; }

    private void OnEdited(object sender, RoutedEventArgs e)
    {
        if (_tracking) IsDirty = true;
    }

    public long? ProductId { get; private set; }

    /// <summary>Gets whether the product can be saved: categories exist and loading succeeded.</summary>
    public bool CanSave { get; private set; }

    public async Task LoadAsync(CatalogService catalog, long? productId, DateOnly businessDate, bool regularTaxMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        ProductId = productId;
        _taxRequired = regularTaxMode;
        HsnField.IsRequired = regularTaxMode;
        TaxField.IsRequired = regularTaxMode;
        CanSave = false;
        _tracking = false;
        IsDirty = false;
        FormValidator.Clear(Fields, Summary);
        FirstLotSection.Visibility = productId is null ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            IReadOnlyList<MasterItem> categories = await catalog.GetCategoriesAsync(cancellationToken);
            CategoryBox.ItemsSource = categories.Where(static item => item.IsActive).ToList();
            BrandBox.ItemsSource = (await catalog.GetBrandsAsync(cancellationToken)).Where(static item => item.IsActive).ToList();
            UnitBox.ItemsSource = await catalog.GetUnitsAsync(cancellationToken);
            IReadOnlyList<TaxRateItem> rates = await catalog.GetTaxRatesAsync(cancellationToken);
            TaxRateBox.ItemsSource = rates.Where(static item => item.IsActive).ToList();
            FirstLotDateBox.SelectedDate = businessDate.ToDateTime(TimeOnly.MinValue);
            if (productId is null)
            {
                SkuBox.Text = await catalog.SuggestSkuAsync(cancellationToken);
                CategoryBox.SelectedIndex = 0;
                UnitBox.SelectedIndex = 0;
                TaxRateBox.SelectedIndex = 0;
                _firstLotCost ??= new LotCostPreview(FirstLotCostBox, FirstLotTaxBox, FirstLotUnitTaxBox, FirstLotInclTaxBox);
                _firstLotTaxChosen = false;
                FollowProductTax(() => _firstLotCost.Load(rates, TaxRateBox.SelectedValue as long?));
                if (categories.Count == 0)
                {
                    Summary.ShowMessage(TranslationSource.Instance["Catalog_CategoryRequiredFirst"]);
                    return;
                }
            }
            else
            {
                ProductInput product = (await catalog.GetAsync(productId.Value, cancellationToken)).Product;
                Show(product);
            }
            CanSave = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
        }
        finally
        {
            // Let queued change notifications from the programmatic fill settle first.
            await Dispatcher.InvokeAsync(() => _tracking = true, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    public void FocusFirstField() => (ProductId is null ? NameField : SkuField).FocusInput();

    private void Show(ProductInput product)
    {
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
        LooseBox.IsChecked = product.IsLoose;
        PluBox.Text = product.PluCode;
        TrackExpiryBox.IsChecked = product.TrackExpiry;
        ShelfLifeBox.Text = product.DefaultShelfLifeDays?.ToString(CultureInfo.InvariantCulture);
        ExpiryAlertBox.Text = product.ExpiryAlertDays?.ToString(CultureInfo.InvariantCulture);
        ReorderBox.Text = product.ReorderLevel.Value.ToString("0.###", TranslationSource.FormatCulture);
        PriceBox.Text = product.DefaultSellingPrice?.Value.ToString("0.###", TranslationSource.FormatCulture);
        MrpBox.Text = product.DefaultMrp?.Value.ToString("0.###", TranslationSource.FormatCulture);
        ProductClassBox.SelectedValue = product.ProductClass;
        BarcodesBox.Text = string.Join(Environment.NewLine, product.Barcodes.Select(static item => item.Barcode));
    }

    private void LooseBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (PluBox is not null) PluBox.IsEnabled = LooseBox.IsChecked == true;
    }

    private void TaxRateBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_firstLotTaxChosen || FirstLotTaxBox?.ItemsSource is null) return;
        FollowProductTax(() => FirstLotTaxBox.SelectedValue = TaxRateBox.SelectedValue);
    }

    private void FirstLotTaxBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_followingProductTax) _firstLotTaxChosen = true;
    }

    private void FollowProductTax(Action change)
    {
        _followingProductTax = true;
        try { change(); }
        finally { _followingProductTax = false; }
    }

    /// <summary>Validates and saves. Returns the product ID, or null when the form shows what to fix.</summary>
    public async Task<long?> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog is null || !CanSave) return null;
        var form = new FormValidator();
        string? sku = form.Text(SkuField, SkuBox.Text, required: true, maxLength: 20);
        string? name = form.Text(NameField, NameBox.Text, required: true, maxLength: 120);
        long? categoryId = form.Choice(CategoryField, CategoryBox.SelectedValue);
        long? unitId = form.Choice(UnitField, UnitBox.SelectedValue);
        string? hsn = form.Text(HsnField, HsnBox.Text, _taxRequired, 8);
        if (hsn is not null && (hsn.Length is not (4 or 6 or 8) || !hsn.All(char.IsAsciiDigit)))
            form.AddKey(HsnField, "Catalog_HsnDigits");
        long? taxRateId = form.Choice(TaxField, TaxRateBox.SelectedValue, _taxRequired);
        Money? price = form.Money(PriceField, PriceBox.Text);
        Money? mrp = form.Money(MrpField, MrpBox.Text);
        if (price is { } p && mrp is { } m && p.Value > m.Value) form.AddKey(PriceField, "Error_PriceAboveMrp");
        decimal? reorder = form.Number(ReorderField, ReorderBox.Text, required: true, minimum: 0);
        int? shelfLife = form.WholeNumber(ShelfLifeField, ShelfLifeBox.Text, minimum: 1);
        int? expiryAlert = form.WholeNumber(ExpiryAlertField, ExpiryAlertBox.Text, minimum: 0);
        bool loose = LooseBox.IsChecked == true;
        string? plu = form.Text(PluField, PluBox.Text, required: loose, maxLength: 30);
        NewProductLotInput? firstLot = ProductId is null && AddFirstLotBox.IsChecked == true ? FirstLot(form) : null;
        if (!form.Apply(Fields, Summary)) return null;

        List<ProductBarcodeInput> barcodes = InputNormalizer.Normalize(BarcodesBox.Text)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Select((barcode, index) => _originalBarcodes.FirstOrDefault(item => item.Barcode == barcode) ?? new ProductBarcodeInput(barcode, Quantity.One,
                barcode.Length == 8 ? "EAN8" : barcode.Length == 12 ? "UPCA" : barcode.Length == 13 ? "EAN13" : "CODE128",
                index == 0 && !_originalBarcodes.Any(static item => item.IsPrimary))).ToList();
        if (barcodes.Count > 0 && !barcodes.Any(static item => item.IsPrimary))
            barcodes[0] = barcodes[0] with { IsPrimary = true };
        var input = new ProductInput(sku!, name!, LocalNameBox.Text, AliasesBox.Text, categoryId!.Value,
            BrandBox.SelectedValue as long?, unitId!.Value, hsn, taxRateId,
            loose, plu, TrackExpiryBox.IsChecked == true, shelfLife, expiryAlert,
            new Quantity(reorder ?? 0), price, mrp, barcodes)
        { ProductClass = ProductClassBox.SelectedValue as string ?? "SALEABLE" };
        try
        {
            if (ProductId is { } existing)
            {
                await _catalog.UpdateAsync(existing, input, cancellationToken);
                _originalBarcodes = barcodes;
                IsDirty = false;
                return existing;
            }
            long created;
            if (firstLot is not null)
            {
                IServiceProvider services = ((App)Application.Current).Services;
                created = await _catalog.CreateWithDirectLotAsync(input, firstLot,
                    services.GetRequiredService<ILotQuantities>(), services.GetRequiredService<DocNumberService>(),
                    services.GetRequiredService<PeriodGuard>(), OperationGuard.NewId(), cancellationToken);
            }
            else created = await _catalog.CreateAsync(input, cancellationToken);
            ProductId = created;
            IsDirty = false;
            return created;
        }
        catch (DomainException exception) when (FieldFor(exception.Code) is { } field)
        {
            field.Error = UserError.Message(exception);
            Summary.Show([(field, field.Error)]);
            field.FocusInput();
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Summary.ShowMessage(UserError.Message(exception));
            return null;
        }
    }

    private NewProductLotInput? FirstLot(FormValidator form)
    {
        Quantity? quantity = form.Quantity(FirstLotQtyField, FirstLotQtyBox.Text);
        Money? cost = form.Money(FirstLotCostField, FirstLotCostBox.Text, required: true);
        long? taxRateId = form.Choice(FirstLotTaxField, FirstLotTaxBox.SelectedValue);
        Money? price = form.Money(FirstLotPriceField, FirstLotPriceBox.Text, required: true);
        Money? mrp = form.Money(FirstLotMrpField, FirstLotMrpBox.Text);
        if (price is { } p && mrp is { } m && p.Value > m.Value) form.AddKey(FirstLotPriceField, "Error_PriceAboveMrp");
        DateOnly? received = form.Date(FirstLotDateField, FirstLotDateBox.SelectedDate);
        DateOnly? expiry = FirstLotExpiryBox.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null;
        if (expiry < received) form.AddKey(FirstLotExpiryField, "Stock_ExpiryBeforeReceived");
        string? reason = form.Text(FirstLotReasonField, FirstLotReasonBox.Text, required: true, maxLength: 500);
        return quantity is null || cost is null || price is null || received is null || reason is null ? null
            : new NewProductLotInput(quantity.Value, cost.Value, price.Value, mrp, FirstLotBatchBox.Text, expiry,
                received.Value, reason)
            { TaxRateId = taxRateId };
    }

    private FormField? FieldFor(ErrorCode code) => code switch
    {
        ErrorCode.BarcodeExists or ErrorCode.InvalidBarcode => BarcodesField,
        ErrorCode.PriceAboveMrp => PriceField,
        ErrorCode.PeriodLocked or ErrorCode.InvalidDate => ProductId is null && AddFirstLotBox.IsChecked == true
            ? FirstLotDateField : null,
        _ => null,
    };
}
