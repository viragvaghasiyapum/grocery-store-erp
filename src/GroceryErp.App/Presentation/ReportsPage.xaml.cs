using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Xps.Packaging;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Localization;
using GroceryErp.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public partial class ReportsPage : Page
{
    private static readonly CultureInfo NumberCulture = CultureInfo.GetCultureInfo("en-IN");
    private static readonly string[] Weekdays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private readonly ReportService _reports;
    private readonly ReportOutputService _output;
    private readonly IClock _clock;
    private readonly DisplayFormatter _formatter;
    private readonly ReportNavigationState _navigation;
    private TabularReport? _currentReport;
    private PnlStatement? _currentPnl;
    private bool _suppressRefresh;
    private readonly bool _loadOnShow;
    private int _refreshVersion;
    private CancellationTokenSource? _refreshCancellation;
    private bool _expiryStatusChoices;
    private CancellationTokenSource? _exportCancellation;
    private bool _previewOpen;

    public ReportsPage() : this(
        ((App)Application.Current).Services.GetRequiredService<ReportService>(),
        ((App)Application.Current).Services.GetRequiredService<ReportOutputService>(),
        ((App)Application.Current).Services.GetRequiredService<IClock>(),
        ((App)Application.Current).Services.GetRequiredService<DisplayFormatter>(),
        ((App)Application.Current).Services.GetRequiredService<ReportNavigationState>())
    { }

    public ReportsPage(ReportService reports, ReportOutputService output, IClock clock,
        DisplayFormatter formatter, ReportNavigationState navigation, bool loadOnShow = true)
    {
        InitializeComponent();
        _loadOnShow = loadOnShow;
        _reports = reports;
        _output = output;
        _clock = clock;
        _formatter = formatter;
        _navigation = navigation;
        ReportPicker.ItemsSource = ReportChoices();
        ReportPicker.SelectedIndex = 0;
        PeriodPicker.ItemsSource = Enum.GetValues<PnlPeriodKind>().Select(kind => new PeriodChoice(kind)).ToList();
        PeriodPicker.SelectedIndex = 0;
        StatusPicker.ItemsSource = SalesStatusChoices();
        StatusPicker.SelectedIndex = 0;
        PaymentPicker.ItemsSource = PaymentChoices();
        PaymentPicker.SelectedIndex = 0;
        DateOnly today = _clock.BusinessDate;
        FromPicker.SelectedDate = new DateTime(today.Year, today.Month, 1);
        ToPicker.SelectedDate = today.ToDateTime(TimeOnly.MinValue);
        PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == Key.F5)
            {
                await RefreshAsync();
                args.Handled = true;
            }
            else if (args.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Export_OnClick(this, new RoutedEventArgs());
                args.Handled = true;
            }
            else if (args.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Preview_OnClick(this, new RoutedEventArgs());
                args.Handled = true;
            }
        };
        if (loadOnShow) Loaded += async (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged += OnTranslationChanged;
            if (_navigation.Consume() is { } request)
            {
                _suppressRefresh = true;
                ReportPicker.SelectedIndex = 1 + (int)request.Kind;
                FromPicker.SelectedDate = request.From.ToDateTime(TimeOnly.MinValue);
                ToPicker.SelectedDate = request.To.ToDateTime(TimeOnly.MinValue);
                PeriodPicker.SelectedIndex = (int)PnlPeriodKind.Custom;
                UpdateFilterAvailability();
                SelectChoice(StatusPicker, request.Status);
                SelectChoice(PaymentPicker, request.PaymentMode);
                ProductFilter.Text = request.ProductId?.ToString(CultureInfo.InvariantCulture) ?? "";
                _suppressRefresh = false;
            }
            RefreshLocalizedChoices();
            UpdateFilterAvailability();
            await RefreshAsync();
        };
        if (loadOnShow) Unloaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= OnTranslationChanged;
            _refreshVersion++;
            _refreshCancellation?.Cancel();
            _exportCancellation?.Cancel();
        };
    }

    private async Task RefreshAsync()
    {
        DateTime selectedFrom = FromPicker.SelectedDate ?? _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        DateTime selectedTo = ToPicker.SelectedDate ?? _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        string timingPage = ReportPicker.SelectedIndex == 0 && MatrixCheck.IsChecked != true
            ? (selectedTo - selectedFrom).TotalDays > 45 ? "reports-pnl-long" : "reports-pnl-short"
            : "reports";
        using var timing = UiRefreshProbe.Track(timingPage, this);
        _refreshCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        int version = ++_refreshVersion;
        try
        {
            _currentReport = null;
            _currentPnl = null;
            DateOnly from = FromPicker.SelectedDate is { } fromDate ? DateOnly.FromDateTime(fromDate) : _clock.BusinessDate;
            DateOnly to = ToPicker.SelectedDate is { } toDate ? DateOnly.FromDateTime(toDate) : _clock.BusinessDate;
            if (to < from) throw new ArgumentException(TranslationSource.Instance["Reports_InvalidDates"]);
            if (ReportPicker.SelectedIndex == 0)
            {
                if (MatrixCheck.IsChecked == true)
                {
                    PnlMatrix matrix = await _reports.GetPnlMatrixAsync(from, to, cancellation.Token);
                    if (version != _refreshVersion) return;
                    _currentReport = ToReport(matrix);
                    _currentPnl = null;
                    ConfigureColumns(_currentReport);
                    timing.Bind(() => ReportGrid.ItemsSource = _currentReport.Rows.Select(row => new DisplayRow(_currentReport.Columns, row)).ToList());
                    PnlPanel.Visibility = Visibility.Collapsed;
                    HourlyPanel.Visibility = Visibility.Collapsed;
                    ReportGrid.Visibility = Visibility.Visible;
                    StatusText.Text = TranslationSource.Instance.Format("Reports_MonthCount", matrix.MonthKeys.Count);
                    TotalsText.Text = "";
                    return;
                }
                PnlComparison? comparison = CompareCheck.IsChecked == true
                    ? await _reports.GetPnlComparisonAsync(from, to, cancellation.Token)
                    : null;
                if (version != _refreshVersion) return;
                _currentPnl = comparison?.Current ?? await _reports.GetPnlAsync(from, to, cancellation.Token);
                if (version != _refreshVersion) return;
                _currentReport = null;
                timing.Bind(() => PnlGrid.ItemsSource = PnlRows(_currentPnl));
                PnlTotals.Text = TranslationSource.Instance.Format("Reports_PnlSummary", _currentPnl.GrossProfit.Value,
                    _currentPnl.GrossMargin.Value, _currentPnl.NetProfit.Value, _currentPnl.NetMargin.Value);
                if (comparison is not null)
                    PnlTotals.Text += TranslationSource.Instance.Format("Reports_PreviousNet", comparison.Previous.NetProfit.Value);
                PnlPanel.Visibility = Visibility.Visible;
                HourlyPanel.Visibility = Visibility.Collapsed;
                ReportGrid.Visibility = Visibility.Collapsed;
                StatusText.Text = $"{from:dd-MM-yyyy} — {to:dd-MM-yyyy}";
                TotalsText.Text = "";
                return;
            }
            ReportKind kind = ((ReportChoice)ReportPicker.SelectedItem).Kind!.Value;
            MatrixCheck.IsChecked = false;
            CompareCheck.IsChecked = false;
            string? status = ((FilterChoice?)StatusPicker.SelectedItem)?.Code;
            string? payment = ((FilterChoice?)PaymentPicker.SelectedItem)?.Code;
            if (!int.TryParse(SlowDaysFilter.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int slowDays) || slowDays is < 1 or > 3650)
                throw new ArgumentException(TranslationSource.Instance["Reports_InvalidFilter"]);
            TabularReport report = await _reports.GetReportAsync(kind, new(From: from, To: to,
                ProductId: ParseId(ProductFilter.Text), LotId: ParseId(LotFilter.Text), VendorId: ParseId(VendorFilter.Text),
                AssetId: ParseId(AssetFilter.Text), Status: status, PaymentMode: payment,
                Entity: Clean(EntityFilter.Text), Action: Clean(ActionFilter.Text), SlowMoverDays: slowDays, Limit: 500),
                cancellation.Token);
            if (version != _refreshVersion) return;
            _currentReport = report with { Title = ((ReportChoice)ReportPicker.SelectedItem).ToString() };
            _currentPnl = null;
            ConfigureColumns(_currentReport);
            timing.Bind(() => ReportGrid.ItemsSource = _currentReport.Rows.Select(row => new DisplayRow(_currentReport.Columns, row)).ToList());
            PnlPanel.Visibility = Visibility.Collapsed;
            if (kind == ReportKind.HourlySales)
            {
                ShowHourlyReport(_currentReport);
            }
            else
            {
                HourlyPanel.Visibility = Visibility.Collapsed;
                ReportGrid.Visibility = Visibility.Visible;
            }
            StatusText.Text = _currentReport.IsTruncated
                ? TranslationSource.Instance.Format("Reports_TruncatedRows", _currentReport.Rows.Count)
                : TranslationSource.Instance.Format("Reports_RowCount", _currentReport.Rows.Count);
            TotalsText.Text = string.Join(" · ", _currentReport.Columns.Where(column => _currentReport.Totals.ContainsKey(column.Key))
                .Select(column => $"{column.Heading}: {_currentReport.Totals[column.Key].ToString("N3", CultureInfo.GetCultureInfo("en-IN"))}"));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (version != _refreshVersion) return;
            ReportGrid.ItemsSource = null;
            PnlGrid.ItemsSource = null;
            HourlyPanel.Visibility = Visibility.Collapsed;
            TotalsText.Text = "";
            StatusText.Text = UserError.Message(exception);
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null;
        }
    }

    private void ConfigureColumns(TabularReport report)
    {
        ReportGrid.Columns.Clear();
        foreach (ReportColumn column in report.Columns)
        {
            bool numeric = column.Kind is ReportValueKind.Money or ReportValueKind.Quantity or ReportValueKind.Percent or ReportValueKind.Count;
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, numeric ? TextAlignment.Right : TextAlignment.Left));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            double width = column.Key is "Product" or "LocalName" or "Details" ? 200 : numeric ? 125 : 145;
            ReportGrid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Heading,
                Width = new DataGridLength(width),
                Binding = new Binding($"[{column.Key}]"),
                ElementStyle = style,
            });
        }
    }

    public void ShowHourlyReport(TabularReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        PnlPanel.Visibility = Visibility.Collapsed;
        ReportGrid.Visibility = Visibility.Collapsed;
        HourlyPanel.Visibility = Visibility.Visible;
        HourlyGrid.Children.Clear();
        AddHourlyCell("", Brushes.Transparent, false);
        for (int hour = 0; hour < 24; hour++)
            AddHourlyCell(hour.ToString("00", CultureInfo.InvariantCulture), Brushes.Transparent, false);
        var values = report.Rows.ToDictionary(row =>
            (Convert.ToString(row.Values["Weekday"], CultureInfo.InvariantCulture) ?? "",
                Convert.ToInt32(row.Values["Hour"], CultureInfo.InvariantCulture)));
        long maximum = report.Rows.Select(row => Convert.ToInt64(row.Values["Revenue"], CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0).Max();
        foreach (string dayCode in Weekdays)
        {
            string day = TranslationSource.Instance[$"Reports_Weekday_{dayCode}"];
            AddHourlyCell(day, Brushes.Transparent, false);
            for (int hour = 0; hour < 24; hour++)
            {
                values.TryGetValue((day, hour), out ReportRow? row);
                long revenue = row is null ? 0 : Convert.ToInt64(row.Values["Revenue"], CultureInfo.InvariantCulture);
                int bills = row is null ? 0 : Convert.ToInt32(row.Values["Bills"], CultureInfo.InvariantCulture);
                double strength = maximum <= 0 ? 0 : Math.Clamp((double)revenue / maximum, 0, 1);
                byte red = (byte)(242 - 138 * strength);
                byte green = (byte)(247 - 99 * strength);
                byte blue = (byte)(252 - 19 * strength);
                string tooltip = TranslationSource.Instance.Format("Reports_HourlyCell", day,
                    hour.ToString("00", CultureInfo.InvariantCulture), bills, Fx3.FromScaled(revenue));
                AddHourlyCell(bills == 0 ? "" : bills.ToString(CultureInfo.InvariantCulture),
                    new SolidColorBrush(Color.FromRgb(red, green, blue)), true, tooltip);
            }
        }
    }

    private void AddHourlyCell(string label, Brush background, bool data, string? tooltip = null)
    {
        var border = new Border
        {
            Margin = new Thickness(1),
            Height = 27,
            Background = background,
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0.5),
            ToolTip = tooltip,
        };
        border.Child = new TextBlock
        {
            Text = label,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = data ? FontWeights.Normal : FontWeights.SemiBold,
        };
        HourlyGrid.Children.Add(border);
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void ReportPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFilterAvailability();
        if (_loadOnShow && IsLoaded && !_suppressRefresh) await RefreshAsync();
    }

    private void UpdateFilterAvailability()
    {
        if (ReportPicker.SelectedItem is not ReportChoice choice) return;
        ReportKind? kind = choice.Kind;
        bool sales = kind == ReportKind.SalesRegister;
        bool expiry = kind == ReportKind.Expiry;
        if (_expiryStatusChoices != expiry)
        {
            _expiryStatusChoices = expiry;
            StatusPicker.ItemsSource = expiry ? ExpiryStatusChoices() : SalesStatusChoices();
            StatusPicker.SelectedIndex = 0;
        }
        StatusPicker.IsEnabled = sales || expiry;
        PaymentPicker.IsEnabled = sales;
        ProductFilter.IsEnabled = kind is ReportKind.StockLedger or ReportKind.ItemSales;
        LotFilter.IsEnabled = kind == ReportKind.StockLedger;
        VendorFilter.IsEnabled = kind is ReportKind.VendorPayables or ReportKind.PurchaseRegister;
        AssetFilter.IsEnabled = kind == ReportKind.AssetSchedule;
        SlowDaysFilter.IsEnabled = kind == ReportKind.SlowMovers;
        EntityFilter.IsEnabled = kind == ReportKind.AuditLog;
        ActionFilter.IsEnabled = kind == ReportKind.AuditLog;
        MatrixCheck.IsEnabled = kind is null;
        CompareCheck.IsEnabled = kind is null && MatrixCheck.IsChecked != true;
    }

    private static FilterChoice[] SalesStatusChoices() =>
    [
        new(null, "Reports_AllStatuses"),
        new("COMPLETED", "Reports_StatusCompleted"),
        new("VOID", "Reports_StatusVoid"),
    ];

    private static FilterChoice[] ExpiryStatusChoices() =>
    [
        new(null, "Reports_AllStatuses"),
        new("EXPIRED", "Reports_StatusExpired"),
        new("CRITICAL", "Reports_StatusCritical"),
        new("SOON", "Reports_StatusSoon"),
    ];

    private static List<ReportChoice> ReportChoices() =>
    [
        new(null, "Reports_Kind_ProfitAndLoss"),
        .. Enum.GetValues<ReportKind>().Select(kind => new ReportChoice(kind, $"Reports_Kind_{kind}")),
    ];

    private static FilterChoice[] PaymentChoices() =>
    [
        new(null, "Reports_AllPayments"),
        new("CASH", "Reports_PaymentCash"),
        new("UPI", "Reports_PaymentUpi"),
        new("CARD", "Reports_PaymentCard"),
        new("OTHER", "Reports_PaymentOther"),
    ];

    public void RefreshLocalizedChoices()
    {
        int reportIndex = ReportPicker.SelectedIndex;
        int periodIndex = PeriodPicker.SelectedIndex;
        string? status = ((FilterChoice?)StatusPicker.SelectedItem)?.Code;
        string? payment = ((FilterChoice?)PaymentPicker.SelectedItem)?.Code;
        bool wasSuppressed = _suppressRefresh;
        _suppressRefresh = true;
        try
        {
            ReportPicker.ItemsSource = ReportChoices();
            ReportPicker.SelectedIndex = reportIndex;
            PeriodPicker.ItemsSource = Enum.GetValues<PnlPeriodKind>().Select(kind => new PeriodChoice(kind)).ToList();
            PeriodPicker.SelectedIndex = periodIndex;
            StatusPicker.ItemsSource = _expiryStatusChoices ? ExpiryStatusChoices() : SalesStatusChoices();
            SelectChoice(StatusPicker, status);
            PaymentPicker.ItemsSource = PaymentChoices();
            SelectChoice(PaymentPicker, payment);
            UpdateFilterAvailability();
        }
        finally { _suppressRefresh = wasSuppressed; }
    }

    private static void SelectChoice(ComboBox picker, string? code)
    {
        picker.SelectedItem = picker.Items.OfType<FilterChoice>().FirstOrDefault(choice => choice.Code == code);
    }

    private async void OnTranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TranslationSource.UiCulture) || !IsLoaded) return;
        RefreshLocalizedChoices();
        await RefreshAsync();
    }

    private async void PeriodPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadOnShow || !IsLoaded || _suppressRefresh || PeriodPicker.SelectedItem is not PeriodChoice choice || choice.Kind == PnlPeriodKind.Custom) return;
        DateRange range = _reports.ResolvePeriod(choice.Kind);
        _suppressRefresh = true;
        FromPicker.SelectedDate = range.From.ToDateTime(TimeOnly.MinValue);
        ToPicker.SelectedDate = range.To.ToDateTime(TimeOnly.MinValue);
        _suppressRefresh = false;
        await RefreshAsync();
    }

    private void DatePicker_OnSelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadOnShow && IsLoaded && !_suppressRefresh) PeriodPicker.SelectedIndex = (int)PnlPeriodKind.Custom;
    }

    private async void FilterPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadOnShow && IsLoaded && !_suppressRefresh && ReportPicker.SelectedIndex > 0) await RefreshAsync();
    }

    private async void MatrixCheck_OnClick(object sender, RoutedEventArgs e)
    {
        UpdateFilterAvailability();
        if (_loadOnShow && ReportPicker.SelectedIndex == 0) await RefreshAsync();
    }

    private async void CompareCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadOnShow && ReportPicker.SelectedIndex == 0 && MatrixCheck.IsChecked != true) await RefreshAsync();
    }

    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null || (_currentPnl is null && _currentReport is null)) return;
        var dialog = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = "report.xlsx" };
        if (dialog.ShowDialog() != true) return;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        CancelExportButton.IsEnabled = true;
        PnlStatement? pnl = _currentPnl;
        TabularReport? report = _currentReport;
        try
        {
            var progress = new Progress<int>(count =>
                StatusText.Text = TranslationSource.Instance.Format("Reports_ExportedRows", count));
            await Task.Run(() =>
            {
                if (pnl is not null) _output.ExportPnlExcel(pnl, dialog.FileName, progress, cancellation.Token);
                else if (report is not null) _output.ExportExcel(report, dialog.FileName, progress, cancellation.Token);
            }, cancellation.Token);
            StatusText.Text = dialog.FileName;
        }
        catch (OperationCanceledException) { StatusText.Text = TranslationSource.Instance["Reports_CancelExport"]; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _exportCancellation = null;
            CancelExportButton.IsEnabled = false;
        }
    }

    private async void ExportFullCsv_OnClick(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null || _currentReport is null ||
            ReportPicker.SelectedItem is not ReportChoice { Kind: ReportKind kind }) return;
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "report-full.csv" };
        if (dialog.ShowDialog() != true) return;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        FullCsvButton.IsEnabled = false;
        CancelExportButton.IsEnabled = true;
        try
        {
            DateOnly from = FromPicker.SelectedDate is { } fromDate
                ? DateOnly.FromDateTime(fromDate) : _clock.BusinessDate;
            DateOnly to = ToPicker.SelectedDate is { } toDate
                ? DateOnly.FromDateTime(toDate) : _clock.BusinessDate;
            if (!int.TryParse(SlowDaysFilter.Text, NumberStyles.None, CultureInfo.InvariantCulture,
                out int slowDays)) throw new ArgumentException(TranslationSource.Instance["Reports_InvalidFilter"]);
            var filter = new ReportFilter(From: from, To: to,
                ProductId: ParseId(ProductFilter.Text), LotId: ParseId(LotFilter.Text),
                VendorId: ParseId(VendorFilter.Text), AssetId: ParseId(AssetFilter.Text),
                Status: ((FilterChoice?)StatusPicker.SelectedItem)?.Code,
                PaymentMode: ((FilterChoice?)PaymentPicker.SelectedItem)?.Code,
                Entity: Clean(EntityFilter.Text), Action: Clean(ActionFilter.Text),
                SlowMoverDays: slowDays);
            var progress = new Progress<long>(count =>
                StatusText.Text = TranslationSource.Instance.Format("Reports_ExportedRows", count));
            long rows = await _reports.ExportCsvAsync(kind, filter, dialog.FileName,
                progress, cancellation.Token);
            StatusText.Text = TranslationSource.Instance.Format("Reports_ExportedRows", rows);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = TranslationSource.Instance["Reports_CancelExport"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _exportCancellation = null;
            FullCsvButton.IsEnabled = true;
            CancelExportButton.IsEnabled = false;
        }
    }

    private void CancelExport_OnClick(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private async void Preview_OnClick(object sender, RoutedEventArgs e)
    {
        if (_previewOpen) return;
        TabularReport? report = _currentReport ?? (_currentPnl is null ? null : _output.BuildPnlReport(_currentPnl));
        if (report is null) return;
        _previewOpen = true;
        DateTime printedAt = _clock.Now;
        if (report.Rows.Count > ReportOutputService.MaxPrintableRows)
            StatusText.Text = TranslationSource.Instance.Format("Reports_PrintPreviewCapped",
                ReportOutputService.MaxPrintableRows);
        string temporary = Path.Combine(Path.GetTempPath(), $"grocery-erp-preview-{Guid.NewGuid():N}.xps");
        XpsDocument? package = null;
        bool shown = false;
        try
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    FixedDocument document = _output.CreateA4(report, printedAt);
                    using (var output = new XpsDocument(temporary, FileAccess.ReadWrite))
                        XpsDocument.CreateXpsDocumentWriter(output).Write(document.DocumentPaginator);
                    ready.SetResult();
                }
                catch (Exception exception) { ready.SetException(exception); }
            })
            { IsBackground = true, Name = "Report preview renderer" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await ready.Task;
            package = new XpsDocument(temporary, FileAccess.Read);
            var viewer = new DocumentViewer { Document = package.GetFixedDocumentSequence() };
            var window = new Window { Title = report.Title, Width = 900, Height = 700, Content = viewer };
            window.Closed += (_, _) =>
            {
                try
                {
                    viewer.Document = null;
                    package.Close();
                }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    _previewOpen = false;
                }
            };
            window.Show();
            shown = true;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            if (!shown)
            {
                package?.Close();
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                }
                _previewOpen = false;
            }
        }
    }

    private static List<PnlDisplayRow> PnlRows(PnlStatement statement)
    {
        var rows = new List<PnlDisplayRow>();
        Add(rows, "Reports_Section_Income", statement.Income);
        rows.Add(new(TranslationSource.Instance["Reports_Section_NetSales"], statement.NetSales.Value.ToString("N3", NumberCulture)));
        Add(rows, "Reports_Section_Cogs", statement.CostOfGoodsSold);
        rows.Add(new(TranslationSource.Instance["Reports_Section_GrossProfit"], statement.GrossProfit.Value.ToString("N3", NumberCulture)));
        Add(rows, "Reports_Section_Expenses", statement.Expenses);
        rows.Add(new(TranslationSource.Instance["Reports_Section_TotalExpenses"], statement.TotalExpenses.Value.ToString("N3", NumberCulture)));
        Add(rows, "Reports_Section_OtherIncome", statement.OtherIncome);
        rows.Add(new(TranslationSource.Instance["Reports_Section_NetProfit"], statement.NetProfit.Value.ToString("N3", NumberCulture)));
        return rows;
    }

    private static void Add(List<PnlDisplayRow> target, string headingKey, IReadOnlyList<PnlLine> lines)
    {
        target.Add(new(TranslationSource.Instance[headingKey], ""));
        target.AddRange(lines.Select(line => new PnlDisplayRow("  " + line.Label, line.Amount.Value.ToString("N3", NumberCulture))));
    }

    private TabularReport ToReport(PnlMatrix matrix)
    {
        var columns = new List<ReportColumn> { new("Label", TranslationSource.Instance["Reports_Particulars"], ReportValueKind.Text) };
        columns.AddRange(matrix.MonthKeys.Select(month => new ReportColumn(month, MonthHeading(month), ReportValueKind.Money)));
        columns.Add(new("Total", TranslationSource.Instance["Reports_Column_Total"], ReportValueKind.Money));
        List<ReportRow> rows = matrix.Rows.Select(row =>
        {
            var values = new Dictionary<string, object?> { ["Label"] = row.Label, ["Total"] = row.Total.Scaled };
            for (int index = 0; index < matrix.MonthKeys.Count; index++) values[matrix.MonthKeys[index]] = row.Months[index].Scaled;
            return new ReportRow(values);
        }).ToList();
        return new(TranslationSource.Instance["Reports_MatrixTitle"], matrix.Period, columns, rows, new Dictionary<string, decimal>());
    }

    private string MonthHeading(string month)
    {
        DateOnly date = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return _formatter.MonthYear(date, TranslationSource.Instance.UiCulture);
    }

    private static long? ParseId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0) return id;
        throw new ArgumentException(TranslationSource.Instance["Reports_InvalidFilter"]);
    }
    private static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PnlDisplayRow(string Label, string Amount);

    private sealed class ReportChoice(ReportKind? kind, string key)
    {
        public ReportKind? Kind { get; } = kind;
        public string Key { get; } = key;
        public override string ToString() => TranslationSource.Instance[Key];
    }

    private sealed class PeriodChoice(PnlPeriodKind kind)
    {
        public PnlPeriodKind Kind { get; } = kind;
        public override string ToString() => TranslationSource.Instance[$"Reports_Period_{Kind}"];
    }

    private sealed class FilterChoice(string? code, string key)
    {
        public string? Code { get; } = code;
        public string Key { get; } = key;
        public override string ToString() => TranslationSource.Instance[Key];
    }

    private sealed class DisplayRow(IReadOnlyList<ReportColumn> columns, ReportRow row)
    {
        public string this[string key]
        {
            get
            {
                ReportColumn column = columns.First(item => item.Key == key);
                object? value = row.Values.GetValueOrDefault(key);
                if (value is null || value is DBNull) return "";
                return column.Kind switch
                {
                    ReportValueKind.Money or ReportValueKind.Quantity or ReportValueKind.Percent =>
                        (value switch { long x => Fx3.FromScaled(x), int x => Fx3.FromScaled(x), decimal x => x, _ => 0m }).ToString("N3", CultureInfo.GetCultureInfo("en-IN")),
                    ReportValueKind.Date when DateOnly.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
                };
            }
        }
    }
}
