using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Catalog;
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
    string name, string? localName, string source, string barcode, decimal price, decimal? mrp, string copiesText,
    bool isSelected, bool isPrintable = true) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; } = name;
    public string? LocalName { get; } = localName;
    public string Source { get; } = source;
    public string Barcode { get; } = barcode;
    public decimal Price { get; } = price;
    public decimal? Mrp { get; } = mrp;
    public string CopiesText { get; set; } = copiesText;
    public bool IsSelected { get; set; } = isSelected;
    public bool IsPrintable { get; } = isPrintable;
    public ObservableCollection<LabelBatchRow> Lots { get; } = [];
    private bool _isLotsExpanded;
    public Visibility LotsVisibility => Lots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExpandedLotsVisibility => Lots.Count > 0 && _isLotsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public string LotsArrow => _isLotsExpanded ? "▴" : "▾";
    public void ToggleLots()
    {
        _isLotsExpanded = !_isLotsExpanded;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedLotsVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LotsArrow)));
    }
    public void AddLot(LabelBatchRow lot)
    {
        Lots.Add(lot);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LotsVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedLotsVisibility)));
    }
    public void ShowLots(IEnumerable<LabelBatchRow> lots)
    {
        Lots.Clear();
        foreach (LabelBatchRow lot in lots) Lots.Add(lot);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LotsVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedLotsVisibility)));
    }
}

public partial class LabelBatchWindow : Window
{
    private int _pageSize = 50;
    private int PageSize => _pageSize;
    private readonly CatalogService _catalog;
    private readonly InventoryService _inventory;
    private readonly ISettings _settings;
    private readonly IReadOnlyList<long> _productIds;
    private readonly IReadOnlyList<long> _lotIds;
    private readonly int _width;
    private readonly int _height;
    private readonly long[] _lotProductIds;
    private readonly Dictionary<long, LabelBatchRow> _productRows = new();
    private readonly Dictionary<long, ProductDetails> _products = new();
    private readonly Dictionary<long, LabelBatchRow> _lotRows = new();
    private readonly List<Dictionary<long, LotPage>> _lotPages = [];
    private int _lotPageIndex;
    private bool _loadingLots;
    private bool _loadingLabels;
    private bool _printing;
    private LabelBatchRow? _previewRow;
    public ObservableCollection<LabelBatchRow> Rows { get; } = [];

    public LabelBatchWindow(CatalogService catalog, InventoryService inventory, ISettings settings,
        IReadOnlyList<long>? productIds = null, IReadOnlyList<long>? lotIds = null, bool loadOnShow = true)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _productIds = productIds ?? [];
        _lotIds = lotIds ?? [];
        _lotProductIds = _productIds.Distinct().ToArray();
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
        _loadingLabels = true;
        try
        {
            SaveButton.IsEnabled = PrintButton.IsEnabled = false;
            PageSizeMenu.IsEnabled = false;
            if (_lotIds.Count > 0)
            {
                PaginationPanel.Visibility = Visibility.Collapsed;
                foreach (LotItem lot in (await _inventory.GetLotsByIdsAsync(_lotIds)).Where(lot => lot.IsActive && lot.OnHand.Value > 0))
                {
                    ProductDetails product = await GetProductAsync(lot.ProductId);
                    EnsureProductRow(lot.ProductId, product, isSelected: false);
                    await AddLotAsync(product, lot, isSelected: true);
                }
            }
            else
            {
                foreach (long id in _lotProductIds)
                {
                    ProductDetails product = await GetProductAsync(id);
                    if (!product.IsActive) continue;
                    string barcode = product.Product.Barcodes.FirstOrDefault(static item => item.IsPrimary)?.Barcode
                        ?? product.Product.Barcodes.FirstOrDefault()?.Barcode
                        ?? (product.Product.DefaultSellingPrice is not null
                            ? await _catalog.GenerateInternalBarcodeAsync(id, _settings.Current.ProductPrefix) : string.Empty);
                    EnsureProductRow(id, product, isSelected: product.Product.DefaultSellingPrice is not null, barcode);
                }
                await LoadFirstLotPageAsync();
            }
            if (Rows.Count > 0) LabelsGrid.SelectedIndex = 0;
            UpdatePreview(AllRows().FirstOrDefault(static row => row.IsSelected && row.IsPrintable));
            StatusText.Text = AllRows().Any(static row => row.IsPrintable) ? string.Empty : TranslationSource.Instance["Catalog_NoLabels"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _loadingLabels = false;
            UpdatePreview();
            if (_lotIds.Count == 0) UpdatePaginationActions();
        }
    }

