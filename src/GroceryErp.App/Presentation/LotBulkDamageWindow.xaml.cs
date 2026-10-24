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

/// <summary>
/// Marks up to 50 selected lots damaged in one reviewed transaction: a quantity per lot, one reason
/// and date, the total quantity and cost shown before the confirmation.
/// </summary>
public partial class LotBulkDamageWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly List<DraftLine> _lines;
    private readonly string _operationId = OperationGuard.NewId();

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
        Loaded += (_, _) => UpdatePreview();
    }

    private void LinesGrid_OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(UpdatePreview);

    private (List<BulkDamageInput> Inputs, List<string> Problems, Quantity Quantity, Money Cost) Read()
    {
        var inputs = new List<BulkDamageInput>(_lines.Count);
        var problems = new List<string>();
        Quantity totalQuantity = Quantity.Zero;
        Money totalCost = Money.Zero;
        foreach (DraftLine line in _lines)
        {
            if (!decimal.TryParse(InputNormalizer.Normalize(line.QuantityText), NumberStyles.Number,
                    TranslationSource.FormatCulture, out decimal value) || value <= 0 || new Quantity(value) > line.Lot.OnHand)
            {
                problems.Add($"{line.LotCode}: {TranslationSource.Instance["Stock_BulkDamageQuantityRange"]}");
                continue;
            }
            var quantity = new Quantity(value);
            inputs.Add(new(line.Lot.Id, line.Lot.OnHand.Scaled, line.Lot.DetailVersion, quantity));
            totalQuantity += quantity;
            totalCost += line.Lot.UnitCost * quantity;
        }
        return (inputs, problems, totalQuantity, totalCost);
    }

    private void UpdatePreview()
    {
        (List<BulkDamageInput> inputs, List<string> problems, Quantity quantity, Money cost) = Read();
        PreviewText.Text = problems.Count > 0 ? string.Empty
            : TranslationSource.Instance.Format("Stock_DamagePreview", inputs.Count, quantity, cost);
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var form = new FormValidator();
        DateOnly? date = form.Date(DateField, DateBox.SelectedDate);
        (List<BulkDamageInput> inputs, List<string> problems, Quantity quantity, Money cost) = Read();
        if (!form.Apply(Fields, Summary, problems)) return;
        string reason = (ReasonBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "OTHER";
        if (MessageBox.Show(TranslationSource.Instance.Format("Stock_DamagePreview", inputs.Count, quantity, cost),
            TranslationSource.Instance["Stock_BulkDamage"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        SaveButton.IsEnabled = false;
        try
        {
            await _service.RecordBulkDamageAsync(inputs, date!.Value, reason, _operationId);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }

    private sealed class DraftLine(LotItem lot)
    {
        public LotItem Lot { get; } = lot;
        public string LotCode => Lot.LotCode;
        public string ProductName => Lot.ProductName;
        public Quantity Available => Lot.OnHand;
        public Money UnitCost => Lot.UnitCost;
        public string QuantityText { get; set; } = lot.OnHand.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
