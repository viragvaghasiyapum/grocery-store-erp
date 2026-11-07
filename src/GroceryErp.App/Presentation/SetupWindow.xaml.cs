using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Core.Validation;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Printing;
using GroceryErp.Sales;
using Microsoft.Win32;
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
    private readonly AppPaths _paths;
    private readonly BackupService _backup;
    private readonly SettingsService _settings;
    private readonly SystemClock _clock;
    private readonly bool _gujaratiKeyboardAvailable;
    private bool _setupCompleted;
    private int _furthestStep;
    private bool _changingStep;

    public SetupWindow(FirstRunService setup, AppPaths paths, BackupService backup, SettingsService settings)
    {
        _setup = setup; _paths = paths; _backup = backup; _settings = settings; InitializeComponent();
        _clock = new SystemClock(settings);
        DataFolder.Text = paths.Root; BackupFolder.Text = paths.DefaultBackupDir;
        _gujaratiKeyboardAvailable = InputLanguageManager.Current.AvailableInputLanguages.Cast<CultureInfo>()
            .Any(culture => culture.Name.StartsWith("gu", StringComparison.OrdinalIgnoreCase));
        TranslationSource.Instance.PropertyChanged += TranslationChanged;
        Closed += (_, _) => TranslationSource.Instance.PropertyChanged -= TranslationChanged;
        UpdateLocalizedText();
    }

    private void Back_OnClick(object sender, RoutedEventArgs e) { if (Steps.SelectedIndex > 0) Steps.SelectedIndex--; }

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (Steps.SelectedIndex < Steps.Items.Count - 1)
        {
            if (!ValidateStep(Steps.SelectedIndex)) return;
            _furthestStep = Math.Max(_furthestStep, Steps.SelectedIndex + 1);
            Steps.SelectedIndex++;
            return;
        }
        for (int step = 0; step < Steps.Items.Count; step++)
            if (!ValidateStep(step)) { Steps.SelectedIndex = step; return; }
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
                    LabelPrinter.Text, labelSize, usbReminder, ScaleRule.Text, _gujaratiKeyboardAvailable,
                    FirstRunService.RequiredSeedKeys.ToDictionary(key => key, key =>
                        SharedStrings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("gu-IN"))
                        ?? throw new InvalidOperationException($"Missing Gujarati seed translation: {key}")),
                    ((ComboBoxItem)ReceiptMode.SelectedItem).Tag?.ToString() ?? "RASTER"));
                await _settings.LoadAsync();
                _setupCompleted = true;
            }
            await Task.Run(() => _backup.Create("first-run"));
            if (!DataFolder.Text.Equals(_paths.Root, StringComparison.OrdinalIgnoreCase))
                DataRootRelocator.Schedule(_paths.Root, DataFolder.Text);
            DialogResult = true;
        }
        catch (DomainException)
        {
            ErrorText.Text = _setupCompleted
                ? Translate("Setup_ErrorBackupFailed")
                : Translate("Error_Validation");
        }
        catch (ArgumentException exception) { ErrorText.Text = UserError.Message(exception); }
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

    private void TranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") UpdateLocalizedText();
    }

    private void Steps_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingStep || !ReferenceEquals(e.Source, Steps)) return;
        if (Steps.SelectedIndex > _furthestStep)
        {
            _changingStep = true;
            Steps.SelectedIndex = _furthestStep;
            _changingStep = false;
        }
        UpdateButtons();
    }

    private void Gstin_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        string normalized = Gstin.Text.Trim().ToUpperInvariant();
        if (normalized.Length >= 2 && normalized[..2].All(char.IsAsciiDigit) && State is not null && string.IsNullOrWhiteSpace(State.Text) &&
            GstStateNames.TryGetValue(normalized[..2], out string? state))
            State.Text = state;
    }

    private void ChooseData_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Translate("Settings_ChooseFolder") };
        if (picker.ShowDialog(this) == true) DataFolder.Text = picker.FolderName;
    }

    private bool ValidateStep(int step)
    {
        switch (step)
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
                catch (DomainException) { return SetError("Setup_ErrorCloudPath"); }
                if (!DataFolder.Text.Equals(_paths.Root, StringComparison.OrdinalIgnoreCase))
                    try { DataRootRelocator.ValidateCandidate(_paths.Root, DataFolder.Text); }
                    catch (DomainException) { return SetError("Setup_ErrorDataFolder"); }
                break;
            case 5:
                if (LotPrefix.Text.Length != 2 || ProductPrefix.Text.Length != 2 ||
                    !LotPrefix.Text.All(char.IsAsciiDigit) || !ProductPrefix.Text.All(char.IsAsciiDigit) ||
                    LotPrefix.Text == ProductPrefix.Text)
                    return SetError("Setup_ErrorPrefixes");
                string scalePrefix = ScaleRule.Text.Trim();
                if (scalePrefix.Length != 0 && (scalePrefix.Length != 2 ||
                    !scalePrefix.All(char.IsAsciiDigit) || scalePrefix == LotPrefix.Text ||
                    scalePrefix == ProductPrefix.Text))
                    return SetError("Setup_ErrorScalePrefix");
                break;
        }
        return true;
    }

    private async void ReceiptTest_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ReceiptPrinter.Text)) { SetError("Setup_ErrorPrinterRequired"); return; }
        try
        {
            ErrorText.Text = "";
            var receipt = new ReceiptData(StoreName.Text, Address.Text, Phone.Text, Gstin.Text,
                "NONE", Translate("Setup_TestDocument"), _clock.Now, null, Array.Empty<ReceiptLine>(),
                Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero,
                Array.Empty<PaymentInput>(), Money.Zero,
                Gujarati.IsChecked == true ? "gu-IN" : "en-IN", Translate("Setup_TestDocument"), false);
            int width = ParseTag(ReceiptWidth, 576);
            byte[] bytes = ((ComboBoxItem)ReceiptMode.SelectedItem).Tag?.ToString() == "TEXT"
                ? GroceryErp.Sales.ReceiptPrinter.RenderText(receipt, width)
                : GroceryErp.Sales.ReceiptPrinter.Render(receipt, width, false);
            await Task.Run(() => RawPrinter.Send(ReceiptPrinter.Text.Trim(), bytes, Translate("Setup_TestDocument")));
            ErrorText.Text = Translate("Setup_TestSent");
        }
        catch (Exception) { SetError("Setup_TestFailed"); }
    }

    private async void LabelTest_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LabelPrinter.Text)) { SetError("Setup_ErrorPrinterRequired"); return; }
        try
        {
            ErrorText.Text = "";
            string[] size = (((ComboBoxItem)LabelSize.SelectedItem).Tag?.ToString() ?? "50x25").Split('x');
            var label = new TsplLabel(Translate("Setup_TestDocument"), null, "210000000001", 1m, null,
                int.Parse(size[0], CultureInfo.InvariantCulture), int.Parse(size[1], CultureInfo.InvariantCulture));
            byte[] bytes = TsplLabelBuilder.Build(label);
            await Task.Run(() => RawPrinter.Send(LabelPrinter.Text.Trim(), bytes, Translate("Setup_TestDocument")));
            ErrorText.Text = Translate("Setup_TestSent");
        }
        catch (Exception) { SetError("Setup_TestFailed"); }
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
