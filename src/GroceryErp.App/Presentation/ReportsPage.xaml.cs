using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Localization;
using GroceryErp.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public partial class ReportsPage : Page
{
    private static readonly string[] PnlChoice = ["Profit and loss"];
    private static readonly string[] StatusChoices = ["All status", "COMPLETED", "VOID"];
    private static readonly string[] PaymentChoices = ["All payments", "CASH", "UPI", "CARD", "OTHER"];
    private readonly ReportService _reports;
    private readonly ReportOutputService _output;
    private readonly IClock _clock;
    private readonly DisplayFormatter _formatter;
    private readonly ReportNavigationState _navigation;
    private TabularReport? _currentReport;
    private PnlStatement? _currentPnl;
    private bool _suppressRefresh;

    public ReportsPage()
    {
        InitializeComponent();
        IServiceProvider services = ((App)Application.Current).Services;
        _reports = services.GetRequiredService<ReportService>();
        _output = services.GetRequiredService<ReportOutputService>();
        _clock = services.GetRequiredService<IClock>();
        _formatter = services.GetRequiredService<DisplayFormatter>();
        _navigation = services.GetRequiredService<ReportNavigationState>();
        ReportPicker.ItemsSource = PnlChoice.Concat(Enum.GetNames<ReportKind>()).ToList();
        ReportPicker.SelectedIndex = 0;
        PeriodPicker.ItemsSource = Enum.GetValues<PnlPeriodKind>();
        PeriodPicker.SelectedItem = PnlPeriodKind.ThisMonth;
        StatusPicker.ItemsSource = StatusChoices;
        StatusPicker.SelectedIndex = 0;
        PaymentPicker.ItemsSource = PaymentChoices;
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
        Loaded += async (_, _) =>
        {
            if (_navigation.Consume() is { } request)
            {
                _suppressRefresh = true;
                ReportPicker.SelectedIndex = 1 + (int)request.Kind;
                FromPicker.SelectedDate = request.From.ToDateTime(TimeOnly.MinValue);
                ToPicker.SelectedDate = request.To.ToDateTime(TimeOnly.MinValue);
                PeriodPicker.SelectedItem = PnlPeriodKind.Custom;
                _suppressRefresh = false;
            }
            await RefreshAsync();
        };
    }

    private async Task RefreshAsync()
    {
        try
        {
            DateOnly from = FromPicker.SelectedDate is { } fromDate ? DateOnly.FromDateTime(fromDate) : _clock.BusinessDate;
            DateOnly to = ToPicker.SelectedDate is { } toDate ? DateOnly.FromDateTime(toDate) : _clock.BusinessDate;
            if (ReportPicker.SelectedIndex == 0)
            {
                if (MatrixCheck.IsChecked == true)
                {
                    PnlMatrix matrix = await _reports.GetPnlMatrixAsync(from, to);
                    _currentReport = ToReport(matrix);
                    _currentPnl = null;
                    ConfigureColumns(_currentReport);
                    ReportGrid.ItemsSource = _currentReport.Rows.Select(row => new DisplayRow(_currentReport.Columns, row)).ToList();
                    PnlPanel.Visibility = Visibility.Collapsed;
                    ReportGrid.Visibility = Visibility.Visible;
                    StatusText.Text = $"{matrix.MonthKeys.Count.ToString(CultureInfo.InvariantCulture)} months";
                    TotalsText.Text = "";
                    return;
                }
                PnlComparison? comparison = CompareCheck.IsChecked == true
                    ? await _reports.GetPnlComparisonAsync(from, to)
                    : null;
                _currentPnl = comparison?.Current ?? await _reports.GetPnlAsync(from, to);
                _currentReport = null;
                PnlGrid.ItemsSource = PnlRows(_currentPnl);
                PnlTotals.Text = $"Gross profit ₹{_currentPnl.GrossProfit.Value:N3} ({_currentPnl.GrossMargin.Value:N3}%)  ·  Net profit ₹{_currentPnl.NetProfit.Value:N3} ({_currentPnl.NetMargin.Value:N3}%)";
                if (comparison is not null)
                    PnlTotals.Text += $"  ·  Previous ₹{comparison.Previous.NetProfit.Value:N3}";
                PnlPanel.Visibility = Visibility.Visible;
                ReportGrid.Visibility = Visibility.Collapsed;
                StatusText.Text = $"{from:dd-MM-yyyy} — {to:dd-MM-yyyy}";
                TotalsText.Text = "";
                return;
            }
            ReportKind kind = Enum.Parse<ReportKind>((string)ReportPicker.SelectedItem, ignoreCase: false);
            MatrixCheck.IsChecked = false;
            CompareCheck.IsChecked = false;
            string? status = StatusPicker.SelectedIndex > 0 ? (string)StatusPicker.SelectedItem : null;
            string? payment = PaymentPicker.SelectedIndex > 0 ? (string)PaymentPicker.SelectedItem : null;
            int slowDays = int.TryParse(SlowDaysFilter.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedDays) ? parsedDays : 90;
            _currentReport = await _reports.GetReportAsync(kind, new(From: from, To: to,
                ProductId: ParseId(ProductFilter.Text), LotId: ParseId(LotFilter.Text), VendorId: ParseId(VendorFilter.Text),
                AssetId: ParseId(AssetFilter.Text), Status: status, PaymentMode: payment,
                Entity: Clean(EntityFilter.Text), Action: Clean(ActionFilter.Text), SlowMoverDays: slowDays));
            _currentPnl = null;
            ConfigureColumns(_currentReport);
            ReportGrid.ItemsSource = _currentReport.Rows.Select(row => new DisplayRow(_currentReport.Columns, row)).ToList();
            PnlPanel.Visibility = Visibility.Collapsed;
            ReportGrid.Visibility = Visibility.Visible;
            StatusText.Text = $"{_currentReport.Rows.Count.ToString(CultureInfo.InvariantCulture)} rows";
            TotalsText.Text = string.Join(" · ", _currentReport.Columns.Where(column => _currentReport.Totals.ContainsKey(column.Key))
                .Select(column => $"{column.Heading}: {_currentReport.Totals[column.Key].ToString("N3", CultureInfo.GetCultureInfo("en-IN"))}"));
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private void ConfigureColumns(TabularReport report)
    {
        ReportGrid.Columns.Clear();
        foreach (ReportColumn column in report.Columns)
            ReportGrid.Columns.Add(new DataGridTextColumn { Header = column.Heading, Width = new DataGridLength(140), Binding = new Binding($"[{column.Key}]") });
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void ReportPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_suppressRefresh) await RefreshAsync();
    }

    private async void PeriodPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressRefresh || PeriodPicker.SelectedItem is not PnlPeriodKind kind || kind == PnlPeriodKind.Custom) return;
        DateRange range = _reports.ResolvePeriod(kind);
        FromPicker.SelectedDate = range.From.ToDateTime(TimeOnly.MinValue);
        ToPicker.SelectedDate = range.To.ToDateTime(TimeOnly.MinValue);
        await RefreshAsync();
    }

    private async void FilterPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_suppressRefresh && ReportPicker.SelectedIndex > 0) await RefreshAsync();
    }

    private async void MatrixCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (ReportPicker.SelectedIndex == 0) await RefreshAsync();
    }

    private async void CompareCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (ReportPicker.SelectedIndex == 0 && MatrixCheck.IsChecked != true) await RefreshAsync();
    }

    private void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = "report.xlsx" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (_currentPnl is not null) _output.ExportPnlExcel(_currentPnl, dialog.FileName);
            else if (_currentReport is not null) _output.ExportExcel(_currentReport, dialog.FileName);
            StatusText.Text = dialog.FileName;
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private void Preview_OnClick(object sender, RoutedEventArgs e)
    {
        TabularReport? report = _currentReport ?? (_currentPnl is null ? null : ToReport(_currentPnl));
        if (report is null) return;
        try
        {
            FixedDocument document = _output.CreateA4(report, _clock.Now);
            var viewer = new DocumentViewer { Document = document };
            var window = new Window { Title = report.Title, Width = 900, Height = 700, Owner = Window.GetWindow(this), Content = viewer };
            window.ShowDialog();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private static List<PnlDisplayRow> PnlRows(PnlStatement statement)
    {
        var rows = new List<PnlDisplayRow>();
        Add(rows, "Income", statement.Income);
        rows.Add(new("Net sales", statement.NetSales.Value.ToString("N3", CultureInfo.GetCultureInfo("en-IN"))));
        Add(rows, "Cost of goods sold", statement.CostOfGoodsSold);
        rows.Add(new("Gross profit", statement.GrossProfit.Value.ToString("N3", CultureInfo.GetCultureInfo("en-IN"))));
        Add(rows, "Operating expenses", statement.Expenses);
        rows.Add(new("Total expenses", statement.TotalExpenses.Value.ToString("N3", CultureInfo.GetCultureInfo("en-IN"))));
        Add(rows, "Other income", statement.OtherIncome);
        rows.Add(new("Net profit", statement.NetProfit.Value.ToString("N3", CultureInfo.GetCultureInfo("en-IN"))));
        return rows;
    }

    private static void Add(List<PnlDisplayRow> target, string heading, IReadOnlyList<PnlLine> lines)
    {
        target.Add(new(heading, ""));
        target.AddRange(lines.Select(line => new PnlDisplayRow("  " + line.Label, line.Amount.Value.ToString("N3", CultureInfo.GetCultureInfo("en-IN")))));
    }

    private static TabularReport ToReport(PnlStatement pnl)
    {
        List<PnlDisplayRow> display = PnlRows(pnl);
        return new("Profit and loss", pnl.Period,
            [new("Label", "Particulars", ReportValueKind.Text), new("Amount", "Amount", ReportValueKind.Text)],
            display.Select(row => new ReportRow(new Dictionary<string, object?> { ["Label"] = row.Label, ["Amount"] = row.Amount })).ToList(),
            new Dictionary<string, decimal>());
    }

    private TabularReport ToReport(PnlMatrix matrix)
    {
        var columns = new List<ReportColumn> { new("Label", "Particulars", ReportValueKind.Text) };
        columns.AddRange(matrix.MonthKeys.Select(month => new ReportColumn(month, MonthHeading(month), ReportValueKind.Money)));
        columns.Add(new("Total", "Total", ReportValueKind.Money));
        List<ReportRow> rows = matrix.Rows.Select(row =>
        {
            var values = new Dictionary<string, object?> { ["Label"] = row.Label, ["Total"] = row.Total.Scaled };
            for (int index = 0; index < matrix.MonthKeys.Count; index++) values[matrix.MonthKeys[index]] = row.Months[index].Scaled;
            return new ReportRow(values);
        }).ToList();
        return new("Profit and loss — monthly matrix", matrix.Period, columns, rows, new Dictionary<string, decimal>());
    }

    private string MonthHeading(string month)
    {
        DateOnly date = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return _formatter.MonthYear(date, TranslationSource.Instance.UiCulture);
    }

    private static long? ParseId(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0 ? id : null;
    private static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PnlDisplayRow(string Label, string Amount);

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
