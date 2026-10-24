using System.Globalization;
using System.Windows;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

/// <summary>Adds or edits a vendor's contact, tax, address and payment details.</summary>
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
            TitleText.Text = Title;
            Loaded += LoadVendorAsync;
        }
        Loaded += (_, _) => NameField.FocusInput();
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
            OpeningBox.Text = vendor.OpeningBalance.Value.ToString("0.###", TranslationSource.FormatCulture);
            NotesBox.Text = vendor.Notes ?? "";
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        string? name = form.Text(NameField, NameBox.Text, required: true, maxLength: 120);
        string? gstin = form.Text(GstinField, GstinBox.Text, maxLength: 15);
        if (gstin is { Length: not 15 }) form.AddKey(GstinField, "Purchasing_GstinHint");
        string? pan = form.Text(PanField, PanBox.Text, maxLength: 10);
        if (pan is { Length: not 10 }) form.AddKey(PanField, "Purchasing_PanLength");
        string? pincode = form.Text(PincodeField, PincodeBox.Text, maxLength: 6);
        if (pincode is not null && (pincode.Length != 6 || !pincode.All(char.IsAsciiDigit)))
            form.AddKey(PincodeField, "Purchasing_PincodeDigits");
        int? terms = form.WholeNumber(TermsField, TermsBox.Text, required: true);
        decimal? opening = form.Number(OpeningField, OpeningBox.Text, required: true);
        if (!form.Apply(Fields, Summary)) return;
        SaveButton.IsEnabled = false;
        try
        {
            var input = new VendorInput(name!, ContactBox.Text, PhoneBox.Text, AltPhoneBox.Text,
                EmailBox.Text, AddressBox.Text, CityBox.Text, StateBox.Text, pincode, gstin, pan,
                UpiBox.Text, BankBox.Text, terms!.Value, new Money(opening!.Value), NotesBox.Text);
            if (_vendorId is long vendorId) await _purchasing.UpdateVendorAsync(vendorId, input);
            else await _purchasing.CreateVendorAsync(input);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }
}
