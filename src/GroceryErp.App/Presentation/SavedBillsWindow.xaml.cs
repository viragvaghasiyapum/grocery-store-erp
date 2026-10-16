using System.Windows;
using System.Windows.Input;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

public partial class SavedBillsWindow : Window
{
    private int PageSize => int.TryParse((PageSizeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString(), out int size) ? size : 50;
    private readonly SalesService? _sales;
    private readonly bool _includeDrafts;
    private readonly long? _excludeId;
    private readonly Stack<(string? CreatedAt, long? Id)> _previous = new();
    private (string? CreatedAt, long? Id) _cursor;
    private SavedBillPage? _page;
    private bool _loading;

    public SavedBillsWindow(IReadOnlyList<HeldSale> bills)
    {
        InitializeComponent();
        PageSizeBox.Visibility = Visibility.Collapsed;
        BillsGrid.ItemsSource = bills;
        BillsGrid.SelectedIndex = bills.Count > 0 ? 0 : -1;
    }

    public SavedBillsWindow(SalesService sales, bool includeDrafts, long? excludeId)
    {
        InitializeComponent();
        _sales = sales;
        _includeDrafts = includeDrafts;
        _excludeId = excludeId;
        Loaded += async (_, _) => await LoadPageAsync(null, null);
    }

    public HeldSale? SelectedBill => BillsGrid.SelectedItem as HeldSale;

    private void Select_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedBill is not null) DialogResult = true;
    }

    private void BillsGrid_OnDoubleClick(object sender, MouseButtonEventArgs e) => Select_OnClick(sender, e);

    private async Task LoadPageAsync(string? createdAt, long? id)
    {
        if (_sales is null || _loading) return;
        _loading = true;
        try
        {
            SavedBillPage page = await _sales.GetSavedBillPageAsync(_includeDrafts, SearchBox.Text,
                createdAt, id, _excludeId, PageSize);
            _page = page;
            _cursor = (createdAt, id);
            BillsGrid.ItemsSource = page.Items;
            BillsGrid.SelectedIndex = page.Items.Count > 0 ? 0 : -1;
            StatusText.Text = page.Items.Count == 0 ? "No saved bills found." : string.Empty;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _loading = false;
            PreviousButton.IsEnabled = _previous.Count > 0;
            NextButton.IsEnabled = _page?.HasMore == true;
        }
    }

    private async void Search_OnClick(object sender, RoutedEventArgs e)
    {
        _previous.Clear();
        await LoadPageAsync(null, null);
    }

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _page is not { HasMore: true, NextCreatedAt: not null, NextId: not null } page) return;
        (string? CreatedAt, long? Id) previous = _cursor;
        await LoadPageAsync(page.NextCreatedAt, page.NextId);
        if (_cursor == (page.NextCreatedAt, page.NextId)) _previous.Push(previous);
        PreviousButton.IsEnabled = _previous.Count > 0;
    }

    private async void Previous_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _previous.Count == 0) return;
        (string? CreatedAt, long? Id) previous = _previous.Peek();
        await LoadPageAsync(previous.CreatedAt, previous.Id);
        if (_cursor == previous) _previous.Pop();
        PreviousButton.IsEnabled = _previous.Count > 0;
    }

    private async void PageSize_OnChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _sales is null) return;
        _previous.Clear();
        await LoadPageAsync(null, null);
    }
}
