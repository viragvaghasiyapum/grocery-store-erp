using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GroceryErp.Core;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Purchases: server-side document, vendor, status, date, line type, product and return-eligibility
/// filters; keyset pages sized to the window; Open/Void/Partial return with eligibility shown; and
/// reviewed bulk void. Filters are kept for the session.
/// </summary>
public partial class PurchasesPage : Page
{
    private const string StateKey = nameof(PurchasesPage);
    private readonly PurchasingService _purchasing;
    private readonly KeysetPager<DateCursor> _pager = new();
    private readonly LatestRequest _requests = new();
    private readonly LatestRequest _eligibility = new();
    private readonly TableSelection<PurchaseListItem> _selection = new(static purchase => purchase.Id);
    private readonly FitPageSize _fit;
    private readonly DispatcherTimer _searchDelay;
    private CheckBox? _pageCheck;
    private State? _pendingState;
    private bool _restoring;

    public PurchasesPage() : this(null) { }

    public PurchasesPage(PurchasingService? purchasing, bool loadOnShow = true)
    {
        InitializeComponent();
        _purchasing = purchasing ?? ((App)Application.Current).Services.GetRequiredService<PurchasingService>();
        _ = new FilterRow(FilterBar, DocumentFilter, MoreFiltersToggle, MoreFiltersPanel)
            .Fold(VendorFilter).Fold(FromLabel, FromBox, ToLabel, ToBox).Fold(StatusFilter);
        _fit = new FitPageSize(PurchasesGrid);
        LanguageRefresh.Attach(this, () => _ = RefreshForLanguageAsync());
        _fit.RowsChanged += async (_, _) => await LoadAsync(PageMove.Same);
        _searchDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await ReloadAsync();
        };
        _selection.Changed += (_, _) => UpdateSelection();
        Selection.ClearRequested += (_, _) =>
        {
            _selection.Clear();
            PurchasesGrid.Items.Refresh();
        };
        Selection.AllMatchingRequested += async (_, _) => await SelectAllMatchingAsync();
        Pager.PreviousRequested += async (_, _) => await LoadAsync(PageMove.Previous);
        Pager.NextRequested += async (_, _) => await LoadAsync(PageMove.Next);
        _pendingState = PageStateStore.Current?.Get<State>(StateKey);
        ApplyState(_pendingState);
        PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (args.Key == Key.F) { DocumentFilter.Focus(); args.Handled = true; }
            else if (args.Key == Key.N) { NewPurchase_OnClick(this, args); args.Handled = true; }
        };
        if (loadOnShow) Loaded += async (_, _) =>
        {
            try
            {
                await LoadVendorsAsync();
                await LoadAsync(PageMove.First);
            }
            catch (Exception exception) { Pager.Status = UserError.Message(exception); }
        };
        Unloaded += (_, _) =>
        {
            _searchDelay.Stop();
            _requests.Cancel();
            _eligibility.Cancel();
            PageStateStore.Current?.Set(StateKey, new State(DocumentFilter.Text ?? string.Empty,
                VendorFilter.SelectedValue as long? ?? 0, StatusFilter.SelectedIndex, FromBox.SelectedDate, ToBox.SelectedDate,
                TypeFilter.SelectedIndex, ProductFilter.Text ?? string.Empty, ReturnableFilter.IsChecked == true,
                MoreFiltersToggle.IsChecked == true));
        };
    }

    private sealed record State(string Document, long VendorId, int Status, DateTime? From, DateTime? To, int Type,
        string Product, bool Returnable, bool MoreFilters);

    private void ApplyState(State? state)
    {
        if (state is null) return;
        _restoring = true;
        DocumentFilter.Text = state.Document;
        StatusFilter.SelectedIndex = state.Status;
        FromBox.SelectedDate = state.From;
        ToBox.SelectedDate = state.To;
        TypeFilter.SelectedIndex = state.Type;
        ProductFilter.Text = state.Product;
        ReturnableFilter.IsChecked = state.Returnable;
        MoreFiltersToggle.IsChecked = state.MoreFilters;
        MoreFiltersPanel.Visibility = state.MoreFilters ? Visibility.Visible : Visibility.Collapsed;
        _restoring = false;
    }

    private async Task LoadVendorsAsync()
    {
        long? selected = VendorFilter.SelectedValue as long? ?? _pendingState?.VendorId;
        _pendingState = null;
        IReadOnlyList<VendorItem> vendors = await _purchasing.GetVendorsAsync(includeInactive: true);
        _restoring = true;
        VendorFilter.ItemsSource = new[] { new VendorItem(0, TranslationSource.Instance["Purchasing_AllVendors"], null, null, Money.Zero, true) }
            .Concat(vendors).ToList();
        VendorFilter.SelectedValue = selected ?? 0;
        if (VendorFilter.SelectedIndex < 0) VendorFilter.SelectedIndex = 0;
        _restoring = false;
    }

    // "All vendors", the status cells and the row count are set in code, so they follow a reload.
    private async Task RefreshForLanguageAsync()
    {
        try
        {
            await LoadVendorsAsync();
            await LoadAsync(PageMove.Same);
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } tag ? tag : null;

    private PurchaseFilter CurrentFilter() => new()
    {
        DocumentQuery = InputNormalizer.Normalize(DocumentFilter.Text ?? string.Empty),
        VendorId = VendorFilter.SelectedValue is long id and > 0 ? id : null,
        Status = SelectedTag(StatusFilter),
        From = FromBox.SelectedDate is { } from ? DateOnly.FromDateTime(from) : null,
        To = ToBox.SelectedDate is { } to ? DateOnly.FromDateTime(to) : null,
        LineKind = SelectedTag(TypeFilter),
        ProductQuery = InputNormalizer.Normalize(ProductFilter.Text ?? string.Empty),
        ReturnableOnly = ReturnableFilter.IsChecked == true,
    };

    private Task ReloadAsync()
    {
        _selection.Clear();
        return LoadAsync(PageMove.First);
    }

    private async Task LoadAsync(PageMove move)
    {
        if (!_pager.CanMove(move)) return;
        using var timing = UiRefreshProbe.Track("purchases", this);
        using LatestRequest.Scope request = _requests.Begin();
        Pager.SetLoading(true);
        try
        {
            PurchaseFilter filter = CurrentFilter();
            if (filter.From > filter.To)
            {
                Pager.Status = TranslationSource.Instance["Common_DateRangeInvalid"];
                return;
            }
            DateCursor? cursor = _pager.CursorFor(move);
            PurchasePage page = await _purchasing.GetPurchasePageAsync(filter, cursor?.Date, cursor?.Id, _fit.Take(), request.Token);
            if (!request.IsCurrent) return;
            _pager.Commit(move, page.HasMore, page.NextDate is { } date && page.NextId is { } next ? new DateCursor(date, next) : null);
            _selection.Refresh(page.Items);
            timing.Bind(() => PurchasesGrid.ItemsSource = page.Items);
            EmptyText.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Pager.Status = TranslationSource.Instance.Format("Purchasing_Count", page.Items.Count);
            UpdateSelection();
            UpdateActions();
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

    private void Text_OnChanged(object sender, TextChangedEventArgs e)
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

    private async void Date_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_restoring) await ReloadAsync();
    }

    private async void Check_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) await ReloadAsync();
    }

    private void MoreFilters_OnClick(object sender, RoutedEventArgs e) =>
        MoreFiltersPanel.Visibility = MoreFiltersToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void ClearFilters_OnClick(object sender, RoutedEventArgs e)
    {
        _restoring = true;
        DocumentFilter.Text = string.Empty;
        VendorFilter.SelectedIndex = 0;
        StatusFilter.SelectedIndex = 0;
        FromBox.SelectedDate = null;
        ToBox.SelectedDate = null;
        TypeFilter.SelectedIndex = 0;
        ProductFilter.Text = string.Empty;
        ReturnableFilter.IsChecked = false;
        _restoring = false;
        _searchDelay.Stop();
        await ReloadAsync();
    }

    private PurchaseListItem? Current => PurchasesGrid.SelectedItem as PurchaseListItem;

    private void PurchasesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

    // Void needs a server check (no document may have used the purchase's stock), so the button
    // explains why it is unavailable instead of failing after a click.
    private async void UpdateActions()
    {
        PurchaseListItem? purchase = Current;
        OpenButton.IsEnabled = purchase is not null;
        VendorReturnButton.IsEnabled = purchase is { Status: "POSTED" };
        ReturnHistoryButton.IsEnabled = purchase is { Status: "POSTED" or "CANCELLED" };
        CancelPurchaseButton.IsEnabled = false;
        CancelPurchaseButton.ToolTip = null;
        if (purchase is not { Status: "POSTED" }) return;
        using LatestRequest.Scope request = _eligibility.Begin();
        try
        {
            IReadOnlyList<PurchasingBulkRow> rows = await _purchasing.PreviewVoidAsync([purchase.Id], request.Token);
            if (!request.IsCurrent || Current?.Id != purchase.Id) return;
            CancelPurchaseButton.IsEnabled = rows.Count == 1 && rows[0].Eligible;
            CancelPurchaseButton.ToolTip = rows is [{ Eligible: false, ReasonKey: { } reason }]
                ? TranslationSource.Instance[reason] : TranslationSource.Instance["Purchasing_VoidAvailable"];
            ToolTipService.SetShowOnDisabled(CancelPurchaseButton, true);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) Pager.Status = UserError.Message(exception);
        }
    }

    private void PurchasesGrid_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Current is not null) Open_OnClick(sender, e);
    }

    private void PurchasesGrid_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Current is not PurchaseListItem purchase) return;
        if (e.Key == Key.Enter)
        {
            Open_OnClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            _selection.Set(purchase, !_selection.IsSelected(purchase));
            PurchasesGrid.Items.Refresh();
            e.Handled = true;
        }
    }

    private void RowCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: PurchaseListItem purchase } box) box.IsChecked = _selection.IsSelected(purchase);
    }

    private void RowCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: PurchaseListItem purchase } box) _selection.Set(purchase, box.IsChecked == true);
    }

    private void PageCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        _pageCheck = sender as CheckBox;
        UpdateSelection();
    }

    private void PageCheck_OnClick(object sender, RoutedEventArgs e)
    {
        PurchaseListItem[] rows = PurchasesGrid.Items.OfType<PurchaseListItem>().ToArray();
        _selection.SetPage(rows, _selection.PageState(rows) != true);
        PurchasesGrid.Items.Refresh();
    }

    private void UpdateSelection()
    {
        PurchaseListItem[] rows = PurchasesGrid.Items.OfType<PurchaseListItem>().ToArray();
        bool? state = _selection.PageState(rows);
        if (_pageCheck is not null) _pageCheck.IsChecked = state;
        Selection.Show(_selection.Count, _selection.AllMatching, state == true, _pager.HasMore || _pager.CanGoBack,
            _selection.AllMatching ? FilterSummary() : null);
    }

    private string FilterSummary() => FilterText.Describe(DocumentFilter, VendorFilter, StatusFilter, FromBox, ToBox,
        TypeFilter, ProductFilter, ReturnableFilter);

    private async Task SelectAllMatchingAsync()
    {
        try
        {
            _selection.SelectAllMatching(await _purchasing.CountPurchasesAsync(CurrentFilter()));
            PurchasesGrid.Items.Refresh();
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async void BulkVoid_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selection.IsEmpty) return;
        PurchaseFilter filter = CurrentFilter();
        DateOnly date = ((App)Application.Current).Services.GetRequiredService<IClock>().BusinessDate;
        var bulk = new BulkAction
        {
            Title = TranslationSource.Instance["Purchasing_CancelSelected"],
            RunLabel = TranslationSource.Instance["Purchasing_CancelPurchase"],
            SelectionSummary = _selection.AllMatching
                ? $"{TranslationSource.Instance.Format("Selection_AllMatchingCount", _selection.Count)} · {FilterSummary()}"
                : TranslationSource.Instance.Format("Selection_Count", _selection.Count),
            Destructive = true,
            // A void reverses every lot, the vendor balance and expenses: about 13-22 ms a purchase.
            BatchSize = 5,
            Plan = async token =>
            {
                IReadOnlyList<long> ids = _selection.AllMatching
                    ? await _purchasing.ResolvePurchaseIdsAsync(filter, cancellationToken: token) : _selection.PickedIds;
                IReadOnlyList<PurchasingBulkRow> rows = await _purchasing.PreviewVoidAsync(ids, token);
                PurchasingBulkRow[] eligible = rows.Where(static row => row.Eligible).ToArray();
                return new BulkPlan(rows.Select(static row => new BulkCandidate(row.Id, row.Label, row.Eligible, row.ReasonKey)).ToArray(),
                    TranslationSource.Instance.Format("Purchasing_VoidEffect", eligible.Length,
                        Money.FromScaled(eligible.Sum(static row => row.Amount.Scaled)),
                        date.ToString("dd MMM yyyy", TranslationSource.FormatCulture)));
            },
            Run = (ids, operation, token) => _purchasing.RunVoidBatchAsync(ids, date, operation, token),
        };
        var window = new BulkActionWindow(bulk) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (!window.ChangedAny) return;
        _selection.Clear();
        await LoadAsync(PageMove.Same);
    }

    private async void NewPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new PurchaseEntryWindow(_purchasing) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        await LoadAsync(PageMove.First);
        if (window.PostedDocumentNumber is { } number)
            Pager.Status = TranslationSource.Instance.Format("Purchasing_PostedNumber", number);
    }

    private async void Open_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is not PurchaseListItem purchase) return;
        if (purchase.Status == "DRAFT")
        {
            var window = new PurchaseEntryWindow(_purchasing, purchase.Id) { Owner = Window.GetWindow(this) };
            window.ShowDialog();
            await LoadAsync(PageMove.Same);
            if (window.PostedDocumentNumber is { } number)
                Pager.Status = TranslationSource.Instance.Format("Purchasing_PostedNumber", number);
            return;
        }
        try
        {
            PurchaseInvoice invoice = await _purchasing.GetInvoiceAsync(purchase.Id);
            new PurchaseInvoiceWindow(invoice) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async void CancelPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is not { Status: "POSTED" } purchase) return;
        if (MessageBox.Show(TranslationSource.Instance["Purchasing_CancelConfirm"],
            TranslationSource.Instance["Purchasing_CancelPurchase"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            IServiceProvider services = ((App)Application.Current).Services;
            await _purchasing.CancelAsync(purchase.Id, services.GetRequiredService<IClock>().BusinessDate, OperationGuard.NewId());
            await LoadAsync(PageMove.Same);
        }
        catch (DomainException exception) when (exception.Code == ErrorCode.CancelNotAllowed)
        {
            Pager.Status = TranslationSource.Instance["Purchasing_CancelBlocked"];
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async void VendorReturn_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is not { Status: "POSTED" } purchase) return;
        var window = new VendorReturnWindow(_purchasing, purchase.Id) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        await LoadAsync(PageMove.Same);
        Pager.Status = TranslationSource.Instance.Format("Purchasing_ReturnPosted", window.DocumentNumber ?? "");
    }

    private async void ReturnHistory_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is not { Status: "POSTED" or "CANCELLED" } purchase) return;
        new VendorReturnHistoryWindow(_purchasing, purchase.Id) { Owner = Window.GetWindow(this) }.ShowDialog();
        await LoadAsync(PageMove.Same);
    }
}
