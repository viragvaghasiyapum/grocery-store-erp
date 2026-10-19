using System.Globalization;
using System.Windows;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

public partial class InternalUseWindow : Window
{
    private readonly PurchasingService _purchasing;
    private readonly long _lotId;
    private readonly IClock _clock;

    public InternalUseWindow(PurchasingService purchasing, long lotId, IClock clock)
    {
        _purchasing = purchasing;
        _lotId = lotId;
        _clock = clock;
        InitializeComponent();
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            decimal amount = decimal.Parse(InputNormalizer.Normalize(QuantityBox.Text),
                NumberStyles.Number, TranslationSource.FormatCulture);
            await _purchasing.ConsumeInternalAsync(_lotId, new Quantity(amount),
                _clock.BusinessDate, ReasonBox.Text, OperationGuard.NewId());
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = UserError.Message(exception);
            SaveButton.IsEnabled = true;
        }
    }
}
