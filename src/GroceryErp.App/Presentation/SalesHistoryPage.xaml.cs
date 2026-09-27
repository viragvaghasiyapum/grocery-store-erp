using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Sales;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class SalesHistoryPage : Page
{
    private readonly SalesService _sales;
    private readonly ReceiptPrinter _printer;
    private readonly IClock _clock;

    public SalesHistoryPage()
    {
        InitializeComponent();
        IServiceProvider services = ((App)Application.Current).Services;
        _sales = services.GetRequiredService<SalesService>();
        _printer = services.GetRequiredService<ReceiptPrinter>();
        _clock = services.GetRequiredService<IClock>();
        DatePicker.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            DateOnly? date = DatePicker.SelectedDate is { } selected ? DateOnly.FromDateTime(selected) : null;
            HistoryGrid.ItemsSource = await _sales.GetHistoryAsync(new(From: date, To: date));
            StatusText.Text = $"{HistoryGrid.Items.Count}";
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Reprint_OnClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SaleHistoryItem sale) return;
        try { _printer.Print(await _sales.GetReceiptAsync(sale.Id, duplicate: true)); StatusText.Text = sale.InvoiceNumber; }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void Void_OnClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SaleHistoryItem sale) return;
        var dialog = new SaleActionWindow(false) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try { await _sales.VoidAsync(sale.Id, dialog.Reason, OperationGuard.NewId()); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void Return_OnClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SaleHistoryItem item) return;
        SaleDetails sale = await _sales.GetAsync(item.Id);
        if (sale.Lines.Count == 0) return;
        var dialog = new SaleActionWindow(true, sale.Lines) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.SelectedLine is null) return;
        try
        {
            await _sales.ReturnAsync(new(item.Id, _clock.BusinessDate, dialog.Reason, RefundMode.Cash,
                [new(dialog.SelectedLine.Id, dialog.Quantity, dialog.Resellable)], OperationGuard.NewId()));
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void DayEnd_OnClick(object sender, RoutedEventArgs e)
    {
        DateOnly date = DatePicker.SelectedDate is { } selected
            ? DateOnly.FromDateTime(selected)
            : _clock.BusinessDate;
        try
        {
            DayEndSummary summary = await _sales.GetDayEndAsync(date);
            StatusText.Text = $"{summary.BillCount} bills · ₹{summary.Net.Value:N3} net · ₹{summary.CashExpected.Value:N3} cash";
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }
}
