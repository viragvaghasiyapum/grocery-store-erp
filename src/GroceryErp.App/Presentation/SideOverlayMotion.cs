using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GroceryErp.App.Presentation;

internal static class SideOverlayMotion
{
    internal static void Show(Grid overlay, Border panel, Border backdrop)
    {
        overlay.Visibility = Visibility.Visible;
        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        panel.RenderTransform = new TranslateTransform();
        ((TranslateTransform)panel.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(56, 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        panel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
        backdrop.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
    }
}
