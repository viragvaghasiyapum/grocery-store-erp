using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GroceryErp.Catalog;
using GroceryErp.Core;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class CatalogMastersWindow : Window
{
    private readonly CatalogService _catalog;
    private MasterItem? _editingCategory;
    private CategoryTreeNode? _dragCategory;
    private Point _dragStart;

    public CatalogMastersWindow(CatalogService catalog, bool loadOnShow = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        InitializeComponent();
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IReadOnlyList<MasterItem> categories = await _catalog.GetCategoriesAsync();
        var nodes = categories.ToDictionary(static item => item.Id, static item => new CategoryTreeNode(item));
        List<CategoryTreeNode> roots = [];
        foreach (CategoryTreeNode node in nodes.Values)
        {
            if (node.Item.ParentId is { } parentId && nodes.TryGetValue(parentId, out CategoryTreeNode? parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }
        CategoryTree.ItemsSource = roots;
        ParentCategoryBox.ItemsSource = categories.Where(static item => item.IsActive).ToList();
        BrandList.ItemsSource = await _catalog.GetBrandsAsync();
        UnitList.ItemsSource = await _catalog.GetUnitDetailsAsync();
        TaxList.ItemsSource = await _catalog.GetTaxRatesAsync();
    }

    private void CategoryTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (CategoryTree.SelectedItem is not CategoryTreeNode node) return;
        MasterItem item = node.Item;
        _editingCategory = item;
        CategoryNameBox.Text = item.Name;
        CategoryLocalBox.Text = item.NameLocal;
        ParentCategoryBox.SelectedValue = item.ParentId;
        CategoryExpiryBox.Text = item.ExpiryAlertDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void BrandList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BrandList.SelectedItem is MasterItem item) BrandNameBox.Text = item.Name;
    }

    private void UnitList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UnitList.SelectedItem is not UnitItem item) return;
        UnitCodeBox.Text = item.Code;
        UnitNameBox.Text = item.Name;
        UnitLocalBox.Text = item.NameLocal;
        UnitFractionBox.IsChecked = item.AllowFraction;
    }

    private void TaxList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaxList.SelectedItem is not TaxRateItem item) return;
        TaxNameBox.Text = item.Name;
        TaxRateValueBox.Text = item.Rate.ToString("0.###", CultureInfo.InvariantCulture);
        TaxActiveBox.IsChecked = item.IsActive;
    }

    private void NewCategory_OnClick(object sender, RoutedEventArgs e)
    {
        if (CategoryTree.SelectedItem is CategoryTreeNode selected && FindContainer(CategoryTree, selected) is { } container)
            container.IsSelected = false;
        _editingCategory = null;
        CategoryNameBox.Clear();
        CategoryLocalBox.Clear();
        CategoryExpiryBox.Clear();
        ParentCategoryBox.SelectedIndex = -1;
        StatusText.Text = string.Empty;
        CategoryNameBox.Focus();
    }

    private void NewBrand_OnClick(object sender, RoutedEventArgs e)
    {
        BrandList.SelectedIndex = -1;
        BrandNameBox.Clear();
        StatusText.Text = string.Empty;
        BrandNameBox.Focus();
    }

    private void NewUnit_OnClick(object sender, RoutedEventArgs e)
    {
        UnitList.SelectedIndex = -1;
        UnitCodeBox.Clear();
        UnitNameBox.Clear();
        UnitLocalBox.Clear();
        UnitFractionBox.IsChecked = false;
        StatusText.Text = string.Empty;
        UnitCodeBox.Focus();
    }

    private void NewTax_OnClick(object sender, RoutedEventArgs e)
    {
        TaxList.SelectedIndex = -1;
        TaxNameBox.Clear();
        TaxRateValueBox.Clear();
        TaxActiveBox.IsChecked = true;
        StatusText.Text = string.Empty;
        TaxNameBox.Focus();
    }

    private async void SaveCategory_OnClick(object sender, RoutedEventArgs e)
    {
        if (Missing(CategoryNameBox, "Catalog_Name")) return;
        try
        {
            int? expiryDays = null;
            if (!string.IsNullOrWhiteSpace(CategoryExpiryBox.Text))
            {
                if (!int.TryParse(CategoryExpiryBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
                {
                    StatusText.Text = TranslationSource.Instance.Format("Form_WholeNumberAtLeast", 0);
                    return;
                }
                expiryDays = parsed;
            }
            var input = new CategoryInput(CategoryNameBox.Text, CategoryLocalBox.Text,
                ParentCategoryBox.SelectedValue as long?, expiryDays);
            if (_editingCategory is { } selected)
                await _catalog.UpdateCategoryAsync(selected.Id, input);
            else
                await _catalog.CreateCategoryAsync(input);
            NewCategory_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void SaveBrand_OnClick(object sender, RoutedEventArgs e)
    {
        if (Missing(BrandNameBox, "Catalog_Name")) return;
        try
        {
            if (BrandList.SelectedItem is MasterItem selected)
                await _catalog.UpdateBrandAsync(selected.Id, BrandNameBox.Text);
            else
                await _catalog.CreateBrandAsync(BrandNameBox.Text);
            NewBrand_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void SaveUnit_OnClick(object sender, RoutedEventArgs e)
    {
        if (Missing(UnitCodeBox, "Catalog_UnitCode") || Missing(UnitNameBox, "Catalog_Name")) return;
        try
        {
            if (UnitList.SelectedItem is UnitItem selected)
                await _catalog.UpdateUnitAsync(selected.Id, UnitCodeBox.Text, UnitNameBox.Text, UnitLocalBox.Text, UnitFractionBox.IsChecked == true);
            else
                await _catalog.CreateUnitAsync(UnitCodeBox.Text, UnitNameBox.Text, UnitLocalBox.Text, UnitFractionBox.IsChecked == true);
            NewUnit_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void SaveTax_OnClick(object sender, RoutedEventArgs e)
    {
        if (Missing(TaxNameBox, "Catalog_Name")) return;
        try
        {
            if (!decimal.TryParse(TaxRateValueBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal rate))
            {
                StatusText.Text = TranslationSource.Instance["Form_InvalidNumber"];
                return;
            }
            if (TaxList.SelectedItem is TaxRateItem selected)
                await _catalog.UpdateTaxRateAsync(selected.Id, TaxNameBox.Text, rate, TaxActiveBox.IsChecked == true);
            else
                await _catalog.CreateTaxRateAsync(TaxNameBox.Text, rate);
            NewTax_OnClick(sender, e);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ShowError(Exception exception) => StatusText.Text = UserError.Message(exception);

    // The service refuses an empty name as an invalid argument, which reads as "Could not complete the
    // action". Each box's length limit already matches the service's, so only an empty one is checked
    // here, after the same normalizing the service does.
    private bool Missing(TextBox box, string labelKey)
    {
        if (!string.IsNullOrWhiteSpace(InputNormalizer.Normalize(box.Text ?? string.Empty))) return false;
        StatusText.Text = TranslationSource.Instance.Format("Form_FieldRequired", TranslationSource.Instance[labelKey]);
        box.Focus();
        return true;
    }

    private void CategoryTree_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCategory = NodeAt(e.OriginalSource);
        _dragStart = e.GetPosition(CategoryTree);
    }

    private void CategoryTree_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCategory is null) return;
        Point current = e.GetPosition(CategoryTree);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        CategoryTreeNode dragged = _dragCategory;
        _dragCategory = null;
        DragDrop.DoDragDrop(CategoryTree, dragged, DragDropEffects.Move);
    }

    private void CategoryTree_OnDragOver(object sender, DragEventArgs e)
    {
        CategoryTreeNode? dragged = e.Data.GetData(typeof(CategoryTreeNode)) as CategoryTreeNode;
        CategoryTreeNode? target = NodeAt(e.OriginalSource);
        e.Effects = dragged is not null && target != dragged && (target is null || target.Item.IsActive)
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void CategoryTree_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CategoryTreeNode)) is not CategoryTreeNode dragged) return;
        CategoryTreeNode? target = NodeAt(e.OriginalSource);
        if (target == dragged || target is { Item: { IsActive: false } }) return;
        try
        {
            MasterItem category = dragged.Item;
            await _catalog.UpdateCategoryAsync(category.Id,
                new CategoryInput(category.Name, category.NameLocal, target?.Item.Id, category.ExpiryAlertDays));
            _editingCategory = null;
            await RefreshAsync();
            StatusText.Text = string.Empty;
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private static CategoryTreeNode? NodeAt(object source)
    {
        for (DependencyObject? element = source as DependencyObject; element is not null;
             element = element is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is TreeViewItem { DataContext: CategoryTreeNode node }) return node;
        return null;
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, CategoryTreeNode node)
    {
        foreach (object item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            if (item == node) return container;
            if (FindContainer(container, node) is { } child) return child;
        }
        return null;
    }

    private sealed class CategoryTreeNode(MasterItem item)
    {
        public MasterItem Item { get; } = item;
        public List<CategoryTreeNode> Children { get; } = [];
    }
}
