using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GroceryErp.Core;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Money out: Damages, Expenses, Employees and Salary payments, each with SQL filters and a details
/// panel for history and corrections. Entry forms open as dialogs, so the tables keep their height.
/// Filters are kept for the session.
/// </summary>
public partial class MoneyOutPage : Page
{
    private const string StateKey = nameof(MoneyOutPage);
    private readonly MoneyOutService _service;
    private readonly InventoryService _inventory;
    private readonly IClock _clock;
    private readonly bool _loadOnShow;
    private readonly KeysetPager<DateCursor> _damagePager = new();
    private readonly KeysetPager<DateCursor> _expensePager = new();
    private readonly LatestRequest _requests = new();
    private readonly LatestRequest _details = new();
    private readonly FitPageSize _damageFit;
    private readonly FitPageSize _expenseFit;
    private readonly DispatcherTimer _searchDelay;
    private CorrectionDating? _damageDating;
    private CorrectionDating? _expenseDating;
    private CorrectionDating? _salaryDating;
    private IReadOnlyList<EmployeeItem> _allEmployees = [];
    private bool _restoring;

    public MoneyOutPage() : this(null, null, null) { }

    public MoneyOutPage(MoneyOutService? service, InventoryService? inventory, IClock? clock, bool loadOnShow = true)
    {
        InitializeComponent();
        IServiceProvider? services = service is null || inventory is null || clock is null
            ? ((App)Application.Current).Services : null;
        _service = service ?? services!.GetRequiredService<MoneyOutService>();
        _inventory = inventory ?? services!.GetRequiredService<InventoryService>();
        _clock = clock ?? services!.GetRequiredService<IClock>();
        _loadOnShow = loadOnShow;
        _ = new ActionStrip(ExpenseActions, ExpenseButtons, ExpenseMoreMenu, ExpenseMore, static () => 160);
        _damageFit = new FitPageSize(DamageGrid);
        _expenseFit = new FitPageSize(ExpenseGrid);
        _damageFit.RowsChanged += async (_, _) => await LoadDamagesAsync(PageMove.Same);
        _expenseFit.RowsChanged += async (_, _) => await LoadExpensesAsync(PageMove.Same);
        DamagePager.PreviousRequested += async (_, _) => await LoadDamagesAsync(PageMove.Previous);
        DamagePager.NextRequested += async (_, _) => await LoadDamagesAsync(PageMove.Next);
        ExpensePager.PreviousRequested += async (_, _) => await LoadExpensesAsync(PageMove.Previous);
        ExpensePager.NextRequested += async (_, _) => await LoadExpensesAsync(PageMove.Next);
        _searchDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await RefreshAsync();
        };
        ApplyState(PageStateStore.Current?.Get<State>(StateKey));
        UpdateActions();
        LanguageRefresh.Attach(this, () => _ = RefreshForLanguageAsync());
        Loaded += (_, _) => PlaceTabActions();
        if (loadOnShow) Loaded += async (_, _) =>
        {
            await LoadMastersAsync();
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _searchDelay.Stop();
            _requests.Cancel();
            _details.Cancel();
            PageStateStore.Current?.Set(StateKey, CaptureState());
        };
    }

    private sealed record State(int Tab, string DamageSearch, DateTime? DamageFrom, DateTime? DamageTo, int DamageStatus,
        int DamageReason, string ExpenseSearch, DateTime? ExpenseFrom, DateTime? ExpenseTo, long ExpenseCategory,
        int ExpenseStatus, string EmployeeSearch, int EmployeeStatus, string SalaryFrom, string SalaryTo,
        long SalaryEmployee, int SalaryStatus);

    private State CaptureState() => new(MoneyOutTabs.SelectedIndex, DamageSearch.Text ?? "", DamageFromBox.SelectedDate,
        DamageToBox.SelectedDate, DamageStatusFilter.SelectedIndex, DamageReasonFilter.SelectedIndex,
        ExpenseSearch.Text ?? "", ExpenseFromBox.SelectedDate, ExpenseToBox.SelectedDate,
        ExpenseCategoryFilter.SelectedValue as long? ?? 0, ExpenseStatusFilter.SelectedIndex,
        EmployeeSearch.Text ?? "", EmployeeStatusFilter.SelectedIndex, SalaryFromMonth.Text, SalaryToMonth.Text,
        SalaryEmployeeFilter.SelectedValue as long? ?? 0, SalaryStatusFilter.SelectedIndex);

    private long _pendingExpenseCategory;
    private long _pendingSalaryEmployee;

    private void ApplyState(State? state)
    {
        _restoring = true;
        DateTime today = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        string month = YearMonth.From(_clock.BusinessDate).ToString();
        if (state is null)
        {
            DamageFromBox.SelectedDate = ExpenseFromBox.SelectedDate = today.AddMonths(-3);
            DamageToBox.SelectedDate = ExpenseToBox.SelectedDate = today;
            SalaryFromMonth.Text = SalaryToMonth.Text = month;
        }
        else
        {
            MoneyOutTabs.SelectedIndex = state.Tab;
            DamageSearch.Text = state.DamageSearch;
            DamageFromBox.SelectedDate = state.DamageFrom;
            DamageToBox.SelectedDate = state.DamageTo;
            DamageStatusFilter.SelectedIndex = state.DamageStatus;
            DamageReasonFilter.SelectedIndex = state.DamageReason;
            ExpenseSearch.Text = state.ExpenseSearch;
            ExpenseFromBox.SelectedDate = state.ExpenseFrom;
            ExpenseToBox.SelectedDate = state.ExpenseTo;
            ExpenseStatusFilter.SelectedIndex = state.ExpenseStatus;
            EmployeeSearch.Text = state.EmployeeSearch;
            EmployeeStatusFilter.SelectedIndex = state.EmployeeStatus;
            SalaryFromMonth.Text = state.SalaryFrom;
            SalaryToMonth.Text = state.SalaryTo;
            SalaryStatusFilter.SelectedIndex = state.SalaryStatus;
            _pendingExpenseCategory = state.ExpenseCategory;
            _pendingSalaryEmployee = state.SalaryEmployee;
        }
        _restoring = false;
    }

    private async Task LoadMastersAsync()
    {
        try
        {
            long category = ExpenseCategoryFilter.SelectedValue as long? ?? _pendingExpenseCategory;
            long employee = SalaryEmployeeFilter.SelectedValue as long? ?? _pendingSalaryEmployee;
            IReadOnlyList<ExpenseCategoryItem> categories = await _service.GetExpenseCategoriesAsync(includeInactive: true);
            _allEmployees = await _service.GetEmployeesAsync(new EmployeeFilter { Status = "ALL" });
            _restoring = true;
            ExpenseCategoryFilter.ItemsSource = new[] { new ExpenseCategoryItem(0, T("MoneyOut_AllCategories"), null, true) }
                .Concat(categories).ToList();
            ExpenseCategoryFilter.SelectedValue = category;
            if (ExpenseCategoryFilter.SelectedIndex < 0) ExpenseCategoryFilter.SelectedIndex = 0;
            SalaryEmployeeFilter.ItemsSource = new[]
            {
                new EmployeeItem(0, T("MoneyOut_AllEmployees"), null, null, null, null, Core.Numerics.Money.Zero, true, null),
            }.Concat(_allEmployees).ToList();
            SalaryEmployeeFilter.SelectedValue = employee;
            if (SalaryEmployeeFilter.SelectedIndex < 0) SalaryEmployeeFilter.SelectedIndex = 0;
        }
        catch (Exception exception) { ShowStatus(UserError.Message(exception)); }
        finally { _restoring = false; }
    }

    // Keeps the tab row's status and actions level with the headers, starting after the last one.
    private void TabHeader_OnSizeChanged(object sender, SizeChangedEventArgs e) => PlaceTabActions();

    // The headers are first sized before the page is loaded, so placement also runs on load.
    private void PlaceTabActions()
    {
        if (PaymentsTab.ActualWidth <= 0 || PresentationSource.FromVisual(PaymentsTab) is null) return;
        Point corner = PaymentsTab.TranslatePoint(new Point(PaymentsTab.ActualWidth, 0), MoneyOutTabs);
        TabActions.Margin = new Thickness(corner.X + 12, corner.Y, 0, 0);
        // Centred on the visible tab, which leaves a gap below it inside the item.
        TabActions.Height = PaymentsTab.Template?.FindName("TabBorder", PaymentsTab) is FrameworkElement border && border.ActualHeight > 0
            ? border.ActualHeight : PaymentsTab.ActualHeight;
    }

    private void UpdateActions()
    {
        int tab = MoneyOutTabs.SelectedIndex;
        DamageActions.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExpenseActions.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        EmployeeActions.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
        SalaryActions.Visibility = tab == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Tabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MoneyOutTabs)) return;
        UpdateActions();
        if (IsLoaded && _loadOnShow && !_restoring) await RefreshAsync();
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
        if (IsLoaded && !_restoring) await RefreshAsync();
    }

    private async void Date_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_restoring) await RefreshAsync();
    }

    private async void ClearFilters_OnClick(object sender, RoutedEventArgs e)
    {
        _restoring = true;
        DateTime today = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        switch (MoneyOutTabs.SelectedIndex)
        {
            case 0:
                DamageSearch.Text = string.Empty;
                DamageFromBox.SelectedDate = today.AddMonths(-3);
                DamageToBox.SelectedDate = today;
                DamageStatusFilter.SelectedIndex = 0;
                DamageReasonFilter.SelectedIndex = 0;
                break;
            case 1:
                ExpenseSearch.Text = string.Empty;
                ExpenseFromBox.SelectedDate = today.AddMonths(-3);
                ExpenseToBox.SelectedDate = today;
                ExpenseCategoryFilter.SelectedIndex = 0;
                ExpenseStatusFilter.SelectedIndex = 0;
                break;
            case 2:
                EmployeeSearch.Text = string.Empty;
                EmployeeStatusFilter.SelectedIndex = 0;
                break;
            default:
                SalaryFromMonth.Text = SalaryToMonth.Text = YearMonth.From(_clock.BusinessDate).ToString();
                SalaryEmployeeFilter.SelectedIndex = 0;
                SalaryStatusFilter.SelectedIndex = 0;
                break;
        }
        _restoring = false;
        _searchDelay.Stop();
        await RefreshAsync();
    }

    // The "All …" filter items, status cells and row counts are set in code; reload the open tab
    // in place so they follow the new language.
    private async Task RefreshForLanguageAsync()
    {
        try
        {
            await LoadMastersAsync();
            switch (MoneyOutTabs.SelectedIndex)
            {
                case 0: await LoadDamagesAsync(PageMove.Same); break;
                case 1: await LoadExpensesAsync(PageMove.Same); break;
                case 2: await LoadEmployeesAsync(); break;
                default: await LoadSalaryRegisterAsync(); break;
            }
        }
        catch (Exception exception) { DamagePager.Status = UserError.Message(exception); }
    }

    private async Task RefreshAsync()
    {
        switch (MoneyOutTabs.SelectedIndex)
        {
            case 0: await LoadDamagesAsync(PageMove.First); break;
            case 1: await LoadExpensesAsync(PageMove.First); break;
            case 2: await LoadEmployeesAsync(); break;
            default: await LoadSalaryRegisterAsync(); break;
        }
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } tag ? tag : null;

    private (DateOnly From, DateOnly To)? Range(DatePicker fromBox, DatePicker toBox, PagerBar pager)
    {
        DateOnly from = fromBox.SelectedDate is { } start ? DateOnly.FromDateTime(start) : new DateOnly(2000, 1, 1);
        DateOnly to = toBox.SelectedDate is { } end ? DateOnly.FromDateTime(end) : _clock.BusinessDate.AddYears(1);
        if (from <= to) return (from, to);
        pager.Status = T("Common_DateRangeInvalid");
        return null;
    }

    private async Task LoadDamagesAsync(PageMove move)
    {
        if (!_damagePager.CanMove(move) || Range(DamageFromBox, DamageToBox, DamagePager) is not { } range) return;
        using var timing = UiRefreshProbe.Track("money-out", this);
        using LatestRequest.Scope request = _requests.Begin();
        DamagePager.SetLoading(true);
        try
        {
            DateCursor? cursor = _damagePager.CursorFor(move);
            DamagePage page = await _service.GetDamagePageAsync(new DamageFilter(range.From, range.To)
            {
                Status = SelectedTag(DamageStatusFilter),
                Reason = SelectedTag(DamageReasonFilter),
                Query = InputNormalizer.Normalize(DamageSearch.Text ?? string.Empty),
            }, cursor?.Date, cursor?.Id, _damageFit.Take(), request.Token);
            if (!request.IsCurrent) return;
            _damagePager.Commit(move, page.HasMore, page.NextDate is { } date && page.NextId is { } id ? new DateCursor(date, id) : null);
            timing.Bind(() => DamageGrid.ItemsSource = page.Items);
            DamageEmpty.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DamagePager.Status = TranslationSource.Instance.Format("MoneyOut_RowCount", page.Items.Count);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) DamagePager.Status = UserError.Message(exception);
        }
        finally
        {
            if (request.IsCurrent) DamagePager.Update(_damagePager.PageNumber, _damagePager.CanGoBack, _damagePager.HasMore);
        }
    }

    private async Task LoadExpensesAsync(PageMove move)
    {
        if (!_expensePager.CanMove(move) || Range(ExpenseFromBox, ExpenseToBox, ExpensePager) is not { } range) return;
        using var timing = UiRefreshProbe.Track("money-out", this);
        using LatestRequest.Scope request = _requests.Begin();
        ExpensePager.SetLoading(true);
        try
        {
            DateCursor? cursor = _expensePager.CursorFor(move);
            ExpensePage page = await _service.GetExpensePageAsync(new ExpenseFilter(range.From, range.To)
            {
                Status = SelectedTag(ExpenseStatusFilter),
                CategoryId = ExpenseCategoryFilter.SelectedValue is long category and > 0 ? category : null,
                Query = InputNormalizer.Normalize(ExpenseSearch.Text ?? string.Empty),
            }, cursor?.Date, cursor?.Id, _expenseFit.Take(), request.Token);
            if (!request.IsCurrent) return;
            _expensePager.Commit(move, page.HasMore, page.NextDate is { } date && page.NextId is { } id ? new DateCursor(date, id) : null);
            timing.Bind(() => ExpenseGrid.ItemsSource = page.Items);
            ExpenseEmpty.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ExpensePager.Status = TranslationSource.Instance.Format("MoneyOut_RowCount", page.Items.Count);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) ExpensePager.Status = UserError.Message(exception);
        }
        finally
        {
            if (request.IsCurrent) ExpensePager.Update(_expensePager.PageNumber, _expensePager.CanGoBack, _expensePager.HasMore);
        }
    }

    private async Task LoadEmployeesAsync()
    {
        using LatestRequest.Scope request = _requests.Begin();
        try
        {
            IReadOnlyList<EmployeeItem> employees = await _service.GetEmployeesAsync(new EmployeeFilter
            {
                Status = (EmployeeStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "ACTIVE",
                Query = InputNormalizer.Normalize(EmployeeSearch.Text ?? string.Empty),
            }, request.Token);
            if (!request.IsCurrent) return;
            EmployeeGrid.ItemsSource = employees;
            EmployeeEmpty.Visibility = employees.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmployeeStatus.Text = TranslationSource.Instance.Format("MoneyOut_RowCount", employees.Count);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) EmployeeStatus.Text = UserError.Message(exception);
        }
    }

    private async Task LoadSalaryRegisterAsync()
    {
        if (!YearMonth.TryParse(SalaryFromMonth.Text.Trim(), out YearMonth from) ||
            !YearMonth.TryParse(SalaryToMonth.Text.Trim(), out YearMonth to))
        {
            SalaryStatus.Text = T("MoneyOut_SalaryFromFormat");
            return;
        }
        if (from > to)
        {
            SalaryStatus.Text = T("Common_DateRangeInvalid");
            return;
        }
        using LatestRequest.Scope request = _requests.Begin();
        try
        {
            IReadOnlyList<SalaryPaymentItem> payments = await _service.GetSalaryRegisterAsync(from, to,
                SalaryEmployeeFilter.SelectedValue is long id and > 0 ? id : null, SelectedTag(SalaryStatusFilter), request.Token);
            if (!request.IsCurrent) return;
            SalaryRegisterGrid.ItemsSource = payments;
            SalaryEmpty.Visibility = payments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SalaryStatus.Text = TranslationSource.Instance.Format("MoneyOut_RowCount", payments.Count);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) SalaryStatus.Text = UserError.Message(exception);
        }
    }

    private void ShowStatus(string text)
    {
        switch (MoneyOutTabs.SelectedIndex)
        {
            case 0: DamagePager.Status = text; break;
            case 1: ExpensePager.Status = text; break;
            case 2: EmployeeStatus.Text = text; break;
            default: SalaryStatus.Text = text; break;
        }
    }

    // Dialog forms keep the tables at full height; each refreshes only its tab after saving.
    private async void RecordDamage_OnClick(object sender, RoutedEventArgs e)
    {
        if (new LotDamageWindow(_service, _clock, _inventory) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
        {
            await LoadDamagesAsync(PageMove.First);
            ShowStatus(T("MoneyOut_Saved"));
        }
    }

    private async void NewExpense_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<ExpenseCategoryItem> categories = await _service.GetExpenseCategoriesAsync();
            if (new ExpenseFormWindow(_service, _clock, categories) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            {
                await LoadExpensesAsync(PageMove.First);
                ShowStatus(T("MoneyOut_Saved"));
            }
        }
        catch (Exception exception) { ShowStatus(UserError.Message(exception)); }
    }

    private async void RepeatExpense_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<ExpenseCategoryItem> categories = await _service.GetExpenseCategoriesAsync();
            var window = new RepeatExpensesWindow(_service, _clock, categories) { Owner = Window.GetWindow(this) };
            window.ShowDialog();
            if (window.SavedCount > 0) await LoadExpensesAsync(PageMove.First);
        }
        catch (Exception exception) { ShowStatus(UserError.Message(exception)); }
    }

    private async void Categories_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new ExpenseCategoriesWindow(_service) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (!window.Changed) return;
        await LoadMastersAsync();
        await LoadExpensesAsync(PageMove.Same);
    }

    private void OpenAssets_OnClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.FindName("RootNavigation") is Wpf.Ui.Controls.NavigationView navigation)
            navigation.Navigate(typeof(AssetsPage));
    }

    private async void NewEmployee_OnClick(object sender, RoutedEventArgs e)
    {
        if (new EmployeeFormWindow(_service, _clock) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        await LoadMastersAsync();
        await LoadEmployeesAsync();
    }

    private async void EditEmployee_OnClick(object sender, RoutedEventArgs e)
    {
        if (EmployeeGrid.SelectedItem is not EmployeeItem employee) return;
        if (new EmployeeFormWindow(_service, _clock, employee) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        await LoadMastersAsync();
        await LoadEmployeesAsync();
    }

    private void EmployeeGrid_OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        EditEmployee_OnClick(sender, e);

    private async void PrepareSalary_OnClick(object sender, RoutedEventArgs e)
    {
        Dictionary<long, string> names = _allEmployees.ToDictionary(static employee => employee.Id, static employee => employee.Name);
        if (new SalaryRunWindow(_service, _clock, names) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        await LoadSalaryRegisterAsync();
        ShowStatus(T("MoneyOut_Saved"));
    }

    private async void RecordAdvance_OnClick(object sender, RoutedEventArgs e)
    {
        if (new AdvanceFormWindow(_service, _clock, _allEmployees) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        await LoadSalaryRegisterAsync();
        ShowStatus(T("MoneyOut_Saved"));
    }

    // A selected entry opens its history and states how an edit would be dated.
    private async void DamageGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DamageGrid.SelectedItem is not DamageItem item) { DamageDetails.Clear(); return; }
        using LatestRequest.Scope request = _details.Begin();
        try
        {
            IReadOnlyList<DamageHistoryItem> history = await _service.GetDamageHistoryAsync(item.Id, request.Token);
            CorrectionDating dating = await _service.PreviewCorrectionAsync(item.Date, request.Token);
            if (!request.IsCurrent || !ReferenceEquals(DamageGrid.SelectedItem, item)) return;
            _damageDating = dating;
            DamageDetails.Show($"{item.DocumentNumber} · {item.ProductName}", dating, item.Status != "VOID",
                history.Select(DamageHistoryText));
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception) { ShowStatus(UserError.Message(exception)); }
    }

    private async void DamageDetails_OnEdit(object? sender, EventArgs e) => await CorrectDamageAsync(false);
    private async void DamageDetails_OnVoid(object? sender, EventArgs e) => await CorrectDamageAsync(true);

    private async Task CorrectDamageAsync(bool voidEntry)
    {
        if (DamageGrid.SelectedItem is not DamageItem item || _damageDating is not { } dating) return;
        var window = new MoneyOutCorrectionWindow(_service, item, dating, voidEntry) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await LoadDamagesAsync(PageMove.Same);
    }

    private static string DamageHistoryText(DamageHistoryItem item) => HistoryText(item.Kind, item.RecordedOn,
        item.Treatment, item.EffectiveDate, string.Join(" · ",
            $"{T("Stock_Quantity")} {Number(item.Quantity.Value)}",
            $"{T("MoneyOut_Recovered")} ₹{Number(item.RecoveredAmount.Value)}",
            DamageReasonConverter.Text(item.Reason),
            $"{T(item.Kind == "ORIGINAL" ? "MoneyOut_Loss" : "MoneyOut_LossChange")} ₹{Number(item.LossChange.Value)}")
        + (item.RestoredLotId is null ? "" : " · " + T("MoneyOut_RestoredNewLot")), item.CorrectionReason);

    private async void ExpenseGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExpenseGrid.SelectedItem is not ExpenseItem item) { ExpenseDetails.Clear(); return; }
        using LatestRequest.Scope request = _details.Begin();
        try
        {
            IReadOnlyList<ExpenseHistoryItem> history = await _service.GetExpenseHistoryAsync(item.Id, request.Token);
            CorrectionDating dating = await _service.PreviewCorrectionAsync(item.Date, request.Token);
            if (!request.IsCurrent || !ReferenceEquals(ExpenseGrid.SelectedItem, item)) return;
            _expenseDating = dating;
            ExpenseDetails.Show($"{Day(item.Date)} · {item.CategoryName}", item.Status == "PURCHASE" ? null : dating,
                item.Status is "ACTIVE" or "CORRECTED", history.Select(ExpenseHistoryText));
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception) { ShowStatus(UserError.Message(exception)); }
    }

    private async void ExpenseDetails_OnEdit(object? sender, EventArgs e) => await CorrectExpenseAsync(false);
    private async void ExpenseDetails_OnVoid(object? sender, EventArgs e) => await CorrectExpenseAsync(true);

    private async Task CorrectExpenseAsync(bool voidEntry)
    {
        if (ExpenseGrid.SelectedItem is not ExpenseItem item || _expenseDating is not { } dating) return;
        try
        {
            IReadOnlyList<ExpenseCategoryItem> categories = await _service.GetExpenseCategoriesAsync();
            var window = new MoneyOutCorrectionWindow(_service, item, categories, dating, voidEntry) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true) await LoadExpensesAsync(PageMove.Same);
        }
        catch (Exception exception) { ShowStatus(UserError.Message(exception)); }
    }

    private static string ExpenseHistoryText(ExpenseHistoryItem item) => HistoryText(item.Kind, item.RecordedOn,
        item.Treatment, item.AppliedOn ?? item.Date, string.Join(" · ", new[]
        {
            Day(item.Date), item.CategoryName, $"₹{Number(item.Amount.Value)}", item.Mode, item.PaidTo,
        }.Where(static value => !string.IsNullOrWhiteSpace(value))), item.CorrectionReason);

    private async void EmployeeGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EditEmployeeButton.IsEnabled = EmployeeGrid.SelectedItem is EmployeeItem;
        if (EmployeeGrid.SelectedItem is not EmployeeItem employee) { EmployeeDetails.Clear(); return; }
        using LatestRequest.Scope request = _details.Begin();
        try
        {
            IReadOnlyList<SalaryTermItem> terms = await _service.GetSalaryTermsAsync(employee.Id, request.Token);
            if (!request.IsCurrent || !ReferenceEquals(EmployeeGrid.SelectedItem, employee)) return;
            EmployeeDetails.Show(employee.Name, null, false, terms.Select(term => TranslationSource.Instance.Format(
                "MoneyOut_SalaryTermLine", term.EffectiveMonth.ToString(), Number(term.MonthlySalary.Value),
                T($"MoneyOut_TermReason_{term.Reason}"), term.CreatedAt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture))),
                actions: false);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception) { ShowStatus(UserError.Message(exception)); }
    }

    private async void SalaryRegisterGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SalaryRegisterGrid.SelectedItem is not SalaryPaymentItem item) { SalaryDetails.Clear(); return; }
        using LatestRequest.Scope request = _details.Begin();
        try
        {
            IReadOnlyList<SalaryHistoryItem> history = await _service.GetSalaryHistoryAsync(item.Id, request.Token);
            CorrectionDating dating = await _service.PreviewCorrectionAsync(item.Period.LastDay, request.Token);
            if (!request.IsCurrent || !ReferenceEquals(SalaryRegisterGrid.SelectedItem, item)) return;
            _salaryDating = dating;
            SalaryDetails.Show($"{item.EmployeeName} · {item.Period}", dating, item.Status != "VOID",
                history.Select(static entry => HistoryText(entry.Kind, entry.RecordedOn, entry.Treatment,
                    entry.EffectiveDate, string.Join(" · ", $"₹{Number(entry.Amount.Value)}",
                        $"{T("MoneyOut_PayDate")} {Day(entry.PayDate)}", entry.Mode), entry.CorrectionReason)));
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception) { ShowStatus(UserError.Message(exception)); }
    }

    private async void SalaryDetails_OnEdit(object? sender, EventArgs e) => await CorrectSalaryAsync(false);
    private async void SalaryDetails_OnVoid(object? sender, EventArgs e) => await CorrectSalaryAsync(true);

    private async Task CorrectSalaryAsync(bool voidEntry)
    {
        if (SalaryRegisterGrid.SelectedItem is not SalaryPaymentItem item || _salaryDating is not { } dating) return;
        var window = new MoneyOutCorrectionWindow(_service, item, dating, voidEntry) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await LoadSalaryRegisterAsync();
    }

    // One history entry: when it was recorded, the P&L date it affects and how it was dated.
    private static string HistoryText(string kind, DateOnly recordedOn, string? treatment, DateOnly pnlDate,
        string details, string? reason)
    {
        string head = kind == "ORIGINAL"
            ? TranslationSource.Instance.Format("MoneyOut_HistoryOriginal", Day(recordedOn), Day(pnlDate))
            : TranslationSource.Instance.Format(kind == "VOID" ? "MoneyOut_HistoryVoid" : "MoneyOut_HistoryEdit",
                Day(recordedOn), Day(pnlDate), T($"MoneyOut_Treatment_{treatment}"));
        return string.IsNullOrWhiteSpace(reason)
            ? head + Environment.NewLine + details
            : head + Environment.NewLine + details + Environment.NewLine + T("MoneyOut_CorrectionReason") + ": " + reason;
    }

    private static string Day(DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
    private static string Number(decimal value) => value.ToString("N3", TranslationSource.FormatCulture);
    private static string T(string key) => TranslationSource.Instance[key];
}
