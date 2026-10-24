using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

/// <summary>Vendors: SQL search and filters, keyset pages, ledger, payments and reviewed bulk activation.</summary>
public partial class VendorsPage : Page
{
    private const string StateKey = nameof(VendorsPage);
    private readonly PurchasingService _purchasing;
    private readonly KeysetPager<NameCursor> _pager = new();
    private readonly LatestRequest _requests = new();
    private readonly TableSelection<VendorItem> _selection = new(static vendor => vendor.Id);
    private readonly FitPageSize _fit;
    private readonly DispatcherTimer _searchDelay;
    private CheckBox? _pageCheck;
    private bool _restoring;

    public VendorsPage() : this(null) { }

    public VendorsPage(PurchasingService? purchasing, bool loadOnShow = true)
    {
        InitializeComponent();
        _purchasing = purchasing ?? ((App)Application.Current).Services.GetRequiredService<PurchasingService>();
        _ = new FilterRow(FilterBar, SearchBox);
        LanguageRefresh.Attach(this, () => _ = LoadAsync(PageMove.Same));
        _fit = new FitPageSize(VendorsGrid);
        _fit.RowsChanged += async (_, _) => await LoadAsync(PageMove.Same);
        _searchDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await ReloadAsync();
        };
        _selection.Changed += (_, _) => UpdateSelection();
        Selection.ClearRequested += (_, _) =>
        {
            _selection.Clear();
            VendorsGrid.Items.Refresh();
        };
        Selection.AllMatchingRequested += async (_, _) => await SelectAllMatchingAsync();
        Pager.PreviousRequested += async (_, _) => await LoadAsync(PageMove.Previous);
        Pager.NextRequested += async (_, _) => await LoadAsync(PageMove.Next);
        if (PageStateStore.Current?.Get<State>(StateKey) is { } state)
        {
            _restoring = true;
            SearchBox.Text = state.Search;
            StatusFilter.SelectedIndex = state.Status;
            BalanceFilter.IsChecked = state.WithBalance;
            _restoring = false;
        }
        PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (args.Key == Key.F) { SearchBox.Focus(); args.Handled = true; }
            else if (args.Key == Key.N) { New_OnClick(this, args); args.Handled = true; }
        };
        if (loadOnShow) Loaded += async (_, _) => await LoadAsync(PageMove.First);
        Unloaded += (_, _) =>
        {
            _searchDelay.Stop();
            _requests.Cancel();
            PageStateStore.Current?.Set(StateKey, new State(SearchBox.Text ?? string.Empty, StatusFilter.SelectedIndex,
                BalanceFilter.IsChecked == true));
        };
    }

    private sealed record State(string Search, int Status, bool WithBalance);

    private VendorFilter CurrentFilter() => new()
    {
        Query = SearchBox.Text,
        Status = (StatusFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "ACTIVE",
        WithBalanceOnly = BalanceFilter.IsChecked == true,
    };

    private Task ReloadAsync()
    {
        _selection.Clear();
        return LoadAsync(PageMove.First);
    }

    private async Task LoadAsync(PageMove move)
    {
        if (!_pager.CanMove(move)) return;
        using var timing = UiRefreshProbe.Track("vendors", this);
        using LatestRequest.Scope request = _requests.Begin();
        Pager.SetLoading(true);
        try
        {
            NameCursor? cursor = _pager.CursorFor(move);
            VendorPage page = await _purchasing.GetVendorPageAsync(CurrentFilter(), cursor?.Name, cursor?.Id, _fit.Take(), request.Token);
            if (!request.IsCurrent) return;
            _pager.Commit(move, page.HasMore, page.NextName is null || page.NextId is null ? null : new NameCursor(page.NextName, page.NextId.Value));
            _selection.Refresh(page.Items);
            timing.Bind(() => VendorsGrid.ItemsSource = page.Items);
            EmptyText.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Pager.Status = TranslationSource.Instance.Format("Purchasing_VendorCount", page.Items.Count);
            UpdateSelection();
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) Pager.Status = UserError.Message(exception);
        }
        finally
        {
            if (request.IsCurrent) Pager.Update(_pager.PageNumber, _pager.CanGoBack, _pager.HasMore);
        }
    }

    private void Search_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _restoring) return;
        _requests.Cancel();
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private async void Filter_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_restoring) await ReloadAsync();
    }

    private async void Check_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) await ReloadAsync();
    }

    private async void ClearFilters_OnClick(object sender, RoutedEventArgs e)
    {
        _restoring = true;
        SearchBox.Text = string.Empty;
        StatusFilter.SelectedIndex = 0;
        BalanceFilter.IsChecked = false;
        _restoring = false;
        _searchDelay.Stop();
        await ReloadAsync();
    }

    private VendorItem? Current => VendorsGrid.SelectedItem as VendorItem;

    private void VendorsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EditButton.IsEnabled = Current is not null;
        LedgerButton.IsEnabled = Current is not null;
    }

    private void VendorsGrid_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Current is not null) Ledger_OnClick(sender, e);
    }

    private void VendorsGrid_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Current is not VendorItem vendor) return;
        if (e.Key == Key.Enter)
        {
            Ledger_OnClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            _selection.Set(vendor, !_selection.IsSelected(vendor));
            VendorsGrid.Items.Refresh();
            e.Handled = true;
        }
    }

    private void RowCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: VendorItem vendor } box) box.IsChecked = _selection.IsSelected(vendor);
    }

    private void RowCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: VendorItem vendor } box) _selection.Set(vendor, box.IsChecked == true);
    }

    private void PageCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        _pageCheck = sender as CheckBox;
        UpdateSelection();
    }

    private void PageCheck_OnClick(object sender, RoutedEventArgs e)
    {
        VendorItem[] rows = VendorsGrid.Items.OfType<VendorItem>().ToArray();
        _selection.SetPage(rows, _selection.PageState(rows) != true);
        VendorsGrid.Items.Refresh();
    }

    private void UpdateSelection()
    {
        VendorItem[] rows = VendorsGrid.Items.OfType<VendorItem>().ToArray();
        bool? state = _selection.PageState(rows);
        if (_pageCheck is not null) _pageCheck.IsChecked = state;
        Selection.Show(_selection.Count, _selection.AllMatching, state == true, _pager.HasMore || _pager.CanGoBack,
            _selection.AllMatching ? FilterSummary() : null);
    }

    private string FilterSummary() => FilterText.Describe(SearchBox, StatusFilter, BalanceFilter);

    private async Task SelectAllMatchingAsync()
    {
        try
        {
            _selection.SelectAllMatching(await _purchasing.CountVendorsAsync(CurrentFilter()));
            VendorsGrid.Items.Refresh();
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async void Activate_OnClick(object sender, RoutedEventArgs e) => await ChangeActiveAsync(true);

    private async void Deactivate_OnClick(object sender, RoutedEventArgs e) => await ChangeActiveAsync(false);

    private async Task ChangeActiveAsync(bool active)
    {
        if (_selection.IsEmpty) return;
        string title = TranslationSource.Instance[active ? "Purchasing_ActivateSelectedVendors" : "Purchasing_DeactivateSelectedVendors"];
        VendorFilter filter = CurrentFilter();
        var bulk = new BulkAction
        {
            Title = title,
            RunLabel = title,
            SelectionSummary = _selection.AllMatching
                ? $"{TranslationSource.Instance.Format("Selection_AllMatchingCount", _selection.Count)} · {FilterSummary()}"
                : TranslationSource.Instance.Format("Selection_Count", _selection.Count),
            Destructive = !active,
            Plan = async token =>
            {
                IReadOnlyList<long> ids = _selection.AllMatching
                    ? await _purchasing.ResolveVendorIdsAsync(filter, cancellationToken: token) : _selection.PickedIds;
                IReadOnlyList<PurchasingBulkRow> rows = await _purchasing.PreviewVendorBulkAsync(ids, active, token);
                return new BulkPlan(rows.Select(static row => new BulkCandidate(row.Id, row.Label, row.Eligible, row.ReasonKey)).ToArray(),
                    active ? null : TranslationSource.Instance["Purchasing_DeactivateEffect"]);
            },
            Run = (ids, operation, token) => _purchasing.RunVendorBulkBatchAsync(ids, active, operation, token),
        };
        var window = new BulkActionWindow(bulk) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (!window.ChangedAny) return;
        _selection.Clear();
        await LoadAsync(PageMove.Same);
    }

    private async void New_OnClick(object sender, RoutedEventArgs e)
    {
        if (new VendorEditorWindow(_purchasing) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await LoadAsync(PageMove.Same);
    }

    private async void Edit_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is not VendorItem vendor) return;
        if (new VendorEditorWindow(_purchasing, vendor.Id) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await LoadAsync(PageMove.Same);
    }

    private void Ledger_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is not VendorItem vendor) return;
        new VendorLedgerWindow(_purchasing, vendor.Id, vendor.Name) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async void Payment_OnClick(object sender, RoutedEventArgs e)
    {
        if (new VendorPaymentWindow(_purchasing, Current?.Id) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await LoadAsync(PageMove.Same);
    }
}
