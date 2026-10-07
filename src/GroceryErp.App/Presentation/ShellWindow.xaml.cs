using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Localization;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using WinMessageBox = System.Windows.MessageBox;
using WinMessageBoxButton = System.Windows.MessageBoxButton;
using WinMessageBoxResult = System.Windows.MessageBoxResult;

namespace GroceryErp.App.Presentation;

public partial class ShellWindow : FluentWindow
{
    private readonly AuthenticationService? _authentication;
    private readonly ISettings? _settings;
    private readonly IClock? _clock;
    private readonly DispatcherTimer? _idleTimer;
    private readonly UsbUpdateService? _updates;
    private readonly BackupService? _backups;
    private DateTime _lastActivity;

    public ShellWindow(
        ShellViewModel viewModel,
        AuthenticationService? authentication = null,
        ISettings? settings = null,
        IClock? clock = null,
        MoneyOutService? moneyOut = null,
        SettingsService? settingsService = null,
        UsbUpdateService? updates = null,
        BackupService? backups = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _authentication = authentication;
        _settings = settings;
        _clock = clock;
        _updates = updates;
        _backups = backups;
        TranslationSource.Instance.PropertyChanged += TranslationChanged;
        _lastActivity = clock?.Now ?? DateTime.MinValue;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, _) => TouchActivity();
        if (authentication is not null && settings is not null && clock is not null)
        {
            _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
                (_, _) => CheckIdle(), Dispatcher);
        }
        Loaded += async (_, _) =>
        {
            RefreshBackupStatus();
            RootNavigation.Navigate(typeof(DashboardPage));
            if (moneyOut is not null)
            {
                IReadOnlyList<ExpiryLot> actionable = await moneyOut.GetExpiryAsync();
                int count = actionable.Count(static lot => lot.Bucket is "EXPIRED" or "CRITICAL");
                ExpiryInfoBadge.Value = count.ToString(CultureInfo.InvariantCulture);
                string today = clock?.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
                string lastPopup = settings?.Current.Values.GetValueOrDefault("expiry.last_popup_date", "") ?? "";
                if (count > 0 && settings?.Current.ExpiryPopupOnLogin == true && settingsService is not null && lastPopup != today)
                {
                    var alert = new ExpiryAlertWindow(count) { Owner = this };
                    alert.ShowDialog();
                    await settingsService.SaveAsync(new Dictionary<string, string> { ["expiry.last_popup_date"] = today });
                    if (alert.ViewRequested) RootNavigation.Navigate(typeof(MoneyOutPage));
                }
            }
        };
        Closed += (_, _) =>
        {
            _idleTimer?.Stop();
            TranslationSource.Instance.PropertyChanged -= TranslationChanged;
        };
        Activated += (_, _) => RefreshBackupStatus();
    }

    private void TranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") RefreshBackupStatus();
    }

    internal void RefreshBackupStatus()
    {
        if (_settings is not null) StoreHeading.Text = string.IsNullOrWhiteSpace(_settings.Current.StoreName)
            ? TranslationSource.Instance["App_Title"] : _settings.Current.StoreName;
        if (_clock is not null) BusinessDate.Text = _clock.BusinessDate.ToString("dd MMM yyyy", TranslationSource.FormatCulture);
        if (_backups is null) return;
        BackupHealth health = _backups.GetHealth();
        bool current = health.BackedUpToday;
        string key = current && health.UsbAgeDays < 7 ? "Settings_StatusReady"
            : health.UsbAgeDays < 14 && current ? "Settings_StatusDue" : "Settings_StatusUrgent";
        BackupStatusButton.Content = TranslationSource.Instance[key];
        BackupStatusButton.Foreground = current && health.UsbAgeDays < 7 ? Brushes.ForestGreen
            : health.UsbAgeDays < 14 && current ? Brushes.DarkGoldenrod : Brushes.Firebrick;
        BackupStatusButton.ToolTip = TranslationSource.Instance["Settings_BackupTitle"];
    }

    private void BackupStatus_OnClick(object sender, RoutedEventArgs e) => RootNavigation.Navigate(typeof(SettingsPage));

    internal void SetExpiryBadge(int count) => ExpiryInfoBadge.Value = count.ToString(CultureInfo.InvariantCulture);
    internal void OpenReports() => RootNavigation.Navigate(typeof(ReportsPage));
    internal void OpenSettings() => RootNavigation.Navigate(typeof(SettingsPage));

    private async void Update_OnClick(object sender, RoutedEventArgs e)
    {
        if (_updates is null) return;
        var picker = new OpenFolderDialog
        {
            Title = TranslationSource.Instance["Settings_Update_SelectFolder"],
            InitialDirectory = _settings?.Current.Values.GetValueOrDefault("update.folder", ""),
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            UpdateButton.IsEnabled = false;
            PendingUsbUpdate? pending = await UsbUpdateService.CheckAsync(picker.FolderName);
            if (pending is null)
            {
                WinMessageBox.Show(TranslationSource.Instance["Settings_Update_None"],
                    TranslationSource.Instance["App_Title"], WinMessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            WinMessageBoxResult install = WinMessageBox.Show(
                TranslationSource.Instance.Format("Settings_Update_Confirm", pending.Version),
                TranslationSource.Instance["App_Title"], WinMessageBoxButton.YesNo, MessageBoxImage.Question);
            if (install == WinMessageBoxResult.Yes)
                await _updates.DownloadAndRestartAsync(pending, picker.FolderName);
        }
        catch (Exception exception)
        {
            WinMessageBox.Show(exception.Message, TranslationSource.Instance["App_Title"],
                WinMessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { UpdateButton.IsEnabled = true; }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            Lock();
            e.Handled = true;
            return;
        }
        TouchActivity();
    }

    private void TouchActivity() => _lastActivity = _clock?.Now ?? _lastActivity;

    private void CheckIdle()
    {
        if (LockOverlay.Visibility == Visibility.Visible || _clock is null || _settings is null) return;
        if ((_clock.Now - _lastActivity).TotalMinutes >= _settings.Current.IdleLockMinutes) Lock();
    }

    private void Lock()
    {
        if (_authentication is null) return;
        LockOverlay.Visibility = Visibility.Visible;
        UnlockPassword.Clear();
        LockError.Text = "";
        Dispatcher.BeginInvoke(UnlockPassword.Focus);
    }

    private void Lock_OnClick(object sender, RoutedEventArgs e) => Lock();

    private async void Unlock_OnClick(object sender, RoutedEventArgs e) => await UnlockAsync();

    private async void UnlockPassword_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await UnlockAsync();
    }

    private async Task UnlockAsync()
    {
        if (_authentication is null) return;
        try
        {
            UnlockPassword.IsEnabled = false;
            await _authentication.UnlockAsync(UnlockPassword.Password);
            LockOverlay.Visibility = Visibility.Collapsed;
            TouchActivity();
        }
        catch (DomainException exception) when (exception.Code == ErrorCode.WrongPassword)
        {
            LockError.Text = TranslationSource.Instance["Error_WrongPassword"];
            UnlockPassword.Clear();
        }
        finally { UnlockPassword.IsEnabled = true; }
    }
}

