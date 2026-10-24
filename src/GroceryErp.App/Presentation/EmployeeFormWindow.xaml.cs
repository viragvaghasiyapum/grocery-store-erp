using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Expenses;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Adds or edits an employee. A changed monthly salary needs the month it applies from; the form
/// states before saving which posted months stay as paid.
/// </summary>
public partial class EmployeeFormWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly IClock _clock;
    private readonly EmployeeItem? _employee;
    private readonly LatestRequest _preview = new();

    public EmployeeFormWindow(MoneyOutService service, IClock clock, EmployeeItem? employee = null)
    {
        InitializeComponent();
        _service = service;
        _clock = clock;
        _employee = employee;
        JoinBox.SelectedDate = clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        if (employee is not null)
        {
            TitleText.Text = employee.Name;
            NameBox.Text = employee.Name;
            PhoneBox.Text = employee.Phone;
            RoleBox.Text = employee.Role;
            JoinBox.SelectedDate = employee.JoinDate?.ToDateTime(TimeOnly.MinValue);
            LeaveBox.SelectedDate = employee.LeaveDate?.ToDateTime(TimeOnly.MinValue);
            SalaryBox.Text = employee.MonthlySalary.Value.ToString("0.###", TranslationSource.FormatCulture);
            NotesBox.Text = employee.Notes;
            ActiveBox.IsChecked = employee.IsActive;
        }
        Loaded += (_, _) => NameField.FocusInput();
    }

    private bool SalaryChanged(out decimal salary)
    {
        var parse = new FormValidator();
        salary = parse.Number(SalaryField, SalaryBox.Text) ?? 0;
        return _employee is not null && parse.IsValid && salary != _employee.MonthlySalary.Value;
    }

    private async void Salary_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (SalaryFromField is null || SalaryHint is null) return;
        if (!SalaryChanged(out _))
        {
            SalaryFromField.Visibility = Visibility.Collapsed;
            SalaryHint.Visibility = Visibility.Collapsed;
            return;
        }
        if (SalaryFromField.Visibility != Visibility.Visible)
        {
            SalaryFromField.Visibility = Visibility.Visible;
            SalaryFromBox.Text = YearMonth.From(_clock.BusinessDate).ToString();
        }
        using LatestRequest.Scope request = _preview.Begin();
        try
        {
            YearMonth effective = YearMonth.Parse(SalaryFromBox.Text.Trim());
            SalaryTermPreview preview = await _service.PreviewSalaryTermAsync(_employee!.Id, effective, request.Token);
            if (!request.IsCurrent) return;
            SalaryHint.Text = TranslationSource.Instance.Format(
                preview.EffectiveMonth < preview.FirstOpenMonth ? "MoneyOut_SalaryFromClosed" : "MoneyOut_SalaryFromHint",
                preview.EffectiveMonth.ToString(), preview.FirstOpenMonth.ToString(), preview.PostedMonths);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { return; }
        catch (FormatException)
        {
            if (!request.IsCurrent) return;
            SalaryHint.Text = TranslationSource.Instance["MoneyOut_SalaryFromFormat"];
        }
        catch (Exception exception) { SalaryHint.Text = UserError.Message(exception); }
        SalaryHint.Visibility = Visibility.Visible;
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        string? name = form.Text(NameField, NameBox.Text, required: true, maxLength: 120);
        Money? salary = form.Money(SalaryField, SalaryBox.Text, required: true);
        DateOnly? join = JoinBox.SelectedDate is { } joined ? DateOnly.FromDateTime(joined) : null;
        DateOnly? leave = LeaveBox.SelectedDate is { } left ? DateOnly.FromDateTime(left) : null;
        if (leave < join) form.AddKey(LeaveField, "MoneyOut_LeaveBeforeJoin");
        YearMonth? salaryFrom = null;
        if (SalaryFromField.Visibility == Visibility.Visible)
        {
            try { salaryFrom = YearMonth.Parse(SalaryFromBox.Text.Trim()); }
            catch (FormatException) { form.AddKey(SalaryFromField, "MoneyOut_SalaryFromFormat"); }
        }
        if (!form.Apply(Fields, Summary)) return;
        SaveButton.IsEnabled = false;
        try
        {
            await _service.SaveEmployeeAsync(_employee?.Id, new EmployeeInput(name!, PhoneBox.Text, RoleBox.Text, join, leave,
                salary!.Value, ActiveBox.IsChecked == true, NotesBox.Text), salaryFrom);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }
}
