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
    private readonly MoneyOutService _service;
    private readonly InventoryService _inventory;
    private readonly IClock _clock;
    private readonly bool _loadOnShow;
    private long? _editingCategoryId;
    private long? _editingEmployeeId;

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
        ExpenseDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        EmployeeJoinDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        SalaryPayDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        SalaryPeriod.Text = YearMonth.From(_clock.BusinessDate).ToString();
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Tabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _loadOnShow) await RefreshAsync();
    }

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
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
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
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void BulkWriteOff_OnClick(object sender, RoutedEventArgs e)
    {
        long[] lots = ActiveExpiryList().SelectedItems.Cast<ExpiryLot>().Where(static item => item.Bucket == "EXPIRED").Select(static item => item.LotId).ToArray();
        if (lots.Length == 0) return;
        if (MessageBox.Show(TranslationSource.Instance.Format("MoneyOut_BulkConfirm", lots.Length),
            TranslationSource.Instance["MoneyOut_WriteOff"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _service.WriteOffExpiredAsync(lots, _clock.BusinessDate, OperationGuard.NewId()); await RefreshAsync(); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void BlockLot_OnClick(object sender, RoutedEventArgs e)
    {
        if (ActiveExpiryList().SelectedItem is not ExpiryLot lot) return;
        if (MessageBox.Show(TranslationSource.Instance.Format("MoneyOut_BlockConfirm", lot.LotCode),
            TranslationSource.Instance["MoneyOut_BlockLot"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _inventory.SetLotActiveAsync(lot.LotId, false); await RefreshAsync(); StatusText.Text = TranslationSource.Instance["MoneyOut_Saved"]; }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void MarkDown_OnClick(object sender, RoutedEventArgs e)
    {
        if (ActiveExpiryList().SelectedItem is not ExpiryLot expiry) return;
        LotItem? lot = (await _inventory.GetLotsAsync(expiry.ProductId)).FirstOrDefault(item => item.Id == expiry.LotId);
        if (lot is null) return;
        var window = new LotPriceWindow(_inventory, lot) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private void PrintExpiry_OnClick(object sender, RoutedEventArgs e)
    {
        ListBox list = ActiveExpiryList();
        List<ExpiryLot> rows = list.SelectedItems.Count > 0
            ? list.SelectedItems.Cast<ExpiryLot>().ToList()
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
            await _service.RecordExpenseAsync(new(DateOnly.FromDateTime(date), category, new Money(Parse(ExpenseAmount.Text)), mode,
                ExpensePaidTo.Text, ExpenseReference.Text, ExpenseNotes.Text), OperationGuard.NewId());
            if (RepeatDraftGrid.SelectedItem is ExpenseInput repeated && RepeatDraftGrid.ItemsSource is List<ExpenseInput> drafts)
            {
                drafts.Remove(repeated);
                RepeatDraftGrid.Items.Refresh();
                if (drafts.Count == 0) RepeatDraftGrid.Visibility = Visibility.Collapsed;
            }
            ExpenseAmount.Clear(); ExpensePaidTo.Clear(); ExpenseReference.Clear(); ExpenseNotes.Clear();
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
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
            ExpenseDate.SelectedDate = drafts[0].Date.ToDateTime(TimeOnly.MinValue);
            ExpenseAmount.Text = drafts[0].Amount.ToString();
            ExpensePaidTo.Text = drafts[0].PaidTo;
            ExpenseReference.Text = drafts[0].Reference;
            ExpenseNotes.Text = drafts[0].Notes;
            SelectMode(ExpenseMode, drafts[0].Mode);
            StatusText.Text = TranslationSource.Instance.Format("MoneyOut_DraftsFound", drafts.Count);
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private void RepeatDraftGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepeatDraftGrid.SelectedItem is not ExpenseInput draft) return;
        ExpenseDate.SelectedDate = draft.Date.ToDateTime(TimeOnly.MinValue);
        ExpenseCategory.SelectedValue = draft.CategoryId;
        ExpenseAmount.Text = draft.Amount.ToString();
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
        catch (Exception exception) { StatusText.Text = exception.Message; }
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
            await _service.SaveEmployeeAsync(_editingEmployeeId, new(EmployeeName.Text, EmployeePhone.Text, EmployeeRole.Text,
                EmployeeJoinDate.SelectedDate is { } join ? DateOnly.FromDateTime(join) : null,
                EmployeeLeaveDate.SelectedDate is { } leave ? DateOnly.FromDateTime(leave) : null,
                new Money(Parse(EmployeeSalary.Text)), EmployeeActive.IsChecked == true, EmployeeNotes.Text));
            NewEmployee_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private void EmployeeGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmployeeGrid.SelectedItem is not EmployeeItem employee) return;
        _editingEmployeeId = employee.Id;
        EmployeeName.Text = employee.Name;
        EmployeePhone.Text = employee.Phone ?? "";
        EmployeeRole.Text = employee.Role ?? "";
        EmployeeJoinDate.SelectedDate = employee.JoinDate?.ToDateTime(TimeOnly.MinValue);
        EmployeeLeaveDate.SelectedDate = employee.LeaveDate?.ToDateTime(TimeOnly.MinValue);
        EmployeeSalary.Text = employee.MonthlySalary.ToString();
        EmployeeActive.IsChecked = employee.IsActive;
        EmployeeNotes.Text = employee.Notes ?? "";
    }

    private void NewEmployee_OnClick(object sender, RoutedEventArgs e)
    {
        EmployeeGrid.SelectedItem = null;
        _editingEmployeeId = null;
        EmployeeName.Clear(); EmployeePhone.Clear(); EmployeeRole.Clear(); EmployeeSalary.Clear();
        EmployeeLeaveDate.SelectedDate = null; EmployeeNotes.Clear(); EmployeeActive.IsChecked = true;
        EmployeeJoinDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        EmployeeName.Focus();
    }

    private async void PrepareSalary_OnClick(object sender, RoutedEventArgs e)
    {
        try { SalaryGrid.ItemsSource = (await _service.PrepareSalaryRunAsync(YearMonth.Parse(SalaryPeriod.Text))).Select(SalaryEditRow.From).ToList(); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
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
            List<SalaryRunLine> lines = SalaryGrid.Items.Cast<SalaryEditRow>().Select(static row => row.ToModel()).ToList();
            await _service.PostSalaryRunAsync(new(period, DateOnly.FromDateTime(payDate), mode, lines), OperationGuard.NewId());
            SalaryGrid.ItemsSource = null; await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void RecordAdvance_OnClick(object sender, RoutedEventArgs e)
    {
        if (EmployeeGrid.SelectedItem is not EmployeeItem employee || SalaryPayDate.SelectedDate is not { } payDate) return;
        try
        {
            string mode = ((ComboBoxItem)SalaryMode.SelectedItem).Tag?.ToString() ?? "BANK";
            await _service.RecordOtherSalaryPaymentAsync(employee.Id, DateOnly.FromDateTime(payDate),
                new Money(Parse(AdvanceAmount.Text)), mode, AdvanceNotes.Text, OperationGuard.NewId());
            AdvanceAmount.Clear(); AdvanceNotes.Clear();
            StatusText.Text = TranslationSource.Instance["MoneyOut_Saved"];
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async Task RefreshAsync()
    {
        try
        {
            DateOnly today = _clock.BusinessDate;
            ExpiredList.ItemsSource = await _service.GetExpiryAsync("EXPIRED");
            CriticalList.ItemsSource = await _service.GetExpiryAsync("CRITICAL");
            SoonList.ItemsSource = await _service.GetExpiryAsync("SOON");
            if (Window.GetWindow(this) is ShellWindow shell)
                shell.SetExpiryBadge(ExpiredList.Items.Count + CriticalList.Items.Count);
            DamageGrid.ItemsSource = await _service.GetDamagesAsync(today.AddYears(-1), today);
            ExpenseGrid.ItemsSource = await _service.GetExpensesAsync(today.AddYears(-1), today);
            long? categoryId = ExpenseCategory.SelectedValue as long?;
            ExpenseCategory.ItemsSource = await _service.GetExpenseCategoriesAsync();
            ExpenseCategory.SelectedValue = categoryId;
            if (ExpenseCategory.SelectedIndex < 0) ExpenseCategory.SelectedIndex = 0;
            RepeatCategories.ItemsSource = ExpenseCategory.ItemsSource;
            IReadOnlyList<ExpenseCategoryItem> categories = await _service.GetExpenseCategoriesAsync(includeInactive: true);
            CategoryMaster.ItemsSource = categories;
            CategoryMaster.SelectedItem = categories.FirstOrDefault(item => item.Id == _editingCategoryId);
            IReadOnlyList<EmployeeItem> employees = await _service.GetEmployeesAsync(includeInactive: true);
            EmployeeGrid.ItemsSource = employees;
            EmployeeGrid.SelectedItem = employees.FirstOrDefault(item => item.Id == _editingEmployeeId);
            DamageLot.ItemsSource = (await _inventory.GetLotsAsync()).Where(static lot => lot.IsActive && lot.OnHand > Quantity.Zero).ToList();
            StatusText.Text = TranslationSource.Instance["MoneyOut_Ready"];
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private ListBox ActiveExpiryList() => ExpiryTabs.SelectedIndex switch
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
        public long EmployeeId { get; init; }
        public string BaseSalary { get; set; } = "0.000";
        public string LeaveDeduction { get; set; } = "0.000";
        public string Overtime { get; set; } = "0.000";
        public string Bonus { get; set; } = "0.000";
        public static SalaryEditRow From(SalaryRunLine line) => new() { EmployeeId = line.EmployeeId, BaseSalary = line.BaseSalary.ToString() };
        public SalaryRunLine ToModel() => new(EmployeeId, new Money(Parse(BaseSalary)), new Money(Parse(LeaveDeduction)), new Money(Parse(Overtime)), new Money(Parse(Bonus)), null);
    }
}
