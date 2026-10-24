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
    private int _searchVersion;

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
        int version = ++_searchVersion;
        try
        {
            IReadOnlyList<CustomerItem> customers = await _sales.SearchCustomersAsync(InputNormalizer.Normalize(SearchBox.Text));
            if (version != _searchVersion) return;
            var visible = customers.ToList();
            if (_currentCustomerId is long id && visible.All(customer => customer.Id != id))
            {
                CustomerItem? selected = await _sales.GetCustomerAsync(id);
                if (version != _searchVersion) return;
                if (selected is not null) visible.Insert(0, selected);
            }
            CustomersGrid.ItemsSource = visible;
            CustomersGrid.SelectedItem = visible.FirstOrDefault(customer => customer.Id == _currentCustomerId);
            Summary.Clear();
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
    }

    private async void SearchBox_OnChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        int version = ++_searchVersion;
        await Task.Delay(180);
        if (version == _searchVersion) await SearchAsync();
    }

    private async void Search_OnClick(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void Create_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        string? name = form.Text(NameField, NameBox.Text, required: true, maxLength: 120);
        string? phone = form.Text(PhoneField, PhoneBox.Text, maxLength: 20);
        string? gstin = form.Text(GstinField, GstinBox.Text, maxLength: 15)?.ToUpperInvariant();
        if (gstin is { Length: not 15 }) form.AddKey(GstinField, "Purchasing_GstinHint");
        if (!form.Apply(Fields, Summary)) return;
        try
        {
            SelectedCustomerId = await _sales.CreateCustomerAsync(name!, phone, gstin);
            DialogResult = true;
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
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
            FormValidator.Clear(Fields);
            Summary.ShowMessage(TranslationSource.Instance["Sales_SelectCustomer"]);
            return;
        }
        SelectedCustomerId = customer.Id;
        DialogResult = true;
    }

    private void CustomersGrid_OnDoubleClick(object sender, MouseButtonEventArgs e) => Use_OnClick(sender, e);
}
