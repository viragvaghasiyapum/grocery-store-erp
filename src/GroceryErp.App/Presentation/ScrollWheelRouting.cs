using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GroceryErp.App.Presentation;

internal static class ScrollWheelRouting
{
    private static readonly ConditionalWeakTable<ScrollViewer, WheelAccumulator> Accumulators = new();

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
            if ((e.Delta < 0 && offset >= range - 0.001) ||
                (e.Delta > 0 && offset <= 0.001))
            {
                if (Accumulators.TryGetValue(viewer, out WheelAccumulator? atEdge))
                {
                    if (horizontal) atEdge.Horizontal = 0;
                    else atEdge.Vertical = 0;
                }
                continue;
            }
            double target;
            if (viewer.CanContentScroll)
            {
                WheelAccumulator accumulator = Accumulators.GetValue(viewer, static _ => new WheelAccumulator());
                double pending = (horizontal ? accumulator.Horizontal : accumulator.Vertical)
                    - e.Delta / 120.0 * 3;
                double wholeRows = Math.Truncate(pending);
                if (horizontal) accumulator.Horizontal = pending - wholeRows;
                else accumulator.Vertical = pending - wholeRows;
                if (wholeRows == 0)
                {
                    e.Handled = true;
                    return;
                }
                target = Math.Clamp(offset + wholeRows, 0, range);
            }
            else target = Math.Clamp(offset - e.Delta / 120.0 * 48, 0, range);
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

    private sealed class WheelAccumulator
    {
        public double Vertical;
        public double Horizontal;
    }
}
