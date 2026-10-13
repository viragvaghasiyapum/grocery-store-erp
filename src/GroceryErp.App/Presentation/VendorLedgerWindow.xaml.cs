using System.Windows;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

public partial class VendorLedgerWindow : Window
{
    private readonly PurchasingService _purchasing;
    private readonly long _vendorId;
    private readonly LedgerTabState[] _ledgerTabs = Enumerable.Range(0, 4).Select(_ => new LedgerTabState()).ToArray();
    private readonly Stack<(DateOnly? Date, long? Id)> _lotBack = new();
    private (DateOnly? Date, long? Id) _lotCursor;
    private VendorLotPage? _lotPage;
    private bool _loading;
    private static readonly string?[] LedgerKinds = ["PURCHASE", "PAYMENT", "VENDOR_RETURN", null];

    private sealed class LedgerTabState
    {
        public Stack<(DateOnly? Date, string? Kind, long? Id)> Back { get; } = new();
        public (DateOnly? Date, string? Kind, long? Id) Cursor;
        public VendorLedgerPage? Page;
    }

    public VendorLedgerWindow(PurchasingService purchasing, long vendorId, string vendorName, bool loadOnShow = true)
    {
        InitializeComponent();
        _purchasing = purchasing;
        _vendorId = vendorId;
        VendorTitle.Text = vendorName;
        if (loadOnShow) Loaded += LoadAsync;
    }

    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadLedgerAsync(0);
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private async Task LoadLedgerAsync(int tab)
    {
        _loading = true;
        UpdateButtons();
        try
        {
            LedgerTabState state = _ledgerTabs[tab];
            VendorLedgerPage page = await _purchasing.GetVendorLedgerPageAsync(_vendorId,
                state.Cursor.Date, state.Cursor.Kind, state.Cursor.Id, filterKind: LedgerKinds[tab]);
            state.Page = page;
            switch (tab)
            {
                case 0: PurchasesGrid.ItemsSource = page.Items; break;
                case 1: PaymentsGrid.ItemsSource = page.Items; break;
                case 2: ReturnsGrid.ItemsSource = page.Items; break;
                case 3: LedgerGrid.ItemsSource = page.Items; break;
            }
            UpdateButtons();
        }
        finally { _loading = false; UpdateButtons(); }
    }

    private async Task LoadLotsAsync()
    {
        _loading = true;
        UpdateButtons();
        try
        {
            _lotPage = await _purchasing.GetVendorLotPageAsync(_vendorId, _lotCursor.Date, _lotCursor.Id);
            LotsGrid.ItemsSource = _lotPage.Items;
            UpdateButtons();
        }
        finally { _loading = false; UpdateButtons(); }
    }

    private async void HistoryTabs_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.Source != HistoryTabs || !IsLoaded) return;
        try
        {
            int tab = HistoryTabs.SelectedIndex;
            if (tab == 4 && _lotPage is null) await LoadLotsAsync();
            else if (tab is >= 0 and < 4 && _ledgerTabs[tab].Page is null) await LoadLedgerAsync(tab);
            UpdateButtons();
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private void UpdateButtons()
    {
        if (PreviousPageButton is null || NextPageButton is null) return;
        int tab = HistoryTabs.SelectedIndex;
        bool lots = tab == 4;
        LedgerTabState? state = tab is >= 0 and < 4 ? _ledgerTabs[tab] : null;
        PreviousPageButton.IsEnabled = !_loading && (lots ? _lotBack.Count > 0 : state?.Back.Count > 0);
        NextPageButton.IsEnabled = !_loading && (lots ? _lotPage?.HasMore == true : state?.Page?.HasMore == true);
    }

    private async void NextPage_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (HistoryTabs.SelectedIndex == 4 && _lotPage is { HasMore: true } lots)
            {
                var previous = _lotCursor;
                _lotCursor = (lots.NextDate, lots.NextId);
                try { await LoadLotsAsync(); _lotBack.Push(previous); }
                catch { _lotCursor = previous; throw; }
            }
            else if (HistoryTabs.SelectedIndex is >= 0 and < 4 &&
                _ledgerTabs[HistoryTabs.SelectedIndex] is { Page: { HasMore: true } ledger } state)
            {
                var previous = state.Cursor;
                state.Cursor = (ledger.NextDate, ledger.NextKind, ledger.NextId);
                try { await LoadLedgerAsync(HistoryTabs.SelectedIndex); state.Back.Push(previous); }
                catch { state.Cursor = previous; throw; }
            }
            UpdateButtons();
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private async void PreviousPage_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (HistoryTabs.SelectedIndex == 4 && _lotBack.Count > 0)
            {
                var current = _lotCursor;
                _lotCursor = _lotBack.Peek();
                try { await LoadLotsAsync(); _lotBack.Pop(); }
                catch { _lotCursor = current; throw; }
            }
            else if (HistoryTabs.SelectedIndex is >= 0 and < 4 &&
                _ledgerTabs[HistoryTabs.SelectedIndex] is { } state && state.Back.Count > 0)
            {
                var current = state.Cursor;
                state.Cursor = state.Back.Peek();
                try { await LoadLedgerAsync(HistoryTabs.SelectedIndex); state.Back.Pop(); }
                catch { state.Cursor = current; throw; }
            }
            UpdateButtons();
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
