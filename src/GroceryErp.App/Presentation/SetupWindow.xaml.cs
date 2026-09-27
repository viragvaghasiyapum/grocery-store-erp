using System.Globalization;
using System.IO;
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
    private static readonly Dictionary<string, string> GstStateNames = new(StringComparer.Ordinal)
    {
        ["01"] = "Jammu and Kashmir",
        ["02"] = "Himachal Pradesh",
        ["03"] = "Punjab",
        ["04"] = "Chandigarh",
        ["05"] = "Uttarakhand",
        ["06"] = "Haryana",
        ["07"] = "Delhi",
        ["08"] = "Rajasthan",
        ["09"] = "Uttar Pradesh",
        ["10"] = "Bihar",
        ["11"] = "Sikkim",
        ["12"] = "Arunachal Pradesh",
        ["13"] = "Nagaland",
        ["14"] = "Manipur",
        ["15"] = "Mizoram",
        ["16"] = "Tripura",
        ["17"] = "Meghalaya",
        ["18"] = "Assam",
        ["19"] = "West Bengal",
        ["20"] = "Jharkhand",
        ["21"] = "Odisha",
        ["22"] = "Chhattisgarh",
        ["23"] = "Madhya Pradesh",
        ["24"] = "Gujarat",
        ["25"] = "Daman and Diu",
        ["26"] = "Dadra and Nagar Haveli",
        ["27"] = "Maharashtra",
        ["28"] = "Andhra Pradesh",
        ["29"] = "Karnataka",
        ["30"] = "Goa",
        ["31"] = "Lakshadweep",
        ["32"] = "Kerala",
        ["33"] = "Tamil Nadu",
        ["34"] = "Puducherry",
        ["35"] = "Andaman and Nicobar Islands",
        ["36"] = "Telangana",
        ["37"] = "Andhra Pradesh",
        ["38"] = "Ladakh",
    };

    private readonly FirstRunService _setup;
    private readonly BackupService _backup;
    private readonly SettingsService _settings;
    private readonly bool _gujaratiKeyboardAvailable;
    private bool _setupCompleted;

    public SetupWindow(FirstRunService setup, AppPaths paths, BackupService backup, SettingsService settings)
    {
        _setup = setup; _backup = backup; _settings = settings; InitializeComponent();
        DataFolder.Text = paths.Root; BackupFolder.Text = paths.DefaultBackupDir;
        _gujaratiKeyboardAvailable = InputLanguageManager.Current.AvailableInputLanguages.Cast<CultureInfo>()
            .Any(culture => culture.Name.StartsWith("gu", StringComparison.OrdinalIgnoreCase));
        Steps.SelectionChanged += (_, _) => UpdateButtons();
        UpdateLocalizedText();
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
        if (Password.Password != ConfirmPassword.Password) { ErrorText.Text = Translate("Setup_ErrorPasswordMismatch"); Steps.SelectedIndex = 2; return; }
        try
        {
            IsEnabled = false;
            if (!_setupCompleted)
            {
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
                _setupCompleted = true;
            }
            _backup.Create("first-run");
            DialogResult = true;
        }
        catch (DomainException exception)
        {
            ErrorText.Text = _setupCompleted
                ? Translate("Setup_ErrorBackupFailed")
                : string.Join(" ", exception.Arguments);
        }
        catch (ArgumentException exception) { ErrorText.Text = exception.Message; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = Translate("Setup_ErrorBackupFailed");
        }
        finally { IsEnabled = true; UpdateButtons(); }
    }

    private void Language_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo(Gujarati.IsChecked == true ? "gu-IN" : "en-IN");
        UpdateLocalizedText();
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
                if (string.IsNullOrWhiteSpace(StoreName.Text)) return SetError("Setup_ErrorStoreRequired");
                if (!string.IsNullOrWhiteSpace(Gstin.Text) && !IndianIds.IsGstin(Gstin.Text.Trim().ToUpperInvariant()))
                    return SetError("Setup_ErrorGstinInvalid");
                break;
            case 2:
                if (string.IsNullOrWhiteSpace(Username.Text)) return SetError("Setup_ErrorUsernameRequired");
                if (Password.Password.Length < 8) return SetError("Setup_ErrorPasswordLength");
                if (Password.Password != ConfirmPassword.Password) return SetError("Setup_ErrorPasswordMismatch");
                break;
            case 3:
                if (string.IsNullOrWhiteSpace(BackupFolder.Text)) return SetError("Setup_ErrorBackupRequired");
                try { AppPaths.RejectCloudSynchronizedPath(BackupFolder.Text); }
                catch (DomainException exception) { return SetError(string.Join(" ", exception.Arguments)); }
                break;
            case 5:
                if (LotPrefix.Text.Length != 2 || ProductPrefix.Text.Length != 2 ||
                    !LotPrefix.Text.All(char.IsAsciiDigit) || !ProductPrefix.Text.All(char.IsAsciiDigit) ||
                    LotPrefix.Text == ProductPrefix.Text)
                    return SetError("Setup_ErrorPrefixes");
                break;
        }
        return true;
    }

    private bool SetError(string key)
    {
        ErrorText.Text = Translate(key);
        return false;
    }

    private static int ParseTag(ComboBox comboBox, int fallback) =>
        int.TryParse(((ComboBoxItem?)comboBox.SelectedItem)?.Tag?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value : fallback;

    private void UpdateButtons()
    {
        BackButton.IsEnabled = Steps.SelectedIndex > 0;
        string key = _setupCompleted
            ? "Setup_RetryBackup"
            : Steps.SelectedIndex == Steps.Items.Count - 1 ? "Setup_Finish" : "Setup_Next";
        NextButton.Content = Translate(key);
    }

    private void UpdateLocalizedText()
    {
        KeyboardStatus.Text = Translate(_gujaratiKeyboardAvailable ? "Setup_KeyboardFound" : "Setup_KeyboardMissing");
        UpdateButtons();
    }

    private static string Translate(string key) => TranslationSource.Instance[key];
}
