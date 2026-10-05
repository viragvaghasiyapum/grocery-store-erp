using System.Windows;
using System.Windows.Input;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

public partial class SavedBillsWindow : Window
{
    public SavedBillsWindow(IReadOnlyList<HeldSale> bills)
    {
        InitializeComponent();
        BillsGrid.ItemsSource = bills;
        BillsGrid.SelectedIndex = bills.Count > 0 ? 0 : -1;
    }

    public HeldSale? SelectedBill => BillsGrid.SelectedItem as HeldSale;

    private void Select_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedBill is not null) DialogResult = true;
    }

    private void BillsGrid_OnDoubleClick(object sender, MouseButtonEventArgs e) => Select_OnClick(sender, e);
}
