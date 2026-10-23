using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

// Edits or voids a posted expense, damage or salary payment. The first line states
// whether the change updates the original open period or posts a correction today.
public sealed class MoneyOutCorrectionWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly bool _void;
    private readonly string _operationId = OperationGuard.NewId();
    private readonly DamageItem? _damage;
    private readonly SalaryPaymentItem? _salary;
    private readonly ExpenseItem? _expense;
    private readonly TextBox _quantity = new();
    private readonly TextBox _amount = new();
    private readonly TextBox _notes = new();
    private readonly TextBox _reason = new() { MaxLength = 240 };
    private readonly TextBox _paidTo = new();
    private readonly TextBox _reference = new();
    private readonly ComboBox _code = new();
    private readonly ComboBox _category = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly DatePicker _date = new() { SelectedDateFormat = DatePickerFormat.Short };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public MoneyOutCorrectionWindow(MoneyOutService service, DamageItem damage, CorrectionDating dating, bool voidEntry)
        : this(service, dating, voidEntry, voidEntry ? "MoneyOut_VoidDamage" : "MoneyOut_CorrectDamage")
    {
        _damage = damage;
        if (!voidEntry)
        {
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
        }
        Finish();
    }

    public MoneyOutCorrectionWindow(MoneyOutService service, SalaryPaymentItem salary, CorrectionDating dating, bool voidEntry)
        : this(service, dating, voidEntry, voidEntry ? "MoneyOut_VoidSalary" : "MoneyOut_CorrectSalary")
    {
        _salary = salary;
        if (!voidEntry)
        {
            AddField(T("MoneyOut_Salary"), _amount, salary.Amount.Value.ToString("0.000", TranslationSource.FormatCulture));
            AddField(T("MoneyOut_PayDate"), _date);
            _date.SelectedDate = salary.PayDate.ToDateTime(TimeOnly.MinValue);
            AddField(T("Purchasing_Mode"), _code);
            AddOption("CASH", "MoneyOut_ModeCash");
            AddOption("UPI", null);
            AddOption("BANK", "MoneyOut_ModeBank");
            AddOption("OTHER", "MoneyOut_ReasonOther");
            SelectCode(salary.Mode);
            AddField(T("Purchasing_Notes"), _notes, salary.Notes ?? "");
        }
        Finish();
    }

    public MoneyOutCorrectionWindow(MoneyOutService service, ExpenseItem expense, IReadOnlyList<ExpenseCategoryItem> categories,
        CorrectionDating dating, bool voidEntry)
        : this(service, dating, voidEntry, voidEntry ? "MoneyOut_VoidExpense" : "MoneyOut_CorrectExpense")
    {
        _expense = expense;
        if (!voidEntry)
        {
            AddField(T("Purchasing_Date"), _date);
            _date.SelectedDate = expense.Date.ToDateTime(TimeOnly.MinValue);
            AddField(T("Catalog_Category"), _category);
            _category.ItemsSource = categories;
            _category.SelectedValue = expense.CategoryId;
            AddField(T("MoneyOut_Amount"), _amount, expense.Amount.Value.ToString("0.000", TranslationSource.FormatCulture));
            AddField(T("Purchasing_Mode"), _code);
            AddOption("CASH", "MoneyOut_ModeCash");
            AddOption("UPI", null);
            AddOption("BANK", "MoneyOut_ModeBank");
            AddOption("CARD", "MoneyOut_ModeCard");
            AddOption("OTHER", "MoneyOut_ReasonOther");
            SelectCode(expense.Mode);
            AddField(T("MoneyOut_PaidTo"), _paidTo, expense.PaidTo ?? "");
            AddField(T("MoneyOut_Reference"), _reference, expense.Reference ?? "");
            AddField(T("Purchasing_Notes"), _notes, expense.Notes ?? "");
        }
        Finish();
    }

    private MoneyOutCorrectionWindow(MoneyOutService service, CorrectionDating dating, bool voidEntry, string titleKey)
    {
        _service = service;
        _void = voidEntry;
        Title = T(titleKey);
        Width = 440;
        SizeToContent = SizeToContent.Height;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(18) }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Body.Children.Add(new TextBlock
        {
            Text = CorrectionDetailsPanel.Describe(dating),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        if (voidEntry)
            Body.Children.Add(new TextBlock { Text = T("MoneyOut_VoidExplanation"), TextWrapping = TextWrapping.Wrap });
    }

    private StackPanel Body => (StackPanel)((ScrollViewer)Content).Content;

    private void AddField(string label, Control control, string? value = null)
    {
        Body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 3) });
        control.MinHeight = 28;
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        Body.Children.Add(control);
        if (control is TextBox text && value is not null) text.Text = value;
    }

    private void AddOption(string code, string? translation) =>
        _code.Items.Add(new ComboBoxItem { Tag = code, Content = translation is null ? code : T(translation) });

    private void SelectCode(string code) => _code.SelectedItem = _code.Items.Cast<ComboBoxItem>()
        .FirstOrDefault(item => (string)item.Tag == code) ?? _code.Items[0];

    private void Finish()
    {
        AddField(T("MoneyOut_CorrectionReason"), _reason);
        _reason.ToolTip = T("MoneyOut_CorrectionReasonRequired");
        Body.Children.Add(_error);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 15, 0, 0)
        };
        var cancel = new Button
        {
            Content = T("Common_Cancel"),
            MinWidth = 80,
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        var save = new Button { Content = T(_void ? "MoneyOut_Void" : "Common_Save"), MinWidth = 80, IsDefault = true };
        save.Click += Save_OnClick;
        buttons.Children.Add(save);
        Body.Children.Add(buttons);
        Loaded += (_, _) => (_void ? _reason : Body.Children.OfType<Control>().FirstOrDefault())?.Focus();
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        Button? saveButton = sender as Button;
        if (saveButton is not null) saveButton.IsEnabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(_reason.Text))
                throw new FormatException(T("MoneyOut_CorrectionReasonRequired"));
            string code = (_code.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            if (_damage is { } damage)
                await (_void
                    ? _service.VoidDamageAsync(damage, _reason.Text, _operationId)
                    : _service.CorrectDamageAsync(damage,
                        new(new Quantity(Parse(_quantity.Text)), new Money(Parse(_amount.Text)), code, _notes.Text),
                        _reason.Text, _operationId));
            else if (_salary is { } salary)
            {
                if (_void) await _service.VoidSalaryPaymentAsync(salary, _reason.Text, _operationId);
                else
                {
                    if (_date.SelectedDate is not { } date) throw new FormatException(T("MoneyOut_PayDate"));
                    await _service.CorrectSalaryPaymentAsync(salary,
                        new(DateOnly.FromDateTime(date), new Money(Parse(_amount.Text)), code, _notes.Text),
                        _reason.Text, _operationId);
                }
            }
            else if (_expense is { } expense)
            {
                if (_void) await _service.VoidExpenseAsync(expense, _reason.Text, _operationId);
                else
                {
                    if (_date.SelectedDate is not { } date || _category.SelectedValue is not long category)
                        throw new FormatException(T("MoneyOut_ValidNumber"));
                    await _service.CorrectExpenseAsync(expense, new(DateOnly.FromDateTime(date), category,
                        new Money(Parse(_amount.Text)), code, _paidTo.Text, _reference.Text, _notes.Text),
                        _reason.Text, _operationId);
                }
            }
            DialogResult = true;
        }
        catch (Exception exception)
        {
            _error.Text = exception is FormatException ? exception.Message : UserError.Message(exception);
            if (saveButton is not null) saveButton.IsEnabled = true;
        }
    }

    private static decimal Parse(string value) => decimal.TryParse(value, NumberStyles.Number,
        CultureInfo.GetCultureInfo("en-IN"), out decimal result)
        ? result : throw new FormatException(T("MoneyOut_ValidNumber"));

    private static string T(string key) => TranslationSource.Instance[key];
}
