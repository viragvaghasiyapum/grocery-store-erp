using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

public partial class SplitPaymentWindow : Window
{
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-IN");
    private readonly Money _total;

    public SplitPaymentWindow(Money total, PaymentMode initialMode = PaymentMode.Cash)
    {
        InitializeComponent();
        _total = total;
        TotalText.Text = $"₹{total.Value.ToString("N3", Digits)}";
        TextBox initial = initialMode switch
        {
            PaymentMode.Upi => UpiBox,
            PaymentMode.Card => CardBox,
            _ => CashBox,
        };
        initial.Text = total.Value.ToString("0.000", Digits);
        Loaded += (_, _) => { initial.SelectAll(); initial.Focus(); };
        UpdateBalance();
    }

    public IReadOnlyList<PaymentInput> Payments { get; private set; } = [];

    private void Amounts_OnChanged(object sender, TextChangedEventArgs e) => UpdateBalance();

    private void UpdateBalance()
    {
        if (BalanceText is null || CashBox is null || UpiBox is null || CardBox is null) return;
        if (!TryAmount(CashBox.Text, out Money cash) || !TryAmount(UpiBox.Text, out Money upi) ||
            !TryAmount(CardBox.Text, out Money card))
        {
            BalanceText.Text = TranslationSource.Instance["Sales_InvalidPayment"];
            return;
        }
        Money difference = _total - cash - upi - card;
        string key = difference > Money.Zero ? "Sales_Remaining" : "Sales_Change";
        BalanceText.Text = TranslationSource.Instance.Format(key,
            Math.Abs(difference.Value).ToString("N3", Digits));
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!TryAmount(CashBox.Text, out Money cash) || !TryAmount(UpiBox.Text, out Money upi) ||
            !TryAmount(CardBox.Text, out Money card))
        {
            ErrorText.Text = TranslationSource.Instance["Sales_InvalidPayment"];
            return;
        }
        Money paid = cash + upi + card;
        if (paid < _total || (paid > _total && cash == Money.Zero))
        {
            ErrorText.Text = TranslationSource.Instance[paid < _total ? "Sales_PaymentShort" : "Sales_NonCashOverpayment"];
            return;
        }
        var payments = new List<PaymentInput>();
        if (cash > Money.Zero) payments.Add(new(PaymentMode.Cash, cash));
        if (upi > Money.Zero) payments.Add(new(PaymentMode.Upi, upi, InputNormalizer.Normalize(UpiReferenceBox.Text).Trim()));
        if (card > Money.Zero) payments.Add(new(PaymentMode.Card, card, InputNormalizer.Normalize(CardReferenceBox.Text).Trim()));
        Payments = payments;
        DialogResult = true;
    }

    private static bool TryAmount(string text, out Money amount)
    {
        amount = Money.Zero;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!decimal.TryParse(InputNormalizer.Normalize(text), NumberStyles.Number, Digits, out decimal parsed) || parsed < 0)
            return false;
        amount = new Money(parsed);
        return true;
    }
}
