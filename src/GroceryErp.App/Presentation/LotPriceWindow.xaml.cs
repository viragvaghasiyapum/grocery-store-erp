using System.Windows;
using GroceryErp.Core.Numerics;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Edits only a lot's selling price, MRP and expiry; quantity changes go through stock documents.</summary>
public partial class LotPriceWindow : Window
{
    private readonly InventoryService _inventory;
    private readonly LotItem _lot;

    public LotPriceWindow(InventoryService inventory, LotItem lot)
    {
        InitializeComponent();
        _inventory = inventory;
        _lot = lot;
        ExpiryBox.SelectedDate = lot.ExpiryDate?.ToDateTime(TimeOnly.MinValue);
        LotText.Text = TranslationSource.Instance.Format("Stock_LotDamageContext", lot.ProductName, lot.LotCode, lot.OnHand, lot.PurchasePrice ?? lot.UnitCost);
        PriceBox.Text = lot.SellingPrice.Value.ToString("0.###", TranslationSource.FormatCulture);
        MrpBox.Text = lot.Mrp?.Value.ToString("0.###", TranslationSource.FormatCulture) ?? "";
        Loaded += (_, _) => PriceField.FocusInput();
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        Money? price = form.Money(PriceField, PriceBox.Text, required: true);
        Money? mrp = form.Money(MrpField, MrpBox.Text);
        if (price is { } p && mrp is { } m && p.Value > m.Value) form.AddKey(PriceField, "Error_PriceAboveMrp");
        DateOnly? expiry = ExpiryBox.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null;
        if (expiry < _lot.ReceivedDate) form.AddKey(ExpiryField, "Stock_ExpiryBeforeReceived");
        if (!form.Apply(Fields, Summary)) return;
        SaveButton.IsEnabled = false;
        try
        {
            await _inventory.EditLotDetailsAsync(new(_lot.Id, _lot.DetailVersion, price!.Value, mrp, expiry));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }
}
