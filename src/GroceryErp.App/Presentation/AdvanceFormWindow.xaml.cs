using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;

namespace GroceryErp.App.Presentation;

/// <summary>Records a salary advance or other payment to one employee.</summary>
public partial class AdvanceFormWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly LatestRequest _period = new();
    private readonly string _operationId = OperationGuard.NewId();

    public AdvanceFormWindow(MoneyOutService service, IClock clock, IReadOnlyList<EmployeeItem> employees)
    {
        InitializeComponent();
        _service = service;
        EmployeeBox.ItemsSource = employees.Where(static employee => employee.IsActive).ToList();
        PayDateBox.SelectedDate = clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        Loaded += async (_, _) =>
        {
            EmployeeField.FocusInput();
            await ShowPeriodAsync();
        };
    }

    private async void PayDate_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await ShowPeriodAsync();
    }

    private async Task ShowPeriodAsync()
    {
        if (PayDateBox.SelectedDate is not { } date) return;
        using LatestRequest.Scope request = _period.Begin();
        try
        {
            CorrectionDating dating = await _service.PreviewCorrectionAsync(DateOnly.FromDateTime(date), request.Token);
            if (request.IsCurrent) PeriodText.Text = PeriodNotice.ForNewEntry(dating);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) PeriodText.Text = UserError.Message(exception);
        }
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        long? employee = form.Choice(EmployeeField, EmployeeBox.SelectedValue);
        Money? amount = form.Money(AmountField, AmountBox.Text, required: true, positive: true);
        DateOnly? payDate = form.Date(PayDateField, PayDateBox.SelectedDate);
        if (!form.Apply(Fields, Summary)) return;
        string mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "CASH";
        SaveButton.IsEnabled = false;
        try
        {
            await _service.RecordOtherSalaryPaymentAsync(employee!.Value, payDate!.Value, amount!.Value, mode,
                NotesBox.Text, _operationId);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }
}
