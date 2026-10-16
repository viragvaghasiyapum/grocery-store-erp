using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace GroceryErp.App.Presentation;

internal static class SectionMotion
{
    private sealed class MotionState
    {
        public bool Open;
        public int Generation;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, MotionState> States = new();
    private static readonly Duration MotionDuration = new(TimeSpan.FromMilliseconds(190));

    internal static void SetOpen(FrameworkElement content, bool open)
    {
        MotionState state = States.GetOrCreateValue(content);
        if (state.Open == open && content.Visibility == (open ? Visibility.Visible : Visibility.Collapsed)) return;
        state.Open = open;
        int generation = ++state.Generation;
        double previousHeight = content.Visibility == Visibility.Visible ? content.ActualHeight : 0;
        content.BeginAnimation(FrameworkElement.HeightProperty, null);
        content.BeginAnimation(UIElement.OpacityProperty, null);

        if (!content.IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            Finish(content, open);
            return;
        }

        if (open)
        {
            content.Visibility = Visibility.Visible;
            content.Height = double.NaN;
            double availableWidth = (content.Parent as FrameworkElement)?.ActualWidth ?? content.ActualWidth;
            if (availableWidth <= 0) availableWidth = 800;
            content.Measure(new Size(availableWidth, double.PositiveInfinity));
            double targetHeight = content.DesiredSize.Height;
            if (targetHeight <= 0)
            {
                Finish(content, true);
                return;
            }
            content.Height = previousHeight;
            content.Opacity = previousHeight > 0 ? 1 : 0;
            Animate(content, previousHeight, targetHeight, content.Opacity, 1, state, generation, true);
        }
        else
        {
            if (previousHeight <= 0)
            {
                Finish(content, false);
                return;
            }
            content.Height = previousHeight;
            content.Opacity = 1;
            content.IsHitTestVisible = false;
            Animate(content, previousHeight, 0, 1, 0, state, generation, false);
        }
    }

    private static void Animate(FrameworkElement content, double fromHeight, double toHeight,
        double fromOpacity, double toOpacity, MotionState state, int generation, bool open)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var height = new DoubleAnimation(fromHeight, toHeight, MotionDuration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        height.Completed += (_, _) =>
        {
            if (state.Generation == generation) Finish(content, open);
        };
        content.BeginAnimation(FrameworkElement.HeightProperty, height);
        content.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(fromOpacity, toOpacity, MotionDuration)
            { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }

    private static void Finish(FrameworkElement content, bool open)
    {
        content.BeginAnimation(FrameworkElement.HeightProperty, null);
        content.BeginAnimation(UIElement.OpacityProperty, null);
        content.Height = double.NaN;
        content.Opacity = 1;
        content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        content.IsHitTestVisible = true;
    }
}
