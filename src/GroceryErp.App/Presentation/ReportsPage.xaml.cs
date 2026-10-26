using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
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
    private readonly ReportService _reports;
    private readonly ReportOutputService _output;
    private readonly IClock _clock;
    private readonly DisplayFormatter _formatter;
    private readonly ReportNavigationState _navigation;
    private readonly HashSet<string> _expandedHeads = new(StringComparer.Ordinal);
    private TabularReport? _currentReport;
    private PnlReport? _currentPnl;
    private PnlPeriodRange? _periodRange;
    private bool _suppressRefresh;
    private readonly bool _loadOnShow;
    private int _refreshVersion;
    private CancellationTokenSource? _refreshCancellation;
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
        _suppressRefresh = true;
        ReportPicker.ItemsSource = ReportChoices();
        ReportPicker.SelectedIndex = 0;
        ScopePicker.ItemsSource = ScopeChoices();
        ScopePicker.SelectedIndex = 0;
        PeriodPicker.ItemsSource = Enum.GetValues<PnlPeriodKind>().Select(kind => new PeriodChoice(kind)).ToList();
        PeriodPicker.SelectedIndex = 0;
        StatusPicker.ItemsSource = SalesStatusChoices();
        StatusPicker.SelectedIndex = 0;
        PaymentPicker.ItemsSource = PaymentChoices();
        PaymentPicker.SelectedIndex = 0;
        DateOnly today = _clock.BusinessDate;
        FromPicker.SelectedDate = new DateTime(today.Year, today.Month, 1);
        ToPicker.SelectedDate = today.ToDateTime(TimeOnly.MinValue);
        RebuildPnlPeriods(null);
        _suppressRefresh = false;
        UpdateFilterAvailability();
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
            try { _periodRange = await _reports.GetPnlPeriodRangeAsync(); }
            catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
            if (_navigation.Consume() is { } request)
            {
                _suppressRefresh = true;
                ReportPicker.SelectedItem = ((IEnumerable<ReportChoice>)ReportPicker.ItemsSource)
                    .FirstOrDefault(choice => choice.Kind == request.Kind);
                FromPicker.SelectedDate = request.From.ToDateTime(TimeOnly.MinValue);
                ToPicker.SelectedDate = request.To.ToDateTime(TimeOnly.MinValue);
                PeriodPicker.SelectedIndex = (int)PnlPeriodKind.Custom;
                UpdateFilterAvailability();
                SelectChoice(StatusPicker, request.Status);
                SelectChoice(PaymentPicker, request.PaymentMode);
                ProductFilter.Text = request.ProductId?.ToString(CultureInfo.InvariantCulture) ?? "";
                MismatchesCheck.IsChecked = request.MismatchesOnly;
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

    private bool IsPnl => ReportPicker.SelectedItem is ReportChoice { Kind: null };
    private PnlScope SelectedScope => (ScopePicker.SelectedItem as ScopeChoice)?.Scope ?? PnlScope.Month;

    private async Task RefreshAsync()
    {
        DateTime selectedFrom = FromPicker.SelectedDate ?? _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        DateTime selectedTo = ToPicker.SelectedDate ?? _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        string timingPage = IsPnl && MonthsCheck.IsChecked != true
            ? SelectedScope == PnlScope.Month || (selectedTo - selectedFrom).TotalDays <= 45 ? "reports-pnl-short" : "reports-pnl-long"
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
            if (IsPnl)
            {
                PnlPeriod period = SelectedScope == PnlScope.Custom || PnlPeriodPicker.SelectedItem is not PnlPeriodItem item
                    ? ReportService.CustomPeriod(from, to) : item.Period;
                PnlReport report = await _reports.GetPnlReportAsync(period, cancellation.Token);
                if (version != _refreshVersion) return;
                PnlReport? previous = CompareCheck.IsChecked == true
                    ? await _reports.GetPnlReportAsync(PreviousPeriod(period), cancellation.Token) : null;
                if (version != _refreshVersion) return;
                _currentPnl = report;
                timing.Bind(() => ShowPnl(report));
                PnlTotals.Text = TranslationSource.Instance.Format("Reports_PnlSummary", report.Statement.GrossProfit.Value,
                    report.Statement.GrossMargin.Value, report.Statement.NetProfit.Value, report.Statement.NetMargin.Value);
                if (previous is not null)
                    PnlTotals.Text += TranslationSource.Instance.Format("Reports_PreviousNet", previous.Statement.NetProfit.Value);
                StatusText.Text = report.Period.Label + (period.Scope == PnlScope.Custom
                    ? " · " + TranslationSource.Instance["Reports_PnlCutMonthNote"] : "");
                TotalsText.Text = "";
                return;
            }
            ReportKind kind = ((ReportChoice)ReportPicker.SelectedItem).Kind!.Value;
            string? status = ((FilterChoice?)StatusPicker.SelectedItem)?.Code;
            string? payment = ((FilterChoice?)PaymentPicker.SelectedItem)?.Code;
            TabularReport report2 = await _reports.GetReportAsync(kind, new(From: from, To: to,
                ProductId: ParseId(ProductFilter.Text), VendorId: ParseId(VendorFilter.Text),
                Status: status, PaymentMode: payment,
                Limit: 500, IncludeExpired: IncludeExpiredCheck.IsChecked == true,
                MismatchesOnly: MismatchesCheck.IsChecked == true),
                cancellation.Token);
            if (version != _refreshVersion) return;
            _currentReport = report2 with { Title = ((ReportChoice)ReportPicker.SelectedItem).ToString() };
            ConfigureColumns(_currentReport);
            timing.Bind(() => ReportGrid.ItemsSource = _currentReport.Rows.Select(row => new DisplayRow(_currentReport.Columns, row)).ToList());
            PnlPanel.Visibility = Visibility.Collapsed;
            PnlStatusBadge.Visibility = Visibility.Collapsed;
            ReportGrid.Visibility = Visibility.Visible;
            string rows = _currentReport.IsTruncated
                ? TranslationSource.Instance.Format("Reports_TruncatedRows", _currentReport.Rows.Count)
                : TranslationSource.Instance.Format("Reports_RowCount", _currentReport.Rows.Count);
            StatusText.Text = _currentReport.Summary is { } summary ? rows + " · " + summary : rows;
            TotalsText.Text = string.Join(" · ", _currentReport.Columns.Where(column => _currentReport.Totals.ContainsKey(column.Key))
                .Select(column => $"{column.Heading}: {_currentReport.Totals[column.Key].ToString("N3", NumberCulture)}"));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (version != _refreshVersion) return;
            ReportGrid.ItemsSource = null;
            PnlGrid.ItemsSource = null;
            TotalsText.Text = "";
            StatusText.Text = UserError.Message(exception);
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null;
        }
    }

    // Head totals come first; selecting a head with categories expands its detail.
    private void ShowPnl(PnlReport report)
    {
        PnlStatusText.Text = _output.PnlStatusText(report);
        PnlStatusBadge.Visibility = Visibility.Visible;
        List<PnlLayoutRow> visible = VisibleRows(report);
        if (MonthsCheck.IsChecked == true)
        {
            var table = new TabularReport(report.Period.Label, report.Period.Range, MatrixColumns(report),
                visible.Select(row => MatrixRow(report, row)).ToList(), new Dictionary<string, decimal>());
            _currentReport = null;
            ConfigureColumns(table);
            ReportGrid.ItemsSource = table.Rows.Select(row => new DisplayRow(table.Columns, row)).ToList();
            ReportGrid.Visibility = Visibility.Visible;
            PnlPanel.Visibility = Visibility.Collapsed;
            return;
        }
        PnlGrid.ItemsSource = visible.Select(row => new PnlDisplayRow(row, DisplayLabel(report, row),
            row.Kind == "SECTION" ? "" : row.Amount.Value.ToString("N3", NumberCulture),
            row.Kind is "SECTION" or "TOTAL")).ToList();
        PnlPanel.Visibility = Visibility.Visible;
        ReportGrid.Visibility = Visibility.Collapsed;
    }

    private List<PnlLayoutRow> VisibleRows(PnlReport report) => report.Rows
        .Where(row => row.Kind != "CATEGORY" || row.ParentCode is { } parent && _expandedHeads.Contains(parent))
        .ToList();

    private string DisplayLabel(PnlReport report, PnlLayoutRow row) => row.Kind switch
    {
        "HEAD" when HasDetail(report, row.Code) => (_expandedHeads.Contains(row.Code) ? "▾ " : "▸ ") + row.Label,
        "HEAD" => "   " + row.Label,
        "CATEGORY" => "        " + row.Label,
        _ => row.Label,
    };

    private static bool HasDetail(PnlReport report, string head) =>
        report.Rows.Any(row => row.Kind == "CATEGORY" && row.ParentCode == head);

    private List<ReportColumn> MatrixColumns(PnlReport report)
    {
        var columns = new List<ReportColumn> { new("Label", TranslationSource.Instance["Reports_Particulars"], ReportValueKind.Text) };
        columns.AddRange(report.Matrix.MonthKeys.Select(month => new ReportColumn(month, MonthHeading(month), ReportValueKind.Money)));
        columns.Add(new("Total", TranslationSource.Instance["Reports_Column_Total"], ReportValueKind.Money));
        return columns;
    }

    private ReportRow MatrixRow(PnlReport report, PnlLayoutRow row)
    {
        var values = new Dictionary<string, object?>
        {
            ["Label"] = DisplayLabel(report, row),
            ["Total"] = row.Kind == "SECTION" ? null : row.Amount.Scaled,
        };
        for (int index = 0; index < report.Matrix.MonthKeys.Count; index++)
            values[report.Matrix.MonthKeys[index]] = row.Kind == "SECTION" ? null : row.Months[index].Scaled;
        return new ReportRow(values);
    }

    private void PnlGrid_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ToggleSelectedHead();

    private void PnlGrid_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = ToggleSelectedHead();
    }

    private bool ToggleSelectedHead()
    {
        if (_currentPnl is not { } report || PnlGrid.SelectedItem is not PnlDisplayRow { Row.Kind: "HEAD" } selected ||
            !HasDetail(report, selected.Row.Code)) return false;
        if (!_expandedHeads.Remove(selected.Row.Code)) _expandedHeads.Add(selected.Row.Code);
        string code = selected.Row.Code;
        ShowPnl(report);
        PnlGrid.SelectedItem = PnlGrid.Items.OfType<PnlDisplayRow>().FirstOrDefault(row => row.Row.Kind == "HEAD" && row.Row.Code == code);
        return true;
    }

    private PnlPeriod PreviousPeriod(PnlPeriod period) => period.Scope switch
    {
        PnlScope.Month => _reports.MonthPeriod(YearMonth.From(period.Range.From).AddMonths(-1)),
        PnlScope.CalendarYear => _reports.CalendarYearPeriod(period.Range.From.Year - 1),
        PnlScope.FiscalYear => _reports.FiscalYearPeriod(period.Range.From.Year - 1),
        _ => ReportService.CustomPeriod(
            period.Range.From.AddDays(period.Range.From.DayNumber - period.Range.To.DayNumber - 1),
            period.Range.From.AddDays(-1)),
    };

    // Months back to the first P&L entry, and each calendar and fiscal year, newest first.
    // A finalized year is marked so the saved version is visible before it opens.
    private void RebuildPnlPeriods(PnlPeriod? keep)
    {
        DateOnly today = _clock.BusinessDate;
        DateOnly first = _periodRange?.FirstEntry is { } entry && entry < today ? entry : today;
        int fiscalStart = _periodRange?.FiscalStartMonth ?? 4;
        IReadOnlyList<string> finalized = _periodRange?.FinalizedYears ?? [];
        string mark = " · " + TranslationSource.Instance["Reports_PnlStatus_FINALIZED"];
        var items = new List<PnlPeriodItem>();
        switch (SelectedScope)
        {
            case PnlScope.Month:
                for (YearMonth month = YearMonth.From(today); month >= YearMonth.From(first) && items.Count < 240; month = month.AddMonths(-1))
                {
                    bool closed = finalized.Contains($"CALENDAR:{AnnualPeriod.Calendar(month.FirstDay).Label}") ||
                        finalized.Contains($"FISCAL:{AnnualPeriod.Fiscal(month.FirstDay, fiscalStart).Label}");
                    PnlPeriod period = _reports.MonthPeriod(month);
                    items.Add(new(period, closed ? period.Label + mark : period.Label));
                }
                break;
            case PnlScope.CalendarYear:
                for (int year = today.Year; year >= first.Year; year--)
                {
                    PnlPeriod period = _reports.CalendarYearPeriod(year);
                    bool closed = finalized.Contains($"CALENDAR:{AnnualPeriod.Calendar(period.Range.From).Label}");
                    items.Add(new(period, closed ? period.Label + mark : period.Label));
                }
                break;
            case PnlScope.FiscalYear:
                for (int year = FiscalYear.StartYear(today, fiscalStart); year >= FiscalYear.StartYear(first, fiscalStart); year--)
                {
                    PnlPeriod period = _reports.FiscalYearPeriod(year);
                    bool closed = finalized.Contains($"FISCAL:{AnnualPeriod.Fiscal(period.Range.From, fiscalStart).Label}");
                    items.Add(new(period, closed ? period.Label + mark : period.Label));
                }
                break;
        }
        bool wasSuppressed = _suppressRefresh;
        _suppressRefresh = true;
        try
        {
            PnlPeriodPicker.ItemsSource = items;
            PnlPeriodPicker.SelectedItem = items.FirstOrDefault(item => keep is not null &&
                item.Period.Scope == keep.Scope && item.Period.Range == keep.Range) ?? items.FirstOrDefault();
        }
        finally { _suppressRefresh = wasSuppressed; }
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
            double width = column.Key is "Product" or "LocalName" or "Details" or "Label" ? 220 : numeric ? 118 : 140;
            ReportGrid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Heading,
                Width = new DataGridLength(width),
                Binding = new Binding($"[{column.Key}]"),
                ElementStyle = style,
            });
        }
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ReportPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFilterAvailability();
        if (_loadOnShow && IsLoaded && !_suppressRefresh) await RefreshAsync();
    }

    // Each report shows only the filters that apply to it.
    private void UpdateFilterAvailability()
    {
        if (ReportPicker.SelectedItem is not ReportChoice choice || PnlPeriodControls is null) return;
        ReportKind? kind = choice.Kind;
        bool pnl = kind is null;
        bool sales = kind == ReportKind.SalesRegister;
        bool custom = SelectedScope == PnlScope.Custom;
        PnlPeriodControls.Visibility = pnl ? Visibility.Visible : Visibility.Collapsed;
        PnlPeriodPicker.Visibility = pnl && !custom ? Visibility.Visible : Visibility.Collapsed;
        RangeControls.Visibility = !pnl || custom ? Visibility.Visible : Visibility.Collapsed;
        PeriodPicker.Visibility = pnl ? Visibility.Collapsed : Visibility.Visible;
        MonthsCheck.Visibility = pnl ? Visibility.Visible : Visibility.Collapsed;
        CompareCheck.Visibility = pnl ? Visibility.Visible : Visibility.Collapsed;
        // Only filters the selected report uses are shown; the section hides when none apply.
        var filters = new (FrameworkElement Panel, bool Applies)[]
        {
            (StatusFilterPanel, sales),
            (PaymentFilterPanel, sales),
            (ProductFilterPanel, kind is ReportKind.ItemSales or ReportKind.StockReconciliation),
            (VendorFilterPanel, kind == ReportKind.PurchaseRegister),
        };
        foreach ((FrameworkElement panel, bool applies) in filters)
            panel.Visibility = applies ? Visibility.Visible : Visibility.Collapsed;
        bool more = !pnl && filters.Any(static filter => filter.Applies);
        MoreFiltersToggle.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        MoreFiltersPanel.Visibility = more && MoreFiltersToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        StatusPicker.IsEnabled = sales;
        PaymentPicker.IsEnabled = sales;
        ProductFilter.IsEnabled = kind is ReportKind.ItemSales or ReportKind.StockReconciliation;
        VendorFilter.IsEnabled = kind == ReportKind.PurchaseRegister;
        IncludeExpiredCheck.Visibility = kind == ReportKind.StockValue ? Visibility.Visible : Visibility.Collapsed;
        MismatchesCheck.Visibility = kind == ReportKind.StockReconciliation ? Visibility.Visible : Visibility.Collapsed;
        PnlCsvButton.IsEnabled = pnl;
        FullCsvButton.IsEnabled = !pnl;
        if (!pnl) PnlStatusBadge.Visibility = Visibility.Collapsed;
    }

    private async void IncludeExpired_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadOnShow && IsLoaded) await RefreshAsync();
    }

    private async void ScopePicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PnlPeriodPicker is null) return;
        RebuildPnlPeriods(null);
        UpdateFilterAvailability();
        if (_loadOnShow && IsLoaded && !_suppressRefresh) await RefreshAsync();
    }

    private async void PnlPeriodPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadOnShow && IsLoaded && !_suppressRefresh && IsPnl) await RefreshAsync();
    }

    private static FilterChoice[] SalesStatusChoices() =>
    [
        new(null, "Reports_AllStatuses"),
        new("COMPLETED", "Reports_StatusCompleted"),
        new("VOID", "Reports_StatusVoid"),
    ];

    private static List<ReportChoice> ReportChoices() =>
    [
        new(null, "Reports_Kind_ProfitAndLoss"),
        .. Enum.GetValues<ReportKind>()
            .Select(static kind => new ReportChoice(kind, $"Reports_Kind_{kind}")),
    ];

    private static List<ScopeChoice> ScopeChoices() =>
        [.. Enum.GetValues<PnlScope>().Select(static scope => new ScopeChoice(scope))];

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
        ReportKind? selectedReport = (ReportPicker.SelectedItem as ReportChoice)?.Kind;
        PnlScope scope = SelectedScope;
        PnlPeriod? period = (PnlPeriodPicker.SelectedItem as PnlPeriodItem)?.Period;
        int periodIndex = PeriodPicker.SelectedIndex;
        string? status = ((FilterChoice?)StatusPicker.SelectedItem)?.Code;
        string? payment = ((FilterChoice?)PaymentPicker.SelectedItem)?.Code;
        bool wasSuppressed = _suppressRefresh;
        _suppressRefresh = true;
        try
        {
            ReportPicker.ItemsSource = ReportChoices();
            ReportPicker.SelectedItem = ((IEnumerable<ReportChoice>)ReportPicker.ItemsSource)
                .FirstOrDefault(choice => choice.Kind == selectedReport);
            ScopePicker.ItemsSource = ScopeChoices();
            ScopePicker.SelectedItem = ((IEnumerable<ScopeChoice>)ScopePicker.ItemsSource).First(choice => choice.Scope == scope);
            RebuildPnlPeriods(period);
            PeriodPicker.ItemsSource = Enum.GetValues<PnlPeriodKind>().Select(kind => new PeriodChoice(kind)).ToList();
            PeriodPicker.SelectedIndex = periodIndex;
            StatusPicker.ItemsSource = SalesStatusChoices();
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
        if (_loadOnShow && IsLoaded && !_suppressRefresh && !IsPnl) PeriodPicker.SelectedIndex = (int)PnlPeriodKind.Custom;
    }

    private async void FilterPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadOnShow && IsLoaded && !_suppressRefresh && !IsPnl) await RefreshAsync();
    }

    private void MonthsCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentPnl is { } report) ShowPnl(report);
    }

    private async void CompareCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadOnShow && IsPnl) await RefreshAsync();
    }

    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null || (_currentPnl is null && _currentReport is null)) return;
        var dialog = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = "report.xlsx" };
        if (dialog.ShowDialog() != true) return;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        ShowExportProgress(true);
        TabularReport? report = _currentPnl is { } pnl ? _output.BuildPnlReport(pnl, MonthsCheck.IsChecked == true) : _currentReport;
        try
        {
            var progress = new Progress<int>(count =>
                StatusText.Text = TranslationSource.Instance.Format("Reports_ExportedRows", count));
            await Task.Run(() => _output.ExportExcel(report!, dialog.FileName, progress, cancellation.Token), cancellation.Token);
            StatusText.Text = dialog.FileName;
        }
        catch (OperationCanceledException) { StatusText.Text = TranslationSource.Instance["Reports_CancelExport"]; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _exportCancellation = null;
            ShowExportProgress(false);
        }
    }

    // The CSV of a finalized year holds only saved values, so repeated exports match byte for byte.
    private async void ExportPnlCsv_OnClick(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null || _currentPnl is not { } report) return;
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "profit-and-loss.csv" };
        if (dialog.ShowDialog() != true) return;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        ShowExportProgress(true);
        try
        {
            await _reports.ExportPnlCsvAsync(report, MonthsCheck.IsChecked == true, dialog.FileName, cancellation.Token);
            StatusText.Text = dialog.FileName;
        }
        catch (OperationCanceledException) { StatusText.Text = TranslationSource.Instance["Reports_CancelExport"]; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _exportCancellation = null;
            ShowExportProgress(false);
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
        ShowExportProgress(true);
        try
        {
            DateOnly from = FromPicker.SelectedDate is { } fromDate
                ? DateOnly.FromDateTime(fromDate) : _clock.BusinessDate;
            DateOnly to = ToPicker.SelectedDate is { } toDate
                ? DateOnly.FromDateTime(toDate) : _clock.BusinessDate;
            var filter = new ReportFilter(From: from, To: to,
                ProductId: ParseId(ProductFilter.Text), VendorId: ParseId(VendorFilter.Text),
                Status: ((FilterChoice?)StatusPicker.SelectedItem)?.Code,
                PaymentMode: ((FilterChoice?)PaymentPicker.SelectedItem)?.Code,
                IncludeExpired: IncludeExpiredCheck.IsChecked == true,
                MismatchesOnly: MismatchesCheck.IsChecked == true);
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
            ShowExportProgress(false);
        }
    }

    private void MoreFilters_OnClick(object sender, RoutedEventArgs e) =>
        MoreFiltersPanel.Visibility = MoreFiltersToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void ShowExportProgress(bool running)
    {
        CancelExportButton.IsEnabled = running;
        CancelExportButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ExportProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelExport_OnClick(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private async void Preview_OnClick(object sender, RoutedEventArgs e)
    {
        if (_previewOpen) return;
        TabularReport? report = _currentPnl is { } pnl ? _output.BuildPnlReport(pnl, MonthsCheck.IsChecked == true) : _currentReport;
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

    private sealed record PnlDisplayRow(PnlLayoutRow Row, string Label, string Amount, bool IsBold);

    private sealed record PnlPeriodItem(PnlPeriod Period, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed class ReportChoice(ReportKind? kind, string key)
    {
        public ReportKind? Kind { get; } = kind;
        public string Key { get; } = key;
        public override string ToString() => TranslationSource.Instance[Key];
    }

    private sealed class ScopeChoice(PnlScope scope)
    {
        public PnlScope Scope { get; } = scope;
        public override string ToString() => TranslationSource.Instance[$"Reports_PnlScope_{Scope}"];
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
                        (value switch { long x => Fx3.FromScaled(x), int x => Fx3.FromScaled(x), decimal x => x, _ => 0m }).ToString("N3", NumberCulture),
                    ReportValueKind.Date when DateOnly.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
                };
            }
        }
    }
}
