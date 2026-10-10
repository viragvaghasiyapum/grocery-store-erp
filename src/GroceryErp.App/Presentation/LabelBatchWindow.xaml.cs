using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using GroceryErp.Printing;
using GroceryErp.Sales;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public sealed class LabelBatchRow(
    string name, string? localName, string source, string barcode, decimal price, decimal? mrp, string copiesText, bool isSelected)
{
    public string Name { get; } = name;
    public string? LocalName { get; } = localName;
    public string Source { get; } = source;
    public string Barcode { get; } = barcode;
    public decimal Price { get; } = price;
    public decimal? Mrp { get; } = mrp;
    public string CopiesText { get; set; } = copiesText;
    public bool IsSelected { get; set; } = isSelected;
}

public partial class LabelBatchWindow : Window
{
    private readonly CatalogService _catalog;
    private readonly InventoryService _inventory;
    private readonly ISettings _settings;
    private readonly IReadOnlyList<long> _productIds;
    private readonly IReadOnlyList<long> _lotIds;
    private readonly int _width;
    private readonly int _height;
    public ObservableCollection<LabelBatchRow> Rows { get; } = [];

    public LabelBatchWindow(CatalogService catalog, InventoryService inventory, ISettings settings,
        IReadOnlyList<long>? productIds = null, IReadOnlyList<long>? lotIds = null, bool loadOnShow = true)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _productIds = productIds ?? [];
        _lotIds = lotIds ?? [];
        (_width, _height) = ParseSize(settings.Current.LabelSize);
        InitializeComponent();
        LabelsGrid.ItemsSource = Rows;
        PrinterBox.Text = settings.Current.LabelPrinterName;
        TemplateBox.ItemsSource = new[]
        {
            new TemplateChoice(TsplLabelTemplate.BarcodePrice, TranslationSource.Instance["Catalog_TemplateBarcodePrice"]),
            new TemplateChoice(TsplLabelTemplate.Price, TranslationSource.Instance["Catalog_TemplatePrice"]),
            new TemplateChoice(TsplLabelTemplate.ShelfEdge, TranslationSource.Instance["Catalog_TemplateShelfEdge"]),
        };
        TemplateBox.SelectedIndex = 0;
        if (loadOnShow) Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            SaveButton.IsEnabled = PrintButton.IsEnabled = false;
            var products = new Dictionary<long, ProductDetails>();
            if (_lotIds.Count > 0)
            {
                HashSet<long> selected = _lotIds.ToHashSet();
                foreach (LotItem lot in (await _inventory.GetLotsAsync()).Where(lot => selected.Contains(lot.Id) && lot.IsActive && lot.OnHand.Value > 0))
                {
                    ProductDetails product = await GetProductAsync(lot.ProductId, products);
                    await AddLotAsync(product, lot, isSelected: true);
                }
            }
            else
            {
                foreach (long id in _productIds.Distinct())
                {
                    ProductDetails product = await GetProductAsync(id, products);
                    if (!product.IsActive) continue;
                    if (product.Product.DefaultSellingPrice is { } price)
                    {
                        string barcode = product.Product.Barcodes.FirstOrDefault(static item => item.IsPrimary)?.Barcode
                            ?? product.Product.Barcodes.FirstOrDefault()?.Barcode
                            ?? await _catalog.GenerateInternalBarcodeAsync(id, _settings.Current.ProductPrefix);
                        Rows.Add(new LabelBatchRow(product.Product.Name, product.Product.NameLocal,
                            TranslationSource.Instance["Catalog_LabelProduct"], barcode, price.Value,
                            product.Product.DefaultMrp?.Value, "1", isSelected: true));
                    }
                    foreach (LotItem lot in (await _inventory.GetLotsAsync(id)).Where(static item => item.IsActive && item.OnHand.Value > 0))
                        await AddLotAsync(product, lot, isSelected: false);
                }
            }
            if (Rows.Count > 0) LabelsGrid.SelectedIndex = 0;
            StatusText.Text = Rows.Count == 0 ? TranslationSource.Instance["Catalog_NoLabels"] : string.Empty;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally { SaveButton.IsEnabled = PrintButton.IsEnabled = Rows.Count > 0; }
    }

    private async Task<ProductDetails> GetProductAsync(long id, Dictionary<long, ProductDetails> cache)
    {
        if (cache.TryGetValue(id, out ProductDetails? product)) return product;
        product = await _catalog.GetAsync(id);
        cache[id] = product;
        return product;
    }

    private async Task AddLotAsync(ProductDetails product, LotItem lot, bool isSelected)
    {
        string barcode = await _catalog.EnsureLotBarcodeAsync(lot.Id, _settings.Current.LotPrefix);
        string copies = Math.Max(1, Math.Ceiling(lot.Received.Value)).ToString(CultureInfo.InvariantCulture);
        Rows.Add(new LabelBatchRow(product.Product.Name, product.Product.NameLocal,
            $"{TranslationSource.Instance["Stock_LotCode"]} {lot.LotCode}", barcode,
            lot.SellingPrice.Value, lot.Mrp?.Value, copies, isSelected));
    }

    private void LabelsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void LabelsGrid_OnCurrentCellChanged(object sender, EventArgs e) => UpdatePreview();
    private void Template_OnChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewName is null) return;
        if (LabelsGrid.SelectedItem is not LabelBatchRow row) return;
        PreviewName.Text = row.Name;
        PreviewLocal.Text = row.LocalName;
        PreviewBarcodeText.Text = row.Barcode;
        PreviewBarcode.Source = BarcodePreview.Create(row.Barcode);
        PreviewPrice.Text = $"₹{row.Price.ToString("0.00", CultureInfo.InvariantCulture)}";
        PreviewMrp.Text = row.Mrp is null ? string.Empty : $"MRP {row.Mrp.Value.ToString("0.00", CultureInfo.InvariantCulture)}";
        TsplLabelTemplate template = (TemplateBox.SelectedItem as TemplateChoice)?.Template ?? TsplLabelTemplate.BarcodePrice;
        PreviewPrice.FontSize = template == TsplLabelTemplate.Price ? 32 : 26;
        PreviewBarcode.Height = template == TsplLabelTemplate.Price ? 46 : 56;
    }

    private List<TsplLabel> BuildLabels()
    {
        LabelsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LabelsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        TsplLabelTemplate template = (TemplateBox.SelectedItem as TemplateChoice)?.Template ?? TsplLabelTemplate.BarcodePrice;
        var labels = new List<TsplLabel>();
        foreach (LabelBatchRow row in Rows.Where(static item => item.IsSelected))
        {
            if (!int.TryParse(InputNormalizer.Normalize(row.CopiesText), NumberStyles.None, CultureInfo.InvariantCulture, out int copies)
                || copies is < 1 or > 100_000)
                throw new InvalidOperationException(TranslationSource.Instance["Catalog_BatchCopiesRange"]);
            labels.Add(new TsplLabel(row.Name, row.LocalName, row.Barcode, row.Price, row.Mrp,
                _width, _height, copies, template));
        }
        if (labels.Count == 0) throw new InvalidOperationException(TranslationSource.Instance["Catalog_SelectLabels"]);
        return labels;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[] command = TsplLabelBuilder.BuildBatch(BuildLabels());
            var dialog = new SaveFileDialog
            {
                Filter = "TSPL print file (*.prn)|*.prn|All files (*.*)|*.*",
                FileName = "labels.prn",
                AddExtension = true,
                DefaultExt = ".prn",
            };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllBytes(dialog.FileName, command);
            StatusText.Text = string.Empty;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void Print_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            PrintButton.IsEnabled = SaveButton.IsEnabled = false;
            LabelsGrid.IsEnabled = false;
            IReadOnlyList<TsplLabel> labels = BuildLabels();
            await ((App)Application.Current).Services.GetRequiredService<ReceiptPrinter>().PrintLabelsAsync(PrinterBox.Text, labels,
                (PrinterModeBox.SelectedItem as ComboBoxItem)?.Tag as string == "DRIVER");
            DialogResult = true;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            PrintButton.IsEnabled = SaveButton.IsEnabled = true;
            LabelsGrid.IsEnabled = true;
        }
    }

    private static (int Width, int Height) ParseSize(string value)
    {
        string[] parts = value.Split(['x', 'X', '×'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int width) &&
               int.TryParse(parts[1], CultureInfo.InvariantCulture, out int height) ? (width, height) : (60, 40);
    }

    private sealed record TemplateChoice(TsplLabelTemplate Template, string Name);
}