    private LabelBatchRow EnsureProductRow(long productId, ProductDetails product, bool isSelected, string barcode = "")
    {
        if (_productRows.TryGetValue(productId, out LabelBatchRow? row)) return row;
        decimal? price = product.Product.DefaultSellingPrice?.Value;
        row = new LabelBatchRow(product.Product.Name, product.Product.NameLocal,
            TranslationSource.Instance["Catalog_LabelProduct"], barcode, price ?? 0,
            product.Product.DefaultMrp?.Value, "1", isSelected && price is not null,
            isPrintable: isSelected && price is not null);
        _productRows.Add(productId, row);
        Rows.Add(row);
        return row;
    }

    private async void PageSize_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } || !int.TryParse(value, out int size) || size == _pageSize) return;
        int previousSize = _pageSize;
        _pageSize = size;
        PageSizeMenu.Header = $"{size} ▾";
        try { await LoadFirstLotPageAsync(); }
        catch (Exception exception)
        {
            _pageSize = previousSize;
            PageSizeMenu.Header = $"{previousSize} ▾";
            StatusText.Text = UserError.Message(exception);
        }
    }

    private async void NextLots_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingLots) return;
        try
        {
            if (_lotPageIndex + 1 < _lotPages.Count)
            {
                ShowLotPage(++_lotPageIndex);
                return;
            }
            if (_lotPages.Count == 0 || !_lotPages[^1].Values.Any(static page => page.HasMore)) return;
            await LoadNextLotPageAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void PreviousLots_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingLots || _lotPageIndex == 0) return;
        ShowLotPage(--_lotPageIndex);
    }

    private async Task LoadFirstLotPageAsync()
    {
        if (_loadingLots) return;
        _loadingLots = true;
        UpdatePaginationActions();
        try
        {
            var page = new Dictionary<long, LotPage>();
            foreach (long productId in _productRows.Keys)
            {
                LotPage lots = await _inventory.GetLotPageAsync(productId,
                    limit: PageSize, availableOnly: true);
                foreach (LotItem lot in lots.Items) await GetOrCreateLotRowAsync(_products[productId], lot);
                page.Add(productId, lots);
            }
            _lotPages.Clear();
            _lotPages.Add(page);
            _lotPageIndex = 0;
            ShowLotPage(0);
        }
        finally { _loadingLots = false; UpdatePaginationActions(); }
    }

    private async Task LoadNextLotPageAsync()
    {
        _loadingLots = true;
        UpdatePaginationActions();
        try
        {
            Dictionary<long, LotPage> previous = _lotPages[_lotPageIndex];
            var page = new Dictionary<long, LotPage>();
            foreach ((long productId, LotPage current) in previous)
            {
                LotPage lots = current.HasMore
                    ? await _inventory.GetLotPageAsync(productId, beforeId: current.NextId,
                        limit: PageSize, availableOnly: true)
                    : current;
                foreach (LotItem lot in lots.Items) await GetOrCreateLotRowAsync(_products[productId], lot);
                page.Add(productId, lots);
            }
            _lotPages.Add(page);
            ShowLotPage(++_lotPageIndex);
        }
        finally { _loadingLots = false; UpdatePaginationActions(); }
    }

    private void ShowLotPage(int index)
    {
        foreach ((long productId, LotPage page) in _lotPages[index])
            _productRows[productId].ShowLots(page.Items.Select(lot => _lotRows[lot.Id]));
        UpdatePaginationActions();
    }

    private void UpdatePaginationActions()
    {
        if (PreviousLotsButton is null) return;
        PageSizeMenu.IsEnabled = !_loadingLots;
        PreviousLotsButton.IsEnabled = !_loadingLots && _lotPageIndex > 0;
        NextLotsButton.IsEnabled = !_loadingLots && _lotPages.Count > 0 &&
            (_lotPageIndex + 1 < _lotPages.Count || _lotPages[_lotPageIndex].Values.Any(static page => page.HasMore));
    }

    private async Task<ProductDetails> GetProductAsync(long id)
    {
        if (_products.TryGetValue(id, out ProductDetails? product)) return product;
        product = await _catalog.GetAsync(id);
        _products[id] = product;
        return product;
    }

    private async Task AddLotAsync(ProductDetails product, LotItem lot, bool isSelected)
    {
        LabelBatchRow row = await GetOrCreateLotRowAsync(product, lot, isSelected);
        _productRows[lot.ProductId].AddLot(row);
    }

    private async Task<LabelBatchRow> GetOrCreateLotRowAsync(ProductDetails product, LotItem lot, bool isSelected = false)
    {
        if (_lotRows.TryGetValue(lot.Id, out LabelBatchRow? row)) return row;
        string barcode = await _catalog.EnsureLotBarcodeAsync(lot.Id, _settings.Current.LotPrefix);
        row = new LabelBatchRow(product.Product.Name, product.Product.NameLocal,
            $"{TranslationSource.Instance["Stock_LotCode"]} {lot.LotCode}", barcode,
            lot.SellingPrice.Value, lot.Mrp?.Value, "1", isSelected);
        _lotRows.Add(lot.Id, row);
        return row;
    }

    private void LabelsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdatePreview(LabelsGrid.SelectedItem as LabelBatchRow);
    private void LabelsGrid_OnCurrentCellChanged(object sender, EventArgs e) =>
        UpdatePreview(LabelsGrid.SelectedItem as LabelBatchRow);
    private void ToggleLots_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LabelBatchRow row }) row.ToggleLots();
    }
    private void LotRow_OnMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Grid { DataContext: LabelBatchRow row }) UpdatePreview(row);
    }
    private void LotCheck_OnClick(object sender, RoutedEventArgs e)
    {
        LabelCheck_OnClick(sender);
    }
    private void ProductLabelCheck_OnClick(object sender, RoutedEventArgs e) => LabelCheck_OnClick(sender);
    private void LabelCheck_OnClick(object sender)
    {
        if (sender is not CheckBox { DataContext: LabelBatchRow row } box) return;
        row.IsSelected = box.IsChecked == true;
        UpdatePreview(row);
    }
    private void Template_OnChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview(_previewRow);

    private void UpdatePreview(LabelBatchRow? selectedRow = null)
    {
        if (PreviewName is null) return;
        LabelBatchRow? row = selectedRow is { IsSelected: true, IsPrintable: true } ? selectedRow
            : _previewRow is { IsSelected: true, IsPrintable: true } ? _previewRow
            : AllRows().FirstOrDefault(static item => item.IsSelected && item.IsPrintable);
        _previewRow = row;
        SaveButton.IsEnabled = PrintButton.IsEnabled = row is not null && !_loadingLabels && !_printing;
        PreviewName.Text = row?.Name ?? string.Empty;
        PreviewLocal.Text = row?.LocalName ?? string.Empty;
        PreviewBarcodeText.Text = row?.Barcode ?? string.Empty;
        PreviewBarcode.Source = string.IsNullOrEmpty(row?.Barcode) ? null : BarcodePreview.Create(row.Barcode);
        PreviewPrice.Text = row is null ? string.Empty : $"₹{row.Price.ToString("0.00", CultureInfo.InvariantCulture)}";
        PreviewMrp.Text = row?.Mrp is { } mrp ? $"MRP {mrp.ToString("0.00", CultureInfo.InvariantCulture)}" : string.Empty;
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
        foreach (LabelBatchRow row in AllRows().Where(static item => item.IsSelected && item.IsPrintable))
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

    private IEnumerable<LabelBatchRow> AllRows() => Rows.Concat(_lotRows.Values)
        .Concat(Rows.SelectMany(static row => row.Lots)).Distinct();

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[] command = TsplLabelBuilder.BuildBatch(BuildLabels());
            var dialog = new SaveFileDialog
            {
                Filter = TranslationSource.Instance["Catalog_PrnFileFilter"],
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
            _printing = true;
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
            _printing = false;
            UpdatePreview();
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
