using System.Windows.Controls;
using GroceryErp.Localization;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

public partial class VendorLedgerView : UserControl
{
    private const int PageSize = 50;
    private static readonly string?[] LedgerKinds = ["PURCHASE", "PAYMENT", "VENDOR_RETURN", null];
    private readonly LedgerTabState[] _ledgerTabs = Enumerable.Range(0, 4).Select(_ => new LedgerTabState()).ToArray();
    private readonly Stack<(DateOnly? Date, long? Id)> _lotBack = new();
    private PurchasingService? _purchasing;
    private long? _vendorId;
    private (DateOnly? Date, long? Id) _lotCursor;
    private VendorLotPage? _lotPage;
    private bool _loading;

    private sealed class LedgerTabState
    {
        public Stack<(DateOnly? Date, string? Kind, long? Id)> Back { get; } = new();
        public (DateOnly? Date, string? Kind, long? Id) Cursor;
        public VendorLedgerPage? Page;
    }

    public VendorLedgerView()
    {
        InitializeComponent();
        LedgerPager.PreviousRequested += (_, _) => Previous();
        LedgerPager.NextRequested += (_, _) => Next();
    }

    public void Initialize(PurchasingService purchasing) => _purchasing = purchasing;

    /// <summary>Shows a vendor from the first page of each tab, starting with the tab already open.</summary>
    public async Task LoadAsync(long vendorId)
    {
        _vendorId = vendorId;
        foreach (LedgerTabState state in _ledgerTabs) { state.Back.Clear(); state.Cursor = default; state.Page = null; }
        _lotBack.Clear();
        _lotCursor = default;
        _lotPage = null;
        foreach (DataGrid grid in new[] { PurchasesGrid, PaymentsGrid, ReturnsGrid, LedgerGrid, LotsGrid }) grid.ItemsSource = null;
        await LoadTabAsync();
    }

    private async Task LoadTabAsync()
    {
        try
        {
            int tab = HistoryTabs.SelectedIndex;
            if (tab == 4) await LoadLotsAsync();
            else if (tab is >= 0 and < 4) await LoadLedgerAsync(tab);
        }
        catch (Exception exception) { LedgerPager.Status = UserError.Message(exception); }
    }

    // A page that arrives after another vendor was opened is dropped.
    private async Task LoadLedgerAsync(int tab)
    {
        if (_purchasing is null || _vendorId is not long vendorId) return;
        _loading = true;
        UpdatePager();
        try
        {
            LedgerTabState state = _ledgerTabs[tab];
            VendorLedgerPage page = await _purchasing.GetVendorLedgerPageAsync(vendorId,
                state.Cursor.Date, state.Cursor.Kind, state.Cursor.Id, PageSize, filterKind: LedgerKinds[tab]);
            if (_vendorId != vendorId) return;
            state.Page = page;
            switch (tab)
            {
                case 0: PurchasesGrid.ItemsSource = page.Items; break;
                case 1: PaymentsGrid.ItemsSource = page.Items; break;
                case 2: ReturnsGrid.ItemsSource = page.Items; break;
                case 3: LedgerGrid.ItemsSource = page.Items; break;
            }
        }
        finally { _loading = false; UpdatePager(); }
    }

    private async Task LoadLotsAsync()
    {
        if (_purchasing is null || _vendorId is not long vendorId) return;
        _loading = true;
        UpdatePager();
        try
        {
            VendorLotPage page = await _purchasing.GetVendorLotPageAsync(vendorId, _lotCursor.Date, _lotCursor.Id, PageSize);
            if (_vendorId != vendorId) return;
            _lotPage = page;
            LotsGrid.ItemsSource = page.Items;
        }
        finally { _loading = false; UpdatePager(); }
    }

    private async void HistoryTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != HistoryTabs || _vendorId is null) return;
        int tab = HistoryTabs.SelectedIndex;
        if (tab == 4 ? _lotPage is null : tab is >= 0 and < 4 && _ledgerTabs[tab].Page is null) await LoadTabAsync();
        else UpdatePager();
    }

    private void UpdatePager()
    {
        int tab = HistoryTabs.SelectedIndex;
        bool lots = tab == 4;
        LedgerTabState? state = tab is >= 0 and < 4 ? _ledgerTabs[tab] : null;
        int back = lots ? _lotBack.Count : state?.Back.Count ?? 0;
        bool more = lots ? _lotPage?.HasMore == true : state?.Page?.HasMore == true;
        int rows = lots ? _lotPage?.Items.Count ?? 0 : state?.Page?.Items.Count ?? 0;
        LedgerPager.Update(back + 1, back > 0, more, _loading);
        if (!_loading) LedgerPager.Status = TranslationSource.Instance.Format("MoneyOut_RowCount", rows);
    }

    private async void Next()
    {
        if (_loading) return;
        // A page for a vendor no longer open must not move the open vendor's cursors.
        long? vendor = _vendorId;
        try
        {
            if (HistoryTabs.SelectedIndex == 4 && _lotPage is { HasMore: true } lots)
            {
                var previous = _lotCursor;
                _lotCursor = (lots.NextDate, lots.NextId);
                try { await LoadLotsAsync(); if (_vendorId == vendor) _lotBack.Push(previous); }
                catch { _lotCursor = previous; throw; }
            }
            else if (HistoryTabs.SelectedIndex is >= 0 and < 4 &&
                _ledgerTabs[HistoryTabs.SelectedIndex] is { Page: { HasMore: true } ledger } state)
            {
                var previous = state.Cursor;
                state.Cursor = (ledger.NextDate, ledger.NextKind, ledger.NextId);
                try { await LoadLedgerAsync(HistoryTabs.SelectedIndex); if (_vendorId == vendor) state.Back.Push(previous); }
                catch { state.Cursor = previous; throw; }
            }
            UpdatePager();
        }
        catch (Exception exception) { LedgerPager.Status = UserError.Message(exception); }
    }

    private async void Previous()
    {
        if (_loading) return;
        // A page for a vendor no longer open must not move the open vendor's cursors.
        long? vendor = _vendorId;
        try
        {
            if (HistoryTabs.SelectedIndex == 4 && _lotBack.Count > 0)
            {
                var current = _lotCursor;
                _lotCursor = _lotBack.Peek();
                try { await LoadLotsAsync(); if (_vendorId == vendor) _lotBack.Pop(); }
                catch { _lotCursor = current; throw; }
            }
            else if (HistoryTabs.SelectedIndex is >= 0 and < 4 &&
                _ledgerTabs[HistoryTabs.SelectedIndex] is { } state && state.Back.Count > 0)
            {
                var current = state.Cursor;
                state.Cursor = state.Back.Peek();
                try { await LoadLedgerAsync(HistoryTabs.SelectedIndex); if (_vendorId == vendor) state.Back.Pop(); }
                catch { state.Cursor = current; throw; }
            }
            UpdatePager();
        }
        catch (Exception exception) { LedgerPager.Status = UserError.Message(exception); }
    }
}
