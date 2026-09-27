using System.Globalization;
using System.Windows;
using GroceryErp.Core.Numerics;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

public partial class VendorEditorWindow : Window
{
    private readonly PurchasingService _purchasing;

    public VendorEditorWindow(PurchasingService purchasing)
    {
        InitializeComponent();
        _purchasing = purchasing;
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _purchasing.CreateVendorAsync(new(NameBox.Text, ContactBox.Text, PhoneBox.Text, AltPhoneBox.Text,
                EmailBox.Text, AddressBox.Text, CityBox.Text, StateBox.Text, PincodeBox.Text, GstinBox.Text, PanBox.Text,
                UpiBox.Text, BankBox.Text, int.Parse(TermsBox.Text, CultureInfo.InvariantCulture),
                new Money(decimal.Parse(OpeningBox.Text, CultureInfo.InvariantCulture)), NotesBox.Text));
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
