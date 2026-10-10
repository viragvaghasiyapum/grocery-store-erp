using System.Globalization;
using System.Windows;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

public partial class VendorEditorWindow : Window
{
    private readonly PurchasingService _purchasing;
    private readonly long? _vendorId;

    public VendorEditorWindow(PurchasingService purchasing, long? vendorId = null)
    {
        InitializeComponent();
        _purchasing = purchasing;
        _vendorId = vendorId;
        if (vendorId is not null)
        {
            Title = TranslationSource.Instance["Purchasing_EditVendor"];
            Loaded += LoadVendorAsync;
        }
    }

    private async void LoadVendorAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            VendorInput vendor = await _purchasing.GetVendorAsync(_vendorId!.Value);
            NameBox.Text = vendor.Name;
            ContactBox.Text = vendor.ContactPerson ?? "";
            PhoneBox.Text = vendor.Phone ?? "";
            AltPhoneBox.Text = vendor.AlternatePhone ?? "";
            EmailBox.Text = vendor.Email ?? "";
            AddressBox.Text = vendor.Address ?? "";
            CityBox.Text = vendor.City ?? "";
            StateBox.Text = vendor.State ?? "";
            PincodeBox.Text = vendor.Pincode ?? "";
            GstinBox.Text = vendor.Gstin ?? "";
            PanBox.Text = vendor.Pan ?? "";
            UpiBox.Text = vendor.UpiId ?? "";
            BankBox.Text = vendor.BankDetails ?? "";
            TermsBox.Text = vendor.PaymentTermsDays.ToString(CultureInfo.InvariantCulture);
            OpeningBox.Text = vendor.OpeningBalance.Value.ToString(CultureInfo.InvariantCulture);
            NotesBox.Text = vendor.Notes ?? "";
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = new VendorInput(NameBox.Text, ContactBox.Text, PhoneBox.Text, AltPhoneBox.Text,
                EmailBox.Text, AddressBox.Text, CityBox.Text, StateBox.Text, PincodeBox.Text, GstinBox.Text, PanBox.Text,
                UpiBox.Text, BankBox.Text, int.Parse(InputNormalizer.Normalize(TermsBox.Text), CultureInfo.InvariantCulture),
                new Money(decimal.Parse(InputNormalizer.Normalize(OpeningBox.Text),
                    NumberStyles.Number, TranslationSource.FormatCulture)), NotesBox.Text);
            if (_vendorId is long vendorId) await _purchasing.UpdateVendorAsync(vendorId, input);
            else await _purchasing.CreateVendorAsync(input);
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
