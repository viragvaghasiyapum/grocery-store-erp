using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Reports;
using GroceryErp.Sales;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public partial class SalesHistoryPage : Page
{
    private const int PageSize = 50;
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-IN");
    private readonly SalesService _sales;
    private readonly ReceiptPrinter _printer;
    private readonly IClock _clock;
    private readonly Stack<(DateOnly? Date, long? Id)> _previous = new();
    private DateOnly? _beforeDate;
    private long? _beforeId;
    private SaleDetails? _selectedDetails;

    public SalesHistoryPage() : this(
        ((App)Application.Current).Services.GetRequiredService<SalesService>(),
        ((App)Application.Current).Services.GetRequiredService<ReceiptPrinter>(),
        ((App)Application.Current).Services.GetRequiredService<IClock>())
    { }

    public SalesHistoryPage(SalesService sales, ReceiptPrinter printer, IClock clock, bool loadOnShow = true)
    {
        InitializeComponent();
        _sales = sales;
        _printer = printer;
        _clock = clock;
        FromPicker.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        ToPicker.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
        Loaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= OnLanguageChanged;
            TranslationSource.Instance.PropertyChanged += OnLanguageChanged;
        };
        Unloaded += (_, _) => TranslationSource.Instance.PropertyChanged -= OnLanguageChanged;
        UpdateActions();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]")
        {
            HistoryGrid.Items.Refresh();
            RefreshDetailHeading();
        }
    }

    private async Task RefreshAsync(bool reset = true)
    {
        try
        {
            if (reset) { _previous.Clear(); _beforeDate = null; _beforeId = null; }
            SaleHistoryFilter filter = CreateFilter(_beforeDate, _beforeId, PageSize);
            IReadOnlyList<SaleHistoryItem> items = await _sales.GetHistoryAsync(filter);
            HistoryGrid.ItemsSource = items;
            StatusText.Text = TranslationSource.Instance.Format("Sales_ResultCount", items.Count);
            PreviousButton.IsEnabled = _previous.Count > 0;
            NextButton.IsEnabled = items.Count == PageSize;
            UpdateActions();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private SaleHistoryFilter CreateFilter(DateOnly? beforeDate, long? beforeId, int limit)
    {
        Money? minimum = ParseOptionalMoney(MinimumBox.Text);
        Money? maximum = ParseOptionalMoney(MaximumBox.Text);
        if (minimum > maximum) throw new FormatException(TranslationSource.Instance["Sales_InvalidRange"]);
        SaleStatus? status = ((ComboBoxItem)StatusBox.SelectedItem).Tag?.ToString() switch
        {
            "Completed" => SaleStatus.Completed,
            "Void" => SaleStatus.Void,
            _ => null,
        };
        PaymentMode? payment = ((ComboBoxItem)PaymentBox.SelectedItem).Tag?.ToString() switch
        {
            "Cash" => PaymentMode.Cash,
            "Upi" => PaymentMode.Upi,
            "Card" => PaymentMode.Card,
            _ => null,
        };
        return new SaleHistoryFilter(
                FromPicker.SelectedDate is { } from ? DateOnly.FromDateTime(from) : null,
                ToPicker.SelectedDate is { } to ? DateOnly.FromDateTime(to) : null,
                status, payment, minimum, maximum, InputNormalizer.Normalize(InvoiceBox.Text).Trim(),
                beforeDate, beforeId, limit);
    }

    private static Money? ParseOptionalMoney(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!decimal.TryParse(InputNormalizer.Normalize(text), NumberStyles.Number, Digits, out decimal value) || value < 0)
            throw new FormatException(TranslationSource.Instance["Sales_InvalidRange"]);
        return new Money(value);
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.ItemsSource is not IReadOnlyList<SaleHistoryItem> { Count: PageSize } items) return;
        _previous.Push((_beforeDate, _beforeId));
        _beforeDate = items[^1].SaleDate;
        _beforeId = items[^1].Id;
        await RefreshAsync(reset: false);
    }

    private async void Previous_OnClick(object sender, RoutedEventArgs e)
    {
        if (_previous.Count == 0) return;
        (_beforeDate, _beforeId) = _previous.Pop();
        await RefreshAsync(reset: false);
    }

    private async void HistoryGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActions();
        if (HistoryGrid.SelectedItem is not SaleHistoryItem item)
        {
            _selectedDetails = null;
            DetailGrid.ItemsSource = null;
            ReturnsGrid.ItemsSource = null;
            MovementsGrid.ItemsSource = null;
            DetailHeading.Text = TranslationSource.Instance["Sales_SelectHistory"];
            return;
        }
        try
        {
            SaleDetails sale = await _sales.GetAsync(item.Id);
            SaleHistoryActivity activity = await _sales.GetHistoryActivityAsync(item.Id);
            if (HistoryGrid.SelectedItem is not SaleHistoryItem selected || selected.Id != item.Id) return;
            _selectedDetails = sale;
            DetailGrid.ItemsSource = sale.Lines;
            ReturnsGrid.ItemsSource = activity.Returns;
            MovementsGrid.ItemsSource = activity.Movements;
            RefreshDetailHeading();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private void RefreshDetailHeading()
    {
        if (_selectedDetails is not { } sale) return;
        string modes = string.Join(", ", sale.Payments.Select(payment =>
        {
            string key = payment.Mode switch
            {
                PaymentMode.Cash => "Receipt_RefundModeCash",
                PaymentMode.Upi => "Receipt_RefundModeUpi",
                PaymentMode.Card => "Receipt_RefundModeCard",
                _ => "Receipt_RefundModeOther",
            };
            return $"{TranslationSource.Instance[key]} ₹{payment.Amount.Value.ToString("N3", Digits)}";
        }));
        DetailHeading.Text = TranslationSource.Instance.Format("Sales_DetailHeading",
            sale.InvoiceNumber ?? sale.Id.ToString(Digits), modes);
    }

    private void UpdateActions()
    {
        if (ReprintButton is null) return;
        bool completed = HistoryGrid.SelectedItem is SaleHistoryItem { Status: SaleStatus.Completed };
        ReprintButton.IsEnabled = completed;
        VoidButton.IsEnabled = completed;
        ReturnButton.IsEnabled = completed;
    }

    private async void Reprint_OnClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SaleHistoryItem sale) return;
        try
        {
            await _printer.PrintAsync(await _sales.GetReceiptAsync(sale.Id, duplicate: true));
            StatusText.Text = TranslationSource.Instance.Format("Sales_Reprinted", sale.InvoiceNumber ?? "");
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void Void_OnClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SaleHistoryItem { Status: SaleStatus.Completed } sale) return;
        var dialog = new SaleActionWindow(false) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try { await _sales.VoidAsync(sale.Id, dialog.Reason, OperationGuard.NewId()); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void Return_OnClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SaleHistoryItem { Status: SaleStatus.Completed } item) return;
        try
        {
            IReadOnlyList<ReturnableSaleLine> lines = await _sales.GetReturnableLinesAsync(item.Id);
            if (lines.Count == 0)
            {
                StatusText.Text = TranslationSource.Instance["Sales_NothingToReturn"];
                return;
            }
            var dialog = new SaleActionWindow(true, lines) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            CompletedReturn returned = await _sales.ReturnAsync(new(item.Id, _clock.BusinessDate, dialog.Reason, dialog.SelectedRefundMode,
                dialog.ReturnLines, OperationGuard.NewId()));
            await RefreshAsync();
            try { await _printer.PrintAsync(await _sales.GetReturnReceiptAsync(returned.ReturnId)); }
            catch (Exception exception)
            {
                StatusText.Text = TranslationSource.Instance.Format("Sales_ReturnPrintFailed", returned.ReturnNumber, exception.Message);
            }
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void DayEnd_OnClick(object sender, RoutedEventArgs e)
    {
        DateOnly date = ToPicker.SelectedDate is { } selected
            ? DateOnly.FromDateTime(selected) : _clock.BusinessDate;
        try
        {
            DayEndSummary summary = await _sales.GetDayEndAsync(date);
            new DayEndWindow(summary, _printer) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = "sales-history.xlsx" };
        if (picker.ShowDialog() != true) return;
        try
        {
            var all = new List<SaleHistoryItem>();
            DateOnly? beforeDate = null;
            long? beforeId = null;
            while (true)
            {
                IReadOnlyList<SaleHistoryItem> page = await _sales.GetHistoryAsync(CreateFilter(beforeDate, beforeId, 500));
                all.AddRange(page);
                if (page.Count < 500) break;
                beforeDate = page[^1].SaleDate;
                beforeId = page[^1].Id;
            }
            var report = new TabularReport(TranslationSource.Instance["Sales_History_Title"], null,
                [new("Date", TranslationSource.Instance["Sales_Date"], ReportValueKind.Date),
                 new("Invoice", TranslationSource.Instance["Sales_Invoice"], ReportValueKind.Text),
                 new("Status", TranslationSource.Instance["Purchasing_Status"], ReportValueKind.Text),
                 new("Items", TranslationSource.Instance["Sales_Items"], ReportValueKind.Count),
                 new("Payment", TranslationSource.Instance["Purchasing_Mode"], ReportValueKind.Text),
                 new("Amount", TranslationSource.Instance["Purchasing_Total"], ReportValueKind.Money)],
                all.Select(item => new ReportRow(new Dictionary<string, object?>
                {
                    ["Date"] = item.SaleDate,
                    ["Invoice"] = item.InvoiceNumber,
                    ["Status"] = TranslationSource.Instance[$"Sales_Status_{item.Status}"],
                    ["Items"] = item.LineCount,
                    ["Payment"] = item.PaymentModes,
                    ["Amount"] = item.GrandTotal.Scaled,
                })).ToArray(), new Dictionary<string, decimal>());
            var output = ((App)Application.Current).Services.GetRequiredService<ReportOutputService>();
            await Task.Run(() => output.ExportExcel(report, picker.FileName));
            StatusText.Text = TranslationSource.Instance.Format("Sales_Exported", all.Count);
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }
}
