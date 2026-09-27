using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using GroceryErp.Core.Messages;
using GroceryErp.Reports;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class DashboardPage : Page
{
    private readonly ReportService _reports;
    private readonly ReportNavigationState _navigation;
    private readonly DispatcherTimer _refreshTimer;
    private DashboardSummary? _summary;

    public DashboardPage()
    {
        InitializeComponent();
        IServiceProvider services = ((App)Application.Current).Services;
        _reports = services.GetRequiredService<ReportService>();
        _navigation = services.GetRequiredService<ReportNavigationState>();
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
        Loaded += async (_, _) => await RefreshAsync();
        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    private async Task RefreshAsync()
    {
        try
        {
            _summary = await _reports.GetDashboardAsync();
            decimal margin = _summary.TodaySales == GroceryErp.Core.Numerics.Money.Zero
                ? 0m : _summary.TodayGrossProfit.Value * 100m / _summary.TodaySales.Value;
            TodaySales.Text = $"₹{_summary.TodaySales.Value:N3} · {_summary.TodayBills.ToString(CultureInfo.InvariantCulture)} bills · avg ₹{_summary.AverageBill.Value:N3} · margin {margin:N3}%";
            MonthSales.Text = $"₹{_summary.MonthSales.Value:N3} · previous ₹{_summary.PreviousComparableMonthSales.Value:N3}";
            StockValue.Text = $"₹{_summary.StockValue.Value:N3}";
            Payables.Text = $"₹{_summary.Payables.Value:N3}";
            Attention.Text = $"{_summary.ExpiredLots} expired · {_summary.CriticalLots} critical · {_summary.SoonLots} soon · {_summary.LowStockProducts} low stock";
            PaymentSplit.Text = string.Join(" · ", _summary.Payments.Select(payment => $"{payment.Mode} ₹{payment.Amount.Value:N3}"));
            Backup.Text = $"Backup: {_summary.LastBackup ?? "—"} · USB: {_summary.LastUsbExport ?? "—"}";
            RevenueGrid.ItemsSource = _summary.TopProductsByRevenue;
            MarginGrid.ItemsSource = _summary.TopProductsByMargin;
            DrawTrend();
        }
        catch (Exception exception) { Attention.Text = exception.Message; }
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
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void Tile_OnClick(object sender, RoutedEventArgs e)
    {
        if (_summary is null || sender is not Button { Tag: string tag }) return;
        DateOnly today = _summary.AsOf;
        (ReportKind kind, DateOnly from) = tag switch
        {
            "Stock" => (ReportKind.StockValue, today),
            "Payables" => (ReportKind.VendorPayables, today),
            "Month" => (ReportKind.SalesRegister, new DateOnly(today.Year, today.Month, 1)),
            _ => (ReportKind.SalesRegister, today),
        };
        _navigation.Open(kind, from, today);
        if (Window.GetWindow(this) is ShellWindow shell) shell.OpenReports();
    }
}
