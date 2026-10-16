using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GroceryErp.App.Presentation;

internal static class ScrollWheelRouting
{
    internal static void Register() => EventManager.RegisterClassHandler(
        typeof(Window), UIElement.PreviewMouseWheelEvent,
        new MouseWheelEventHandler(OnPreviewMouseWheel));

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.OriginalSource is not DependencyObject source) return;
        bool horizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        for (DependencyObject? current = source; current is not null && current != sender; current = Parent(current))
        {
            if (current is not ScrollViewer viewer) continue;
            double range = horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight;
            if (range <= 0) continue;
            double offset = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
            double step = viewer.CanContentScroll ? 3 : 48;
            double target = Math.Clamp(offset - e.Delta / 120.0 * step, 0, range);
            if (Math.Abs(target - offset) < 0.001) continue;
            if (horizontal) viewer.ScrollToHorizontalOffset(target);
            else viewer.ScrollToVerticalOffset(target);
            e.Handled = true;
            return;
        }
    }

    private static DependencyObject? Parent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}
