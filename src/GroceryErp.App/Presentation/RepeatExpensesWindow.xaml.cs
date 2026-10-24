using System.Globalization;
using System.Windows;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Repeats last month's expenses for chosen categories: drafts are reviewed and edited, then each
/// selected draft is saved as its own expense with its own operation ID.
/// </summary>
public partial class RepeatExpensesWindow : Window
{
    private readonly MoneyOutService _service;
    private readonly IClock _clock;
    private readonly IReadOnlyList<ExpenseCategoryItem> _categories;

    public RepeatExpensesWindow(MoneyOutService service, IClock clock, IReadOnlyList<ExpenseCategoryItem> categories)
    {
        InitializeComponent();
        _service = service;
        _clock = clock;
        _categories = categories.Where(static category => category.IsActive).ToList();
        Categories.ItemsSource = _categories;
    }

    /// <summary>Gets how many expenses were saved.</summary>
    public int SavedCount { get; private set; }

    private async void Find_OnClick(object sender, RoutedEventArgs e)
    {
        long[] categories = Categories.SelectedItems.Cast<ExpenseCategoryItem>().Select(static item => item.Id).ToArray();
        if (categories.Length == 0)
        {
            Summary.ShowMessage(TranslationSource.Instance["MoneyOut_ChooseCategories"]);
            return;
        }
        try
        {
            Summary.Clear();
            IReadOnlyList<ExpenseInput> drafts = await _service.RepeatLastMonthAsync(YearMonth.From(_clock.BusinessDate), categories);
            DraftGrid.ItemsSource = drafts.Select(draft => new DraftRow(draft,
                _categories.FirstOrDefault(category => category.Id == draft.CategoryId)?.Name ?? string.Empty)).ToList();
            StatusText.Text = TranslationSource.Instance.Format("MoneyOut_DraftsFound", drafts.Count);
            SaveButton.IsEnabled = drafts.Count > 0;
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        DraftGrid.CommitEdit();
        DraftGrid.CommitEdit();
        if (DraftGrid.ItemsSource is not List<DraftRow> rows) return;
        var problems = new List<string>();
        var inputs = new List<(DraftRow Row, ExpenseInput Input)>();
        foreach (DraftRow row in rows.Where(static row => row.IsSelected && !row.Saved))
        {
            if (row.ToInput() is { } input) inputs.Add((row, input));
            else problems.Add(TranslationSource.Instance.Format("MoneyOut_DraftInvalid", row.CategoryName));
        }
        if (problems.Count > 0)
        {
            Summary.Show([], problems);
            return;
        }
        SaveButton.IsEnabled = false;
        try
        {
            foreach ((DraftRow row, ExpenseInput input) in inputs)
            {
                await _service.RecordExpenseAsync(input, row.OperationId);
                row.Saved = true;
                row.IsSelected = false;
                SavedCount++;
            }
            DraftGrid.Items.Refresh();
            Summary.Clear();
            StatusText.Text = TranslationSource.Instance.Format("MoneyOut_DraftsSaved", inputs.Count);
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
        finally { SaveButton.IsEnabled = rows.Any(static row => !row.Saved); }
    }

    private sealed class DraftRow(ExpenseInput draft, string categoryName)
    {
        public string OperationId { get; } = OperationGuard.NewId();
        public bool IsSelected { get; set; } = true;
        public bool Saved { get; set; }
        public string CategoryName { get; } = categoryName;
        public string DateText { get; set; } = draft.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public string AmountText { get; set; } = draft.Amount.Value.ToString("0.###", TranslationSource.FormatCulture);
        public string Mode { get; } = draft.Mode;
        public string? PaidTo { get; set; } = draft.PaidTo;

        public ExpenseInput? ToInput() =>
            DateOnly.TryParseExact(InputNormalizer.Normalize(DateText), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly date) &&
            decimal.TryParse(InputNormalizer.Normalize(AmountText), NumberStyles.Number, TranslationSource.FormatCulture,
                out decimal amount) && amount > 0
                ? draft with { Date = date, Amount = new Money(amount), PaidTo = PaidTo }
                : null;
    }
}
