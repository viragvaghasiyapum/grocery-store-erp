using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Validation;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Wpf.Ui.Controls;

namespace GroceryErp.App.Presentation;

public partial class SetupWindow : FluentWindow
{
    private static readonly IReadOnlyDictionary<string, string> GstStateNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["01"] = "Jammu and Kashmir", ["02"] = "Himachal Pradesh", ["03"] = "Punjab", ["04"] = "Chandigarh",
        ["05"] = "Uttarakhand", ["06"] = "Haryana", ["07"] = "Delhi", ["08"] = "Rajasthan",
        ["09"] = "Uttar Pradesh", ["10"] = "Bihar", ["11"] = "Sikkim", ["12"] = "Arunachal Pradesh",
        ["13"] = "Nagaland", ["14"] = "Manipur", ["15"] = "Mizoram", ["16"] = "Tripura",
        ["17"] = "Meghalaya", ["18"] = "Assam", ["19"] = "West Bengal", ["20"] = "Jharkhand",
        ["21"] = "Odisha", ["22"] = "Chhattisgarh", ["23"] = "Madhya Pradesh", ["24"] = "Gujarat",
        ["25"] = "Daman and Diu", ["26"] = "Dadra and Nagar Haveli", ["27"] = "Maharashtra", ["28"] = "Andhra Pradesh",
        ["29"] = "Karnataka", ["30"] = "Goa", ["31"] = "Lakshadweep", ["32"] = "Kerala",
        ["33"] = "Tamil Nadu", ["34"] = "Puducherry", ["35"] = "Andaman and Nicobar Islands", ["36"] = "Telangana",
        ["37"] = "Andhra Pradesh", ["38"] = "Ladakh",
    };

    private readonly FirstRunService _setup;
    private readonly BackupService _backup;
    private readonly SettingsService _settings;
    private readonly bool _gujaratiKeyboardAvailable;

    public SetupWindow(FirstRunService setup, AppPaths paths, BackupService backup, SettingsService settings)
    {
        _setup = setup; _backup = backup; _settings = settings; InitializeComponent();
        DataFolder.Text = paths.Root; BackupFolder.Text = paths.DefaultBackupDir;
        _gujaratiKeyboardAvailable = InputLanguageManager.Current.AvailableInputLanguages
            .Any(culture => culture.Name.StartsWith("gu", StringComparison.OrdinalIgnoreCase));
        KeyboardStatus.Text = _gujaratiKeyboardAvailable
            ? "Gujarati Phonetic keyboard detected."
            : "Gujarati Phonetic keyboard was not found on this computer.";
        Steps.SelectionChanged += (_, _) => UpdateButtons(); UpdateButtons();
    }

    private void Back_OnClick(object sender, RoutedEventArgs e) { if (Steps.SelectedIndex > 0) Steps.SelectedIndex--; }

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (Steps.SelectedIndex < Steps.Items.Count - 1)
        {
            if (!ValidateCurrentStep()) return;
            Steps.SelectedIndex++;
            return;
        }
        if (Password.Password != ConfirmPassword.Password) { ErrorText.Text = "Passwords do not match."; Steps.SelectedIndex = 2; return; }
        try
        {
            IsEnabled = false;
            string taxMode = ((ComboBoxItem)TaxMode.SelectedItem).Tag?.ToString() ?? "REGULAR";
            int receiptWidth = ParseTag(ReceiptWidth, 576);
            int usbReminder = ParseTag(UsbReminder, 7);
            string labelSize = ((ComboBoxItem)LabelSize.SelectedItem).Tag?.ToString() ?? "50x25";
            await _setup.CompleteAsync(new(
                Username.Text, Password.Password, StoreName.Text, Address.Text, Phone.Text, Gstin.Text,
                State.Text, taxMode, Gujarati.IsChecked == true ? "gu-IN" : "en-IN", BackupFolder.Text,
                LotPrefix.Text, ProductPrefix.Text, ReceiptPrinter.Text, receiptWidth,
                LabelPrinter.Text, labelSize, A4Printer.Text, usbReminder, ScaleRule.Text,
                _gujaratiKeyboardAvailable, StartImport.IsChecked == true ? "IMPORT" : "EMPTY"));
            await _settings.LoadAsync();
            _backup.Create("first-run");
            DialogResult = true;
        }
        catch (DomainException exception) { ErrorText.Text = string.Join(" ", exception.Arguments); }
        catch (ArgumentException exception) { ErrorText.Text = exception.Message; }
        finally { IsEnabled = true; }
    }

    private void Language_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo(Gujarati.IsChecked == true ? "gu-IN" : "en-IN");
    }

    private void Gstin_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        string normalized = Gstin.Text.Trim().ToUpperInvariant();
        if (normalized.Length >= 2 && normalized[..2].All(char.IsAsciiDigit) && State is not null && string.IsNullOrWhiteSpace(State.Text) &&
            GstStateNames.TryGetValue(normalized[..2], out string? state))
            State.Text = state;
    }

    private bool ValidateCurrentStep()
    {
        switch (Steps.SelectedIndex)
        {
            case 1:
                if (string.IsNullOrWhiteSpace(StoreName.Text)) return SetError("Store name is required.");
                if (!string.IsNullOrWhiteSpace(Gstin.Text) && !IndianIds.IsGstin(Gstin.Text.Trim().ToUpperInvariant()))
                    return SetError("Enter a valid GSTIN or leave it blank.");
                break;
            case 2:
                if (string.IsNullOrWhiteSpace(Username.Text)) return SetError("Username is required.");
                if (Password.Password.Length < 8) return SetError("Password must contain at least 8 characters.");
                if (Password.Password != ConfirmPassword.Password) return SetError("Passwords do not match.");
                break;
            case 3:
                if (string.IsNullOrWhiteSpace(BackupFolder.Text)) return SetError("Backup folder is required.");
                try { AppPaths.RejectCloudSynchronizedPath(BackupFolder.Text); }
                catch (DomainException exception) { return SetError(string.Join(" ", exception.Arguments)); }
                break;
            case 5:
                if (LotPrefix.Text.Length != 2 || ProductPrefix.Text.Length != 2 ||
                    !LotPrefix.Text.All(char.IsAsciiDigit) || !ProductPrefix.Text.All(char.IsAsciiDigit) ||
                    LotPrefix.Text == ProductPrefix.Text)
                    return SetError("Barcode prefixes must be different two-digit values.");
                break;
        }
        return true;
    }

    private bool SetError(string message)
    {
        ErrorText.Text = message;
        return false;
    }

    private static int ParseTag(ComboBox comboBox, int fallback) =>
        int.TryParse(((ComboBoxItem?)comboBox.SelectedItem)?.Tag?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value : fallback;

    private void UpdateButtons()
    {
        BackButton.IsEnabled = Steps.SelectedIndex > 0;
        NextButton.Content = Steps.SelectedIndex == Steps.Items.Count - 1 ? "Finish" : "Next";
    }
}
