using Wpf.Ui.Controls;

namespace GroceryErp.App.Presentation;

public partial class ShellWindow : FluentWindow
{
    public ShellWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => RootNavigation.Navigate(typeof(DemoPage));
    }
}

