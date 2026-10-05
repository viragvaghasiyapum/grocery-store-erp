using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class VendorPaymentWindow : Window
{
    private readonly PurchasingService _purchasing;
    public VendorPaymentWindow(PurchasingService purchasing)
    {
        InitializeComponent();
        _purchasing = purchasing;
        Loaded += async (_, _) =>
        {
            VendorBox.ItemsSource = await _purchasing.GetVendorsAsync();
            DateBox.SelectedDate = ((App)Application.Current).Services.GetRequiredService<IClock>()
                .BusinessDate.ToDateTime(TimeOnly.MinValue);
            ModeBox.SelectedIndex = 0;
        };
    }
    private async void VendorBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PurchaseBox is null) return;
        try
        {
            PurchaseBox.ItemsSource = VendorBox.SelectedValue is long vendorId
                ? await _purchasing.GetVendorPurchaseChoicesAsync(vendorId) : null;
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }
    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string mode = ((ComboBoxItem)ModeBox.SelectedItem).Content.ToString() ?? "CASH";
            await _purchasing.RecordPaymentAsync(new((long)VendorBox.SelectedValue,
                PurchaseBox.SelectedValue is long purchaseId ? purchaseId : null,
                DateOnly.FromDateTime(DateBox.SelectedDate ?? throw new InvalidOperationException()),
                new Money(decimal.Parse(InputNormalizer.Normalize(AmountBox.Text),
                    NumberStyles.Number, TranslationSource.FormatCulture)), mode, ReferenceBox.Text, NotesBox.Text), OperationGuard.NewId());
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
