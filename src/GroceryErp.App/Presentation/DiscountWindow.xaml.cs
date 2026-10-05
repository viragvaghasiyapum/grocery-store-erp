using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class DiscountWindow : Window
{
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-IN");
    private readonly Money _baseAmount;

    public DiscountWindow(Money baseAmount, Money current)
    {
        InitializeComponent();
        _baseAmount = baseAmount;
        Discount = current;
        BaseText.Text = TranslationSource.Instance.Format("Sales_DiscountBase", baseAmount.Value.ToString("N3", Digits));
        ValueBox.Text = current.Value.ToString("0.###", Digits);
        Loaded += (_, _) => { ValueBox.SelectAll(); ValueBox.Focus(); };
    }

    public Money Discount { get; private set; }

    private bool TryGetDiscount(out Money discount)
    {
        discount = Money.Zero;
        if (!decimal.TryParse(InputNormalizer.Normalize(ValueBox.Text), NumberStyles.Number, Digits, out decimal value) ||
            value < 0) return false;
        if (ModeBox.SelectedIndex == 1)
        {
            if (value > 100) return false;
            discount = _baseAmount.PercentOf(new Percent(value));
        }
        else discount = new Money(value);
        return discount <= _baseAmount;
    }

    private void Value_OnChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void Mode_OnChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText is null) return;
        PreviewText.Text = TryGetDiscount(out Money discount)
            ? TranslationSource.Instance.Format("Sales_DiscountPreview", discount.Value.ToString("N3", Digits))
            : TranslationSource.Instance["Sales_DiscountInvalid"];
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetDiscount(out Money discount))
        {
            ErrorText.Text = TranslationSource.Instance["Sales_DiscountInvalid"];
            return;
        }
        Discount = discount;
        DialogResult = true;
    }
}
