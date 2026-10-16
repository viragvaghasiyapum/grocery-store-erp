using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

internal sealed class MoneyOutCorrectionWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly DateOnly _correctionDate;
    private readonly DamageItem? _damage;
    private readonly SalaryPaymentItem? _salary;
    private readonly TextBox _quantity = new();
    private readonly TextBox _amount = new();
    private readonly TextBox _notes = new();
    private readonly TextBox _reason = new();
    private readonly ComboBox _code = new();
    private readonly DatePicker _payDate = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };

    public MoneyOutCorrectionWindow(MoneyOutService service, DamageItem damage, DateOnly correctionDate)
        : this(service, correctionDate)
    {
        _damage = damage;
        Title = T("MoneyOut_CorrectDamage");
        AddField(T("Stock_Quantity"), _quantity, damage.Quantity.Value.ToString("0.000", TranslationSource.FormatCulture));
        AddField(T("MoneyOut_Recovered"), _amount, damage.RecoveredAmount.Value.ToString("0.000", TranslationSource.FormatCulture));
        AddField(T("MoneyOut_Reason"), _code);
        AddOption("EXPIRED", "MoneyOut_ReasonExpired");
        AddOption("BROKEN", "MoneyOut_ReasonBroken");
        AddOption("SPOILED", "MoneyOut_ReasonSpoiled");
        AddOption("PEST", "MoneyOut_ReasonPest");
        AddOption("LOST", "MoneyOut_ReasonLost");
        AddOption("CUSTOMER_RETURN", "MoneyOut_ReasonReturn");
        AddOption("OTHER", "MoneyOut_ReasonOther");
        SelectCode(damage.Reason);
        AddField(T("Purchasing_Notes"), _notes, damage.Notes ?? "");
        Finish();
    }

    public MoneyOutCorrectionWindow(MoneyOutService service, SalaryPaymentItem salary, DateOnly correctionDate)
        : this(service, correctionDate)
    {
        _salary = salary;
        Title = T("MoneyOut_CorrectSalary");
        AddField(T("MoneyOut_Salary"), _amount, salary.Amount.Value.ToString("0.000", TranslationSource.FormatCulture));
        AddField(T("MoneyOut_PayDate"), _payDate);
        _payDate.SelectedDate = salary.PayDate.ToDateTime(TimeOnly.MinValue);
        AddField(T("Purchasing_Mode"), _code);
        AddOption("CASH", "MoneyOut_ModeCash");
        AddOption("UPI", null);
        AddOption("BANK", "MoneyOut_ModeBank");
        AddOption("OTHER", "MoneyOut_ReasonOther");
        SelectCode(salary.Mode);
        AddField(T("Purchasing_Notes"), _notes, salary.Notes ?? "");
        Finish();
    }

    private MoneyOutCorrectionWindow(MoneyOutService service, DateOnly correctionDate)
    {
        _service = service;
        _correctionDate = correctionDate;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(18) } };
    }

    private StackPanel Body => (StackPanel)((ScrollViewer)Content).Content;

    private void AddField(string label, Control control, string? value = null)
    {
        Body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 3) });
        control.MinHeight = 28;
        Body.Children.Add(control);
        if (control is TextBox text && value is not null) text.Text = value;
    }

    private void AddOption(string code, string? translation) =>
        _code.Items.Add(new ComboBoxItem { Tag = code, Content = translation is null ? code : T(translation) });

    private void SelectCode(string code) => _code.SelectedItem = _code.Items.Cast<ComboBoxItem>()
        .First(item => (string)item.Tag == code);

    private void Finish()
    {
        AddField(T("MoneyOut_CorrectionReason"), _reason);
        _reason.ToolTip = T("MoneyOut_CorrectionReasonRequired");
        Body.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 15, 0, 0) };
        var cancel = new Button { Content = T("Common_Cancel"), MinWidth = 80,
            Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        var save = new Button { Content = T("Common_Save"), MinWidth = 80 };
        save.Click += Save_OnClick;
        buttons.Children.Add(save);
        Body.Children.Add(buttons);
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        Button? saveButton = sender as Button;
        if (saveButton is not null) saveButton.IsEnabled = false;
        try
        {
            string code = ((ComboBoxItem)_code.SelectedItem).Tag.ToString()!;
            if (_damage is { } damage)
                await _service.CorrectDamageAsync(damage,
                    new(new Quantity(Parse(_quantity.Text)), new Money(Parse(_amount.Text)), code, _notes.Text),
                    _correctionDate, _reason.Text, OperationGuard.NewId());
            else if (_salary is { } salary)
            {
                if (_payDate.SelectedDate is not { } date) throw new FormatException(T("MoneyOut_PayDate"));
                await _service.CorrectSalaryPaymentAsync(salary,
                    new(DateOnly.FromDateTime(date), new Money(Parse(_amount.Text)), code, _notes.Text),
                    _correctionDate, _reason.Text, OperationGuard.NewId());
            }
            DialogResult = true;
        }
        catch (Exception exception)
        {
            _error.Text = UserError.Message(exception);
            if (saveButton is not null) saveButton.IsEnabled = true;
        }
    }

    private static decimal Parse(string value) => decimal.TryParse(value, NumberStyles.Number,
        CultureInfo.GetCultureInfo("en-IN"), out decimal result)
        ? result : throw new FormatException(T("MoneyOut_ValidNumber"));

    private static string T(string key) => TranslationSource.Instance[key];
}
