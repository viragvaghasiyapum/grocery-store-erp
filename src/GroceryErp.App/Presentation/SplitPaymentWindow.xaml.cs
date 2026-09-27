using System.Globalization;
using System.Windows;
using GroceryErp.Core.Numerics;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

public partial class SplitPaymentWindow : Window
{
    private readonly Money _total;

    public SplitPaymentWindow(Money total)
    {
        InitializeComponent();
        _total = total;
        TotalText.Text = $"Pay ₹{total.Value:N3}";
        CashBox.Text = total.Value.ToString("0.000", CultureInfo.GetCultureInfo("en-IN"));
    }

    public IReadOnlyList<PaymentInput> Payments { get; private set; } = [];

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var payments = new List<PaymentInput>();
        Add(CashBox.Text, PaymentMode.Cash, payments);
        Add(UpiBox.Text, PaymentMode.Upi, payments);
        if (payments.Aggregate(Money.Zero, static (sum, item) => sum + item.Amount) < _total) return;
        Payments = payments;
        DialogResult = true;
    }

    private static void Add(string text, PaymentMode mode, List<PaymentInput> target)
    {
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("en-IN"), out decimal amount) && amount > 0)
            target.Add(new(mode, new Money(amount)));
    }
}
