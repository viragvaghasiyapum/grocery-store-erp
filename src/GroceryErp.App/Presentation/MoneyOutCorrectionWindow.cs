using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
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
    private readonly TextBox _notes = new() { MaxLength = 500 };
    private readonly TextBox _reason = new() { MaxLength = 240 };
    private readonly TextBox _paidTo = new() { MaxLength = 120 };
    private readonly TextBox _reference = new() { MaxLength = 120 };
    private readonly ComboBox _code = new();
    private readonly ComboBox _category = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly DatePicker _date = new() { SelectedDateFormat = DatePickerFormat.Short };
    private readonly FormErrorSummary _summary = new();
    private readonly StackPanel _fields = new();
    private readonly Dictionary<Control, FormField> _fieldOf = [];
    private readonly Button _save = new() { MinWidth = 80, IsDefault = true };

    public MoneyOutCorrectionWindow(MoneyOutService service, DamageItem damage, CorrectionDating dating, bool voidEntry)
        : this(service, dating, voidEntry, voidEntry ? "MoneyOut_VoidDamage" : "MoneyOut_CorrectDamage")
    {
        _damage = damage;
        if (!voidEntry)
        {
            AddField("Stock_Quantity", _quantity, damage.Quantity.Value.ToString("0.###", TranslationSource.FormatCulture), required: true);
            AddField("MoneyOut_Recovered", _amount, damage.RecoveredAmount.Value.ToString("0.###", TranslationSource.FormatCulture));
            AddField("MoneyOut_Reason", _code, required: true);
            AddOption("EXPIRED", "MoneyOut_ReasonExpired");
            AddOption("BROKEN", "MoneyOut_ReasonBroken");
            AddOption("SPOILED", "MoneyOut_ReasonSpoiled");
            AddOption("PEST", "MoneyOut_ReasonPest");
            AddOption("LOST", "MoneyOut_ReasonLost");
            AddOption("CUSTOMER_RETURN", "MoneyOut_ReasonReturn");
            AddOption("OTHER", "MoneyOut_ReasonOther");
            SelectCode(damage.Reason);
            AddField("Purchasing_Notes", _notes, damage.Notes ?? "");
        }
        Finish();
    }

    public MoneyOutCorrectionWindow(MoneyOutService service, SalaryPaymentItem salary, CorrectionDating dating, bool voidEntry)
        : this(service, dating, voidEntry, voidEntry ? "MoneyOut_VoidSalary" : "MoneyOut_CorrectSalary")
    {
        _salary = salary;
        if (!voidEntry)
        {
            AddField("MoneyOut_Salary", _amount, salary.Amount.Value.ToString("0.###", TranslationSource.FormatCulture), required: true);
            AddField("MoneyOut_PayDate", _date, required: true);
            _date.SelectedDate = salary.PayDate.ToDateTime(TimeOnly.MinValue);
            AddField("Purchasing_Mode", _code, required: true);
            AddOption("CASH", "MoneyOut_ModeCash");
            AddOption("UPI", null);
            AddOption("BANK", "MoneyOut_ModeBank");
            AddOption("OTHER", "MoneyOut_ReasonOther");
            SelectCode(salary.Mode);
            AddField("Purchasing_Notes", _notes, salary.Notes ?? "");
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
            AddField("Purchasing_Date", _date, required: true);
            _date.SelectedDate = expense.Date.ToDateTime(TimeOnly.MinValue);
            AddField("Catalog_Category", _category, required: true);
            _category.ItemsSource = categories;
            _category.SelectedValue = expense.CategoryId;
            AddField("MoneyOut_Amount", _amount, expense.Amount.Value.ToString("0.###", TranslationSource.FormatCulture), required: true);
            AddField("Purchasing_Mode", _code, required: true);
            AddOption("CASH", "MoneyOut_ModeCash");
            AddOption("UPI", null);
            AddOption("BANK", "MoneyOut_ModeBank");
            AddOption("CARD", "MoneyOut_ModeCard");
            AddOption("OTHER", "MoneyOut_ReasonOther");
            SelectCode(expense.Mode);
            AddField("MoneyOut_PaidTo", _paidTo, expense.PaidTo ?? "");
            AddField("MoneyOut_Reference", _reference, expense.Reference ?? "");
            AddField("Purchasing_Notes", _notes, expense.Notes ?? "");
        }
        Finish();
    }

    private MoneyOutCorrectionWindow(MoneyOutService service, CorrectionDating dating, bool voidEntry, string titleKey)
    {
        _service = service;
        _void = voidEntry;
        Title = T(titleKey);
        Width = 460;
        Height = 560;
        MinHeight = 320;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "AppFont");
        NumberSubstitution.SetCultureSource(this, NumberCultureSource.Override);
        NumberSubstitution.SetCultureOverride(this, System.Windows.Markup.XmlLanguage.GetLanguage("en-IN").GetSpecificCulture());
        NumberSubstitution.SetSubstitution(this, NumberSubstitutionMethod.European);
        var title = new TextBlock { Text = Title };
        title.SetResourceReference(StyleProperty, "FormTitle");
        var treatment = new TextBlock { Text = CorrectionDetailsPanel.Describe(dating), FontWeight = FontWeights.SemiBold };
        treatment.SetResourceReference(StyleProperty, "FormContext");
        var header = new StackPanel { Children = { title, treatment } };
        if (voidEntry)
        {
            var explanation = new TextBlock { Text = T("MoneyOut_VoidExplanation") };
            explanation.SetResourceReference(StyleProperty, "FormContext");
            header.Children.Add(explanation);
        }
        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(header);
        Grid.SetRow(_summary, 1);
        layout.Children.Add(_summary);
        var scroller = new ScrollViewer
        {
            Content = _fields,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 2);
        layout.Children.Add(scroller);
        Content = layout;
    }

    private void AddField(string labelKey, Control control, string? value = null, bool required = false)
    {
        var field = new FormField { Label = T(labelKey), IsRequired = required, Content = control };
        _fieldOf[control] = field;
        _fields.Children.Add(field);
        if (control is TextBox text && value is not null) text.Text = value;
    }

    private void AddOption(string code, string? translation) =>
        _code.Items.Add(new ComboBoxItem { Tag = code, Content = translation is null ? code : T(translation) });

    private void SelectCode(string code) => _code.SelectedItem = _code.Items.Cast<ComboBoxItem>()
        .FirstOrDefault(item => (string)item.Tag == code) ?? _code.Items[0];

    private void Finish()
    {
        AddField("MoneyOut_CorrectionReason", _reason, required: true);
        _fieldOf[_reason].Hint = T("MoneyOut_CorrectionReasonRequired");
        var cancel = new Button { Content = T("Common_Cancel"), MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        _save.Content = T(_void ? "MoneyOut_Void" : "Common_Save");
        _save.Click += Save_OnClick;
        var footer = new StackPanel { Children = { cancel, _save } };
        footer.SetResourceReference(StyleProperty, "FormFooter");
        Grid.SetRow(footer, 3);
        ((Grid)Content).Children.Add(footer);
        Loaded += (_, _) => (_void ? _fieldOf[_reason] : _fields.Children.OfType<FormField>().First()).FocusInput();
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        string? reason = form.Text(_fieldOf[_reason], _reason.Text, required: true, maxLength: 240);
        string code = (_code.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        Quantity? quantity = !_void && _damage is not null ? form.Quantity(_fieldOf[_quantity], _quantity.Text, positive: false) : null;
        Money? amount = !_void && _fieldOf.ContainsKey(_amount)
            ? form.Money(_fieldOf[_amount], _amount.Text, required: _damage is null) : null;
        DateOnly? date = !_void && _fieldOf.ContainsKey(_date) ? form.Date(_fieldOf[_date], _date.SelectedDate) : null;
        long? category = !_void && _expense is not null ? form.Choice(_fieldOf[_category], _category.SelectedValue) : null;
        if (!form.Apply(_fields, _summary)) return;
        _save.IsEnabled = false;
        try
        {
            if (_damage is { } damage)
            {
                // Restored units whose lot and product were both deleted need a product first.
                DamageCorrection correction = _void
                    ? new(Quantity.Zero, Money.Zero, damage.Reason, damage.Notes)
                    : new(quantity!.Value, amount ?? Money.Zero, code, _notes.Text);
                Quantity restoring = new(damage.Quantity.Value - correction.Quantity.Value);
                if (restoring > Quantity.Zero &&
                    await _service.GetDamageRestoreDraftAsync(damage.Id, restoring) is { } draft)
                {
                    RestoreProductChoice? restore = await RestoreProductWindow.AskAsync(this, draft);
                    if (restore is null)
                    {
                        _save.IsEnabled = true;
                        return;
                    }
                    correction = correction with { Restore = restore };
                }
                await _service.CorrectDamageAsync(damage, correction, reason!, _operationId);
            }
            else if (_salary is { } salary)
                await (_void
                    ? _service.VoidSalaryPaymentAsync(salary, reason!, _operationId)
                    : _service.CorrectSalaryPaymentAsync(salary, new(date!.Value, amount!.Value, code, _notes.Text),
                        reason!, _operationId));
            else if (_expense is { } expense)
                await (_void
                    ? _service.VoidExpenseAsync(expense, reason!, _operationId)
                    : _service.CorrectExpenseAsync(expense, new(date!.Value, category!.Value, amount!.Value, code,
                        _paidTo.Text, _reference.Text, _notes.Text), reason!, _operationId));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            _summary.ShowMessage(UserError.Message(exception));
            _save.IsEnabled = true;
        }
    }

    private static string T(string key) => TranslationSource.Instance[key];
}
