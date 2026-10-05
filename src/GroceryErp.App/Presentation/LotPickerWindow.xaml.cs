using System.Windows;
using System.Windows.Input;
using GroceryErp.Inventory;

namespace GroceryErp.App.Presentation;

public partial class LotPickerWindow : Window
{
    public LotPickerWindow(IReadOnlyList<LotItem> lots, long currentLotId)
    {
        InitializeComponent();
        LotsGrid.ItemsSource = lots;
        LotsGrid.SelectedItem = lots.FirstOrDefault(lot => lot.Id == currentLotId) ??
            (lots.Count > 0 ? lots[0] : null);
    }

    public LotItem? SelectedLot => LotsGrid.SelectedItem as LotItem;

    private void Select_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedLot is not null) DialogResult = true;
    }

    private void LotsGrid_OnDoubleClick(object sender, MouseButtonEventArgs e) => Select_OnClick(sender, e);
}
