using System.Windows;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;

namespace GroceryErp.App.Presentation;

/// <summary>Adds a reasoned direct-origin lot with its saved cost; the cost reaches P&amp;L only when sold.</summary>
public partial class DirectLotWindow : Window
{
    private readonly InventoryService _inventory;
    private readonly long _productId;
    private readonly string _operationId = OperationGuard.NewId();

    public DirectLotWindow(InventoryService inventory, long productId, IClock clock)
    {
        _inventory = inventory;
        _productId = productId;
        InitializeComponent();
        DateBox.SelectedDate = clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        Loaded += (_, _) => QuantityField.FocusInput();
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        Quantity? quantity = form.Quantity(QuantityField, QuantityBox.Text);
        Money? cost = form.Money(CostField, CostBox.Text, required: true);
        Money? price = form.Money(PriceField, PriceBox.Text, required: true);
        Money? mrp = form.Money(MrpField, MrpBox.Text);
        if (price is { } p && mrp is { } m && p.Value > m.Value) form.AddKey(PriceField, "Error_PriceAboveMrp");
        DateOnly? received = form.Date(DateField, DateBox.SelectedDate);
        DateOnly? expiry = ExpiryBox.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null;
        if (expiry < received) form.AddKey(ExpiryField, "Stock_ExpiryBeforeReceived");
        string? reason = form.Text(ReasonField, ReasonBox.Text, required: true, maxLength: 500);
        string? batch = form.Text(BatchField, BatchBox.Text, maxLength: 40);
        if (!form.Apply(Fields, Summary)) return;
        SaveButton.IsEnabled = false;
        try
        {
            await _inventory.CreateDirectLotAsync(new(_productId, quantity!.Value, cost!.Value, price!.Value, mrp,
                expiry, batch, received!.Value), reason!, _operationId);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }
}
