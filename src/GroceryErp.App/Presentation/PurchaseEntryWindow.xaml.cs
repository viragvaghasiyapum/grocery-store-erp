using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly long? _openingDraftId;
    private long? _purchaseId;
    private Money _roundStep = new(1);
    private bool _taxIsCost;
    private (PurchaseEditLine Line, PropertyInfo Property, object? Value)? _uneditedCell;
    // Every rate's percent, including inactive ones a reopened draft still uses.
    private readonly Dictionary<long, Percent> _ratePercents = [];
    public ObservableCollection<PurchaseEditLine> Lines { get; } = [];
    public IReadOnlyList<PurchaseKindChoice> LineKinds { get; }
    public ObservableCollection<ExpenseCategoryItem> ExpenseCategories { get; } = [];

    /// <summary>Gets the tax rates a line can choose: the active ones, and any rate a loaded draft already uses.</summary>
    public ObservableCollection<TaxRateItem> TaxRates { get; } = [];

    /// <summary>Gets the GRN number after a successful post, so the caller can show it.</summary>
    public string? PostedDocumentNumber { get; private set; }

    public PurchaseEntryWindow(PurchasingService purchasing, long? draftId = null, bool loadOnShow = true)
    {
        LineKinds =
        [
            new("PRODUCT", TranslationSource.Instance["Purchasing_TypeProduct"]),
            new("INTERNAL", TranslationSource.Instance["Purchasing_TypeInternal"]),
            new("EXPENSE", TranslationSource.Instance["Purchasing_TypeExpense"]),
        ];
        InitializeComponent();
        _purchasing = purchasing;
        _openingDraftId = draftId;
        DataContext = this;
        Lines.CollectionChanged += Lines_OnCollectionChanged;
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control) return;
            Save_OnClick(this, args);
            args.Handled = true;
        };
        if (loadOnShow) Loaded += LoadAsync;
    }

    // Typed rates and discounts include tax when the bill prints them that way.
    private bool RatesIncludeTax => RatesIncludeTaxBox.IsChecked == true;

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
            foreach (ExpenseCategoryItem category in await services.GetRequiredService<MoneyOutService>()
                .GetExpenseCategoriesAsync())
                ExpenseCategories.Add(category);
            IReadOnlyList<TaxRateItem> rates = await _catalog.GetTaxRatesAsync();
            UseTaxRates(rates);
            if (_openingDraftId is long draftId)
            {
                PurchaseDraft draft = await _purchasing.GetDraftAsync(draftId);
                _purchaseId = draft.Id;
                PurchaseDraftInput input = draft.Input;
                // A rate made inactive since the draft was saved stays available to its lines.
                foreach (TaxRateItem rate in rates.Where(rate => !rate.IsActive &&
                             input.Lines.Any(line => line.TaxRateId == rate.Id)))
                    TaxRates.Add(rate);
                VendorBox.SelectedValue = input.VendorId;
                RatesIncludeTaxBox.IsChecked = input.RatesIncludeTax;
                InvoiceBox.Text = input.VendorInvoiceNumber ?? "";
                InvoiceDateBox.SelectedDate = input.VendorInvoiceDate?.ToDateTime(TimeOnly.MinValue);
                ReceivedDateBox.SelectedDate = input.ReceivedDate.ToDateTime(TimeOnly.MinValue);
                BillTotalBox.Text = input.VendorBillTotal?.Value.ToString(CultureInfo.InvariantCulture) ?? "";
                ChargesBox.Text = input.OtherCharges.Value.ToString(CultureInfo.InvariantCulture);
                NotesBox.Text = input.Notes ?? "";
                foreach (PurchaseLineInput line in input.Lines)
                {
                    PurchaseEditLine editLine = PurchaseEditLine.FromInput(line);
                    editLine.ProductName = (await _catalog.GetAsync(line.ProductId)).Product.Name;
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
            UpdateTotals();
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    /// <summary>Sets the rates the Tax lists offer (the active ones) and every rate's percent for the totals.</summary>
    public void UseTaxRates(IReadOnlyList<TaxRateItem> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        _ratePercents.Clear();
        TaxRates.Clear();
        foreach (TaxRateItem rate in rates)
        {
            _ratePercents[rate.Id] = new Percent(rate.Rate);
            if (rate.IsActive) TaxRates.Add(rate);
        }
        UpdateTotals();
    }

    private Percent RateOf(long? taxRateId) =>
        taxRateId is { } id && _ratePercents.TryGetValue(id, out Percent rate) ? rate : Percent.Zero;

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            if (await SaveAsync() is not long id) return;
            _purchaseId = id;
            ErrorText.Text = TranslationSource.Instance["Purchasing_DraftSaved"];
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
        finally { SetBusy(false); }
    }

    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            if (await SaveAsync() is not long id) return;
            _purchaseId = id;
            PostedPurchase posted = await _purchasing.PostAsync(id, OperationGuard.NewId());
            PostedDocumentNumber = posted.DocumentNumber;
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
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
        finally { SetBusy(false); }
    }

    // Checks the header and every line before saving; the entered rows stay as typed on failure.
    private async Task<long?> SaveAsync()
    {
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var form = new FormValidator();
        long? vendorId = form.Choice(VendorField, VendorBox.SelectedValue);
        DateOnly? received = form.Date(ReceivedField, ReceivedDateBox.SelectedDate);
        DateOnly? invoiceDate = InvoiceDateBox.SelectedDate is DateTime date ? DateOnly.FromDateTime(date) : null;
        if (invoiceDate > received) form.AddKey(InvoiceDateField, "Purchasing_InvoiceAfterReceived");
        Money? charges = form.Money(ChargesField, ChargesBox.Text);
        Money? billTotal = form.Money(BillTotalField, BillTotalBox.Text);
        var lineErrors = new List<string>();
        var lines = new List<PurchaseLineInput>();
        int number = 0;
        foreach (PurchaseEditLine line in Lines.Where(static line => line.ProductId > 0))
        {
            number++;
            if (line.Problem() is { } problem)
                lineErrors.Add(TranslationSource.Instance.Format("Purchasing_LineError", number, TranslationSource.Instance[problem]));
            else lines.Add(line.ToInput());
        }
        if (number == 0) lineErrors.Add(TranslationSource.Instance["Purchasing_NoLines"]);
        if (!form.Apply(this, Summary, lineErrors)) return null;
        var input = new PurchaseDraftInput(vendorId!.Value, InvoiceBox.Text, invoiceDate, received!.Value, billTotal,
            charges ?? Money.Zero, NotesBox.Text, lines)
        { RatesIncludeTax = RatesIncludeTax };
        return await _purchasing.SaveDraftAsync(_purchaseId, input);
    }

    // Ctrl+D copies the cell above into the current cell (spreadsheet copy-down). The line type,
    // product and expense category are never copied, so a row's classification is always chosen.
    // Ctrl+Delete removes the selected lines.
    private void LinesGrid_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            RemoveLines(LinesGrid.SelectedItems.OfType<PurchaseEditLine>().ToList());
            return;
        }
        if (e.Key != Key.D || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        // Type and expense category are list columns, not bound text columns, and the product is
        // read-only. The tax list is the one list column that copies down.
        if (LinesGrid.CurrentItem is not PurchaseEditLine line) return;
        int index = Lines.IndexOf(line);
        if (index <= 0) return;
        if (ReferenceEquals(LinesGrid.CurrentColumn, TaxColumn))
        {
            line.TaxRateId = Lines[index - 1].TaxRateId;
            return;
        }
        if (BoundProperty(LinesGrid.CurrentColumn) is not { } property) return;
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        property.SetValue(line, property.GetValue(Lines[index - 1]));
        LinesGrid.Items.Refresh();
        UpdateTotals();
    }

    private static PropertyInfo? BoundProperty(DataGridColumn? column) =>
        column is DataGridBoundColumn { IsReadOnly: false, Binding: Binding { Path.Path: { Length: > 0 } path } } &&
        typeof(PurchaseEditLine).GetProperty(path) is { CanWrite: true } property ? property : null;

    private void RemoveLine_OnClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PurchaseEditLine line) RemoveLines([line]);
    }

    private void RemoveSelectedLines_OnClick(object sender, RoutedEventArgs e) =>
        RemoveLines(LinesGrid.SelectedItems.OfType<PurchaseEditLine>().ToList());

    // A value being typed in another line is kept; the grid lets go of a removed line's edit itself.
    private void RemoveLines(List<PurchaseEditLine> lines)
    {
        if (lines.Count == 0) return;
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (PurchaseEditLine line in lines) Lines.Remove(line);
    }

    private void Lines_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (PurchaseEditLine line in e.OldItems?.OfType<PurchaseEditLine>() ?? []) line.PropertyChanged -= Line_OnPropertyChanged;
        foreach (PurchaseEditLine line in e.NewItems?.OfType<PurchaseEditLine>() ?? []) line.PropertyChanged += Line_OnPropertyChanged;
        ShowExpenseCategories();
        UpdateTotals();
    }

    private void Line_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PurchaseEditLine.LineKind)) ShowExpenseCategories();
        else if (e.PropertyName == nameof(PurchaseEditLine.TaxRateId)) UpdateTotals();
    }

    private void ShowExpenseCategories() => ExpenseCategoryColumn.Visibility =
        Lines.Any(static line => line.IsExpenseLine) ? Visibility.Visible : Visibility.Collapsed;

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void SetBusy(bool busy) { SaveButton.IsEnabled = !busy; PostButton.IsEnabled = !busy; }
    private async void LinesGrid_OnCurrentCellChanged(object sender, EventArgs e)
    {
        UpdateTotals();
        if (LinesGrid.CurrentItem is PurchaseEditLine line) await ApplyDefaultsAsync(line);
    }

    private void LinesGrid_OnPreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e) =>
        _uneditedCell = e.Row.Item is PurchaseEditLine line && BoundProperty(e.Column) is { } property
            ? (line, property, property.GetValue(line)) : null;

    // Figures reach the line while they are typed, so Esc puts back the value from before the edit.
    private void LinesGrid_OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel)
        {
            if (_uneditedCell is { } unedited && ReferenceEquals(unedited.Line, e.Row.Item))
            {
                unedited.Property.SetValue(unedited.Line, unedited.Value);
                UpdateTotals();
            }
        }
        else if (e.EditingElement is TextBox box) box.Text = InputNormalizer.Normalize(box.Text);
        _uneditedCell = null;
    }

    private void LinesGrid_OnSourceUpdated(object sender, DataTransferEventArgs e) => UpdateTotals();

    private async Task ApplyDefaultsAsync(PurchaseEditLine line)
    {
        if (line.ProductId <= 0 ||
            line.DefaultsLoadedForProductId == line.ProductId || VendorBox.SelectedValue is not long vendorId ||
            ReceivedDateBox.SelectedDate is not DateTime receivedDate) return;
        try
        {
            PurchaseLineDefaults defaults = await _purchasing.GetLineDefaultsAsync(vendorId, line.ProductId,
                DateOnly.FromDateTime(receivedDate));
            // The line starts at the product's rate when it is active; otherwise one must be chosen.
            line.TaxRateId = defaults.TaxRateId is { } rateId && TaxRates.Any(rate => rate.Id == rateId) ? rateId : null;
            // The vendor's last rate is stored before tax; a bill typed including tax shows it with tax.
            Percent taxRate = RateOf(line.TaxRateId);
            line.Rate = defaults.LastRate is { } lastRate
                ? (RatesIncludeTax ? lastRate + lastRate.PercentOf(taxRate) : lastRate).Value : 0;
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

    // The figures already typed stay as they are; only their meaning changes, so the totals follow.
    private void RatesIncludeTax_OnChanged(object sender, RoutedEventArgs e)
    {
        RateColumn.Header = TranslationSource.Instance[RatesIncludeTax ? "Purchasing_RateInclGst" : "Purchasing_Rate"];
        UpdateTotals();
    }

    // A new purchase starts the way the vendor's last bill was typed, until it has a line.
    private async void VendorBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_purchaseId is not null || Lines.Count > 0 || VendorBox.SelectedValue is not long vendorId) return;
        try
        {
            bool includeTax = await _purchasing.GetLastRatesIncludeTaxAsync(vendorId);
            if (_purchaseId is null && Lines.Count == 0 && Equals(VendorBox.SelectedValue, vendorId))
                RatesIncludeTaxBox.IsChecked = includeTax;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private async void ProductSearchBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ProductChoices.IsOpen)
        {
            e.Handled = true;
            ProductChoices.FocusSelected();
            return;
        }
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
                ProductInput product = (await _catalog.GetAsync(productId)).Product;
                await AddLineAsync(productId, product.Name, product.ProductClass, scan.Quantity?.Value ?? 1m);
                return;
            }
            IReadOnlyList<ProductListItem> matches = await _catalog.SearchAsync(new ProductSearchFilter(query, Limit: 50));
            if (matches.Count == 1)
            {
                await AddLineAsync(matches[0].Id, matches[0].Name, matches[0].ProductClass);
                return;
            }
            ErrorText.Text = matches.Count == 0
                ? TranslationSource.Instance["Purchasing_NoProduct"]
                : TranslationSource.Instance.Format("Purchasing_MatchesFound", matches.Count);
            if (matches.Count > 1) ProductChoices.Open(matches);
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private void ProductChoices_OnClosed(object? sender, EventArgs e) => ProductSearchBox.Focus();

    private async void ProductChoices_OnPicked(object? sender, ProductListItem product)
    {
        try { await AddLineAsync(product.Id, product.Name, product.ProductClass); }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    // An internal-use product starts as an Internal use line, which is the only type it can be bought as
    // besides Expense.
    private async Task AddLineAsync(long productId, string name, string? productClass, decimal quantity = 1m)
    {
        var line = new PurchaseEditLine
        {
            ProductId = productId,
            ProductName = name,
            LineKind = productClass == "INTERNAL" ? "INTERNAL" : "PRODUCT",
            Quantity = quantity,
        };
        Lines.Add(line);
        LinesGrid.SelectedItem = line;
        LinesGrid.ScrollIntoView(line);
        ProductSearchBox.Clear();
        ErrorText.Text = "";
        await ApplyDefaultsAsync(line);
    }

    private void UpdateTotals()
    {
        if (SubtotalText is null || TaxText is null || ComputedText is null || DifferenceText is null) return;
        try
        {
            List<PurchaseEditLine> editLines = Lines.Where(static line => line.ProductId > 0).ToList();
            bool includeTax = RatesIncludeTax;
            Percent[] rates = editLines.Select(line => RateOf(line.TaxRateId)).ToArray();
            List<PurchaseLineInput> lines = editLines
                .Select((line, index) => includeTax ? line.ToInput().ExcludingTax(rates[index]) : line.ToInput()).ToList();
            Money subtotal = Money.Zero;
            Money discounts = Money.Zero;
            Money tax = Money.Zero;
            var costLines = new List<PurchaseCostLine>(lines.Count);
            for (int index = 0; index < lines.Count; index++)
            {
                PurchaseLineInput line = lines[index];
                subtotal += line.Rate * line.Quantity;
                discounts += line.Discount;
                Money lineTax = Tax.FromExclusive(line.Rate * line.Quantity - line.Discount, rates[index]).Tax;
                tax += lineTax;
                costLines.Add(new PurchaseCostLine(line.Quantity, line.FreeQuantity, line.Rate, line.Discount, lineTax));
            }
            Money charges = new(Parse(ChargesBox?.Text ?? "0"));
            IReadOnlyList<PurchaseUnitCost> units = LandedCost.Compute(costLines, charges, _taxIsCost);
            var warnings = new List<string>();
            DateOnly received = ReceivedDateBox.SelectedDate is DateTime picked ? DateOnly.FromDateTime(picked) : App.BusinessDate;
            for (int index = 0; index < editLines.Count; index++)
            {
                PurchaseEditLine editLine = editLines[index];
                PurchaseLineInput line = lines[index];
                PurchaseUnitCost unit = units[index];
                // Margin is measured as P&L books it: the selling price before the line's tax against
                // the purchase price in Regular mode; with no tax on sales, the whole price against the
                // purchase price including tax.
                Money saleValue = _taxIsCost ? line.SellingPrice : Tax.FromInclusive(line.SellingPrice, rates[index]).Taxable;
                decimal? margin = editLine.IsStockLine && saleValue > Money.Zero
                    ? (saleValue.Value - unit.Booked.Value) * 100m / saleValue.Value : null;
                editLine.SetCostPreview(AmountConverter.Text(TwoDecimals.Round(unit.BeforeTax)),
                    AmountConverter.Text(TwoDecimals.Remainder(unit.InclTax, unit.BeforeTax)),
                    AmountConverter.Text(TwoDecimals.Round(unit.InclTax)),
                    margin is null ? "—" : margin.Value.ToString("N1", TranslationSource.FormatCulture) + "%",
                    margin < 0m);
                string product = editLine.ProductName;
                if (line.ExpiryDate is DateOnly expiry && expiry <= received.AddDays(editLine.ExpiryAlertDays))
                    warnings.Add(TranslationSource.Instance.Format("Purchasing_WarningShortExpiry", product));
                if (editLine.IsStockLine && saleValue < unit.Booked)
                    warnings.Add(TranslationSource.Instance.Format("Purchasing_WarningBelowCost", product));
                if (editLine.LastPurchaseRate is decimal lastRate && lastRate > 0m &&
                    Math.Abs(line.Rate.Value - lastRate) > lastRate * 0.1m)
                    warnings.Add(TranslationSource.Instance.Format("Purchasing_WarningRateChanged", product));
            }
            foreach (PurchaseEditLine line in Lines.Except(editLines)) line.ClearCostPreview();
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
            foreach (PurchaseEditLine line in Lines) line.ClearCostPreview();
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
        public string ProductName { get; set; } = "";
        private string _lineKind = "PRODUCT";
        public string LineKind
        {
            get => _lineKind;
            set { if (_lineKind == value) return; _lineKind = value; Notify(); Notify(nameof(IsStockLine)); Notify(nameof(IsExpenseLine)); }
        }
        public bool IsStockLine => LineKind != "EXPENSE";
        public bool IsExpenseLine => LineKind == "EXPENSE";
        public long? ExpenseCategoryId { get; set; }
        private long? _taxRateId;

        /// <summary>Gets or sets the tax rate chosen for the line from the Tax list.</summary>
        public long? TaxRateId
        {
            get => _taxRateId;
            set { if (_taxRateId == value) return; _taxRateId = value; Notify(); }
        }
        public long DefaultsLoadedForProductId { get; set; }
        public decimal? LastPurchaseRate { get; private set; }
        public int ExpiryAlertDays { get; private set; }

        /// <summary>Gets one piece's purchase price before tax, as shown.</summary>
        public string PurchasePriceText { get; private set; } = "";

        /// <summary>Gets one piece's tax as shown, so the shown prices add up.</summary>
        public string UnitTaxText { get; private set; } = "";

        /// <summary>Gets one piece's purchase price including tax, as shown.</summary>
        public string InclTaxText { get; private set; } = "";
        public string MarginText { get; private set; } = "";
        public bool MarginNegative { get; private set; }
        public string? Batch { get; set; }
        public string? ManufacturingDate { get; set; }
        public string? Expiry { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal FreeQuantity { get; set; }
        public decimal Rate { get; set; }
        public decimal Discount { get; set; }
        public decimal? Mrp { get; set; }
        public decimal SellingPrice { get; set; }
        public void ApplyMetadata(PurchaseLineDefaults defaults)
        {
            ArgumentNullException.ThrowIfNull(defaults);
            LastPurchaseRate = defaults.LastRate?.Value;
            ExpiryAlertDays = defaults.ExpiryAlertDays;
        }
        public void SetCostPreview(string purchasePrice, string unitTax, string inclTax, string margin, bool marginNegative)
        {
            if (PurchasePriceText == purchasePrice && UnitTaxText == unitTax && InclTaxText == inclTax &&
                MarginText == margin && MarginNegative == marginNegative) return;
            PurchasePriceText = purchasePrice;
            UnitTaxText = unitTax;
            InclTaxText = inclTax;
            MarginText = margin;
            MarginNegative = marginNegative;
            Notify(nameof(PurchasePriceText));
            Notify(nameof(UnitTaxText));
            Notify(nameof(InclTaxText));
            Notify(nameof(MarginText));
            Notify(nameof(MarginNegative));
        }
        public void ClearCostPreview() => SetCostPreview("", "", "", "", false);
        private void Notify([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// <summary>Gets the resource key of the first problem that would stop saving this line, or null.</summary>
        public string? Problem()
        {
            if (Quantity <= 0) return "Purchasing_LineQuantity";
            if (FreeQuantity < 0 || Rate < 0 || Discount < 0 || SellingPrice < 0 || Mrp < 0)
                return "Purchasing_LineNegative";
            if (TaxRateId is null) return "Purchasing_TaxRateRequired";
            if (IsExpenseLine && ExpenseCategoryId is null) return "Purchasing_ExpenseCategoryRequired";
            if (!IsDate(ManufacturingDate) || !IsDate(Expiry)) return "Purchasing_LineDateFormat";
            if (IsStockLine && Mrp is { } mrp && SellingPrice > mrp) return "Error_PriceAboveMrp";
            return null;
        }

        private static bool IsDate(string? text) => string.IsNullOrWhiteSpace(text) ||
            DateOnly.TryParseExact(InputNormalizer.Normalize(text), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
        public PurchaseLineInput ToInput() => new(ProductId, Batch,
            string.IsNullOrWhiteSpace(ManufacturingDate) ? null : DateOnly.ParseExact(InputNormalizer.Normalize(ManufacturingDate), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(Expiry) ? null : DateOnly.ParseExact(InputNormalizer.Normalize(Expiry), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            new Quantity(Quantity), new Quantity(FreeQuantity), new Money(Rate), new Money(Discount), TaxRateId,
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
            TaxRateId = line.TaxRateId,
            Mrp = line.Mrp?.Value,
            SellingPrice = line.SellingPrice.Value,
        };
    }

    public sealed record PurchaseKindChoice(string Code, string Label);
}
