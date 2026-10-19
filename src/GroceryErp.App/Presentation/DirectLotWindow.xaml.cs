using System.Globalization;
using System.Windows;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class DirectLotWindow : Window
{
    private readonly InventoryService _inventory;
    private readonly long _productId;

    public DirectLotWindow(InventoryService inventory, long productId, IClock clock)
    {
        _inventory = inventory;
        _productId = productId;
        InitializeComponent();
        DateBox.SelectedDate = clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            decimal quantity = Parse(QuantityBox.Text);
            decimal cost = Parse(CostBox.Text);
            decimal price = Parse(PriceBox.Text);
            decimal? mrp = string.IsNullOrWhiteSpace(MrpBox.Text) ? null : Parse(MrpBox.Text);
            DateOnly date = DateOnly.FromDateTime(DateBox.SelectedDate ??
                throw new FormatException("Received date is required."));
            await _inventory.CreateDirectLotAsync(new(_productId, new Quantity(quantity),
                new Money(cost), new Money(price), mrp is null ? null : new Money(mrp.Value),
                ExpiryBox.SelectedDate is { } expiry ? DateOnly.FromDateTime(expiry) : null,
                BatchBox.Text.Trim(), date), ReasonBox.Text, OperationGuard.NewId());
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = UserError.Message(exception);
            SaveButton.IsEnabled = true;
        }
    }

    private static decimal Parse(string input) => decimal.Parse(InputNormalizer.Normalize(input),
        NumberStyles.Number, TranslationSource.FormatCulture);
}
