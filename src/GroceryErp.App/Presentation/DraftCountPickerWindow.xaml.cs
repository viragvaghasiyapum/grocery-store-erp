using System.Windows;
using System.Windows.Input;
using GroceryErp.Inventory;

namespace GroceryErp.App.Presentation;

public partial class DraftCountPickerWindow : Window
{
    private readonly InventoryService _inventory;
    private readonly Stack<long?> _previous = new();
    private long? _beforeId;
    private DraftCountPage? _page;
    private bool _loading;
    public long? SelectedCountId { get; private set; }

    public DraftCountPickerWindow(InventoryService inventory)
    {
        InitializeComponent();
        _inventory = inventory;
        Loaded += async (_, _) => await LoadPageAsync();
    }

    private async Task<bool> LoadPageAsync()
    {
        _loading = true;
        PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        try
        {
            _page = await _inventory.GetDraftCountPageAsync(_beforeId);
            DraftGrid.ItemsSource = _page.Items;
            PreviousButton.IsEnabled = _previous.Count > 0;
            NextButton.IsEnabled = _page.HasMore;
            ErrorText.Text = _page.Items.Count == 0 && _previous.Count == 0
                ? GroceryErp.Localization.TranslationSource.Instance["Stock_NoDraftCount"] : "";
            return true;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); return false; }
        finally { _loading = false; }
    }

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _page is not { HasMore: true, NextId: long next }) return;
        long? previous = _beforeId;
        _beforeId = next;
        if (await LoadPageAsync()) _previous.Push(previous);
        else _beforeId = previous;
        PreviousButton.IsEnabled = _previous.Count > 0;
    }

    private async void Previous_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _previous.Count == 0) return;
        long? current = _beforeId;
        _beforeId = _previous.Peek();
        if (await LoadPageAsync()) _previous.Pop();
        else _beforeId = current;
    }

    private void DraftGrid_OnDoubleClick(object sender, MouseButtonEventArgs e) => Open();
    private void Open_OnClick(object sender, RoutedEventArgs e) => Open();
    private void Open()
    {
        if (DraftGrid.SelectedItem is not StockCountSummary draft) return;
        SelectedCountId = draft.Id;
        DialogResult = true;
    }
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
