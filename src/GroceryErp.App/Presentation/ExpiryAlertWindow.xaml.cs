using System.Globalization;
using System.Windows;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class ExpiryAlertWindow : Window
{
    public ExpiryAlertWindow(int count)
    {
        InitializeComponent();
        AlertText.Text = string.Format(CultureInfo.GetCultureInfo("en-IN"),
            TranslationSource.Instance["MoneyOut_ExpiryAlertBody"], count);
    }

    public bool ViewRequested { get; private set; }

    private void Later_OnClick(object sender, RoutedEventArgs e) => Close();

    private void View_OnClick(object sender, RoutedEventArgs e)
    {
        ViewRequested = true;
        Close();
    }
}
