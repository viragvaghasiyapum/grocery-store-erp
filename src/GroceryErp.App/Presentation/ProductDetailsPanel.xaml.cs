using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GroceryErp.Catalog;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// The Products slide-in panel for one product: Basics (the product form), Lots and the read-only
/// pre-cutover History. Lots and History are created only when their tab is first selected, and
/// are released when another product opens.
/// </summary>
public partial class ProductDetailsPanel : UserControl
{
    private CatalogService? _catalog;
    private InventoryService? _inventory;
    private DateOnly _businessDate;
    private bool _regularTaxMode;
    private ProductListItem? _product;

    public ProductDetailsPanel()
    {
        InitializeComponent();
        LanguageRefresh.Attach(this, () =>
        {
            if (_product is not null) ShowSummary(_product);
            SavedText.Text = string.Empty;
        });
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || args.Handled) return;
            args.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? CloseRequested;

    /// <summary>Raised after the product or its stock changed, so the table refreshes its row.</summary>
    public event EventHandler? ProductChanged;

    public long? ProductId => _product?.Id;

    /// <summary>Gets whether Basics has edits that would be lost by opening another product.</summary>
    public bool HasUnsavedChanges => Form.IsDirty;

    public string ProductName => _product?.Name ?? string.Empty;

    public void Initialize(CatalogService catalog, InventoryService? inventory, DateOnly businessDate, bool regularTaxMode)
    {
        _catalog = catalog;
        _inventory = inventory;
        _businessDate = businessDate;
        _regularTaxMode = regularTaxMode;
    }

    /// <summary>Shows a product; keeps the selected tab and builds that tab's content for it.</summary>
    public async Task OpenAsync(ProductListItem product, bool lotsFirst = false)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (_catalog is null) return;
        _product = product;
        ShowSummary(product);
        LotsHost.Content = null;
        HistoryHost.Content = null;
        SavedText.Text = string.Empty;
        if (lotsFirst) Tabs.SelectedItem = LotsTab;
        EnsureTabContent();
        SaveButton.IsEnabled = false;
        await Form.LoadAsync(_catalog, product.Id, _businessDate, _regularTaxMode);
        SaveButton.IsEnabled = Form.CanSave;
    }

    /// <summary>Updates the header after the table reloaded the product's row.</summary>
    public void Refresh(ProductListItem product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (product.Id != _product?.Id) return;
        _product = product;
        ShowSummary(product);
    }

    private void ShowSummary(ProductListItem product)
    {
        NameText.Text = product.Name;
        NameText.ToolTip = product.Name;
        ContextText.Text = string.Join(" · ", new[]
        {
            product.Sku, product.Category, TranslationSource.Instance[product.ProductClass == "INTERNAL"
                ? "Catalog_InternalUse" : "Catalog_Saleable"],
            product.IsActive ? null : TranslationSource.Instance["Catalog_ArchivedStatus"],
        }.Where(static part => !string.IsNullOrEmpty(part)));
        StockText.Text = product.SellingPrice is { } price
            ? TranslationSource.Instance.Format("Catalog_StockSummary", product.Stock, product.Uom, price)
            : TranslationSource.Instance.Format("Catalog_StockSummaryNoPrice", product.Stock, product.Uom);
    }

    private void Tabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Tabs)) EnsureTabContent();
    }

    private void EnsureTabContent()
    {
        if (_product is null || _inventory is null) return;
        if (Tabs.SelectedItem == LotsTab && LotsHost.Content is null)
        {
            var lots = new ProductLotsView(_inventory, _product.Id, _product.ProductClass == "INTERNAL");
            lots.StockChanged += (_, _) => ProductChanged?.Invoke(this, EventArgs.Empty);
            LotsHost.Content = lots;
        }
        else if (Tabs.SelectedItem == HistoryTab && HistoryHost.Content is null)
            HistoryHost.Content = new ProductLedgerView(_inventory, _product.Id);
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            if (await Form.SaveAsync() is null) return;
            SavedText.Text = TranslationSource.Instance["Catalog_Saved"];
            ProductChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { SaveButton.IsEnabled = Form.CanSave; }
    }

    private async void Revert_OnClick(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || _product is null) return;
        SavedText.Text = string.Empty;
        await Form.LoadAsync(_catalog, _product.Id, _businessDate, _regularTaxMode);
        SaveButton.IsEnabled = Form.CanSave;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
