using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Wpf.Ui.Controls;

namespace GroceryErp.App.Presentation;

public partial class ShellWindow : FluentWindow
{
    private readonly AuthenticationService? _authentication;
    private readonly ISettings? _settings;
    private readonly IClock? _clock;
    private readonly DispatcherTimer? _idleTimer;
    private DateTime _lastActivity;

    public ShellWindow(
        ShellViewModel viewModel,
        AuthenticationService? authentication = null,
        ISettings? settings = null,
        IClock? clock = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _authentication = authentication;
        _settings = settings;
        _clock = clock;
        _lastActivity = clock?.Now ?? DateTime.MinValue;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, _) => TouchActivity();
        if (authentication is not null && settings is not null && clock is not null)
        {
            _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
                (_, _) => CheckIdle(), Dispatcher);
        }
        Loaded += (_, _) => RootNavigation.Navigate(typeof(DemoPage));
        Closed += (_, _) => _idleTimer?.Stop();
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

