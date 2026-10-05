using System.Windows;
using System.Windows.Input;
using GroceryErp.Core;
using GroceryErp.Localization;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

public partial class CustomerWindow : Window
{
    private readonly SalesService _sales;
    private readonly long? _currentCustomerId;

    public CustomerWindow(SalesService sales, long? currentCustomerId, bool loadOnShow = true)
    {
        InitializeComponent();
        _sales = sales;
        _currentCustomerId = currentCustomerId;
        if (loadOnShow) Loaded += async (_, _) => await SearchAsync();
    }

    public long? SelectedCustomerId { get; private set; }

    private async Task SearchAsync()
    {
        try
        {
            IReadOnlyList<CustomerItem> customers = await _sales.SearchCustomersAsync(InputNormalizer.Normalize(SearchBox.Text));
            CustomersGrid.ItemsSource = customers;
            CustomersGrid.SelectedItem = customers.FirstOrDefault(customer => customer.Id == _currentCustomerId);
            ErrorText.Text = "";
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }

    private async void Search_OnClick(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void Create_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            long id = await _sales.CreateCustomerAsync(NameBox.Text,
                InputNormalizer.Normalize(PhoneBox.Text), InputNormalizer.Normalize(GstinBox.Text).ToUpperInvariant());
            SelectedCustomerId = id;
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }

    private void WalkIn_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedCustomerId = null;
        DialogResult = true;
    }

    private void Use_OnClick(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not CustomerItem customer)
        {
            ErrorText.Text = TranslationSource.Instance["Sales_SelectCustomer"];
            return;
        }
        SelectedCustomerId = customer.Id;
        DialogResult = true;
    }

    private void CustomersGrid_OnDoubleClick(object sender, MouseButtonEventArgs e) => Use_OnClick(sender, e);
}
