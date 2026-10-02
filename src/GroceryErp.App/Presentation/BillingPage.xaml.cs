using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
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
    private readonly ReceiptPrinter _printer;
    private long? _saleId;
    private string _completionOperationId = OperationGuard.NewId();
    private long? _lastCompletedSaleId;
    private ScanBurstDetector? _detector;

    public BillingPage()
    {
        InitializeComponent();
        IServiceProvider services = ((App)Application.Current).Services;
        _sales = services.GetRequiredService<SalesService>();
        _resolver = services.GetRequiredService<ScanResolver>();
        _catalog = services.GetRequiredService<CatalogService>();
        _printer = services.GetRequiredService<ReceiptPrinter>();
        Loaded += BillingPage_OnLoaded;
        Unloaded += (_, _) => { _detector?.Dispose(); _detector = null; };
        PreviewKeyDown += OnShortcut;
    }

    private async void BillingPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_detector is null && Window.GetWindow(this) is { } window)
        {
            ISettings settings = ((App)Application.Current).Services.GetRequiredService<ISettings>();
            _detector = new(settings.Current.ScannerMaxGapMs, settings.Current.ScannerMinLength);
            _detector.ScanReceived += async (_, code) => await AddTextAsync(code);
            _detector.Attach(window);
        }
        await RefreshAsync();
        ScanBox.Focus();
    }

    private async void ScanBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string value = ScanBox.Text;
        ScanBox.Clear();
        await AddTextAsync(value);
    }

    private async Task AddTextAsync(string text)
    {
        try
        {
            Quantity prefix = Quantity.One;
            string code = text.Trim();
            int separator = code.IndexOf('*', StringComparison.Ordinal);
            if (separator > 0 && decimal.TryParse(code[..separator], NumberStyles.Number,
                    CultureInfo.GetCultureInfo("en-IN"), out decimal parsed))
            {
                prefix = new Quantity(parsed);
                code = code[(separator + 1)..];
            }
            ScanResult result = await _resolver.ResolveAsync(code);
            long productId;
            Quantity quantity;
            long? lotId;
            if (result.Kind == ScanResultKind.NotFound)
            {
                IReadOnlyList<ProductListItem> matches = await _catalog.SearchAsync(new(Query: code, Limit: 2));
                bool looksLikeBarcode = code.Length >= 8 && code.All(char.IsAsciiDigit);
                if (matches.Count == 1 && !looksLikeBarcode)
                    productId = matches[0].Id;
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
                ? await _sales.AddPriceLabelAsync(_saleId, productId, totalPrice, code)
                : await _sales.AddProductAsync(_saleId, productId, quantity, lotId, code);
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
        finally { ScanBox.Focus(); }
    }

    private async Task RefreshAsync()
    {
        if (_saleId is null)
        {
            BillGrid.ItemsSource = null;
            GrossText.Text = DiscountText.Text = TaxText.Text = TotalText.Text = "₹0.000";
            return;
        }
        try
        {
            SaleDetails sale = await _sales.GetAsync(_saleId.Value);
            BillGrid.ItemsSource = sale.Lines;
            GrossText.Text = $"₹{sale.Totals.Gross.Value:N3}";
            DiscountText.Text = $"₹{sale.Totals.Discount.Value:N3}";
            TaxText.Text = $"₹{sale.Totals.Tax.Value:N3}";
            TotalText.Text = $"₹{sale.Totals.GrandTotal.Value:N3}";
            StatusText.Text = sale.Status.ToString();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private async Task PayAsync(PaymentMode mode)
    {
        if (_saleId is null) return;
        try
        {
            SaleDetails sale = await _sales.GetAsync(_saleId.Value);
            CompletedSale completed = await _sales.CompleteAsync(new(_saleId.Value,
                [new(mode, sale.Totals.GrandTotal)], _completionOperationId));
            _lastCompletedSaleId = completed.SaleId;
            try
            {
                ReceiptData receipt = await _sales.GetReceiptAsync(completed.SaleId);
                _printer.Print(receipt);
                if (mode == PaymentMode.Cash) _printer.OpenDrawer();
            }
            catch (Exception printError) { StatusText.Text = printError.Message; }
            StartNew();
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private async void Cash_OnClick(object sender, RoutedEventArgs e) => await PayAsync(PaymentMode.Cash);
    private async void Upi_OnClick(object sender, RoutedEventArgs e) => await PayAsync(PaymentMode.Upi);
    private async void Split_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null) return;
        SaleDetails sale = await _sales.GetAsync(_saleId.Value);
        var dialog = new SplitPaymentWindow(sale.Totals.GrandTotal) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            CompletedSale completed = await _sales.CompleteAsync(new(_saleId.Value, dialog.Payments, _completionOperationId));
            _lastCompletedSaleId = completed.SaleId;
            ReceiptData receipt = await _sales.GetReceiptAsync(completed.SaleId);
            try { _printer.Print(receipt); if (dialog.Payments.Any(static p => p.Mode == PaymentMode.Cash)) _printer.OpenDrawer(); }
            catch (Exception error) { StatusText.Text = error.Message; }
            StartNew();
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private async void Hold_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null) return;
        try { await _sales.HoldAsync(_saleId.Value); StartNew(); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private async void Resume_OnClick(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<HeldSale> held = await _sales.GetHeldAsync();
        if (held.Count == 0) return;
        try { _saleId = held[0].Id; await _sales.ResumeAsync(_saleId.Value); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private async void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null || BillGrid.SelectedItem is not SaleLine line) return;
        try { await _sales.SetLineQuantityAsync(_saleId.Value, line.Id, Quantity.Zero); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private async void Quantity_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null || BillGrid.SelectedItem is not SaleLine line) return;
        var dialog = new QuantityWindow(line.Quantity) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try { await _sales.SetLineQuantityAsync(_saleId.Value, line.Id, dialog.Quantity); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private async void LineDiscount_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null || BillGrid.SelectedItem is not SaleLine line) return;
        var dialog = new QuantityWindow(Quantity.Zero) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _sales.SetLineDiscountAsync(_saleId.Value, line.Id, new Money(dialog.Quantity.Value));
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private async void BillDiscount_OnClick(object sender, RoutedEventArgs e)
    {
        if (_saleId is null) return;
        var dialog = new QuantityWindow(Quantity.Zero) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _sales.SetBillDiscountAsync(_saleId.Value, new Money(dialog.Quantity.Value));
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = Message(exception); }
    }

    private void New_OnClick(object sender, RoutedEventArgs e) { StartNew(); _ = RefreshAsync(); }
    private void StartNew() { _saleId = null; _completionOperationId = OperationGuard.NewId(); ScanBox.Focus(); }

    private async void OnShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control && _lastCompletedSaleId is { } lastSaleId)
        {
            try { _printer.Print(await _sales.GetReceiptAsync(lastSaleId, duplicate: true)); }
            catch (Exception exception) { StatusText.Text = Message(exception); }
            e.Handled = true;
        }
        else if (e.Key == Key.F2) { StartNew(); await RefreshAsync(); e.Handled = true; }
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
        else if (e.Key == Key.Delete) { Delete_OnClick(sender, e); e.Handled = true; }
    }

    private static string Message(Exception exception) => exception is DomainException domain
        ? TranslationSource.Instance[$"Error_{domain.Code}"]
        : exception.Message;

    public void Dispose()
    {
        _detector?.Dispose();
        _detector = null;
        GC.SuppressFinalize(this);
    }
}
