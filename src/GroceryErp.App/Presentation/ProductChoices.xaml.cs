using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using GroceryErp.Catalog;

namespace GroceryErp.App.Presentation;

/// <summary>
/// The products matching a typed search, listed under the search box with their code, price and
/// stock. The arrows move through them, Enter or a click picks one, and Esc or a click elsewhere
/// closes the list; the owner adds the picked product and takes the keyboard back on Closed.
/// </summary>
public partial class ProductChoices : Popup
{
    public ProductChoices() => InitializeComponent();

    public event EventHandler<ProductListItem>? Picked;

    /// <summary>Lists the matches with the first one selected and moves the keyboard into the list.</summary>
    public void Open(IReadOnlyList<ProductListItem> matches)
    {
        List.ItemsSource = matches;
        List.SelectedIndex = 0;
        IsOpen = true;
        _ = Dispatcher.BeginInvoke(FocusSelected, DispatcherPriority.Loaded);
    }

    /// <summary>Moves the keyboard into the open list, as Down in the search box does.</summary>
    public void FocusSelected()
    {
        if (List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) is ListBoxItem item) item.Focus();
        else List.Focus();
    }

    private void List_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            IsOpen = false;
        }
        else if (e.Key == Key.Enter && List.SelectedItem is ProductListItem product)
        {
            e.Handled = true;
            Pick(product);
        }
    }

    private void List_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ProductListItem product) Pick(product);
    }

    private void Pick(ProductListItem product)
    {
        IsOpen = false;
        Picked?.Invoke(this, product);
    }
}
