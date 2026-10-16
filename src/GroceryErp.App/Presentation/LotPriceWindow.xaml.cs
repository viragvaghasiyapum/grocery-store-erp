using System.Globalization;
using System.Windows;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class LotPriceWindow : Window
{
    private readonly InventoryService _inventory;
    private readonly LotItem _lot;
    public LotPriceWindow(InventoryService inventory, LotItem lot)
    {
        InitializeComponent();
        _inventory = inventory;
        _lot = lot;
        BatchBox.Text = lot.BatchNumber ?? "";
        ExpiryBox.SelectedDate = lot.ExpiryDate?.ToDateTime(TimeOnly.MinValue);
        CostBox.Text = lot.UnitCost.Value.ToString("0.000", CultureInfo.InvariantCulture);
        LotText.Text = $"{lot.ProductName} — {lot.LotCode}";
        PriceBox.Text = lot.SellingPrice.Value.ToString("0.000", CultureInfo.InvariantCulture);
        MrpBox.Text = lot.Mrp?.Value.ToString("0.000", CultureInfo.InvariantCulture) ?? "";
        QuantityBox.Text = "0";
    }
    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Money price = new(decimal.Parse(InputNormalizer.Normalize(PriceBox.Text),
                NumberStyles.Number, TranslationSource.FormatCulture));
            Money? mrp = string.IsNullOrWhiteSpace(MrpBox.Text) ? null : new(decimal.Parse(
                InputNormalizer.Normalize(MrpBox.Text), NumberStyles.Number, TranslationSource.FormatCulture));
            Money cost = new(decimal.Parse(InputNormalizer.Normalize(CostBox.Text),
                NumberStyles.Number, TranslationSource.FormatCulture));
            Quantity quantity = new(decimal.Parse(InputNormalizer.Normalize(QuantityBox.Text),
                NumberStyles.Number, TranslationSource.FormatCulture));
            DateOnly date = ((App)Application.Current).Services.GetRequiredService<IClock>().BusinessDate;
            await _inventory.CorrectLotAsync(new(_lot, BatchBox.Text,
                ExpiryBox.SelectedDate is { } expiry ? DateOnly.FromDateTime(expiry) : null,
                cost, price, mrp, quantity, date, ReasonBox.Text), OperationGuard.NewId());
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
