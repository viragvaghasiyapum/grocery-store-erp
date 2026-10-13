using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GroceryErp.Inventory;

namespace GroceryErp.App.Presentation;

public partial class LotPickerWindow : Window
{
    private readonly InventoryService? _inventory;
    private readonly long _productId;
    private readonly long _currentLotId;
    private readonly Stack<long?> _previous = new();
    private long? _beforeId;
    private LotPage? _page;
    private int _searchVersion;
    private bool _loading;

    public LotPickerWindow(InventoryService inventory, long productId, long currentLotId)
    {
        InitializeComponent();
        _inventory = inventory;
        _productId = productId;
        _currentLotId = currentLotId;
        Loaded += async (_, _) => await LoadPageAsync();
    }

    public LotPickerWindow(IReadOnlyList<LotItem> lots, long currentLotId)
    {
        InitializeComponent();
        _currentLotId = currentLotId;
        SearchBox.Visibility = Visibility.Collapsed;
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

    private async Task LoadPageAsync()
    {
        if (_inventory is null) return;
        int version = ++_searchVersion;
        _loading = true;
        PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        try
        {
            LotPage page = await _inventory.GetLotPageAsync(_productId, SearchBox.Text,
                _beforeId, availableOnly: true);
            if (version != _searchVersion) return;
            _page = page;
            var items = page.Items.ToList();
            if (_beforeId is null && SearchBox.Text.Length == 0 &&
                items.All(item => item.Id != _currentLotId))
            {
                LotItem? current = (await _inventory.GetLotsByIdsAsync([_currentLotId]))
                    .FirstOrDefault(item => item.ProductId == _productId && item.IsActive && item.OnHand.Value > 0);
                if (current is not null) items.Insert(0, current);
            }
            LotsGrid.ItemsSource = items;
            LotsGrid.SelectedItem = items.FirstOrDefault(item => item.Id == _currentLotId) ?? items.FirstOrDefault();
            PreviousButton.IsEnabled = _previous.Count > 0;
            NextButton.IsEnabled = page.HasMore;
        }
        catch (Exception exception) { System.Windows.MessageBox.Show(UserError.Message(exception)); }
        finally { _loading = false; }
    }

    private async void SearchBox_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        int version = ++_searchVersion;
        await Task.Delay(180);
        if (version != _searchVersion) return;
        _previous.Clear();
        _beforeId = null;
        await LoadPageAsync();
    }

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _page is not { HasMore: true, NextId: long next }) return;
        long? previous = _beforeId;
        _beforeId = next;
        await LoadPageAsync();
        _previous.Push(previous);
        PreviousButton.IsEnabled = true;
    }

    private async void Previous_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _previous.Count == 0) return;
        _beforeId = _previous.Pop();
        await LoadPageAsync();
    }
}
