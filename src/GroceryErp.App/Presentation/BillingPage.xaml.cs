using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using GroceryErp.Sales;
using GroceryErp.UI.Common;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class BillingPage : Page, IDisposable
{
    private readonly SalesService _sales;
    private readonly ScanResolver _resolver;
    private readonly CatalogService _catalog;
    private readonly InventoryService _inventory;
    private readonly ReceiptPrinter _printer;
    private long? _saleId;
    private string _completionOperationId = OperationGuard.NewId();
    private long? _lastCompletedSaleId;
    private ScanBurstDetector? _detector;
    private bool _offeredSavedBills;
    private bool _isCompleting;
    private long? _lastScannedProductId;
    private Quantity _choiceQuantity = Quantity.One;
    private Task _billOperations = Task.CompletedTask;
    private int _refreshVersion;
    private readonly DispatcherTimer _printerTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    public BillingPage() : this(
        ((App)Application.Current).Services.GetRequiredService<SalesService>(),
        ((App)Application.Current).Services.GetRequiredService<ScanResolver>(),
        ((App)Application.Current).Services.GetRequiredService<CatalogService>(),
        ((App)Application.Current).Services.GetRequiredService<InventoryService>(),
        ((App)Application.Current).Services.GetRequiredService<ReceiptPrinter>())
    { }

    public BillingPage(SalesService sales, ScanResolver resolver, CatalogService catalog,
        InventoryService inventory, ReceiptPrinter printer, bool loadOnShow = true)
    {
        InitializeComponent();
        _sales = sales;
        _resolver = resolver;
        _catalog = catalog;
        _inventory = inventory;
        _printer = printer;
        _printerTimer.Tick += async (_, _) => await RefreshPrinterAsync();
        UpdateLineActions();
        if (loadOnShow)
        {
            Loaded += BillingPage_OnLoaded;
            Loaded += (_, _) =>
            {
                TranslationSource.Instance.PropertyChanged -= OnLanguageChanged;
                TranslationSource.Instance.PropertyChanged += OnLanguageChanged;
            };
        }
        Unloaded += (_, _) =>
        {
            _detector?.Dispose();
            _detector = null;
            _printerTimer.Stop();
            TranslationSource.Instance.PropertyChanged -= OnLanguageChanged;
        };
        PreviewKeyDown += OnShortcut;
    }

    private async void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") await RefreshAsync();
    }

    private async void BillingPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_detector is null && Window.GetWindow(this) is { } window)
            {
                ISettings settings = ((App)Application.Current).Services.GetRequiredService<ISettings>();
                _detector = new(settings.Current.ScannerMaxGapMs, settings.Current.ScannerMinLength);
                _detector.ScanReceived += async (_, code) =>
                {
                    (Window.GetWindow(this) as ShellWindow)?.RecordScannerActivity();
                    ProductChoices.IsOpen = false;
                    string typed = InputNormalizer.Normalize(ScanBox.Text).Trim();
                    int marker = typed.IndexOf('*', StringComparison.Ordinal);
                    string input = marker > 0 ? typed[..(marker + 1)] + code : code;
                    ScanBox.Clear();
                    await EnqueueBillActionAsync(() => AddTextAsync(input, scanned: true));
                };
                _detector.Attach(window);
            }
            if (!_offeredSavedBills)
            {
                _offeredSavedBills = true;
                SavedBillPage available = await _sales.GetSavedBillPageAsync(
                    includeDrafts: true, excludeId: _saleId, limit: 1);
                if (available.Items.Count > 0) await OpenSavedBillsAsync(includeDrafts: true);
            }
            await RefreshAsync();
            await RefreshHeldChipsAsync();
            await RefreshPrinterAsync();
            _printerTimer.Start();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        ScanBox.Focus();
    }

    private async void ScanBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ProductChoices.IsOpen)
        {
            e.Handled = true;
            ProductChoices.FocusSelected();
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string value = ScanBox.Text;
        if (value.Trim().EndsWith('*')) return;
        ScanBox.Clear();
        await EnqueueBillActionAsync(() => AddTextAsync(value));
    }

    private Task EnqueueBillActionAsync(Func<Task> action)
    {
        Task previous = _billOperations;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _billOperations = completion.Task;
        return RunAsync();

        async Task RunAsync()
        {
            try { await previous; await action(); }
            catch (Exception exception) { StatusText.Text = Message(exception); }
            finally { completion.TrySetResult(); }
        }
    }

    private async Task AddTextAsync(string text, bool scanned = false)
    {
        try
        {
            Quantity prefix = Quantity.One;
            string code = InputNormalizer.Normalize(text).Trim();
            int separator = code.IndexOf('*', StringComparison.Ordinal);
            if (separator >= 0)
            {
                if (separator == 0 || !decimal.TryParse(code[..separator], NumberStyles.Number,
                        CultureInfo.GetCultureInfo("en-IN"), out decimal parsed) || parsed <= 0 ||
                    separator == code.Length - 1)
                {
                    StatusText.Text = TranslationSource.Instance["Pos_QuantityPrefixInvalid"];
                    return;
                }
                prefix = new Quantity(parsed);
                code = code[(separator + 1)..];
            }
            if (code.Length == 0) return;
            ScanResult result = await _resolver.ResolveAsync(code);
            long productId;
            Quantity quantity;
            long? lotId;
            string? barcode = code;
            if (result.Kind == ScanResultKind.NotFound)
            {
                // A typed name is a search: one match joins the bill and several are listed to pick
                // from. Only a code that reads as a barcode (a long number, or anything the scanner
                // sent that matches nothing) is offered as an unknown barcode, so a name is never
                // saved as one.
                bool looksLikeBarcode = code.Length >= 8 && code.All(char.IsAsciiDigit);
                IReadOnlyList<ProductListItem> matches = looksLikeBarcode ? []
                    : await _catalog.SearchAsync(new(Query: code, Limit: 50, SaleableOnly: true));
                if (matches.Count > 1)
                {
                    _choiceQuantity = prefix;
                    StatusText.Text = TranslationSource.Instance.Format("Purchasing_MatchesFound", matches.Count);
                    ProductChoices.Open(matches);
                    return;
                }
                if (matches.Count == 1)
                {
                    productId = matches[0].Id;
                    barcode = null;
                }
                else if (!looksLikeBarcode && !scanned)
                {
                    StatusText.Text = TranslationSource.Instance["Purchasing_NoProduct"];
                    return;
                }
                else
                {
                    IServiceProvider services = ((App)Application.Current).Services;
                    var dialog = new UnknownBarcodeWindow(_catalog,
                        services.GetRequiredService<InventoryService>(), code,
                        services.GetRequiredService<IClock>().BusinessDate)
                    { Owner = Window.GetWindow(this) };
                    if (dialog.ShowDialog() != true || dialog.ResolvedProductId is null) return;
                    if (!dialog.CanAddToBill)
                    {
                        StatusText.Text = TranslationSource.Instance["Pos_ProductSavedNoStock"];
                        return;
                    }
                    productId = dialog.ResolvedProductId.Value;
                }
                quantity = prefix;
                lotId = null;
            }
            else
            {
                productId = result.ProductId ?? throw new DomainException(ErrorCode.BarcodeNotFound, code);
                lotId = result.LotId;
                quantity = result.Kind == ScanResultKind.Price
                    ? prefix
                    : new Quantity((result.Quantity ?? Quantity.One).Value * prefix.Value);
            }
            _saleId = result.Kind == ScanResultKind.Price && result.Price is { } totalPrice
                ? await _sales.AddPriceLabelAsync(_saleId, productId, totalPrice * prefix, code)
                : await _sales.AddProductAsync(_saleId, productId, quantity, lotId, barcode);
            _lastScannedProductId = productId;
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    // The quantity typed before * in the search applies to the product picked from the list.
    private async void ProductChoices_OnPicked(object? sender, ProductListItem product) =>
        await EnqueueBillActionAsync(async () =>
        {
            try
            {
                _saleId = await _sales.AddProductAsync(_saleId, product.Id, _choiceQuantity);
                _lastScannedProductId = product.Id;
                await RefreshAsync();
            }
            catch (Exception exception) { StatusText.Text = Message(exception); }
            finally { ScanBox.Focus(); }
        });

    private void ProductChoices_OnClosed(object? sender, EventArgs e) => ScanBox.Focus();

    private async Task RefreshAsync()
    {
        using var timing = UiRefreshProbe.Track("billing", this);
        int version = ++_refreshVersion;
        long? saleId = _saleId;
        if (_saleId is null)
        {
            BillGrid.ItemsSource = null;
            GrossText.Text = DiscountText.Text = TaxText.Text = TotalText.Text = RoundOffText.Text = "₹0.000";
            SavingsText.Text = "";
            ItemCountText.Text = "0";
            CustomerText.Text = TranslationSource.Instance["Sales_WalkIn"];
            LastScanText.Text = "";
            StatusText.Text = TranslationSource.Instance["Sales_ReadyToScan"];
            UpdateLineActions();
            return;
        }
        try
        {
            SaleDetails sale = await _sales.GetAsync(saleId!.Value);
            if (version != _refreshVersion || _saleId != saleId) return;
            long? selectedLineId = (BillGrid.SelectedItem as SaleLine)?.Id;
            timing.Bind(() => BillGrid.ItemsSource = sale.Lines);
            BillGrid.SelectedItem = sale.Lines.FirstOrDefault(line => line.Id == selectedLineId) ??
                (sale.Lines.Count > 0 ? sale.Lines[0] : null);
            GrossText.Text = $"₹{sale.Totals.Gross.Value:N3}";
            DiscountText.Text = $"₹{sale.Totals.Discount.Value:N3}";
            TaxText.Text = $"₹{sale.Totals.Tax.Value:N3}";
            RoundOffText.Text = $"₹{sale.Totals.RoundOff.Value:N3}";
            TotalText.Text = $"₹{sale.Totals.GrandTotal.Value:N3}";
            SavingsText.Text = sale.Totals.MrpSavings > Money.Zero
                ? TranslationSource.Instance.Format("Sales_Savings", sale.Totals.MrpSavings.Value.ToString("N3", CultureInfo.GetCultureInfo("en-IN")))
                : "";
            ItemCountText.Text = sale.Lines.Count.ToString(CultureInfo.InvariantCulture);
            CustomerText.Text = sale.CustomerName ?? TranslationSource.Instance["Sales_WalkIn"];
            SaleLine? last = sale.Lines.LastOrDefault(line => line.ProductId == _lastScannedProductId);
            LastScanText.Text = last is null ? "" : TranslationSource.Instance.Format("Sales_LastScan",
                last.ProductName, last.StockOnHand.Value.ToString("0.###", CultureInfo.GetCultureInfo("en-IN")),
                last.Mrp?.Value.ToString("N3", CultureInfo.GetCultureInfo("en-IN")) ?? "—");
            StatusText.Text = TranslationSource.Instance[$"Sales_Status_{sale.Status}"];
            UpdateLineActions();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private Task PayAsync(PaymentMode mode) => EnqueueBillActionAsync(() => PayCoreAsync(mode));

    private async Task PayCoreAsync(PaymentMode mode)
    {
        if (_saleId is null || _isCompleting) return;
        try
        {
            SaleDetails sale = await _sales.GetAsync(_saleId.Value);
            var dialog = new SplitPaymentWindow(sale.Totals.GrandTotal, mode) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            await CompleteAsync(dialog.Payments);
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    private async Task CompleteAsync(IReadOnlyList<PaymentInput> payments)
    {
        if (_saleId is null || _isCompleting) return;
        _isCompleting = true;
        try
        {
            CompletedSale completed = await _sales.CompleteAsync(new(_saleId.Value,
                payments, _completionOperationId));
            _lastCompletedSaleId = completed.SaleId;
            string? printError = null;
            try
            {
                ReceiptData receipt = await _sales.GetReceiptAsync(completed.SaleId);
                await _printer.QueueReceiptAsync(receipt);
            }
            catch (Exception exception) { printError = UserError.Message(exception); }
            if (payments.Any(static payment => payment.Mode == PaymentMode.Cash))
            {
                try { await _printer.QueueDrawerAsync(); }
                catch (Exception exception)
                {
                    // Each message is a sentence; one that the receipt already gave (no printer chosen) is not repeated.
                    string drawerError = UserError.Message(exception);
                    printError = printError is null || printError == drawerError ? drawerError : $"{printError} {drawerError}";
                }
            }
            StartNew();
            await RefreshAsync();
            await RefreshPrinterAsync();
            StatusText.Text = printError is null
                ? TranslationSource.Instance.Format("Sales_Completed", completed.InvoiceNumber)
                : TranslationSource.Instance.Format("Sales_PrintFailed", completed.InvoiceNumber, printError);
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { _isCompleting = false; ScanBox.Focus(); }
    }

    private async void Cash_OnClick(object sender, RoutedEventArgs e) => await PayAsync(PaymentMode.Cash);
    private async void Upi_OnClick(object sender, RoutedEventArgs e) => await PayAsync(PaymentMode.Upi);
    private async void Split_OnClick(object sender, RoutedEventArgs e) =>
        await EnqueueBillActionAsync(SplitCoreAsync);

    private async Task SplitCoreAsync()
    {
        if (_saleId is null || _isCompleting) return;
        try
        {
            SaleDetails sale = await _sales.GetAsync(_saleId.Value);
            var dialog = new SplitPaymentWindow(sale.Totals.GrandTotal) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true) await CompleteAsync(dialog.Payments);
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    private async void Hold_OnClick(object sender, RoutedEventArgs e) =>
        await EnqueueBillActionAsync(HoldCoreAsync);

    private async Task HoldCoreAsync()
    {
        if (_saleId is null) return;
        var dialog = new HoldBillWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) { ScanBox.Focus(); return; }
        try { await _sales.HoldAsync(_saleId.Value, dialog.Label); StartNew(); await RefreshAsync(); await RefreshHeldChipsAsync(); }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    private async void Resume_OnClick(object sender, RoutedEventArgs e) =>
        await EnqueueBillActionAsync(ResumeCoreAsync);

    private async Task ResumeCoreAsync()
    {
        try { await OpenSavedBillsAsync(includeDrafts: false); }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    private async Task OpenSavedBillsAsync(bool includeDrafts)
    {
        var picker = new SavedBillsWindow(_sales, includeDrafts, _saleId) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.SelectedBill is not { } selected) return;
        await ResumeSelectedAsync(selected, selected.IsHeld);
    }

    private async Task ResumeSelectedAsync(HeldSale selected, bool isHeld)
    {
        if (isHeld)
        {
            HeldBillReview review = await _sales.GetHeldBillReviewAsync(selected.Id);
            IClock clock = ((App)Application.Current).Services.GetRequiredService<IClock>();
            var confirmation = new HeldBillReviewWindow(review, clock) { Owner = Window.GetWindow(this) };
            if (confirmation.ShowDialog() != true) return;
            await _sales.ResumeReviewedAsync(review);
        }
        _saleId = selected.Id;
        _completionOperationId = OperationGuard.NewId();
        await RefreshAsync();
        await RefreshHeldChipsAsync();
    }

    private async Task RefreshHeldChipsAsync()
    {
        IReadOnlyList<HeldSale> held = await _sales.GetRecentHeldAsync();
        HeldChips.ItemsSource = held.Select(item => new HeldChipItem(item,
            string.IsNullOrWhiteSpace(item.Label)
                ? TranslationSource.Instance.Format("Sales_HeldChip", item.Id)
                : item.Label)).ToArray();
    }

    private async void HeldChip_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not HeldChipItem item) return;
        try { await EnqueueBillActionAsync(() => ResumeSelectedAsync(item.Bill, true)); }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    private async Task RefreshPrinterAsync()
    {
        try
        {
            if (_printer.HasFailedJobs)
            {
                PrinterStatusText.Text = TranslationSource.Instance["Sales_PrinterJobFailed"];
                PrinterStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ExpiryExpiredBrush");
                return;
            }
            bool ready = await _printer.IsReadyAsync().WaitAsync(TimeSpan.FromSeconds(3));
            PrinterStatusText.Text = TranslationSource.Instance[ready ? "Sales_PrinterReady" : "Sales_PrinterUnavailable"];
            PrinterStatusText.Foreground = ready
                ? (System.Windows.Media.Brush)FindResource("ExpiryReadyBrush")
                : (System.Windows.Media.Brush)FindResource("ExpiryExpiredBrush");
        }
        catch (Exception)
        {
            PrinterStatusText.Text = TranslationSource.Instance["Sales_PrinterUnavailable"];
        }
    }

    private sealed record HeldChipItem(HeldSale Bill, string Label);

    private async void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null || BillGrid.SelectedItem is not SaleLine line) return;
        try { await _sales.SetLineQuantityAsync(_saleId.Value, line.Id, Quantity.Zero); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private void BillGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLineActions();

    private void UpdateLineActions()
    {
        if (QuantityButton is null) return;
        bool selected = BillGrid.SelectedItem is SaleLine;
        QuantityButton.IsEnabled = selected;
        LotButton.IsEnabled = selected;
        PriceButton.IsEnabled = selected;
        LineDiscountButton.IsEnabled = selected;
        DeleteButton.IsEnabled = selected;
    }

    private async void Quantity_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null || BillGrid.SelectedItem is not SaleLine line) return;
        SaleDetails sale;
        try { sale = await _sales.GetAsync(_saleId.Value); }
        catch (Exception exception) { StatusText.Text = Message(exception); return; }
        Quantity total = new(sale.Lines.Where(item => item.ProductId == line.ProductId)
            .Sum(static item => item.Quantity.Value));
        var dialog = new QuantityWindow(total, allowZero: true) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try { await _sales.SetProductQuantityAsync(_saleId.Value, line.ProductId, dialog.Quantity); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    private async void Lot_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null || BillGrid.SelectedItem is not SaleLine line) return;
        try
        {
            var picker = new LotPickerWindow(_inventory, line.ProductId, line.LotId)
            { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() != true || picker.SelectedLot is not { } selected) return;
            await _sales.ChangeLineLotAsync(_saleId.Value, line.Id, selected.Id);
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    private async void LineDiscount_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null || BillGrid.SelectedItem is not SaleLine line) return;
        var dialog = new DiscountWindow(line.UnitPrice * line.Quantity, line.LineDiscount)
        { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _sales.SetLineDiscountAsync(_saleId.Value, line.Id, dialog.Discount);
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private async void Price_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null || BillGrid.SelectedItem is not SaleLine line) return;
        QuantityWindow dialog = QuantityWindow.ForPrice(line.UnitPrice);
        dialog.Owner = Window.GetWindow(this);
        if (dialog.ShowDialog() != true) { ScanBox.Focus(); return; }
        try
        {
            await _sales.SetLinePriceAsync(_saleId.Value, line.Id, new Money(dialog.Quantity.Value));
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    private async void BillDiscount_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null) return;
        SaleDetails sale;
        try { sale = await _sales.GetAsync(_saleId.Value); }
        catch (Exception exception) { StatusText.Text = Message(exception); return; }
        Money baseAmount = sale.Lines.Aggregate(Money.Zero, static (sum, line) =>
            sum + line.UnitPrice * line.Quantity - line.LineDiscount);
        var dialog = new DiscountWindow(baseAmount, sale.BillDiscount)
        { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _sales.SetBillDiscountAsync(_saleId.Value, dialog.Discount);
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private async void Customer_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            long? currentId = _saleId is { } id ? (await _sales.GetAsync(id)).CustomerId : null;
            var dialog = new CustomerWindow(_sales, currentId) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            if (_saleId is null) _saleId = await _sales.CreateDraftAsync();
            await _sales.SetCustomerAsync(_saleId.Value, dialog.SelectedCustomerId);
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    private async void New_OnClick(object sender, RoutedEventArgs e) => await NewBillAsync();
    private Task NewBillAsync() => EnqueueBillActionAsync(async () => { StartNew(); await RefreshAsync(); });
    private void StartNew() { ++_refreshVersion; _saleId = null; _lastScannedProductId = null; _completionOperationId = OperationGuard.NewId(); ScanBox.Focus(); }

    private async void OnShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control &&
            (_printer.HasFailedJobs || _lastCompletedSaleId is not null))
        {
            try
            {
                SavedPrintJob? review = _printer.OldestReviewJob;
                if (review is not null &&
                    MessageBox.Show(TranslationSource.Instance.Format("Sales_PrintReviewPrompt", review.Document, review.Printer),
                        TranslationSource.Instance["App_Title"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    e.Handled = true;
                    return;
                }
                if (!await _printer.RetryFailedAsync(review?.Id) && review is null &&
                    _lastCompletedSaleId is { } lastSaleId)
                    await _printer.PrintAsync(await _sales.GetReceiptAsync(lastSaleId, duplicate: true));
                await RefreshPrinterAsync();
            }
            catch (Exception exception) { StatusText.Text = Message(exception); }
            e.Handled = true;
        }
        else if (e.Key == Key.F2) { await NewBillAsync(); e.Handled = true; }
        else if (e.Key == Key.F3) { ScanBox.Focus(); e.Handled = true; }
        else if (e.Key == Key.F4) { Quantity_OnClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.F5) { LineDiscount_OnClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.F6) { BillDiscount_OnClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.F7) { Hold_OnClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.F8) { Resume_OnClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.F9) { await PayAsync(PaymentMode.Cash); e.Handled = true; }
        else if (e.Key == Key.F10) { await PayAsync(PaymentMode.Upi); e.Handled = true; }
        else if (e.Key == Key.F11) { Split_OnClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.F12) { await PayAsync(PaymentMode.Cash); e.Handled = true; }
        else if (e.Key == Key.U && Keyboard.Modifiers == ModifierKeys.Control) { Customer_OnClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { Lot_OnClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { Delete_OnClick(sender, e); e.Handled = true; }
    }

    private static string Message(Exception exception) => UserError.Message(exception);

    public void Dispose()
    {
        _detector?.Dispose();
        _detector = null;
        GC.SuppressFinalize(this);
    }
}
