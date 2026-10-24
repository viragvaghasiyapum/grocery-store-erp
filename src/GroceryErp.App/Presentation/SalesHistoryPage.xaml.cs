using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Sales;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public partial class SalesHistoryPage : Page
{
    private const string StateKey = nameof(SalesHistoryPage);
    private readonly FitPageSize _fit;
    private int _pageLimit = 30;
    private int PageSize => _pageLimit;
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-IN");
    private readonly SalesService _sales;
    private readonly ReceiptPrinter _printer;
    private readonly IClock _clock;
    private readonly Stack<(DateOnly? Date, long? Id)> _previous = new();
    private DateOnly? _beforeDate;
    private long? _beforeId;
    private SaleDetails? _selectedDetails;
    private SaleHistoryActivity? _selectedActivity;
    private readonly int[] _detailPages = new int[3];
    private const int DetailPageSize = 50;
    private bool _loadingHistory;
    private bool _pendingReset;
    private CancellationTokenSource? _exportCancellation;

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
        RestoreState();
        _ = new FilterRow(FilterBar, ProductBox, MoreFiltersToggle, MoreFiltersPanel)
            .Fold(FromLabel, FromPicker, ToLabel, ToPicker).Fold(StatusBox);
        _fit = new FitPageSize(HistoryGrid);
        _fit.RowsChanged += async (_, _) => await RefreshAsync(reset: false);
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
            InvoiceBox.Focus();
            args.Handled = true;
        };
        if (loadOnShow) Loaded += async (_, _) => { await RefreshAsync(); };
        Loaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= OnLanguageChanged;
            TranslationSource.Instance.PropertyChanged += OnLanguageChanged;
        };
        Unloaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= OnLanguageChanged;
            _exportCancellation?.Cancel();
            CloseDetails();
            PageStateStore.Current?.Set(StateKey, new State(FromPicker.SelectedDate, ToPicker.SelectedDate,
                InvoiceBox.Text ?? "", ProductBox.Text ?? "", StatusBox.SelectedIndex, CustomerBox.Text ?? "",
                PaymentBox.SelectedIndex, MinimumBox.Text, MaximumBox.Text, MoreFiltersToggle.IsChecked == true));
        };
        UpdateActions();
    }

    private sealed record State(DateTime? From, DateTime? To, string Invoice, string Product, int Status, string Customer,
        int Payment, string Minimum, string Maximum, bool MoreFilters);

    private void RestoreState()
    {
        if (PageStateStore.Current?.Get<State>(StateKey) is not { } state) return;
        FromPicker.SelectedDate = state.From;
        ToPicker.SelectedDate = state.To;
        InvoiceBox.Text = state.Invoice;
        ProductBox.Text = state.Product;
        StatusBox.SelectedIndex = state.Status;
        CustomerBox.Text = state.Customer;
        PaymentBox.SelectedIndex = state.Payment;
        MinimumBox.Text = state.Minimum;
        MaximumBox.Text = state.Maximum;
        MoreFiltersToggle.IsChecked = state.MoreFilters;
        MoreFiltersPanel.Visibility = state.MoreFilters ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoreFilters_OnClick(object sender, RoutedEventArgs e) =>
        MoreFiltersPanel.Visibility = MoreFiltersToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]")
        {
            HistoryGrid.Items.Refresh();
            RefreshDetailHeading();
            RenderDetailPage();
        }
    }

    private async Task<bool> RefreshAsync(bool reset = true, DateOnly? requestedDate = null,
        long? requestedId = null, bool forward = false, bool backward = false)
    {
        using var timing = UiRefreshProbe.Track("sales-history", this);
        if (_loadingHistory)
        {
            if (reset) _pendingReset = true;
            return false;
        }
        _loadingHistory = true;
        PreviousButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        try
        {
            DateOnly? pageDate = reset ? null : forward || backward ? requestedDate : _beforeDate;
            long? pageId = reset ? null : forward || backward ? requestedId : _beforeId;
            _pageLimit = _fit.Take();
            SaleHistoryFilter filter = CreateFilter(pageDate, pageId, PageSize);
            IReadOnlyList<SaleHistoryItem> items = await _sales.GetHistoryAsync(filter);
            // Commit navigation only after the requested page succeeds. Rapid clicks
            // cannot mutate a cursor while a previous request is in flight.
            if (reset) _previous.Clear();
            else if (forward) _previous.Push((_beforeDate, _beforeId));
            else if (backward) _previous.Pop();
            _beforeDate = pageDate;
            _beforeId = pageId;
            timing.Bind(() => HistoryGrid.ItemsSource = items);
            CloseDetails();
            StatusText.Text = TranslationSource.Instance.Format("Sales_ResultCount", items.Count);
            PreviousButton.IsEnabled = _previous.Count > 0;
            NextButton.IsEnabled = items.Count == PageSize;
            PageText.Text = _previous.Count > 0 || items.Count == PageSize
                ? TranslationSource.Instance.Format("Pager_Page", _previous.Count + 1) : string.Empty;
            UpdateActions();
            return true;
        }
        catch (Exception exception)
        {
            StatusText.Text = UserError.Message(exception);
            PreviousButton.IsEnabled = _previous.Count > 0;
            NextButton.IsEnabled = HistoryGrid.ItemsSource is IReadOnlyList<SaleHistoryItem> rows && rows.Count == PageSize;
            return false;
        }
        finally
        {
            _loadingHistory = false;
            if (_pendingReset)
            {
                _pendingReset = false;
                await RefreshAsync();
            }
        }
    }

    private SaleHistoryFilter CreateFilter(DateOnly? beforeDate, long? beforeId, int limit)
    {
        Money? minimum = ParseOptionalMoney(MinimumBox.Text);
        Money? maximum = ParseOptionalMoney(MaximumBox.Text);
        if (minimum > maximum) throw new FormatException(TranslationSource.Instance["Sales_InvalidRange"]);
        SaleStatus? status = ((ComboBoxItem)StatusBox.SelectedItem).Tag?.ToString() switch
        {
            "Completed" => SaleStatus.Completed,
            "PartiallyRefunded" => SaleStatus.PartiallyRefunded,
            "Refunded" => SaleStatus.Refunded,
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
                beforeDate, beforeId, limit,
                InputNormalizer.Normalize(ProductBox.Text).Trim(),
                InputNormalizer.Normalize(CustomerBox.Text).Trim());
    }

    private static Money? ParseOptionalMoney(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!decimal.TryParse(InputNormalizer.Normalize(text), NumberStyles.Number, Digits, out decimal value) || value < 0)
            throw new FormatException(TranslationSource.Instance["Sales_InvalidRange"]);
        return new Money(value);
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Filter_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RefreshAsync();
    }

    private async void ClearFilters_OnClick(object sender, RoutedEventArgs e)
    {
        FromPicker.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        ToPicker.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        foreach (TextBox box in new[] { InvoiceBox, ProductBox, CustomerBox, MinimumBox, MaximumBox }) box.Clear();
        StatusBox.SelectedIndex = 0;
        PaymentBox.SelectedIndex = 0;
        await RefreshAsync();
    }

    private static string BillContext(SaleHistoryItem sale) => TranslationSource.Instance.Format("Sales_ReturnContext",
        sale.InvoiceNumber ?? sale.Id.ToString(Digits), sale.SaleDate.ToString("dd-MM-yyyy", Digits),
        sale.GrandTotal.Value.ToString("N2", Digits));

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingHistory) return;
        if (HistoryGrid.ItemsSource is not IReadOnlyList<SaleHistoryItem> items || items.Count != PageSize) return;
        await RefreshAsync(reset: false, requestedDate: items[^1].SaleDate,
            requestedId: items[^1].Id, forward: true);
    }

    private async void Previous_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingHistory || _previous.Count == 0) return;
        (DateOnly? date, long? id) = _previous.Peek();
        await RefreshAsync(reset: false, requestedDate: date, requestedId: id, backward: true);
    }

    private void HistoryGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActions();
        CloseDetails();
    }

    private async void ViewBillItems_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SaleHistoryItem item }) return;
        HistoryGrid.SelectedItem = item;
        try
        {
            SaleDetails sale = await _sales.GetAsync(item.Id);
            SaleHistoryActivity activity = await _sales.GetHistoryActivityAsync(item.Id);
            if (HistoryGrid.SelectedItem is not SaleHistoryItem selected || selected.Id != item.Id) return;
            _selectedDetails = sale;
            _selectedActivity = activity;
            Array.Clear(_detailPages);
            DetailTabs.SelectedIndex = 0;
            RenderDetailPage();
            RefreshDetailHeading();
            SideOverlayMotion.Show(DetailOverlay, DetailPanel, DetailBackdrop);
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void CloseDetails()
    {
        DetailOverlay.Visibility = Visibility.Collapsed;
        _selectedDetails = null;
        _selectedActivity = null;
        DetailGrid.ItemsSource = null;
        ReturnsGrid.ItemsSource = null;
        MovementsGrid.ItemsSource = null;
        DetailHeading.Text = TranslationSource.Instance["Sales_SelectHistory"];
    }

    private void CloseDetails_OnClick(object sender, RoutedEventArgs e) => CloseDetails();

    private void DetailBackdrop_OnMouseDown(object sender, MouseButtonEventArgs e) => CloseDetails();

    private void DetailTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == DetailTabs) RenderDetailPage();
    }

    private void PreviousDetail_OnClick(object sender, RoutedEventArgs e)
    {
        int tab = DetailTabs.SelectedIndex;
        if (tab < 0 || _detailPages[tab] == 0) return;
        _detailPages[tab]--;
        RenderDetailPage();
    }

    private void NextDetail_OnClick(object sender, RoutedEventArgs e)
    {
        int tab = DetailTabs.SelectedIndex;
        if (tab < 0 || (_detailPages[tab] + 1) * DetailPageSize >= DetailCount(tab)) return;
        _detailPages[tab]++;
        RenderDetailPage();
    }

    private int DetailCount(int tab) => tab switch
    {
        0 => _selectedDetails?.Lines.Count ?? 0,
        1 => _selectedActivity?.Returns.Count ?? 0,
        2 => _selectedActivity?.Movements.Count ?? 0,
        _ => 0,
    };

    private void RenderDetailPage()
    {
        if (DetailTabs is null || DetailPageText is null) return;
        int tab = DetailTabs.SelectedIndex;
        if (tab < 0 || tab >= _detailPages.Length) return;
        int count = DetailCount(tab);
        int start = _detailPages[tab] * DetailPageSize;
        if (start >= count && count > 0) start = (_detailPages[tab] = (count - 1) / DetailPageSize) * DetailPageSize;
        switch (tab)
        {
            case 0: DetailGrid.ItemsSource = _selectedDetails?.Lines.Skip(start).Take(DetailPageSize).ToArray(); break;
            case 1: ReturnsGrid.ItemsSource = _selectedActivity?.Returns.Skip(start).Take(DetailPageSize).ToArray(); break;
            case 2: MovementsGrid.ItemsSource = _selectedActivity?.Movements.Skip(start).Take(DetailPageSize).ToArray(); break;
        }
        DetailPageText.Text = count == 0 ? "0" : $"{start + 1}–{Math.Min(start + DetailPageSize, count)} / {count}";
        PreviousDetailButton.IsEnabled = start > 0;
        NextDetailButton.IsEnabled = start + DetailPageSize < count;
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
        SaleStatus? status = (HistoryGrid.SelectedItem as SaleHistoryItem)?.Status;
        ReprintButton.IsEnabled = status is SaleStatus.Completed or SaleStatus.PartiallyRefunded or SaleStatus.Refunded or SaleStatus.Void;
        VoidButton.IsEnabled = status == SaleStatus.Completed;
        ReturnButton.IsEnabled = status is SaleStatus.Completed or SaleStatus.PartiallyRefunded;
    }

    private async void Reprint_OnClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SaleHistoryItem sale) return;
        try
        {
            await _printer.PrintAsync(await _sales.GetReceiptAsync(sale.Id, duplicate: true));
            StatusText.Text = TranslationSource.Instance.Format("Sales_Reprinted", sale.InvoiceNumber ?? "");
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void Void_OnClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SaleHistoryItem { Status: SaleStatus.Completed } sale) return;
        var dialog = new SaleActionWindow(false, context: BillContext(sale)) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try { await _sales.VoidAsync(sale.Id, dialog.Reason, OperationGuard.NewId()); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void Return_OnClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SaleHistoryItem
            { Status: SaleStatus.Completed or SaleStatus.PartiallyRefunded } item) return;
        try
        {
            IReadOnlyList<ReturnableSaleLine> lines = await _sales.GetReturnableLinesAsync(item.Id);
            if (lines.Count == 0)
            {
                StatusText.Text = TranslationSource.Instance["Sales_NothingToReturn"];
                return;
            }
            var dialog = new SaleActionWindow(true, lines, BillContext(item)) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            var reviewedLines = new List<ReturnLineInput>(dialog.ReturnLines.Count);
            ReturnRecreationOptions? recreationOptions = null;
            foreach (ReturnLineInput selected in dialog.ReturnLines)
            {
                // Only resold units can need a recreated product; damage keeps the sale snapshot.
                ReturnableSaleLine source = lines.Single(line => line.Line.Id == selected.SaleLineId);
                if (source.OriginalLotAvailable || !selected.Resellable)
                {
                    reviewedLines.Add(selected);
                    continue;
                }
                ReturnRecreationDraft draft = await _sales.GetReturnRecreationDraftAsync(selected.SaleLineId);
                if (!draft.ProductMissing || draft.MissingFields.Count == 0)
                {
                    reviewedLines.Add(selected);
                    continue;
                }
                recreationOptions ??= await _sales.GetReturnRecreationOptionsAsync();
                var review = new ReturnRecreationWindow(draft, recreationOptions)
                { Owner = Window.GetWindow(this) };
                if (review.ShowDialog() != true) return;
                reviewedLines.Add(selected with { Recreation = review.Result });
            }
            CompletedReturn returned = await _sales.ReturnAsync(new(item.Id, _clock.BusinessDate, dialog.Reason, dialog.SelectedRefundMode,
                reviewedLines, OperationGuard.NewId()));
            await RefreshAsync();
            try { await _printer.PrintAsync(await _sales.GetReturnReceiptAsync(returned.ReturnId)); }
            catch (Exception exception)
            {
                StatusText.Text = TranslationSource.Instance.Format("Sales_ReturnPrintFailed", returned.ReturnNumber, UserError.Message(exception));
            }
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
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
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null) return;
        var picker = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "sales-history.csv" };
        if (picker.ShowDialog() != true) return;
        string temporary = picker.FileName + "." + Guid.NewGuid().ToString("N") + ".partial";
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        ExportButton.IsEnabled = false;
        CancelExportButton.IsEnabled = true;
        ExportProgress.Visibility = Visibility.Visible;
        try
        {
            SaleHistoryFilter filter = CreateFilter(null, null, 500);
            string[] headings = [TranslationSource.Instance["Sales_Date"], TranslationSource.Instance["Sales_Invoice"],
                TranslationSource.Instance["Purchasing_Status"], TranslationSource.Instance["Sales_Items"],
                TranslationSource.Instance["Purchasing_Mode"], TranslationSource.Instance["Purchasing_Total"]];
            var statusNames = Enum.GetValues<SaleStatus>().ToDictionary(status => status,
                status => TranslationSource.Instance[$"Sales_Status_{status}"]);
            long exported = 0;
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 64 * 1024))
            {
                // The file states the filters it was made with and ends with totals that reconcile to its rows.
                await writer.WriteLineAsync(Csv(TranslationSource.Instance["Sales_ExportFilters"]) + "," + Csv(FilterSummary()));
                await writer.WriteLineAsync(string.Join(',', headings.Select(Csv)));
                decimal grandTotal = 0;
                DateOnly? beforeDate = null;
                long? beforeId = null;
                while (true)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    IReadOnlyList<SaleHistoryItem> page = await _sales.GetHistoryAsync(
                        filter with { BeforeDate = beforeDate, BeforeId = beforeId }, cancellation.Token);
                    var chunk = new StringBuilder(page.Count * 96);
                    foreach (SaleHistoryItem item in page)
                    {
                        chunk.Append(Csv(item.SaleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Append(',')
                            .Append(Csv(item.InvoiceNumber)).Append(',')
                            .Append(Csv(statusNames[item.Status])).Append(',')
                            .Append(item.LineCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                            .Append(Csv(item.PaymentModes)).Append(',')
                            .Append(item.GrandTotal.Value.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine();
                        grandTotal += item.GrandTotal.Value;
                    }
                    await writer.WriteAsync(chunk.ToString().AsMemory(), cancellation.Token);
                    exported += page.Count;
                    if (exported % 5000 == 0) StatusText.Text = TranslationSource.Instance.Format("Sales_Exported", exported);
                    if (page.Count < 500) break;
                    beforeDate = page[^1].SaleDate;
                    beforeId = page[^1].Id;
                }
                await writer.WriteLineAsync(Csv(TranslationSource.Instance["Sales_ExportTotal"]) + "," +
                    Csv(exported.ToString(CultureInfo.InvariantCulture)) + ",,,," +
                    grandTotal.ToString("0.00", CultureInfo.InvariantCulture));
                await writer.FlushAsync(cancellation.Token);
                await file.FlushAsync(cancellation.Token);
            }
            File.Move(temporary, picker.FileName, overwrite: true);
            StatusText.Text = TranslationSource.Instance.Format("Sales_Exported", exported);
        }
        catch (OperationCanceledException) { StatusText.Text = TranslationSource.Instance["Sales_ExportCancelled"]; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _exportCancellation = null;
            ExportButton.IsEnabled = true;
            CancelExportButton.IsEnabled = false;
            ExportProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelExport_OnClick(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private static string Csv(string? text) => CsvFormat.Text(text);

    private string FilterSummary()
    {
        var parts = new List<string>
        {
            $"{TranslationSource.Instance["Sales_From"]} {FromPicker.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}",
            $"{TranslationSource.Instance["Sales_To"]} {ToPicker.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}",
        };
        foreach (TextBox box in new TextBox[] { InvoiceBox, ProductBox, CustomerBox, MinimumBox, MaximumBox })
            if (!string.IsNullOrWhiteSpace(box.Text))
                parts.Add($"{System.Windows.Automation.AutomationProperties.GetName(box)}: {box.Text.Trim()}");
        foreach (ComboBox box in new[] { StatusBox, PaymentBox })
            if (box.SelectedIndex > 0 && box.SelectedItem is ComboBoxItem { Content: string text }) parts.Add(text);
        return string.Join(" · ", parts);
    }
}
