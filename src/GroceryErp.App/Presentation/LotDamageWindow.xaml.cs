using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class LotDamageWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly LotItem _lot;

    public LotDamageWindow(MoneyOutService service, IClock clock, LotItem lot)
    {
        InitializeComponent();
        _service = service;
        _lot = lot;
        LotText.Text = TranslationSource.Instance.Format("Stock_LotDamageContext",
            lot.ProductName, lot.LotCode, lot.OnHand, lot.UnitCost);
        DateBox.SelectedDate = clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        if (lot.ExpiryBucketCode == "EXPIRED") ReasonBox.SelectedIndex = 0;
        else ReasonBox.SelectedIndex = 5;
    }

    private static decimal Parse(string text) => decimal.Parse(InputNormalizer.Normalize(text),
        NumberStyles.Number, TranslationSource.FormatCulture);

    private void Preview_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (PreviewText is null || QuantityBox is null || RecoveredBox is null) return;
        if (!decimal.TryParse(InputNormalizer.Normalize(QuantityBox.Text), NumberStyles.Number,
                TranslationSource.FormatCulture, out decimal quantity) || quantity <= 0 ||
            !decimal.TryParse(InputNormalizer.Normalize(RecoveredBox.Text), NumberStyles.Number,
                TranslationSource.FormatCulture, out decimal recovered) || recovered < 0)
        {
            PreviewText.Text = string.Empty;
            return;
        }
        Money cost = _lot.UnitCost * new Quantity(quantity);
        Money recoveredAmount = new(recovered);
        PreviewText.Text = $"{TranslationSource.Instance["MoneyOut_Cost"]}: {cost}  •  " +
            $"{TranslationSource.Instance["MoneyOut_Recovered"]}: {recoveredAmount}  •  " +
            $"{TranslationSource.Instance["Stock_NetLoss"]}: {cost - recoveredAmount}";
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Quantity quantity = new(Parse(QuantityBox.Text));
            if (quantity <= Quantity.Zero || quantity > _lot.OnHand)
                throw new InvalidOperationException(TranslationSource.Instance["Stock_DamageQuantityRange"]);
            Money recovered = new(Parse(RecoveredBox.Text));
            if (DateBox.SelectedDate is not { } date)
                throw new InvalidOperationException(TranslationSource.Instance["Stock_ChooseDate"]);
            string reason = (ReasonBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "OTHER";
            if (MessageBox.Show(TranslationSource.Instance.Format("MoneyOut_DamageConfirm", quantity, _lot.LotCode),
                TranslationSource.Instance["MoneyOut_RecordDamage"], MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _service.RecordDamageAsync(new(_lot.Id, DateOnly.FromDateTime(date), quantity,
                reason, recovered, NotesBox.Text), OperationGuard.NewId());
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }
}
