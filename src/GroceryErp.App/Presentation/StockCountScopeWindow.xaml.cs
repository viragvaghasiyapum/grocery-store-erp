using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class StockCountScopeWindow : Window
{
    private readonly CatalogService _catalog;

    public StockCountScopeWindow(CatalogService catalog, bool loadOnShow = true)
    {
        InitializeComponent();
        _catalog = catalog;
        UpdateCategoryCount();
        if (loadOnShow) Loaded += LoadAsync;
    }

    public IReadOnlyCollection<long>? CategoryIds { get; private set; }
    public string? Notes { get; private set; }

    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        try { CategoriesBox.ItemsSource = await _catalog.GetCategoriesAsync(); }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }

    private void AllCategoriesBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (CategoriesBox is not null) CategoriesBox.IsEnabled = AllCategoriesBox.IsChecked != true;
    }

    private void CategoriesBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateCategoryCount();

    private void ClearCategories_OnClick(object sender, RoutedEventArgs e) => CategoriesBox.UnselectAll();

    private void UpdateCategoryCount()
    {
        if (CategorySelectedCount is not null && CategoriesBox is not null)
            CategorySelectedCount.Text = TranslationSource.Instance.Format("Selection_Count", CategoriesBox.SelectedItems.Count);
    }

    private void Continue_OnClick(object sender, RoutedEventArgs e)
    {
        if (AllCategoriesBox.IsChecked != true && CategoriesBox.SelectedItems.Count == 0)
        {
            ErrorText.Text = TranslationSource.Instance["Stock_SelectCategory"];
            return;
        }
        CategoryIds = AllCategoriesBox.IsChecked == true ? null
            : CategoriesBox.SelectedItems.Cast<MasterItem>().Select(static category => category.Id).ToArray();
        Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
