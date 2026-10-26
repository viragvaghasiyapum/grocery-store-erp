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
using GroceryErp.Sales;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Win32;
using Serilog;
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
    private readonly ReceiptPrinter? _printer;
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<NavigationViewItem, string> _moduleNavigation = [];
    private readonly InfoBadge _expiryInfoBadge = new() { Value = "0", Visibility = Visibility.Collapsed };
    private DateTime _lastActivity;
    private DateTime? _lastScan;
    private PrinterState _printerState = PrinterState.Checking;
    private bool _checkingPrinter;

    private enum PrinterState { Checking, Ready, Unavailable, Review }

    public ShellWindow(
        ShellViewModel viewModel,
        AuthenticationService? authentication = null,
        ISettings? settings = null,
        IClock? clock = null,
        MoneyOutService? moneyOut = null,
        UsbUpdateService? updates = null,
        BackupService? backups = null,
        IReadOnlyList<IModule>? modules = null,
        ReceiptPrinter? printer = null)
    {
        InitializeComponent();
        foreach (NavItem item in (modules ?? App.CreateModules()).SelectMany(static module => module.Navigation)
                     .OrderBy(static item => item.Order))
        {
            var view = new NavigationViewItem
            {
                Content = TranslationSource.Instance[item.TitleKey],
                ToolTip = TranslationSource.Instance[item.TitleKey],
                Icon = new SymbolIcon { Symbol = Enum.Parse<SymbolRegular>(item.Icon) },
                TargetPageType = item.PageType,
            };
            if (item.BadgeProvider == "expiry") view.InfoBadge = _expiryInfoBadge;
            RootNavigation.MenuItems.Add(view);
            _moduleNavigation.Add(view, item.TitleKey);
        }
        DataContext = viewModel;
        _authentication = authentication;
        _settings = settings;
        _clock = clock;
        _updates = updates;
        _backups = backups;
        _printer = printer;
        _deviceTimer.Tick += async (_, _) =>
        {
            RenderScannerStatus();
            await RefreshPrinterStatusAsync();
        };
        RenderPrinterStatus();
        RenderScannerStatus();
        TranslationSource.Instance.PropertyChanged += TranslationChanged;
        _lastActivity = clock?.Now ?? DateTime.MinValue;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, _) => TouchActivity();
        // The title bar holds the brand and every status control in one row; a narrow window
        // drops the tagline first so the status controls keep their room.
        SizeChanged += (_, args) =>
            StoreTagline.Visibility = args.NewSize.Width >= 1280 ? Visibility.Visible : Visibility.Collapsed;
        if (authentication is not null && settings is not null && clock is not null)
        {
            _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
                (_, _) => CheckIdle(), Dispatcher);
        }
        Loaded += async (_, _) =>
        {
            RefreshBackupStatus();
            RootNavigation.Navigate(typeof(DashboardPage));
            await RefreshPrinterStatusAsync();
            _deviceTimer.Start();
            if (moneyOut is not null) SetExpiryBadge(await moneyOut.GetActionableExpiryCountAsync());
        };
        Closed += (_, _) =>
        {
            _idleTimer?.Stop();
            _deviceTimer.Stop();
            TranslationSource.Instance.PropertyChanged -= TranslationChanged;
        };
        Activated += async (_, _) =>
        {
            RefreshBackupStatus();
            RenderScannerStatus();
            await RefreshPrinterStatusAsync();
        };
    }

    private void TranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]")
        {
            RefreshBackupStatus();
            RenderPrinterStatus();
            RenderScannerStatus();
            foreach ((NavigationViewItem view, string key) in _moduleNavigation)
            {
                view.Content = TranslationSource.Instance[key];
                view.ToolTip = TranslationSource.Instance[key];
            }
        }
    }

    internal void RefreshBackupStatus()
    {
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

    private async Task RefreshPrinterStatusAsync()
    {
        if (_checkingPrinter || _printer is null) return;
        _checkingPrinter = true;
        try
        {
            _printerState = _printer.HasFailedJobs ? PrinterState.Review
                : await _printer.IsReadyAsync().WaitAsync(TimeSpan.FromSeconds(3))
                    ? PrinterState.Ready : PrinterState.Unavailable;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Receipt printer status check failed");
            _printerState = PrinterState.Unavailable;
        }
        finally
        {
            _checkingPrinter = false;
            RenderPrinterStatus();
        }
    }

    private void RenderPrinterStatus()
    {
        string key = _printerState switch
        {
            PrinterState.Ready => "Shell_PrinterReady",
            PrinterState.Unavailable => "Shell_PrinterUnavailable",
            PrinterState.Review => "Shell_PrinterReview",
            _ => "Shell_PrinterChecking",
        };
        PrinterStatusText.Text = TranslationSource.Instance[key];
        PrinterStatusText.Foreground = _printerState switch
        {
            PrinterState.Ready => Brushes.ForestGreen,
            PrinterState.Review or PrinterState.Unavailable => Brushes.Firebrick,
            _ => Brushes.DarkGoldenrod,
        };
        PrinterStatusBadge.ToolTip = TranslationSource.Instance[_printerState == PrinterState.Review
            ? "Sales_PrinterJobFailed" : _printerState == PrinterState.Ready
                ? "Sales_PrinterReady" : _printerState == PrinterState.Checking
                    ? "Shell_PrinterChecking" : "Sales_PrinterUnavailable"];
    }

    internal void RecordScannerActivity()
    {
        _lastScan = _clock?.Now ?? App.ClockNow;
        RenderScannerStatus();
    }

    internal void RefreshScannerStatus() => RenderScannerStatus();

    private void RenderScannerStatus()
    {
        string port = _settings?.Current.ScannerComPort ?? "";
        if (ScannerComPorts.IsComPort(port))
        {
            ScannerComPort? device = ScannerComPorts.GetPorts()
                .FirstOrDefault(device => string.Equals(device.Name, port, StringComparison.OrdinalIgnoreCase));
            string key = device is null ? "Shell_ScannerDisconnected"
                : device.IsBluetoothVirtual ? "Shell_ScannerVirtual" : "Shell_ScannerConnected";
            ScannerStatusText.Text = TranslationSource.Instance.Format(
                key, port);
            ScannerStatusText.Foreground = device is null ? Brushes.Firebrick
                : device.IsBluetoothVirtual ? Brushes.DarkGoldenrod : Brushes.ForestGreen;
            ScannerStatusBadge.ToolTip = TranslationSource.Instance.Format(
                key + "Help", port);
        }
        else if (_lastScan is { } lastScan)
        {
            string time = lastScan.ToString("HH:mm", TranslationSource.FormatCulture);
            string dateTime = lastScan.ToString("yyyy-MM-dd HH:mm", TranslationSource.FormatCulture);
            ScannerStatusText.Text = TranslationSource.Instance.Format("Shell_ScannerLastScan", time);
            ScannerStatusText.Foreground = Brushes.ForestGreen;
            ScannerStatusBadge.ToolTip = TranslationSource.Instance.Format("Shell_ScannerLastScanHelp", dateTime);
        }
        else
        {
            ScannerStatusText.Text = TranslationSource.Instance["Shell_ScannerSelectPort"];
            ScannerStatusText.Foreground = Brushes.DarkGoldenrod;
            ScannerStatusBadge.ToolTip = TranslationSource.Instance["Shell_ScannerSelectPortHelp"];
        }
    }

    private void BackupStatus_OnClick(object sender, RoutedEventArgs e) => RootNavigation.Navigate(typeof(SettingsPage));

    // A zero badge is noise; it shows only while expired or expiring stock needs action.
    internal void SetExpiryBadge(int count)
    {
        _expiryInfoBadge.Value = count.ToString(CultureInfo.InvariantCulture);
        _expiryInfoBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    internal void OpenReports() => RootNavigation.Navigate(typeof(ReportsPage));
    internal void OpenProducts() => RootNavigation.Navigate(typeof(ProductsPage));
    internal void OpenVendors() => RootNavigation.Navigate(typeof(VendorsPage));
    internal void OpenSettings() => RootNavigation.Navigate(typeof(SettingsPage));
    internal void ShowMaintenanceWarning() => MaintenanceWarning.Visibility = Visibility.Visible;

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
            PendingUsbUpdate? pending = await _updates.CheckAsync(picker.FolderName);
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
            {
                await ((App)Application.Current).WaitForStartupMaintenanceAsync();
                await _updates.DownloadAndRestartAsync(pending);
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "USB update failed");
            WinMessageBox.Show(TranslationSource.Instance["Settings_ActionFailed"], TranslationSource.Instance["App_Title"],
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

    internal bool IsIdleFor(TimeSpan interval) => _clock is null || _clock.Now - _lastActivity >= interval;

    private void CheckIdle()
    {
        if (LockOverlay.Visibility == Visibility.Visible || _clock is null || _settings is null) return;
        if ((_clock.Now - _lastActivity).TotalMinutes >= _settings.Current.IdleLockMinutes) Lock();
    }

    private void Lock()
    {
        if (_authentication is null) return;
        LockOverlay.Visibility = Visibility.Visible;
        // The status controls sit in the title bar, above the overlay; hidden, they cannot be used.
        StatusBar.Visibility = Visibility.Hidden;
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
            StatusBar.Visibility = Visibility.Visible;
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

