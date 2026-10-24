using System.Windows;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

/// <summary>Records internal consumption from an internal-use lot; it has no profit-and-loss effect.</summary>
public partial class InternalUseWindow : Window
{
    private readonly PurchasingService _purchasing;
    private readonly long _lotId;
    private readonly IClock _clock;
    private readonly string _operationId = OperationGuard.NewId();

    public InternalUseWindow(PurchasingService purchasing, long lotId, IClock clock)
    {
        _purchasing = purchasing;
        _lotId = lotId;
        _clock = clock;
        InitializeComponent();
        Loaded += (_, _) => QuantityField.FocusInput();
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        Quantity? quantity = form.Quantity(QuantityField, QuantityBox.Text);
        string? reason = form.Text(ReasonField, ReasonBox.Text, required: true, maxLength: 500);
        if (!form.Apply(Fields, Summary)) return;
        SaveButton.IsEnabled = false;
        try
        {
            await _purchasing.ConsumeInternalAsync(_lotId, quantity!.Value, _clock.BusinessDate, reason!, _operationId);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }
}
