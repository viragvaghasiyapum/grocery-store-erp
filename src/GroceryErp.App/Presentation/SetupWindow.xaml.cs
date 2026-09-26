using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Wpf.Ui.Controls;

namespace GroceryErp.App.Presentation;

public partial class SetupWindow : FluentWindow
{
    private readonly FirstRunService _setup;
    private readonly BackupService _backup;
    private readonly SettingsService _settings;

    public SetupWindow(FirstRunService setup, AppPaths paths, BackupService backup, SettingsService settings)
    {
        _setup = setup; _backup = backup; _settings = settings; InitializeComponent();
        DataFolder.Text = paths.Root; BackupFolder.Text = paths.DefaultBackupDir;
        Steps.SelectionChanged += (_, _) => UpdateButtons(); UpdateButtons();
    }

    private void Back_OnClick(object sender, RoutedEventArgs e) { if (Steps.SelectedIndex > 0) Steps.SelectedIndex--; }

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (Steps.SelectedIndex < Steps.Items.Count - 1) { Steps.SelectedIndex++; return; }
        if (Password.Password != ConfirmPassword.Password) { ErrorText.Text = "Passwords do not match."; Steps.SelectedIndex = 2; return; }
        try
        {
            IsEnabled = false;
            string taxMode = ((ComboBoxItem)TaxMode.SelectedItem).Tag?.ToString() ?? "REGULAR";
            await _setup.CompleteAsync(new(
                Username.Text, Password.Password, StoreName.Text, Address.Text, Phone.Text, Gstin.Text,
                State.Text, taxMode, Gujarati.IsChecked == true ? "gu-IN" : "en-IN", BackupFolder.Text,
                LotPrefix.Text, ProductPrefix.Text));
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

    private void UpdateButtons()
    {
        BackButton.IsEnabled = Steps.SelectedIndex > 0;
        NextButton.Content = Steps.SelectedIndex == Steps.Items.Count - 1 ? "Finish" : "Next";
    }
}
