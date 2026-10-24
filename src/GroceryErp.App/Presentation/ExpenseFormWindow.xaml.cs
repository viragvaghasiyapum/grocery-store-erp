using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Records one expense, stating before saving how its date counts in profit and loss.</summary>
public partial class ExpenseFormWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly LatestRequest _period = new();
    private readonly string _operationId = OperationGuard.NewId();

    public ExpenseFormWindow(MoneyOutService service, IClock clock, IReadOnlyList<ExpenseCategoryItem> categories,
        ExpenseInput? draft = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(categories);
        InitializeComponent();
        _service = service;
        CategoryBox.ItemsSource = categories.Where(static category => category.IsActive).ToList();
        DateBox.SelectedDate = (draft?.Date ?? clock.BusinessDate).ToDateTime(TimeOnly.MinValue);
        if (draft is not null)
        {
            CategoryBox.SelectedValue = draft.CategoryId;
            AmountBox.Text = draft.Amount.Value.ToString("0.###", TranslationSource.FormatCulture);
            PaidToBox.Text = draft.PaidTo;
            ReferenceBox.Text = draft.Reference;
            NotesBox.Text = draft.Notes;
            foreach (ComboBoxItem item in ModeBox.Items)
                if (string.Equals(item.Tag as string, draft.Mode, StringComparison.Ordinal)) ModeBox.SelectedItem = item;
        }
        Loaded += async (_, _) =>
        {
            (draft is null ? CategoryField : AmountField).FocusInput();
            await ShowPeriodAsync();
        };
    }

    private async void Date_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await ShowPeriodAsync();
    }

    private async Task ShowPeriodAsync()
    {
        if (DateBox.SelectedDate is not { } date) return;
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
        DateOnly? date = form.Date(DateField, DateBox.SelectedDate);
        long? category = form.Choice(CategoryField, CategoryBox.SelectedValue);
        if (CategoryBox.SelectedItem is ExpenseCategoryItem item && LooksLikeAsset(item.Name))
            form.AddKey(CategoryField, "MoneyOut_AssetWarning");
        Money? amount = form.Money(AmountField, AmountBox.Text, required: true, positive: true);
        string? paidTo = form.Text(PaidToField, PaidToBox.Text, maxLength: 120);
        string? reference = form.Text(ReferenceField, ReferenceBox.Text, maxLength: 120);
        if (!form.Apply(Fields, Summary)) return;
        string mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "CASH";
        SaveButton.IsEnabled = false;
        try
        {
            await _service.RecordExpenseAsync(new ExpenseInput(date!.Value, category!.Value, amount!.Value, mode,
                paidTo, reference, NotesBox.Text), _operationId);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            SaveButton.IsEnabled = true;
        }
    }

    // Buying equipment is an asset, not an expense; its depreciation reaches profit and loss instead.
    private static bool LooksLikeAsset(string category) =>
        category.Contains("equipment", StringComparison.OrdinalIgnoreCase) ||
        category.Contains("furniture", StringComparison.OrdinalIgnoreCase) ||
        category.Contains("computer", StringComparison.OrdinalIgnoreCase) ||
        category.Contains("vehicle", StringComparison.OrdinalIgnoreCase) ||
        category.Contains("asset", StringComparison.OrdinalIgnoreCase);
}
