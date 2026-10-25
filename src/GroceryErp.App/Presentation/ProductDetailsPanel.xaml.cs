using System.Windows;
using System.Windows.Controls;
using GroceryErp.Catalog;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// The Products detail panel for one product: Basics (the product form) and Lots. The Lots
/// view is created only when its tab is first selected, and is released when another product opens.
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
        TabActionsHost.Content = null;
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
        Panel.Title = product.Name;
        Panel.Facts = string.Join(" · ", new[]
        {
            product.Sku, product.Category, TranslationSource.Instance[product.ProductClass == "INTERNAL"
                ? "Catalog_InternalUse" : "Catalog_Saleable"],
            product.IsActive ? null : TranslationSource.Instance["Catalog_ArchivedStatus"],
        }.Where(static part => !string.IsNullOrEmpty(part)));
        Panel.Summary = product.SellingPrice is { } price
            ? TranslationSource.Instance.Format("Catalog_StockSummary", product.Stock, product.Uom, price)
            : TranslationSource.Instance.Format("Catalog_StockSummaryNoPrice", product.Stock, product.Uom);
    }

    private void Tabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Tabs)) EnsureTabContent();
    }

    private void EnsureTabContent()
    {
        if (_product is not null && _inventory is not null && Tabs.SelectedItem == LotsTab && LotsHost.Content is null)
        {
            var lots = new ProductLotsView(_inventory, _product.Id, _product.ProductClass == "INTERNAL");
            lots.StockChanged += (_, _) => ProductChanged?.Invoke(this, EventArgs.Empty);
            LotsHost.Content = lots;
            TabActionsHost.Content = lots.DetachActions();
        }
        TabActionsHost.Visibility = Tabs.SelectedItem == LotsTab && TabActionsHost.Content is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // Keeps the tab actions level with the headers and starting just after the last one.
    private void TabHeader_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!LotsTab.IsLoaded || LotsTab.ActualWidth <= 0) return;
        Point corner = LotsTab.TranslatePoint(new Point(LotsTab.ActualWidth, 0), Tabs);
        TabActionsHost.Margin = new Thickness(corner.X + 12, corner.Y, 0, 0);
        TabActionsHost.Height = LotsTab.ActualHeight;
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

    private void Panel_OnCloseRequested(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
