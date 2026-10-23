using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class PurchaseEntryWindow : Window
{
    private readonly PurchasingService _purchasing;
    private CatalogService? _catalog;
    private ScanResolver? _scanner;
    private readonly List<ProductListItem> _availableProducts = [];
    private readonly long? _openingDraftId;
    private long? _purchaseId;
    private Money _roundStep = new(1);
    private bool _taxIsCost;
    public ObservableCollection<PurchaseEditLine> Lines { get; } = [];

    public PurchaseEntryWindow(PurchasingService purchasing, long? draftId = null, bool loadOnShow = true)
    {
        InitializeComponent();
        _purchasing = purchasing;
        _openingDraftId = draftId;
        DataContext = this;
        if (loadOnShow) Loaded += LoadAsync;
    }

    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            IServiceProvider services = ((App)Application.Current).Services;
            _catalog = services.GetRequiredService<CatalogService>();
            _scanner = services.GetRequiredService<ScanResolver>();
            _roundStep = Money.FromScaled(services.GetRequiredService<ISettings>().Current.BillRoundTo);
            _taxIsCost = !string.Equals(services.GetRequiredService<ISettings>().Current.TaxMode,
                "REGULAR", StringComparison.Ordinal);
            VendorBox.ItemsSource = await _purchasing.GetVendorsAsync();
            ExpenseCategoryColumn.ItemsSource = await services.GetRequiredService<MoneyOutService>()
                .GetExpenseCategoriesAsync();
            LineKindColumn.ItemsSource = new[]
            {
                new PurchaseKindChoice("PRODUCT", TranslationSource.Instance["Purchasing_TypeProduct"]),
                new PurchaseKindChoice("INTERNAL", TranslationSource.Instance["Purchasing_TypeInternal"]),
                new PurchaseKindChoice("EXPENSE", TranslationSource.Instance["Purchasing_TypeExpense"]),
            };
            ProductColumn.ItemsSource = _availableProducts;
            if (_openingDraftId is long draftId)
            {
                PurchaseDraft draft = await _purchasing.GetDraftAsync(draftId);
                _purchaseId = draft.Id;
                PurchaseDraftInput input = draft.Input;
                VendorBox.SelectedValue = input.VendorId;
                InvoiceBox.Text = input.VendorInvoiceNumber ?? "";
                InvoiceDateBox.SelectedDate = input.VendorInvoiceDate?.ToDateTime(TimeOnly.MinValue);
                ReceivedDateBox.SelectedDate = input.ReceivedDate.ToDateTime(TimeOnly.MinValue);
                BillTotalBox.Text = input.VendorBillTotal?.Value.ToString(CultureInfo.InvariantCulture) ?? "";
                ChargesBox.Text = input.OtherCharges.Value.ToString(CultureInfo.InvariantCulture);
                NotesBox.Text = input.Notes ?? "";
                foreach (PurchaseLineInput line in input.Lines)
                {
                    await EnsureProductChoiceAsync(line.ProductId);
                    PurchaseEditLine editLine = PurchaseEditLine.FromInput(line);
                    PurchaseLineDefaults defaults = await _purchasing.GetLineDefaultsAsync(input.VendorId,
                        line.ProductId, input.ReceivedDate);
                    editLine.ApplyMetadata(defaults);
                    Lines.Add(editLine);
                }
            }
            else
            {
                DateTime today = services.GetRequiredService<IClock>().BusinessDate.ToDateTime(TimeOnly.MinValue);
                InvoiceDateBox.SelectedDate = today;
                ReceivedDateBox.SelectedDate = today;
            }
            if (Lines.Count == 0) Lines.Add(new());
            UpdateTotals();
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try { SetBusy(true); _purchaseId = await SaveAsync(); ErrorText.Text = TranslationSource.Instance["Purchasing_DraftSaved"]; }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            _purchaseId = await SaveAsync();
            PostedPurchase posted = await _purchasing.PostAsync(_purchaseId.Value, OperationGuard.NewId());
            if (!posted.WasReplay && posted.LotIds.Count > 0 && MessageBox.Show(
                TranslationSource.Instance["Catalog_PrintReceivedLabels"],
                TranslationSource.Instance["Catalog_PrintLabels"], MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                IServiceProvider services = ((App)Application.Current).Services;
                try
                {
                    new LabelBatchWindow(services.GetRequiredService<CatalogService>(),
                        services.GetRequiredService<InventoryService>(), services.GetRequiredService<ISettings>(),
                        lotIds: posted.LotIds)
                    { Owner = this }.ShowDialog();
                }
                catch (Exception labelError)
                {
                    MessageBox.Show(UserError.Message(labelError), TranslationSource.Instance["Catalog_PrintLabels"]);
                }
            }
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private Task<long> SaveAsync()
    {
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        long vendorId = VendorBox.SelectedValue is long value ? value : throw new InvalidOperationException("Select a vendor.");
        DateOnly received = DateOnly.FromDateTime(ReceivedDateBox.SelectedDate ?? throw new InvalidOperationException("Select a received date."));
        DateOnly? invoiceDate = InvoiceDateBox.SelectedDate is DateTime date ? DateOnly.FromDateTime(date) : null;
        List<PurchaseLineInput> lines = Lines.Where(static line => line.ProductId > 0).Select(line => line.ToInput()).ToList();
        Money? billTotal = ParseOptionalMoney(BillTotalBox.Text);
        var input = new PurchaseDraftInput(vendorId, InvoiceBox.Text, invoiceDate, received, billTotal,
            new Money(Parse(ChargesBox.Text)), NotesBox.Text, lines);
        return _purchasing.SaveDraftAsync(_purchaseId, input);
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void SetBusy(bool busy) { SaveButton.IsEnabled = !busy; PostButton.IsEnabled = !busy; }
    private async void LinesGrid_OnCurrentCellChanged(object sender, EventArgs e)
    {
        UpdateTotals();
        if (LinesGrid.CurrentItem is PurchaseEditLine line) await ApplyDefaultsAsync(line);
    }

    private void LinesGrid_OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditingElement is TextBox box) box.Text = InputNormalizer.Normalize(box.Text);
    }

    private async Task ApplyDefaultsAsync(PurchaseEditLine line)
    {
        if (line.ProductId <= 0 ||
            line.DefaultsLoadedForProductId == line.ProductId || VendorBox.SelectedValue is not long vendorId ||
            ReceivedDateBox.SelectedDate is not DateTime receivedDate) return;
        try
        {
            PurchaseLineDefaults defaults = await _purchasing.GetLineDefaultsAsync(vendorId, line.ProductId,
                DateOnly.FromDateTime(receivedDate));
            line.Rate = defaults.LastRate?.Value ?? 0;
            line.TaxRate = defaults.TaxRate.Value;
            line.Mrp = defaults.Mrp?.Value;
            line.SellingPrice = defaults.SellingPrice?.Value ?? 0;
            line.Expiry = defaults.SuggestedExpiry?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            line.ApplyMetadata(defaults);
            line.DefaultsLoadedForProductId = line.ProductId;
            LinesGrid.Items.Refresh();
            UpdateTotals();
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }
    private void TotalsInput_OnTextChanged(object sender, TextChangedEventArgs e) => UpdateTotals();

    private async void ProductSearchBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await FindProductAsync();
    }

    private async void FindProduct_OnClick(object sender, RoutedEventArgs e) => await FindProductAsync();

    private async Task FindProductAsync()
    {
        if (_scanner is null || _catalog is null) return;
        try
        {
            string query = ProductSearchBox.Text.Trim();
            if (query.Length == 0) return;
            ScanResult scan = await _scanner.ResolveAsync(query);
            if (scan.ProductId is long productId)
            {
                await EnsureProductChoiceAsync(productId);
                var line = new PurchaseEditLine
                {
                    ProductId = productId,
                    Quantity = scan.Quantity?.Value ?? 1m,
                };
                Lines.Add(line);
                LinesGrid.SelectedItem = line;
                LinesGrid.ScrollIntoView(line);
                await ApplyDefaultsAsync(line);
                ProductSearchBox.Clear();
                ErrorText.Text = "";
                return;
            }
            IReadOnlyList<ProductListItem> matches = await _catalog.SearchAsync(new ProductSearchFilter(query, Limit: 50));
            foreach (ProductListItem match in matches)
                if (_availableProducts.All(product => product.Id != match.Id)) _availableProducts.Add(match);
            var existingIds = Lines.Select(static line => line.ProductId).ToHashSet();
            ProductColumn.ItemsSource = matches.Concat(_availableProducts.Where(product => existingIds.Contains(product.Id)))
                .DistinctBy(static product => product.Id).ToList();
            if (matches.Count == 1)
            {
                var line = new PurchaseEditLine { ProductId = matches[0].Id };
                Lines.Add(line);
                LinesGrid.SelectedItem = line;
                await ApplyDefaultsAsync(line);
                ProductSearchBox.Clear();
                ErrorText.Text = "";
            }
            else
            {
                ErrorText.Text = matches.Count == 0
                    ? TranslationSource.Instance["Purchasing_NoProduct"]
                    : TranslationSource.Instance.Format("Purchasing_MatchesFound", matches.Count);
                if (matches.Count > 1) OpenProductChoices();
            }
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private void OpenProductChoices()
    {
        PurchaseEditLine? line = Lines.FirstOrDefault(static item => item.ProductId == 0);
        if (line is null)
        {
            line = new PurchaseEditLine();
            Lines.Add(line);
        }
        LinesGrid.SelectedItem = line;
        LinesGrid.CurrentCell = new DataGridCellInfo(line, ProductColumn);
        LinesGrid.ScrollIntoView(line, ProductColumn);
        Dispatcher.BeginInvoke(() =>
        {
            LinesGrid.Focus();
            LinesGrid.BeginEdit();
            if (FindVisualChild<ComboBox>(LinesGrid) is { } choices)
            {
                choices.MaxDropDownHeight = 320;
                choices.Focus();
                choices.IsDropDownOpen = true;
            }
        }, DispatcherPriority.Loaded);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) return result;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private async Task EnsureProductChoiceAsync(long productId)
    {
        if (_catalog is null || _availableProducts.Any(product => product.Id == productId)) return;
        ProductDetails details = await _catalog.GetAsync(productId);
        _availableProducts.Add(new ProductListItem { Id = productId, Name = details.Product.Name, Sku = details.Product.Sku });
        ProductColumn.ItemsSource = _availableProducts;
    }

    private void UpdateTotals()
    {
        if (SubtotalText is null || TaxText is null || ComputedText is null || DifferenceText is null) return;
        try
        {
            List<PurchaseEditLine> editLines = Lines.Where(static line => line.ProductId > 0).ToList();
            List<PurchaseLineInput> lines = editLines.Select(static line => line.ToInput()).ToList();
            Money subtotal = Money.Zero;
            Money discounts = Money.Zero;
            Money tax = Money.Zero;
            var costLines = new List<PurchaseCostLine>(lines.Count);
            foreach (PurchaseLineInput line in lines)
            {
                subtotal += line.Rate * line.Quantity;
                discounts += line.Discount;
                Money lineTax = Tax.FromExclusive(line.Rate * line.Quantity - line.Discount, line.TaxRate).Tax;
                tax += lineTax;
                costLines.Add(new PurchaseCostLine(line.Quantity, line.FreeQuantity, line.Rate, line.Discount, lineTax));
            }
            Money charges = new(Parse(ChargesBox?.Text ?? "0"));
            IReadOnlyList<Money> landed = LandedCost.Compute(costLines, charges, _taxIsCost);
            var warnings = new List<string>();
            DateOnly received = DateOnly.FromDateTime(ReceivedDateBox.SelectedDate ?? DateTime.Today);
            for (int index = 0; index < editLines.Count; index++)
            {
                PurchaseEditLine editLine = editLines[index];
                PurchaseLineInput line = lines[index];
                Money sellingExTax = editLine.PriceIncludesTax
                    ? Tax.FromInclusive(line.SellingPrice, line.TaxRate).Taxable : line.SellingPrice;
                decimal? margin = sellingExTax > Money.Zero
                    ? (sellingExTax.Value - landed[index].Value) * 100m / sellingExTax.Value : null;
                editLine.SetCostPreview(landed[index].Value.ToString("N2", TranslationSource.FormatCulture),
                    margin is null ? "—" : margin.Value.ToString("N1", TranslationSource.FormatCulture) + "%",
                    margin < 0m);
                string product = _availableProducts.FirstOrDefault(p => p.Id == line.ProductId)?.Name
                    ?? ProductColumn.ItemsSource?.OfType<ProductListItem>()
                        .FirstOrDefault(p => p.Id == line.ProductId)?.Name
                    ?? line.ProductId.ToString(CultureInfo.InvariantCulture);
                if (line.ExpiryDate is DateOnly expiry && expiry <= received.AddDays(editLine.ExpiryAlertDays))
                    warnings.Add(TranslationSource.Instance.Format("Purchasing_WarningShortExpiry", product));
                if (sellingExTax < landed[index])
                    warnings.Add(TranslationSource.Instance.Format("Purchasing_WarningBelowCost", product));
                if (editLine.LastPurchaseRate is decimal lastRate && lastRate > 0m &&
                    Math.Abs(line.Rate.Value - lastRate) > lastRate * 0.1m)
                    warnings.Add(TranslationSource.Instance.Format("Purchasing_WarningRateChanged", product));
            }
            foreach (PurchaseEditLine line in Lines.Except(editLines)) line.SetCostPreview("", "", false);
            WarningText.Text = string.Join("  •  ", warnings);
            Money beforeRound = subtotal - discounts + tax + charges;
            Money roundOff = MoneyMath.RoundOff(beforeRound, _roundStep);
            Money total = beforeRound + roundOff;
            var format = new DisplayFormatter();
            SubtotalText.Text = format.Number(subtotal.Value);
            DiscountText.Text = format.Number(discounts.Value);
            TaxText.Text = format.Number(tax.Value);
            RoundOffText.Text = format.Number(roundOff.Value);
            ComputedText.Text = format.Number(total.Value);
            Money? bill = ParseOptionalMoney(BillTotalBox?.Text ?? "");
            DifferenceText.Text = bill is null ? "—" : format.Number((bill.Value - total).Value);
            DifferenceText.Foreground = bill is not null && bill.Value != total ? Brushes.Crimson : Brushes.ForestGreen;
        }
        catch (Exception)
        {
            foreach (PurchaseEditLine line in Lines) line.SetCostPreview("", "", false);
            WarningText.Text = "";
            ComputedText.Text = "—";
            DifferenceText.Text = "—";
        }
    }
    private static decimal Parse(string value) => decimal.Parse(
        string.IsNullOrWhiteSpace(value) ? "0" : InputNormalizer.Normalize(value),
        NumberStyles.Number, TranslationSource.FormatCulture);
    private static Money? ParseOptionalMoney(string value) => string.IsNullOrWhiteSpace(value) ? null : new Money(Parse(value));

    public sealed class PurchaseEditLine : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public long ProductId { get; set; }
        private string _lineKind = "PRODUCT";
        public string LineKind
        {
            get => _lineKind;
            set { if (_lineKind == value) return; _lineKind = value; Notify(); Notify(nameof(IsStockLine)); Notify(nameof(IsExpenseLine)); }
        }
        public bool IsStockLine => LineKind != "EXPENSE";
        public bool IsExpenseLine => LineKind == "EXPENSE";
        public long? ExpenseCategoryId { get; set; }
        public long DefaultsLoadedForProductId { get; set; }
        public decimal? LastPurchaseRate { get; private set; }
        public int ExpiryAlertDays { get; private set; }
        public bool PriceIncludesTax { get; private set; } = true;
        public string LandedCostText { get; private set; } = "";
        public string MarginText { get; private set; } = "";
        public bool MarginNegative { get; private set; }
        public string? Batch { get; set; }
        public string? ManufacturingDate { get; set; }
        public string? Expiry { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal FreeQuantity { get; set; }
        public decimal Rate { get; set; }
        public decimal Discount { get; set; }
        public decimal TaxRate { get; set; }
        public decimal? Mrp { get; set; }
        public decimal SellingPrice { get; set; }
        public void ApplyMetadata(PurchaseLineDefaults defaults)
        {
            LastPurchaseRate = defaults.LastRate?.Value;
            ExpiryAlertDays = defaults.ExpiryAlertDays;
            PriceIncludesTax = defaults.PriceIncludesTax;
        }
        public void SetCostPreview(string landedCost, string margin, bool marginNegative)
        {
            if (LandedCostText == landedCost && MarginText == margin && MarginNegative == marginNegative) return;
            LandedCostText = landedCost;
            MarginText = margin;
            MarginNegative = marginNegative;
            Notify(nameof(LandedCostText));
            Notify(nameof(MarginText));
            Notify(nameof(MarginNegative));
        }
        private void Notify([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        public PurchaseLineInput ToInput() => new(ProductId, Batch,
            string.IsNullOrWhiteSpace(ManufacturingDate) ? null : DateOnly.ParseExact(InputNormalizer.Normalize(ManufacturingDate), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(Expiry) ? null : DateOnly.ParseExact(InputNormalizer.Normalize(Expiry), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            new Quantity(Quantity), new Quantity(FreeQuantity), new Money(Rate), new Money(Discount), new Percent(TaxRate),
            Mrp is null ? null : new Money(Mrp.Value), new Money(SellingPrice))
        { LineKind = LineKind, ExpenseCategoryId = IsExpenseLine ? ExpenseCategoryId : null };
        public static PurchaseEditLine FromInput(PurchaseLineInput line) => new()
        {
            ProductId = line.ProductId,
            LineKind = line.LineKind,
            ExpenseCategoryId = line.ExpenseCategoryId,
            Batch = line.BatchNumber,
            DefaultsLoadedForProductId = line.ProductId,
            ManufacturingDate = line.ManufacturingDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Expiry = line.ExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Quantity = line.Quantity.Value,
            FreeQuantity = line.FreeQuantity.Value,
            Rate = line.Rate.Value,
            Discount = line.Discount.Value,
            TaxRate = line.TaxRate.Value,
            Mrp = line.Mrp?.Value,
            SellingPrice = line.SellingPrice.Value,
        };
    }

    public sealed record PurchaseKindChoice(string Code, string Label);
}
