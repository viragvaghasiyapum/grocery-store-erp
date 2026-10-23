using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class MoneyOutPage : Page
{
    private int PageSize => int.TryParse((PageSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int size) ? size : 50;
    private readonly MoneyOutService _service;
    private readonly InventoryService _inventory;
    private readonly IClock _clock;
    private readonly bool _loadOnShow;
    private long? _editingCategoryId;
    private long? _editingEmployeeId;
    private readonly Dictionary<string, ExpiryPage> _expiryPages = new();
    private readonly Dictionary<string, (DateOnly? Date, long? Id)> _expiryCursors = new();
    private readonly Dictionary<string, Stack<(DateOnly? Date, long? Id)>> _expiryPrevious = new();
    private readonly Stack<(DateOnly? Date, long? Id)> _damagePrevious = new();
    private readonly Stack<(DateOnly? Date, long? Id)> _expensePrevious = new();
    private (DateOnly? Date, long? Id) _damageCursor;
    private (DateOnly? Date, long? Id) _expenseCursor;
    private DamagePage? _damagePage;
    private ExpensePage? _expensePage;
    private CorrectionDating? _damageDating;
    private CorrectionDating? _expenseDating;
    private CorrectionDating? _salaryDating;
    private Money? _editingEmployeeSalary;
    private int _salaryHintVersion;
    private string _expenseOperationId = OperationGuard.NewId();
    private readonly Dictionary<long, ExpiryLot> _selectedExpiryLots = new();
    private readonly Dictionary<DataGrid, CheckBox> _expiryHeaderChecks = new();
    private bool _changingExpenseSection;
    private CheckBox? _salaryPageCheck;

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
        UpdateExpirySelection();
        UpdateRepeatCategoryCount();
        ExpenseDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        EmployeeJoinDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        SalaryPayDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        SalaryPeriod.Text = YearMonth.From(_clock.BusinessDate).ToString();
        SalaryRegisterPeriod.Text = SalaryPeriod.Text;
        AdvancePayDate.SelectedDate = SalaryPayDate.SelectedDate;
        DateTime today = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        DamageFromBox.SelectedDate = ExpenseFromBox.SelectedDate = today.AddYears(-1);
        DamageToBox.SelectedDate = ExpenseToBox.SelectedDate = today;
        UpdatePagerVisibility();
        UpdateExpiryActionVisibility();
        UpdateSalarySelection();
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Tabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MoneyOutTabs)) return;
        if (MoneyOutTabs.SelectedIndex != 0 && ExpiryDamagePopup is not null)
            ExpiryDamagePopup.IsOpen = false;
        UpdatePagerVisibility();
        if (IsLoaded && _loadOnShow) await RefreshAsync();
    }

    private void UpdatePagerVisibility()
    {
        if (ExpiryPager is null) return;
        ExpiryPager.Visibility = MoneyOutTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        DamagePager.Visibility = MoneyOutTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        ExpensePager.Visibility = MoneyOutTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageControls.Visibility = MoneyOutTabs.SelectedIndex == 3 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ExpiryTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ExpiryTabs)) return;
        if (ExpiryDamagePopup is not null) ExpiryDamagePopup.IsOpen = false;
        if (IsLoaded) ClearExpirySelection();
        UpdateExpiryButtons();
        UpdateExpiryActionVisibility();
    }

    private void ExpiryList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateExpiryActionVisibility();

    private void UpdateExpiryActionVisibility()
    {
        if (ExpiryTabs is null || BulkWriteOffButton is null || ExpiryRecordDamageButton is null) return;
        BulkWriteOffButton.Visibility = ExpiryTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool hasLot = ActiveExpiryList().SelectedItem is ExpiryLot;
        ExpiryRecordDamageButton.IsEnabled = hasLot;
        ExpiryBlockLotButton.IsEnabled = hasLot;
        ExpiryMarkDownButton.IsEnabled = hasLot;
        if (!hasLot && ExpiryDamagePopup is not null) ExpiryDamagePopup.IsOpen = false;
    }

    private void OpenExpiryDamage_OnClick(object sender, RoutedEventArgs e) =>
        ExpiryDamagePopup.IsOpen = !ExpiryDamagePopup.IsOpen;

    private async void RecordDamage_OnClick(object sender, RoutedEventArgs e)
    {
        if (ActiveExpiryList().SelectedItem is not ExpiryLot lot) return;
        try
        {
            Quantity quantity = new(Parse(DamageQuantity.Text));
            Money recovered = new(Parse(DamageRecovered.Text, 0));
            string reason = ((ComboBoxItem)DamageReason.SelectedItem).Tag?.ToString() ?? "OTHER";
            if (!ConfirmDamage(lot.LotCode, quantity)) return;
            await _service.RecordDamageAsync(new(lot.LotId, _clock.BusinessDate, quantity, reason, recovered, null), OperationGuard.NewId());
            DamageQuantity.Clear();
            ExpiryDamagePopup.IsOpen = false;
            ClearExpirySelection();
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void RecordAnyDamage_OnClick(object sender, RoutedEventArgs e)
    {
        if (DamageLot.SelectedItem is not LotItem lot) return;
        try
        {
            string reason = ((ComboBoxItem)DamageAnyReason.SelectedItem).Tag?.ToString() ?? "OTHER";
            Quantity quantity = new(Parse(DamageAnyQuantity.Text));
            if (!ConfirmDamage(lot.LotCode, quantity)) return;
            await _service.RecordDamageAsync(new(lot.Id, _clock.BusinessDate, quantity,
                reason, new Money(Parse(DamageAnyRecovered.Text, 0)), DamageNotes.Text), OperationGuard.NewId());
            DamageAnyQuantity.Clear();
            DamageAnyRecovered.Text = "0";
            DamageNotes.Clear();
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    // A selected entry opens its history and states how an edit would be dated.
    private async void DamageGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DamageGrid.SelectedItem is not DamageItem item) { DamageDetails.Clear(); return; }
        try
        {
            IReadOnlyList<DamageHistoryItem> history = await _service.GetDamageHistoryAsync(item.Id);
            CorrectionDating dating = await _service.PreviewCorrectionAsync(item.Date);
            if (!ReferenceEquals(DamageGrid.SelectedItem, item)) return;
            _damageDating = dating;
            DamageDetails.Show($"{item.DocumentNumber} · {item.ProductName}", dating, item.Status != "VOID",
                history.Select(DamageHistoryText));
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void DamageDetails_OnEdit(object? sender, EventArgs e) => await CorrectDamageAsync(false);
    private async void DamageDetails_OnVoid(object? sender, EventArgs e) => await CorrectDamageAsync(true);

    private async Task CorrectDamageAsync(bool voidEntry)
    {
        if (DamageGrid.SelectedItem is not DamageItem item || _damageDating is not { } dating) return;
        var window = new MoneyOutCorrectionWindow(_service, item, dating, voidEntry) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private static string DamageHistoryText(DamageHistoryItem item) => HistoryText(item.Kind, item.RecordedOn,
        item.Treatment, item.EffectiveDate, string.Join(" · ",
            $"{T("Stock_Quantity")} {Number(item.Quantity.Value)}",
            $"{T("MoneyOut_Recovered")} ₹{Number(item.RecoveredAmount.Value)}",
            ReasonText(item.Reason),
            $"{T(item.Kind == "ORIGINAL" ? "MoneyOut_Loss" : "MoneyOut_LossChange")} ₹{Number(item.LossChange.Value)}")
        + (item.RestoredLotId is null ? "" : " · " + T("MoneyOut_RestoredNewLot")), item.CorrectionReason);

    private async void BulkWriteOff_OnClick(object sender, RoutedEventArgs e)
    {
        long[] lots = _selectedExpiryLots.Values.Where(static item => item.Bucket == "EXPIRED")
            .Select(static item => item.LotId).ToArray();
        if (lots.Length == 0) return;
        if (MessageBox.Show(TranslationSource.Instance.Format("MoneyOut_BulkConfirm", lots.Length),
            TranslationSource.Instance["MoneyOut_WriteOff"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _service.WriteOffExpiredAsync(lots, _clock.BusinessDate, OperationGuard.NewId()); ClearExpirySelection(); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void BlockLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (ActiveExpiryList().SelectedItem is not ExpiryLot lot) return;
        if (MessageBox.Show(TranslationSource.Instance.Format("MoneyOut_BlockConfirm", lot.LotCode),
            TranslationSource.Instance["MoneyOut_BlockLot"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _inventory.SetLotActiveAsync(lot.LotId, false); ExpiryDamagePopup.IsOpen = false; ClearExpirySelection(); await RefreshAsync(); StatusText.Text = TranslationSource.Instance["MoneyOut_Saved"]; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void MarkDown_OnClick(object sender, RoutedEventArgs e)
    {
        if (ActiveExpiryList().SelectedItem is not ExpiryLot expiry) return;
        IReadOnlyList<LotItem> lots = await _inventory.GetLotsByIdsAsync([expiry.LotId]);
        if (lots.Count == 0) return;
        LotItem lot = lots[0];
        var window = new LotPriceWindow(_inventory, lot) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) { ExpiryDamagePopup.IsOpen = false; ClearExpirySelection(); await RefreshAsync(); }
    }

    private void PrintExpiry_OnClick(object sender, RoutedEventArgs e)
    {
        DataGrid list = ActiveExpiryList();
        List<ExpiryLot> rows = _selectedExpiryLots.Count > 0
            ? _selectedExpiryLots.Values.ToList()
            : list.Items.Cast<ExpiryLot>().ToList();
        if (rows.Count == 0) return;
        var document = new FlowDocument { PagePadding = new Thickness(40), ColumnWidth = double.PositiveInfinity };
        document.Blocks.Add(new Paragraph(new Run(TranslationSource.Instance["MoneyOut_VendorListTitle"]))
        { FontSize = 20, FontWeight = FontWeights.SemiBold });
        foreach (IGrouping<string, ExpiryLot> vendor in rows.GroupBy(static item => item.VendorName ?? "-"))
        {
            document.Blocks.Add(new Paragraph(new Run(vendor.Key)) { FontWeight = FontWeights.Bold });
            foreach (ExpiryLot lot in vendor)
                document.Blocks.Add(new Paragraph(new Run($"{lot.Sku}  {lot.ProductName}  {lot.LotCode}  {lot.ExpiryDate:yyyy-MM-dd}  {lot.Quantity}")) { Margin = new Thickness(12, 0, 0, 4) });
        }
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() == true)
            dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, TranslationSource.Instance["MoneyOut_VendorListTitle"]);
    }

    private async void SaveExpense_OnClick(object sender, RoutedEventArgs e)
    {
        if (ExpenseDate.SelectedDate is not { } date || ExpenseCategory.SelectedValue is not long category) return;
        try
        {
            if (ExpenseCategory.SelectedItem is ExpenseCategoryItem categoryItem && LooksLikeAsset(categoryItem.Name))
            {
                StatusText.Text = TranslationSource.Instance["MoneyOut_AssetWarning"];
                return;
            }
            string mode = ((ComboBoxItem)ExpenseMode.SelectedItem).Tag?.ToString() ?? "CASH";
            var input = new ExpenseInput(DateOnly.FromDateTime(date), category, new Money(Parse(ExpenseAmount.Text)), mode,
                ExpensePaidTo.Text, ExpenseReference.Text, ExpenseNotes.Text);
            await _service.RecordExpenseAsync(input, _expenseOperationId);
            _expenseOperationId = OperationGuard.NewId();
            if (RepeatDraftGrid.SelectedItem is ExpenseInput repeated && RepeatDraftGrid.ItemsSource is List<ExpenseInput> drafts)
            {
                drafts.Remove(repeated);
                RepeatDraftGrid.Items.Refresh();
                if (drafts.Count == 0) RepeatDraftGrid.Visibility = Visibility.Collapsed;
            }
            ExpenseAmount.Clear(); ExpensePaidTo.Clear(); ExpenseReference.Clear(); ExpenseNotes.Clear();
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void ExpenseGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExpenseGrid.SelectedItem is not ExpenseItem item) { ExpenseDetails.Clear(); return; }
        try
        {
            IReadOnlyList<ExpenseHistoryItem> history = await _service.GetExpenseHistoryAsync(item.Id);
            CorrectionDating dating = await _service.PreviewCorrectionAsync(item.Date);
            if (!ReferenceEquals(ExpenseGrid.SelectedItem, item)) return;
            _expenseDating = dating;
            ExpenseDetails.Show($"{Day(item.Date)} · {item.CategoryName}", item.Status == "PURCHASE" ? null : dating,
                item.Status is "ACTIVE" or "CORRECTED", history.Select(ExpenseHistoryText));
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
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
            if (window.ShowDialog() == true) await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private static string ExpenseHistoryText(ExpenseHistoryItem item) => HistoryText(item.Kind, item.RecordedOn,
        item.Treatment, item.AppliedOn ?? item.Date, string.Join(" · ", new[]
        {
            Day(item.Date), item.CategoryName, $"₹{Number(item.Amount.Value)}", item.Mode, item.PaidTo,
        }.Where(static value => !string.IsNullOrWhiteSpace(value))), item.CorrectionReason);

    private void NewExpense_OnClick(object sender, RoutedEventArgs e)
    {
        ExpenseGrid.SelectedItem = null;
        ShowExpenseEntry();
        ExpenseDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        ExpenseAmount.Clear(); ExpensePaidTo.Clear(); ExpenseReference.Clear(); ExpenseNotes.Clear();
        ExpenseAmount.Focus();
    }

    private void OpenAssets_OnClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.FindName("RootNavigation") is Wpf.Ui.Controls.NavigationView navigation)
            navigation.Navigate(typeof(AssetsPage));
    }

    private async void RepeatExpense_OnClick(object sender, RoutedEventArgs e)
    {
        long[] categories = RepeatCategories.SelectedItems.Cast<ExpenseCategoryItem>().Select(static item => item.Id).ToArray();
        if (categories.Length == 0 && ExpenseCategory.SelectedValue is long category) categories = [category];
        if (categories.Length == 0) return;
        try
        {
            IReadOnlyList<ExpenseInput> drafts = await _service.RepeatLastMonthAsync(YearMonth.From(_clock.BusinessDate), categories);
            if (drafts.Count == 0) return;
            RepeatDraftGrid.ItemsSource = drafts;
            RepeatDraftGrid.Visibility = Visibility.Visible;
            ShowExpenseEntry();
            ExpenseDate.SelectedDate = drafts[0].Date.ToDateTime(TimeOnly.MinValue);
            ExpenseAmount.Text = drafts[0].Amount.Value.ToString("0.000", TranslationSource.FormatCulture);
            ExpensePaidTo.Text = drafts[0].PaidTo;
            ExpenseReference.Text = drafts[0].Reference;
            ExpenseNotes.Text = drafts[0].Notes;
            SelectMode(ExpenseMode, drafts[0].Mode);
            StatusText.Text = TranslationSource.Instance.Format("MoneyOut_DraftsFound", drafts.Count);
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void RepeatCategories_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRepeatCategoryCount();

    private void ExpenseSection_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_changingExpenseSection || ExpenseEntryPanel is null || ExpenseCategoryPanel is null) return;
        _changingExpenseSection = true;
        try
        {
            if (ReferenceEquals(sender, ExpenseEntryTab) && ExpenseEntryTab.IsChecked == true)
                ExpenseCategoryTab.IsChecked = false;
            else if (ReferenceEquals(sender, ExpenseCategoryTab) && ExpenseCategoryTab.IsChecked == true)
                ExpenseEntryTab.IsChecked = false;
            SectionMotion.SetOpen(ExpenseEntryPanel, ExpenseEntryTab.IsChecked == true);
            SectionMotion.SetOpen(ExpenseCategoryPanel, ExpenseCategoryTab.IsChecked == true);
        }
        finally { _changingExpenseSection = false; }
    }

    private void ShowExpenseEntry()
    {
        ExpenseCategoryTab.IsChecked = false;
        ExpenseEntryTab.IsChecked = true;
    }

    private void ClearRepeatCategories_OnClick(object sender, RoutedEventArgs e) => RepeatCategories.UnselectAll();

    private void UpdateRepeatCategoryCount()
    {
        if (RepeatCategorySelectedCount is not null && RepeatCategories is not null)
            RepeatCategorySelectedCount.Text = TranslationSource.Instance.Format("Selection_Count", RepeatCategories.SelectedItems.Count);
    }

    private void RepeatDraftGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepeatDraftGrid.SelectedItem is not ExpenseInput draft) return;
        ExpenseDate.SelectedDate = draft.Date.ToDateTime(TimeOnly.MinValue);
        ExpenseCategory.SelectedValue = draft.CategoryId;
        ExpenseAmount.Text = draft.Amount.Value.ToString("0.000", TranslationSource.FormatCulture);
        ExpensePaidTo.Text = draft.PaidTo;
        ExpenseReference.Text = draft.Reference;
        ExpenseNotes.Text = draft.Notes;
        SelectMode(ExpenseMode, draft.Mode);
    }

    private async void AddCategory_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _service.SaveExpenseCategoryAsync(_editingCategoryId, NewCategoryName.Text, NewCategoryLocal.Text, CategoryActive.IsChecked == true);
            NewCategory_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void ExpenseCategory_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryMaster.SelectedItem is not ExpenseCategoryItem item) return;
        _editingCategoryId = item.Id;
        NewCategoryName.Text = item.Name;
        NewCategoryLocal.Text = item.LocalName ?? "";
        CategoryActive.IsChecked = item.IsActive;
    }

    private void NewCategory_OnClick(object sender, RoutedEventArgs e)
    {
        _editingCategoryId = null;
        NewCategoryName.Clear(); NewCategoryLocal.Clear(); CategoryActive.IsChecked = true;
        NewCategoryName.Focus();
    }

    private async void AddEmployee_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            YearMonth? salaryFrom = EmployeeSalaryFromPanel.Visibility == Visibility.Visible
                ? YearMonth.Parse(EmployeeSalaryFrom.Text.Trim()) : null;
            await _service.SaveEmployeeAsync(_editingEmployeeId, new(EmployeeName.Text, EmployeePhone.Text, EmployeeRole.Text,
                EmployeeJoinDate.SelectedDate is { } join ? DateOnly.FromDateTime(join) : null,
                EmployeeLeaveDate.SelectedDate is { } leave ? DateOnly.FromDateTime(leave) : null,
                new Money(Parse(EmployeeSalary.Text)), EmployeeActive.IsChecked == true, EmployeeNotes.Text), salaryFrom);
            NewEmployee_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (FormatException exception) { StatusText.Text = exception.Message; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void EmployeeGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmployeeGrid.SelectedItem is not EmployeeItem employee) { EmployeeDetails.Clear(); return; }
        EmployeeDetailsExpander.IsExpanded = true;
        _editingEmployeeId = employee.Id;
        _editingEmployeeSalary = employee.MonthlySalary;
        EmployeeName.Text = employee.Name;
        EmployeePhone.Text = employee.Phone ?? "";
        EmployeeRole.Text = employee.Role ?? "";
        EmployeeJoinDate.SelectedDate = employee.JoinDate?.ToDateTime(TimeOnly.MinValue);
        EmployeeLeaveDate.SelectedDate = employee.LeaveDate?.ToDateTime(TimeOnly.MinValue);
        EmployeeSalary.Text = employee.MonthlySalary.Value.ToString("0.000", TranslationSource.FormatCulture);
        EmployeeActive.IsChecked = employee.IsActive;
        EmployeeNotes.Text = employee.Notes ?? "";
        try
        {
            IReadOnlyList<SalaryTermItem> terms = await _service.GetSalaryTermsAsync(employee.Id);
            if (!ReferenceEquals(EmployeeGrid.SelectedItem, employee)) return;
            EmployeeDetails.Show(employee.Name, null, false, terms.Select(term => TranslationSource.Instance.Format(
                "MoneyOut_SalaryTermLine", term.EffectiveMonth.ToString(), Number(term.MonthlySalary.Value),
                T($"MoneyOut_TermReason_{term.Reason}"), term.CreatedAt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture))),
                actions: false);
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    // A changed salary needs the month it applies from; posted payments stay as paid.
    private async void EmployeeSalary_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (EmployeeSalaryFromPanel is null || EmployeeSalaryHint is null) return;
        bool changed = _editingEmployeeId is not null && _editingEmployeeSalary is { } previous &&
            decimal.TryParse(EmployeeSalary.Text, NumberStyles.Number, TranslationSource.FormatCulture, out decimal salary) &&
            salary != previous.Value;
        if (!changed)
        {
            EmployeeSalaryFromPanel.Visibility = Visibility.Collapsed;
            EmployeeSalaryHint.Visibility = Visibility.Collapsed;
            return;
        }
        if (EmployeeSalaryFromPanel.Visibility != Visibility.Visible)
        {
            EmployeeSalaryFromPanel.Visibility = Visibility.Visible;
            EmployeeSalaryFrom.Text = YearMonth.From(_clock.BusinessDate).ToString();
        }
        int version = ++_salaryHintVersion;
        try
        {
            YearMonth effective = YearMonth.Parse(EmployeeSalaryFrom.Text.Trim());
            SalaryTermPreview preview = await _service.PreviewSalaryTermAsync(_editingEmployeeId!.Value, effective);
            if (version != _salaryHintVersion) return;
            EmployeeSalaryHint.Text = TranslationSource.Instance.Format(
                preview.EffectiveMonth < preview.FirstOpenMonth ? "MoneyOut_SalaryFromClosed" : "MoneyOut_SalaryFromHint",
                preview.EffectiveMonth.ToString(), preview.FirstOpenMonth.ToString(), preview.PostedMonths);
        }
        catch (FormatException)
        {
            if (version != _salaryHintVersion) return;
            EmployeeSalaryHint.Text = T("MoneyOut_SalaryFromFormat");
        }
        catch (Exception exception) { EmployeeSalaryHint.Text = UserError.Message(exception); }
        EmployeeSalaryHint.Visibility = Visibility.Visible;
    }

    private void NewEmployee_OnClick(object sender, RoutedEventArgs e)
    {
        EmployeeDetailsExpander.IsExpanded = true;
        EmployeeGrid.SelectedItem = null;
        _editingEmployeeId = null;
        _editingEmployeeSalary = null;
        EmployeeName.Clear(); EmployeePhone.Clear(); EmployeeRole.Clear(); EmployeeSalary.Clear();
        EmployeeLeaveDate.SelectedDate = null; EmployeeNotes.Clear(); EmployeeActive.IsChecked = true;
        EmployeeJoinDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        EmployeeName.Focus();
    }

    private async void PrepareSalary_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Dictionary<long, string> employeeNames = EmployeeGrid.Items.OfType<EmployeeItem>()
                .ToDictionary(static employee => employee.Id, static employee => employee.Name);
            SalaryGrid.ItemsSource = (await _service.PrepareSalaryRunAsync(YearMonth.Parse(SalaryPeriod.Text)))
                .Select(line => SalaryEditRow.From(line, employeeNames.GetValueOrDefault(line.EmployeeId,
                    line.EmployeeId.ToString(TranslationSource.FormatCulture)))).ToList();
            UpdateSalarySelection();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void SalaryPageCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        _salaryPageCheck = (CheckBox)sender;
        UpdateSalarySelection();
    }

    private void SalaryPageCheck_OnClick(object sender, RoutedEventArgs e)
    {
        SalaryEditRow[] rows = SalaryGrid.Items.Cast<SalaryEditRow>().ToArray();
        if (rows.Length == 0) return;
        bool select = !rows.All(static row => row.IsSelected);
        foreach (SalaryEditRow row in rows) row.IsSelected = select;
        SalaryGrid.Items.Refresh();
        UpdateSalarySelection();
    }

    private void SalaryCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: SalaryEditRow row } box) row.IsSelected = box.IsChecked == true;
        UpdateSalarySelection();
    }

    private void UpdateSalarySelection()
    {
        if (SalaryGrid is null || SalarySelectedCount is null) return;
        SalaryEditRow[] rows = SalaryGrid.ItemsSource is null ? [] : SalaryGrid.Items.Cast<SalaryEditRow>().ToArray();
        int selected = rows.Count(static row => row.IsSelected);
        SalarySelectedCount.Text = TranslationSource.Instance.Format("Selection_Count", selected);
        PostSalaryButton.IsEnabled = selected > 0;
        if (_salaryPageCheck is not null)
            _salaryPageCheck.IsChecked = rows.Length == 0 ? false : selected == 0 ? false : selected == rows.Length ? true : null;
    }

    private async void PostSalary_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            YearMonth period = YearMonth.Parse(SalaryPeriod.Text);
            if (SalaryPayDate.SelectedDate is not { } payDate) return;
            string mode = ((ComboBoxItem)SalaryMode.SelectedItem).Tag?.ToString() ?? "BANK";
            SalaryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            SalaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
            List<SalaryRunLine> lines = SalaryGrid.Items.Cast<SalaryEditRow>()
                .Where(static row => row.IsSelected).Select(static row => row.ToModel()).ToList();
            if (lines.Count == 0) return;
            await _service.PostSalaryRunAsync(new(period, DateOnly.FromDateTime(payDate), mode, lines), OperationGuard.NewId());
            SalaryGrid.ItemsSource = null;
            UpdateSalarySelection();
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void RecordAdvance_OnClick(object sender, RoutedEventArgs e)
    {
        if (AdvanceEmployeeBox.SelectedItem is not EmployeeItem employee || AdvancePayDate.SelectedDate is not { } payDate) return;
        try
        {
            string mode = ((ComboBoxItem)AdvanceMode.SelectedItem).Tag?.ToString() ?? "BANK";
            await _service.RecordOtherSalaryPaymentAsync(employee.Id, DateOnly.FromDateTime(payDate),
                new Money(Parse(AdvanceAmount.Text)), mode, AdvanceNotes.Text, OperationGuard.NewId());
            AdvanceAmount.Clear(); AdvanceNotes.Clear();
            await RefreshSalaryRegisterAsync();
            StatusText.Text = TranslationSource.Instance["MoneyOut_Saved"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void SalaryRegisterGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SalaryRegisterGrid.SelectedItem is not SalaryPaymentItem item) { SalaryDetails.Clear(); return; }
        try
        {
            IReadOnlyList<SalaryHistoryItem> history = await _service.GetSalaryHistoryAsync(item.Id);
            CorrectionDating dating = await _service.PreviewCorrectionAsync(item.Period.LastDay);
            if (!ReferenceEquals(SalaryRegisterGrid.SelectedItem, item)) return;
            _salaryDating = dating;
            SalaryDetails.Show($"{item.EmployeeName} · {item.Period}", dating, item.Status != "VOID",
                history.Select(static entry => HistoryText(entry.Kind, entry.RecordedOn, entry.Treatment,
                    entry.EffectiveDate, string.Join(" · ", $"₹{Number(entry.Amount.Value)}",
                        $"{T("MoneyOut_PayDate")} {Day(entry.PayDate)}", entry.Mode), entry.CorrectionReason)));
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void SalaryDetails_OnEdit(object? sender, EventArgs e) => await CorrectSalaryAsync(false);
    private async void SalaryDetails_OnVoid(object? sender, EventArgs e) => await CorrectSalaryAsync(true);

    private async Task CorrectSalaryAsync(bool voidEntry)
    {
        if (SalaryRegisterGrid.SelectedItem is not SalaryPaymentItem item || _salaryDating is not { } dating) return;
        var window = new MoneyOutCorrectionWindow(_service, item, dating, voidEntry) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true)
            try { await RefreshSalaryRegisterAsync(); }
            catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
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

    private static string ReasonText(string reason) => reason switch
    {
        "EXPIRED" => T("MoneyOut_ReasonExpired"),
        "BROKEN" => T("MoneyOut_ReasonBroken"),
        "SPOILED" => T("MoneyOut_ReasonSpoiled"),
        "PEST" => T("MoneyOut_ReasonPest"),
        "LOST" => T("MoneyOut_ReasonLost"),
        "CUSTOMER_RETURN" => T("MoneyOut_ReasonReturn"),
        _ => T("MoneyOut_ReasonOther"),
    };

    private static string Day(DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
    private static string Number(decimal value) => value.ToString("N3", TranslationSource.FormatCulture);
    private static string T(string key) => TranslationSource.Instance[key];

    private async void RefreshSalaryRegister_OnClick(object sender, RoutedEventArgs e)
    {
        try { await RefreshSalaryRegisterAsync(); }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async Task RefreshSalaryRegisterAsync()
    {
        YearMonth month = YearMonth.Parse(SalaryRegisterPeriod.Text);
        SalaryRegisterGrid.ItemsSource = await _service.GetSalaryRegisterAsync(month, month);
    }

    private async Task RefreshAsync()
    {
        using var timing = UiRefreshProbe.Track("money-out", this);
        try
        {
            switch (MoneyOutTabs.SelectedIndex)
            {
                case 0:
                    foreach (string bucket in new[] { "EXPIRED", "CRITICAL", "SOON" })
                    {
                        _expiryPrevious.Remove(bucket);
                        await LoadExpiryPageAsync(bucket, null, null);
                    }
                    if (Window.GetWindow(this) is ShellWindow shell)
                        shell.SetExpiryBadge(await _service.GetActionableExpiryCountAsync());
                    break;
                case 1:
                    _damagePrevious.Clear();
                    await LoadDamagePageAsync(null, null);
                    await SearchDamageLotsAsync();
                    break;
                case 2:
                    _expensePrevious.Clear();
                    await LoadExpensePageAsync(null, null);
                    long? categoryId = ExpenseCategory.SelectedValue as long?;
                    ExpenseCategory.ItemsSource = await _service.GetExpenseCategoriesAsync();
                    ExpenseCategory.SelectedValue = categoryId;
                    if (ExpenseCategory.SelectedIndex < 0) ExpenseCategory.SelectedIndex = 0;
                    RepeatCategories.ItemsSource = ExpenseCategory.ItemsSource;
                    IReadOnlyList<ExpenseCategoryItem> categories = await _service.GetExpenseCategoriesAsync(includeInactive: true);
                    CategoryMaster.ItemsSource = categories;
                    CategoryMaster.SelectedItem = categories.FirstOrDefault(item => item.Id == _editingCategoryId);
                    break;
                case 3:
                    IReadOnlyList<EmployeeItem> employees = await _service.GetEmployeesAsync(includeInactive: true);
                    EmployeeGrid.ItemsSource = employees;
                    EmployeeGrid.SelectedItem = employees.FirstOrDefault(item => item.Id == _editingEmployeeId);
                    long? advanceEmployeeId = AdvanceEmployeeBox.SelectedValue as long?;
                    AdvanceEmployeeBox.ItemsSource = employees.Where(static employee => employee.IsActive).ToList();
                    AdvanceEmployeeBox.SelectedValue = advanceEmployeeId;
                    await RefreshSalaryRegisterAsync();
                    break;
            }
            StatusText.Text = TranslationSource.Instance["MoneyOut_Ready"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async Task LoadExpiryPageAsync(string bucket, DateOnly? afterDate, long? afterId)
    {
        ExpiryPage page = await _service.GetExpiryPageAsync(bucket, afterDate, afterId, PageSize);
        _expiryPages[bucket] = page;
        _expiryCursors[bucket] = (afterDate, afterId);
        switch (bucket)
        {
            case "EXPIRED": UiRefreshProbe.RecordBinding("money-out", () => ExpiredList.ItemsSource = page.Items); break;
            case "CRITICAL": UiRefreshProbe.RecordBinding("money-out", () => CriticalList.ItemsSource = page.Items); break;
            case "SOON": UiRefreshProbe.RecordBinding("money-out", () => SoonList.ItemsSource = page.Items); break;
        }
        UpdateExpiryHeaderChecks();
        UpdateExpiryButtons();
    }

    private string ActiveExpiryBucket() => ExpiryTabs.SelectedIndex switch
    {
        1 => "CRITICAL",
        2 => "SOON",
        _ => "EXPIRED",
    };

    private void ExpiryCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: ExpiryLot lot } box)
            box.IsChecked = _selectedExpiryLots.ContainsKey(lot.LotId);
    }

    private void ExpiryCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ExpiryLot lot } box) return;
        if (box.IsChecked == true) _selectedExpiryLots[lot.LotId] = lot;
        else _selectedExpiryLots.Remove(lot.LotId);
        UpdateExpirySelection();
    }

    private void ExpiryHeaderCheck_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: DataGrid grid } box) return;
        _expiryHeaderChecks[grid] = box;
        UpdateExpiryHeaderCheck(grid);
    }

    private void ExpiryHeaderCheck_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: DataGrid grid } box &&
            _expiryHeaderChecks.TryGetValue(grid, out CheckBox? current) && ReferenceEquals(current, box))
            _expiryHeaderChecks.Remove(grid);
    }

    private void ExpiryHeaderCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: DataGrid grid } box) return;
        bool select = box.IsChecked == true;
        foreach (ExpiryLot lot in grid.Items.OfType<ExpiryLot>())
        {
            if (select) _selectedExpiryLots[lot.LotId] = lot;
            else _selectedExpiryLots.Remove(lot.LotId);
        }
        RefreshExpiryChecks();
    }

    private void ClearExpirySelection()
    {
        _selectedExpiryLots.Clear();
        RefreshExpiryChecks();
    }

    private void UpdateExpiryHeaderChecks()
    {
        foreach (DataGrid grid in _expiryHeaderChecks.Keys.ToArray()) UpdateExpiryHeaderCheck(grid);
    }

    private void UpdateExpiryHeaderCheck(DataGrid grid)
    {
        if (!_expiryHeaderChecks.TryGetValue(grid, out CheckBox? box)) return;
        ExpiryLot[] rows = grid.Items.OfType<ExpiryLot>().ToArray();
        int selected = rows.Count(lot => _selectedExpiryLots.ContainsKey(lot.LotId));
        box.IsEnabled = rows.Length > 0;
        box.IsChecked = selected == 0 ? false : selected == rows.Length ? true : null;
    }

    private void RefreshExpiryChecks()
    {
        if (ExpiredList is null) return;
        ExpiredList.Items.Refresh(); CriticalList.Items.Refresh(); SoonList.Items.Refresh();
        UpdateExpirySelection();
    }

    private void UpdateExpirySelection()
    {
        if (ExpirySelectedCount is null) return;
        ExpirySelectedCount.Text = TranslationSource.Instance.Format("Selection_Count", _selectedExpiryLots.Count);
        BulkWriteOffButton.IsEnabled = _selectedExpiryLots.Count > 0 &&
            _selectedExpiryLots.Values.All(static lot => lot.Bucket == "EXPIRED");
        UpdateExpiryHeaderChecks();
    }

    private async void PageSize_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await RefreshAsync();
    }

    private void UpdateExpiryButtons()
    {
        if (PreviousExpiryButton is null) return;
        string bucket = ActiveExpiryBucket();
        PreviousExpiryButton.IsEnabled = _expiryPrevious.GetValueOrDefault(bucket)?.Count > 0;
        NextExpiryButton.IsEnabled = _expiryPages.GetValueOrDefault(bucket)?.HasMore == true;
    }

    private async void NextExpiry_OnClick(object sender, RoutedEventArgs e)
    {
        string bucket = ActiveExpiryBucket();
        if (!_expiryPages.TryGetValue(bucket, out ExpiryPage? page) || !page.HasMore ||
            page.NextDate is null || page.NextId is null) return;
        (DateOnly? Date, long? Id) previous = _expiryCursors.GetValueOrDefault(bucket);
        try
        {
            await LoadExpiryPageAsync(bucket, page.NextDate, page.NextId);
            if (!_expiryPrevious.TryGetValue(bucket, out Stack<(DateOnly? Date, long? Id)>? stack))
                _expiryPrevious[bucket] = stack = new();
            stack.Push(previous);
            UpdateExpiryButtons();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void PreviousExpiry_OnClick(object sender, RoutedEventArgs e)
    {
        string bucket = ActiveExpiryBucket();
        if (!_expiryPrevious.TryGetValue(bucket, out Stack<(DateOnly? Date, long? Id)>? stack) || stack.Count == 0) return;
        (DateOnly? Date, long? Id) previous = stack.Peek();
        try
        {
            await LoadExpiryPageAsync(bucket, previous.Date, previous.Id);
            stack.Pop();
            UpdateExpiryButtons();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void SearchDamageLot_OnClick(object sender, RoutedEventArgs e)
    {
        try { await SearchDamageLotsAsync(); }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async Task SearchDamageLotsAsync() => DamageLot.ItemsSource =
        (await _inventory.GetLotPageAsync(query: DamageLotSearchBox.Text, availableOnly: true)).Items;

    private async Task LoadDamagePageAsync(DateOnly? beforeDate, long? beforeId)
    {
        DateOnly from = DateOnly.FromDateTime(DamageFromBox.SelectedDate ?? DateTime.MinValue);
        DateOnly to = DateOnly.FromDateTime(DamageToBox.SelectedDate ?? DateTime.MaxValue);
        DamagePage page = await _service.GetDamagePageAsync(from, to, beforeDate, beforeId, PageSize);
        _damagePage = page;
        _damageCursor = (beforeDate, beforeId);
        UiRefreshProbe.RecordBinding("money-out", () => DamageGrid.ItemsSource = page.Items);
        PreviousDamageButton.IsEnabled = _damagePrevious.Count > 0;
        NextDamageButton.IsEnabled = page.HasMore;
    }

    private async Task LoadExpensePageAsync(DateOnly? beforeDate, long? beforeId)
    {
        DateOnly from = DateOnly.FromDateTime(ExpenseFromBox.SelectedDate ?? DateTime.MinValue);
        DateOnly to = DateOnly.FromDateTime(ExpenseToBox.SelectedDate ?? DateTime.MaxValue);
        ExpensePage page = await _service.GetExpensePageAsync(from, to, beforeDate, beforeId, PageSize);
        _expensePage = page;
        _expenseCursor = (beforeDate, beforeId);
        UiRefreshProbe.RecordBinding("money-out", () => ExpenseGrid.ItemsSource = page.Items);
        PreviousExpenseButton.IsEnabled = _expensePrevious.Count > 0;
        NextExpenseButton.IsEnabled = page.HasMore;
    }

    private async void DamageFilter_OnClick(object sender, RoutedEventArgs e)
    {
        try { _damagePrevious.Clear(); await LoadDamagePageAsync(null, null); }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void ExpenseFilter_OnClick(object sender, RoutedEventArgs e)
    {
        try { _expensePrevious.Clear(); await LoadExpensePageAsync(null, null); }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void NextDamage_OnClick(object sender, RoutedEventArgs e)
    {
        if (_damagePage is not { HasMore: true, NextDate: not null, NextId: not null } page) return;
        (DateOnly? Date, long? Id) previous = _damageCursor;
        try { await LoadDamagePageAsync(page.NextDate, page.NextId); _damagePrevious.Push(previous); PreviousDamageButton.IsEnabled = true; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void PreviousDamage_OnClick(object sender, RoutedEventArgs e)
    {
        if (_damagePrevious.Count == 0) return;
        (DateOnly? Date, long? Id) previous = _damagePrevious.Peek();
        try { await LoadDamagePageAsync(previous.Date, previous.Id); _damagePrevious.Pop(); PreviousDamageButton.IsEnabled = _damagePrevious.Count > 0; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void NextExpense_OnClick(object sender, RoutedEventArgs e)
    {
        if (_expensePage is not { HasMore: true, NextDate: not null, NextId: not null } page) return;
        (DateOnly? Date, long? Id) previous = _expenseCursor;
        try { await LoadExpensePageAsync(page.NextDate, page.NextId); _expensePrevious.Push(previous); PreviousExpenseButton.IsEnabled = true; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void PreviousExpense_OnClick(object sender, RoutedEventArgs e)
    {
        if (_expensePrevious.Count == 0) return;
        (DateOnly? Date, long? Id) previous = _expensePrevious.Peek();
        try { await LoadExpensePageAsync(previous.Date, previous.Id); _expensePrevious.Pop(); PreviousExpenseButton.IsEnabled = _expensePrevious.Count > 0; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private DataGrid ActiveExpiryList() => ExpiryTabs.SelectedIndex switch
    {
        1 => CriticalList,
        2 => SoonList,
        _ => ExpiredList,
    };

    private static bool LooksLikeAsset(string category) =>
        category.Contains("equipment", StringComparison.OrdinalIgnoreCase) ||
        category.Contains("furniture", StringComparison.OrdinalIgnoreCase) ||
        category.Contains("computer", StringComparison.OrdinalIgnoreCase) ||
        category.Contains("vehicle", StringComparison.OrdinalIgnoreCase) ||
        category.Contains("asset", StringComparison.OrdinalIgnoreCase);

    private static void SelectMode(ComboBox box, string mode)
    {
        foreach (ComboBoxItem item in box.Items)
            if (string.Equals(item.Tag?.ToString(), mode, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
    }

    private static bool ConfirmDamage(string lotCode, Quantity quantity) =>
        MessageBox.Show(TranslationSource.Instance.Format("MoneyOut_DamageConfirm", quantity, lotCode),
            TranslationSource.Instance["MoneyOut_RecordDamage"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private static decimal Parse(string? text, decimal fallback = decimal.MinValue)
    {
        if (string.IsNullOrWhiteSpace(text) && fallback != decimal.MinValue) return fallback;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("en-IN"), out decimal value))
            throw new FormatException(TranslationSource.Instance["MoneyOut_ValidNumber"]);
        return value;
    }

    private sealed class SalaryEditRow
    {
        public bool IsSelected { get; set; } = true;
        public long EmployeeId { get; init; }
        public string EmployeeName { get; init; } = "";
        public string BaseSalary { get; set; } = "0.000";
        public string LeaveDeduction { get; set; } = "0.000";
        public string Overtime { get; set; } = "0.000";
        public string Bonus { get; set; } = "0.000";
        public static SalaryEditRow From(SalaryRunLine line, string employeeName) => new()
        {
            EmployeeId = line.EmployeeId,
            EmployeeName = employeeName,
            BaseSalary = line.BaseSalary.Value.ToString("0.000", TranslationSource.FormatCulture),
        };
        public SalaryRunLine ToModel() => new(EmployeeId, new Money(Parse(BaseSalary)), new Money(Parse(LeaveDeduction)), new Money(Parse(Overtime)), new Money(Parse(Bonus)), null);
    }
}
