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

public partial class LotBulkDamageWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly List<DraftLine> _lines;

    public LotBulkDamageWindow(MoneyOutService service, IClock clock, IReadOnlyCollection<LotItem> lots)
    {
        ArgumentNullException.ThrowIfNull(lots);
        if (lots.Count is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(lots));
        InitializeComponent();
        _service = service;
        _lines = lots.Select(static lot => new DraftLine(lot)).ToList();
        LinesGrid.ItemsSource = _lines;
        DateBox.SelectedDate = clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        ReasonBox.SelectedIndex = lots.All(static lot => lot.ExpiryBucketCode == "EXPIRED") ? 0 : 5;
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (DateBox.SelectedDate is not { } date)
                throw new InvalidOperationException(TranslationSource.Instance["Stock_ChooseDate"]);
            var inputs = new List<BulkDamageInput>(_lines.Count);
            Quantity totalQuantity = Quantity.Zero;
            Money totalCost = Money.Zero;
            foreach (DraftLine line in _lines)
            {
                Quantity quantity = new(decimal.Parse(InputNormalizer.Normalize(line.QuantityText),
                    NumberStyles.Number, TranslationSource.FormatCulture));
                if (quantity <= Quantity.Zero || quantity > line.Lot.OnHand)
                    throw new InvalidOperationException(TranslationSource.Instance["Stock_BulkDamageQuantityRange"]);
                inputs.Add(new(line.Lot.Id, line.Lot.OnHand.Scaled, line.Lot.DetailVersion, quantity));
                totalQuantity += quantity;
                totalCost += line.Lot.UnitCost * quantity;
            }
            string reason = (ReasonBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "OTHER";
            string summary = TranslationSource.Instance.Format("Stock_DamagePreview", inputs.Count,
                totalQuantity, totalCost);
            if (MessageBox.Show(summary, TranslationSource.Instance["Stock_BulkDamage"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _service.RecordBulkDamageAsync(inputs, DateOnly.FromDateTime(date), reason,
                OperationGuard.NewId());
            DialogResult = true;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private sealed class DraftLine(LotItem lot)
    {
        public LotItem Lot { get; } = lot;
        public string LotCode => Lot.LotCode;
        public string ProductName => Lot.ProductName;
        public Quantity Available => Lot.OnHand;
        public Money UnitCost => Lot.UnitCost;
        public string QuantityText { get; set; } = lot.OnHand.Value.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
