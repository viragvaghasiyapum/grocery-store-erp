using System.Windows;
using System.Windows.Input;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Security;
using Wpf.Ui.Controls;

namespace GroceryErp.App.Presentation;

public partial class LoginWindow : FluentWindow
{
    private readonly AuthenticationService _authentication;
    public LoginWindow(AuthenticationService authentication) { _authentication = authentication; InitializeComponent(); Loaded += (_, _) => PasswordBox.Focus(); }

    private async void Login_OnClick(object sender, RoutedEventArgs e) => await LoginAsync();
    private async void PasswordBox_OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await LoginAsync(); }

    private async Task LoginAsync()
    {
        try
        {
            IsEnabled = false;
            await _authentication.LoginAsync(UsernameBox.Text, PasswordBox.Password);
            DialogResult = true;
        }
        catch (DomainException exception) when (exception.Code == ErrorCode.WrongPassword)
        {
            ErrorText.Text = GroceryErp.Localization.TranslationSource.Instance["Error_WrongPassword"];
            PasswordBox.Clear(); PasswordBox.Focus();
        }
        finally { IsEnabled = true; }
    }
}
