using System.Globalization;
using System.Windows;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

public partial class DayEndWindow : Window
{
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-IN");
    private readonly DayEndSummary _summary;
    private readonly ReceiptPrinter _printer;

    public DayEndWindow(DayEndSummary summary, ReceiptPrinter printer)
    {
        InitializeComponent();
        _summary = summary;
        _printer = printer;
        DateText.Text = summary.Date.ToString("dd MMM yyyy", Digits);
        var rows = new List<SummaryRow>();
        void Add(string key, Money value) => rows.Add(new(TranslationSource.Instance[key], value.Value.ToString("N3", Digits)));
        void Count(string key, int value) => rows.Add(new(TranslationSource.Instance[key], value.ToString(Digits)));
        Count("Sales_Bills", summary.BillCount);
        Add("Sales_Gross", summary.Gross);
        Add("Purchasing_Discount", summary.Discounts);
        Add("Receipt_Taxable", summary.Net);
        Add("Receipt_Gst", summary.Tax);
        Add("Sales_RoundOff", summary.RoundOff);
        Add("Sales_Payable", summary.Payable);
        foreach (PaymentTotal payment in summary.Payments)
        {
            string key = payment.Mode switch
            {
                PaymentMode.Cash => "Receipt_RefundModeCash",
                PaymentMode.Upi => "Receipt_RefundModeUpi",
                PaymentMode.Card => "Receipt_RefundModeCard",
                _ => "Receipt_RefundModeOther",
            };
            Add(key, payment.Amount);
        }
        Count("Sales_Voids", summary.VoidCount);
        Add("Sales_VoidValue", summary.VoidValue);
        Count("Sales_ReturnsTab", summary.ReturnCount);
        Add("Sales_ReturnValue", summary.ReturnValue);
        Add("Sales_CashSales", summary.Payments.FirstOrDefault(static payment => payment.Mode == PaymentMode.Cash)?.Amount ?? Money.Zero);
        Add("Sales_CashChange", summary.CashChange);
        Add("Sales_CashRefunds", summary.CashRefunds);
        Add("Sales_CashExpenses", summary.CashExpenses);
        Add("Sales_CashExpected", summary.CashExpected);
        SummaryGrid.ItemsSource = rows;
    }

    private async void Print_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _printer.PrintDayEndAsync(_summary);
            StatusText.Text = TranslationSource.Instance["Sales_DayEndPrinted"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private sealed record SummaryRow(string Label, string Value);
}
