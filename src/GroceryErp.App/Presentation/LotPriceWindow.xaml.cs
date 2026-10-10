using System.Globalization;
using System.Windows;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Inventory;
using GroceryErp.Localization;

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
        LotText.Text = $"{lot.ProductName} — {lot.LotCode}";
        PriceBox.Text = lot.SellingPrice.Value.ToString("0.000", CultureInfo.InvariantCulture);
        MrpBox.Text = lot.Mrp?.Value.ToString("0.000", CultureInfo.InvariantCulture) ?? "";
    }
    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Money price = new(decimal.Parse(InputNormalizer.Normalize(PriceBox.Text),
                NumberStyles.Number, TranslationSource.FormatCulture));
            Money? mrp = string.IsNullOrWhiteSpace(MrpBox.Text) ? null : new(decimal.Parse(
                InputNormalizer.Normalize(MrpBox.Text), NumberStyles.Number, TranslationSource.FormatCulture));
            await _inventory.ChangeLotPriceAsync(_lot.Id, price, mrp);
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
