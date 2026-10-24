using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public partial class SettingsPage : Page
{
    private readonly BackupService _backups;
    private readonly SettingsService _settings;
    private readonly AuthenticationService _authentication;
    private readonly IClock _clock;
    private readonly AppPaths _paths;

    public SettingsPage() : this(
        ((App)Application.Current).Services.GetRequiredService<BackupService>(),
        ((App)Application.Current).Services.GetRequiredService<SettingsService>(),
        ((App)Application.Current).Services.GetRequiredService<AuthenticationService>(),
        ((App)Application.Current).Services.GetRequiredService<IClock>(),
        ((App)Application.Current).Services.GetRequiredService<AppPaths>())
    {
    }

    public SettingsPage(BackupService backups, SettingsService settings,
        AuthenticationService authentication, IClock clock, AppPaths paths)
    {
        InitializeComponent();
        _backups = backups;
        _settings = settings;
        _authentication = authentication;
        _clock = clock;
        _paths = paths;
        DataRoot.Text = _paths.Root;
        Loaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged += TranslationChanged;
            RefreshDrives(); RefreshScannerPorts(); RefreshStatus();
        };
        Unloaded += (_, _) => TranslationSource.Instance.PropertyChanged -= TranslationChanged;
    }

    private void TranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") { RefreshScannerPorts(); RefreshStatus(); }
    }

    private void RefreshStatus()
    {
        TranslationSource text = TranslationSource.Instance;
        BackupHealth health = _backups.GetHealth();
        BackupFolder.Text = _backups.BackupDirectory;
        BackupStatus.Text = health.LastBackup is null
            ? text["Settings_NoBackup"]
            : text.Format("Settings_LastBackup", health.LastBackup.Value.ToString("dd MMM yyyy HH:mm", TranslationSource.FormatCulture));
        UsbStatus.Text = health.LastUsbExport is null
            ? text["Settings_NoUsbExport"]
            : text.Format("Settings_LastUsbExport", health.LastUsbExport.Value.ToString("dd MMM yyyy HH:mm", TranslationSource.FormatCulture));
        if (Window.GetWindow(this) is ShellWindow shell) shell.RefreshBackupStatus();
        _ = RefreshCompactionStatusAsync();
    }

    private async Task RefreshCompactionStatusAsync()
    {
        try
        {
            CompactionEstimate estimate = await Task.Run(_backups.EstimateCompaction);
            CompactStatus.Text = TranslationSource.Instance.Format(
                estimate.IsWorthwhile ? "Settings_CompactStatus" : "Settings_CompactNotNeeded",
                CompactionWindow.FormatBytes(estimate.ReclaimableBytes), CompactionWindow.FormatBytes(estimate.DatabaseBytes));
        }
        catch (Exception exception) { CompactStatus.Text = UserError.Message(exception); }
    }

    private async void Compact_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CompactButton.IsEnabled = false;
            var app = (App)Application.Current;
            await app.WaitForStartupMaintenanceAsync();
            CompactionEstimate estimate = await Task.Run(_backups.EstimateCompaction);
            TranslationSource text = TranslationSource.Instance;
            if (!estimate.HasSpace)
            {
                StatusText.Foreground = Brushes.Crimson;
                StatusText.Text = text.Format("Settings_CompactNoSpace",
                    CompactionWindow.FormatBytes(estimate.RequiredFreeBytes), CompactionWindow.FormatBytes(estimate.AvailableFreeBytes));
                return;
            }
            if (MessageBox.Show(text.Format("Settings_CompactConfirm", CompactionWindow.FormatBytes(estimate.ReclaimableBytes),
                        CompactionWindow.FormatBytes(estimate.RequiredFreeBytes)),
                    text["Settings_CompactAction"], MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await Task.Run(_backups.RequestCompaction);
            // The compaction itself saves a verified backup before it swaps the copy in.
            app.SkipCloseBackup();
            MessageBox.Show(text["Settings_CompactRestart"], text["App_Title"], MessageBoxButton.OK, MessageBoxImage.Information);
            await app.RequestShutdownAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { CompactButton.IsEnabled = true; }
    }

    private void RefreshDrives()
    {
        RemovableDrives.ItemsSource = DriveInfo.GetDrives()
            .Where(static drive => drive.DriveType == DriveType.Removable && drive.IsReady)
            .Select(static drive => drive.RootDirectory.FullName).ToArray();
        RemovableDrives.SelectedIndex = RemovableDrives.Items.Count > 0 ? 0 : -1;
    }

    private void RefreshDrives_OnClick(object sender, RoutedEventArgs e) => RefreshDrives();

    private void RefreshScannerPorts()
    {
        string selected = (ScannerPort.SelectedItem as ComboBoxItem)?.Tag as string
            ?? _settings.Current.ScannerComPort;
        if (!ScannerComPorts.IsComPort(selected)) selected = "";
        ScannerPort.Items.Clear();
        ScannerPort.Items.Add(new ComboBoxItem
        {
            Content = TranslationSource.Instance["Settings_ScannerKeyboard"],
            Tag = "",
        });
        foreach (ScannerComPort port in ScannerComPorts.GetPorts())
            ScannerPort.Items.Add(new ComboBoxItem
            {
                Content = port.IsBluetoothVirtual
                    ? TranslationSource.Instance.Format("Settings_ScannerBluetoothVirtual", port.Name)
                    : port.Name,
                Tag = port.Name,
            });
        if (ScannerComPorts.IsComPort(selected) && !ScannerPort.Items.OfType<ComboBoxItem>()
                .Any(item => string.Equals(item.Tag as string, selected, StringComparison.OrdinalIgnoreCase)))
            ScannerPort.Items.Add(new ComboBoxItem
            {
                Content = TranslationSource.Instance.Format("Settings_ScannerPortMissing", selected),
                Tag = selected,
            });
        ScannerPort.SelectedItem = ScannerPort.Items.OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag as string, selected, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshScannerPorts_OnClick(object sender, RoutedEventArgs e) => RefreshScannerPorts();

    private async void SaveScannerPort_OnClick(object sender, RoutedEventArgs e)
    {
        string port = (ScannerPort.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        try
        {
            SaveScannerPortButton.IsEnabled = false;
            await _settings.SaveAsync(new Dictionary<string, string> { ["scanner.com_port"] = port });
            ShowStatus("Settings_ScannerPortSaved");
            (Window.GetWindow(this) as ShellWindow)?.RefreshScannerStatus();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SaveScannerPortButton.IsEnabled = true; }
    }

    private async void MoveData_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = TranslationSource.Instance["Settings_ChooseFolder"] };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            DataRootRelocator.ValidateCandidate(_paths.Root, picker.FolderName);
            MoveDataButton.IsEnabled = false;
            ShowStatus("Settings_BackupRunning");
            var app = (App)Application.Current;
            await app.WaitForStartupMaintenanceAsync();
            await Task.Run(() => _backups.Create("pre-data-move"));
            DataRootRelocator.Schedule(_paths.Root, picker.FolderName);
            MessageBox.Show(TranslationSource.Instance["Settings_DataMoveRestart"],
                TranslationSource.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Information);
            await app.RequestShutdownAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { MoveDataButton.IsEnabled = true; }
    }

    private async void Backup_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            BackupButton.IsEnabled = false;
            ShowStatus("Settings_BackupRunning");
            await ((App)Application.Current).WaitForStartupMaintenanceAsync();
            await Task.Run(() => _backups.Create("manual"));
            ShowStatus("Settings_BackupSuccess");
            RefreshStatus();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { BackupButton.IsEnabled = true; }
    }

    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        if (RemovableDrives.SelectedItem is not string drive)
        {
            ShowStatus("Settings_SelectUsb");
            return;
        }
        try
        {
            ExportButton.IsEnabled = false;
            ExportProgress.Visibility = Visibility.Visible;
            ShowStatus("Settings_ExportRunning");
            await ((App)Application.Current).WaitForStartupMaintenanceAsync();
            IReadOnlyList<string> copied = await Task.Run(() => _backups.ExportToUsb(drive));
            await _settings.SaveAsync(new Dictionary<string, string>
            {
                ["backup.last_usb_export"] = _clock.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            });
            StatusText.Text = TranslationSource.Instance.Format("Settings_ExportSuccess",
                copied.Count.ToString(CultureInfo.InvariantCulture));
            StatusText.Foreground = Brushes.ForestGreen;
            RefreshStatus();
        }
        catch (Exception exception) { ShowError(exception); }
        finally
        {
            ExportButton.IsEnabled = true;
            ExportProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void Restore_OnClick(object sender, RoutedEventArgs e)
    {
        RestoreActionButton.IsEnabled = false;
        RestoreCandidate? candidate = null;
        try
        {
            var picker = new OpenFileDialog
            {
                Title = TranslationSource.Instance["Settings_SelectBackup"],
                Filter = TranslationSource.Instance["Settings_SelectBackupFilter"],
                InitialDirectory = _backups.BackupDirectory,
                CheckFileExists = true,
            };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            ShowStatus("Settings_InspectRunning");
            candidate = await Task.Run(() => _backups.InspectRestore(picker.FileName));
            CurrentDatabaseInfo current = await Task.Run(_backups.InspectCurrent);
            var confirmation = new RestoreWindow(current, candidate.Manifest) { Owner = Window.GetWindow(this) };
            if (confirmation.ShowDialog() != true) { ShowStatus("Settings_RestoreCancelled"); return; }
            ShowStatus("Settings_RestoreRunning");
            var app = (App)Application.Current;
            await app.WaitForStartupMaintenanceAsync();
            await Task.Run(() => _backups.ApplyRestore(candidate));
            app.SkipCloseBackup();
            MessageBox.Show(TranslationSource.Instance["Settings_RestoreRestart"],
                TranslationSource.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Information);
            await app.RequestShutdownAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally
        {
            if (candidate is not null) _backups.DiscardRestoreCandidate(candidate);
            RestoreActionButton.IsEnabled = true;
        }
    }

    private async void ChangePassword_OnClick(object sender, RoutedEventArgs e)
    {
        if (NewPassword.Password.Length < 8) { ShowStatus("Setup_ErrorPasswordLength"); return; }
        if (NewPassword.Password != ConfirmPassword.Password) { ShowStatus("Setup_ErrorPasswordMismatch"); return; }
        try
        {
            ChangePasswordButton.IsEnabled = false;
            await _authentication.ChangePasswordAsync(CurrentPassword.Password, NewPassword.Password);
            CurrentPassword.Clear(); NewPassword.Clear(); ConfirmPassword.Clear();
            ShowStatus("Settings_PasswordSaved");
        }
        catch (DomainException exception) when (exception.Code == ErrorCode.WrongPassword)
        {
            ShowStatus("Error_WrongPassword");
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ChangePasswordButton.IsEnabled = true; }
    }

    private void ShowStatus(string key)
    {
        StatusText.Foreground = Brushes.ForestGreen;
        StatusText.Text = TranslationSource.Instance[key];
    }

    private void ShowError(Exception exception)
    {
        StatusText.Foreground = Brushes.Crimson;
        StatusText.Text = UserError.Message(exception);
    }
}
