using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using GroceryErp.Core.Messages;
using GroceryErp.Localization;
using GroceryErp.Reports;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class DashboardPage : Page
{
    private readonly ReportService _reports;
    private readonly ReportNavigationState _navigation;
    private readonly DispatcherTimer _refreshTimer;
    private DashboardSummary? _summary;
    private CancellationTokenSource? _refreshCancellation;
    private int _refreshVersion;

    public DashboardPage() : this(
        ((App)Application.Current).Services.GetRequiredService<ReportService>(),
        ((App)Application.Current).Services.GetRequiredService<ReportNavigationState>())
    { }

    public DashboardPage(ReportService reports, ReportNavigationState navigation, bool loadOnShow = true)
    {
        InitializeComponent();
        _reports = reports;
        _navigation = navigation;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            await RefreshAsync();
        };
        WeakReferenceMessenger.Default.Register<DashboardPage, SaleCompletedMessage>(this,
            static (recipient, _) => recipient.ScheduleRefresh());
        PreviewKeyDown += async (_, args) =>
        {
            if (args.Key != Key.F5) return;
            await RefreshAsync();
            args.Handled = true;
        };
        if (loadOnShow) Loaded += async (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged += OnTranslationChanged;
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _refreshTimer.Stop();
            _refreshVersion++;
            _refreshCancellation?.Cancel();
            TranslationSource.Instance.PropertyChanged -= OnTranslationChanged;
        };
    }

    private async Task RefreshAsync()
    {
        using var timing = UiRefreshProbe.Track("dashboard", this);
        _refreshCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        int version = ++_refreshVersion;
        try
        {
            DashboardSummary summary = await _reports.GetDashboardAsync(cancellation.Token);
            if (version != _refreshVersion) return;
            _summary = summary;
            decimal margin = _summary.TodayTaxableSales == GroceryErp.Core.Numerics.Money.Zero
                ? 0m : _summary.TodayGrossProfit.Value * 100m / _summary.TodayTaxableSales.Value;
            TodaySales.Text = $"₹{_summary.TodaySales.Value:N3}";
            TodayDetails.Text = TranslationSource.Instance.Format("Dashboard_TodayDetails",
                _summary.TodayBills, _summary.AverageBill.Value, margin);
            MonthSales.Text = $"₹{_summary.MonthSales.Value:N3}";
            MonthDetails.Text = TranslationSource.Instance.Format("Dashboard_MonthDetails", _summary.PreviousComparableMonthSales.Value);
            StockValue.Text = $"₹{_summary.StockValue.Value:N3}";
            Payables.Text = $"₹{_summary.Payables.Value:N3}";
            Attention.Text = TranslationSource.Instance.Format("Dashboard_AttentionDetails", _summary.ExpiredLots,
                _summary.CriticalLots, _summary.SoonLots, _summary.LowStockProducts);
            ExpiredButton.Content = TranslationSource.Instance.Format("Dashboard_ExpiredCount", _summary.ExpiredLots);
            CriticalButton.Content = TranslationSource.Instance.Format("Dashboard_CriticalCount", _summary.CriticalLots);
            SoonButton.Content = TranslationSource.Instance.Format("Dashboard_SoonCount", _summary.SoonLots);
            LowStockButton.Content = TranslationSource.Instance.Format("Dashboard_LowStockCount", _summary.LowStockProducts);
            PaymentSplit.Text = _summary.Payments.Count == 0 ? TranslationSource.Instance["Dashboard_NoPayments"]
                : TranslationSource.Instance["Dashboard_Payments"] + " " + string.Join(" · ",
                    _summary.Payments.Select(payment => $"{PaymentLabel(payment.Mode)} ₹{payment.Amount.Value:N3}"));
            Backup.Text = TranslationSource.Instance.Format("Dashboard_BackupDetails",
                _summary.LastBackup ?? "—", _summary.LastUsbExport ?? "—");
            timing.Bind(() =>
            {
                RevenueGrid.ItemsSource = _summary.TopProductsByRevenue.Select(ToDisplay).ToList();
                MarginGrid.ItemsSource = _summary.TopProductsByMargin.Select(ToDisplay).ToList();
            });
            TrendDates.Text = TranslationSource.Instance.Format("Dashboard_TrendDates",
                _summary.AsOf.AddDays(-29).ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                _summary.AsOf.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture));
            DrawTrend();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (version == _refreshVersion) Attention.Text = UserError.Message(exception);
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null;
        }
    }

    private void DrawTrend()
    {
        TrendCanvas.Children.Clear();
        if (_summary?.Trend.Count is not > 1 || TrendCanvas.ActualWidth <= 0) return;
        decimal maximum = _summary.Trend.Max(static point => point.Sales.Value);
        if (maximum <= 0) return;
        double width = Math.Max(1, TrendCanvas.ActualWidth - 16);
        double height = Math.Max(1, TrendCanvas.ActualHeight - 16);
        var line = new Polyline { Stroke = Brushes.DodgerBlue, StrokeThickness = 2 };
        for (int index = 0; index < _summary.Trend.Count; index++)
        {
            double x = 8 + width * index / (_summary.Trend.Count - 1);
            double y = 8 + height * (1 - (double)(_summary.Trend[index].Sales.Value / maximum));
            line.Points.Add(new Point(x, y));
        }
        TrendCanvas.Children.Add(line);
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void TrendCanvas_OnSizeChanged(object sender, SizeChangedEventArgs e) => DrawTrend();
    private void ScheduleRefresh()
    {
        if (!IsLoaded) return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private async void OnTranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranslationSource.UiCulture) && IsLoaded)
            await RefreshAsync();
    }

    private void Tile_OnClick(object sender, RoutedEventArgs e)
    {
        if (_summary is null || sender is not Button { Tag: string tag }) return;
        DateOnly today = _summary.AsOf;
        (ReportKind kind, DateOnly from, string? status) = tag switch
        {
            "Stock" => (ReportKind.StockValue, today, null),
            "Payables" => (ReportKind.VendorPayables, today, null),
            "Month" => (ReportKind.SalesRegister, new DateOnly(today.Year, today.Month, 1), null),
            "EXPIRED" or "CRITICAL" or "SOON" => (ReportKind.Expiry, today, tag),
            "LowStock" => (ReportKind.LowStock, today, null),
            _ => (ReportKind.SalesRegister, today, null),
        };
        OpenReport(kind, from, today, status);
    }

    private void Trend_OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_summary is { } summary) OpenReport(ReportKind.SalesRegister, summary.AsOf.AddDays(-29), summary.AsOf);
    }

    private void Payments_OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_summary is { } summary) OpenReport(ReportKind.SalesRegister, summary.AsOf, summary.AsOf);
    }

    private void Backup_OnClick(object sender, MouseButtonEventArgs e)
    {
        if (Window.GetWindow(this) is ShellWindow shell) shell.OpenSettings();
    }

    private void TopProduct_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_summary is not { } summary || sender is not DataGrid { SelectedItem: TopDisplay product }) return;
        OpenReport(ReportKind.ItemSales, summary.AsOf.AddDays(-29), summary.AsOf, productId: product.ProductId);
    }

    private void OpenReport(ReportKind kind, DateOnly from, DateOnly to, string? status = null, long? productId = null)
    {
        _navigation.Open(kind, from, to, status: status, productId: productId);
        if (Window.GetWindow(this) is ShellWindow shell) shell.OpenReports();
    }

    private static TopDisplay ToDisplay(TopProduct product) => new(product.ProductId,
        TranslationSource.Instance.UiCulture.Name == "gu-IN" && !string.IsNullOrWhiteSpace(product.LocalName)
            ? product.LocalName : product.Name,
        product.Quantity.Value.ToString("N3", TranslationSource.FormatCulture),
        product.Revenue.Value.ToString("N3", TranslationSource.FormatCulture),
        product.Margin.Value.ToString("N3", TranslationSource.FormatCulture));

    private static string PaymentLabel(string mode) => mode switch
    {
        "CASH" => TranslationSource.Instance["Reports_PaymentCash"],
        "UPI" => TranslationSource.Instance["Reports_PaymentUpi"],
        "CARD" => TranslationSource.Instance["Reports_PaymentCard"],
        "OTHER" => TranslationSource.Instance["Reports_PaymentOther"],
        _ => mode,
    };

    private sealed record TopDisplay(long ProductId, string Product, string Quantity, string Revenue, string Margin);
}
