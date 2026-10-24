using System.Windows;
using System.Windows.Controls;
using GroceryErp.Expenses;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Adds, renames and deactivates expense categories.</summary>
public partial class ExpenseCategoriesWindow : Window
{
    private readonly MoneyOutService _service;
    private long? _editingId;

    public ExpenseCategoriesWindow(MoneyOutService service)
    {
        InitializeComponent();
        _service = service;
        Loaded += async (_, _) =>
        {
            await LoadAsync();
            NameField.FocusInput();
        };
    }

    /// <summary>Gets whether any category was saved, so the caller reloads its category lists.</summary>
    public bool Changed { get; private set; }

    private async Task LoadAsync()
    {
        try { CategoryList.ItemsSource = await _service.GetExpenseCategoriesAsync(includeInactive: true); }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
    }

    private void CategoryList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not ExpenseCategoryItem item) return;
        FormValidator.Clear(Fields, Summary);
        _editingId = item.Id;
        NameBox.Text = item.Name;
        LocalBox.Text = item.LocalName ?? string.Empty;
        ActiveBox.IsChecked = item.IsActive;
        SavedText.Text = string.Empty;
    }

    private void New_OnClick(object sender, RoutedEventArgs e)
    {
        FormValidator.Clear(Fields, Summary);
        CategoryList.SelectedItem = null;
        _editingId = null;
        NameBox.Clear();
        LocalBox.Clear();
        ActiveBox.IsChecked = true;
        SavedText.Text = string.Empty;
        NameField.FocusInput();
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        string? name = form.Text(NameField, NameBox.Text, required: true, maxLength: 80);
        string? local = form.Text(LocalField, LocalBox.Text, maxLength: 80);
        if (!form.Apply(Fields, Summary)) return;
        SaveButton.IsEnabled = false;
        try
        {
            _editingId = await _service.SaveExpenseCategoryAsync(_editingId, name!, local, ActiveBox.IsChecked == true);
            Changed = true;
            await LoadAsync();
            SavedText.Text = TranslationSource.Instance["MoneyOut_Saved"];
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); }
        finally { SaveButton.IsEnabled = true; }
    }
}
