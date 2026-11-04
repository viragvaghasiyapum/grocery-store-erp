using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Prepares one month's salary lines, lets the operator adjust and choose them, then posts the run.</summary>
public partial class SalaryRunWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly IReadOnlyDictionary<long, string> _employeeNames;
    private readonly LatestRequest _period = new();
    private readonly string _operationId = OperationGuard.NewId();

    public SalaryRunWindow(MoneyOutService service, IClock clock, IReadOnlyDictionary<long, string> employeeNames)
    {
        InitializeComponent();
        _service = service;
        _employeeNames = employeeNames;
        PeriodBox.Text = YearMonth.From(clock.BusinessDate).ToString();
        PayDateBox.SelectedDate = clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        Loaded += async (_, _) =>
        {
            PeriodField.FocusInput();
            await ShowPeriodAsync();
        };
    }

    private async void PayDate_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await ShowPeriodAsync();
    }

    private async Task ShowPeriodAsync()
    {
        if (!YearMonth.TryParse(PeriodBox.Text.Trim(), out YearMonth period)) return;
        using LatestRequest.Scope request = _period.Begin();
        try
        {
            CorrectionDating dating = await _service.PreviewCorrectionAsync(period.LastDay, request.Token);
            if (request.IsCurrent) PeriodText.Text = PeriodNotice.ForNewEntry(dating);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) PeriodText.Text = UserError.Message(exception);
        }
    }

    private async void Prepare_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        if (!YearMonth.TryParse(PeriodBox.Text.Trim(), out YearMonth period)) form.AddKey(PeriodField, "MoneyOut_SalaryFromFormat");
        if (!form.Apply(Fields, Summary)) return;
        try
        {
            IReadOnlyList<SalaryRunLine> lines = await _service.PrepareSalaryRunAsync(period);
            SalaryGrid.ItemsSource = lines.Select(line => SalaryRow.From(line,
                _employeeNames.GetValueOrDefault(line.EmployeeId, line.EmployeeId.ToString(CultureInfo.InvariantCulture)))).ToList();
            PostButton.IsEnabled = lines.Count > 0;
            await ShowPeriodAsync();
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
    }

    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        SalaryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        SalaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var form = new FormValidator();
        if (!YearMonth.TryParse(PeriodBox.Text.Trim(), out YearMonth period)) form.AddKey(PeriodField, "MoneyOut_SalaryFromFormat");
        DateOnly? payDate = form.Date(PayDateField, PayDateBox.SelectedDate);
        var problems = new List<string>();
        var lines = new List<SalaryRunLine>();
        foreach (SalaryRow row in (SalaryGrid.ItemsSource as List<SalaryRow> ?? []).Where(static row => row.IsSelected))
        {
            if (row.ToModel() is not { } line)
                problems.Add(TranslationSource.Instance.Format("MoneyOut_SalaryLineInvalid", row.EmployeeName));
            else if (line.LeaveDeduction > line.BaseSalary ||
                     line.BaseSalary - line.LeaveDeduction + line.Overtime + line.Bonus <= Money.Zero)
                problems.Add(TranslationSource.Instance.Format("MoneyOut_SalaryLinePaysNothing", row.EmployeeName));
            else lines.Add(line);
        }
        if (lines.Count == 0 && problems.Count == 0) problems.Add(TranslationSource.Instance["MoneyOut_SalaryChooseLines"]);
        if (!form.Apply(Fields, Summary, problems)) return;
        string mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "BANK";
        if (MessageBox.Show(TranslationSource.Instance.Format("MoneyOut_SalaryPostConfirm", lines.Count, period.ToString(),
                Money.FromScaled(lines.Sum(static line => (line.BaseSalary - line.LeaveDeduction + line.Overtime + line.Bonus).Scaled))),
            TranslationSource.Instance["MoneyOut_PostSalary"], MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        PostButton.IsEnabled = false;
        try
        {
            await _service.PostSalaryRunAsync(new SalaryRunInput(period, payDate!.Value, mode, lines), _operationId);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            PostButton.IsEnabled = true;
        }
    }

    private sealed class SalaryRow
    {
        public bool IsSelected { get; set; } = true;
        public long EmployeeId { get; init; }
        public string EmployeeName { get; init; } = string.Empty;
        public string BaseSalary { get; set; } = "0";
        public string LeaveDeduction { get; set; } = "0";
        public string Overtime { get; set; } = "0";
        public string Bonus { get; set; } = "0";

        public static SalaryRow From(SalaryRunLine line, string employeeName) => new()
        {
            EmployeeId = line.EmployeeId,
            EmployeeName = employeeName,
            BaseSalary = line.BaseSalary.Value.ToString("0.###", TranslationSource.FormatCulture),
        };

        public SalaryRunLine? ToModel() =>
            Amount(BaseSalary) is { } salary && Amount(LeaveDeduction) is { } leave && Amount(Overtime) is { } overtime &&
            Amount(Bonus) is { } bonus
                ? new SalaryRunLine(EmployeeId, salary, leave, overtime, bonus, null)
                : null;

        private static Money? Amount(string text) =>
            decimal.TryParse(InputNormalizer.Normalize(string.IsNullOrWhiteSpace(text) ? "0" : text), NumberStyles.Number,
                TranslationSource.FormatCulture, out decimal value) && value >= 0 ? new Money(value) : null;
    }
}
