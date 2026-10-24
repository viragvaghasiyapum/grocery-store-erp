using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Keeps a row of action buttons on one line so the table below keeps its height. The buttons
/// keep their order; when the row is too narrow, the last ones move into a More menu, whose items
/// show each button's text and enabled state and run its click, and they come back when there is
/// room. The menu must be the last child of the button panel.
/// </summary>
internal sealed class ActionStrip
{
    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);
    private readonly FrameworkElement _room;
    private readonly Panel _buttons;
    private readonly FrameworkElement _menu;
    private readonly MenuItem _more;
    private readonly Button[] _order;
    private readonly DispatcherTimer _settle;
    private int _kept = -1;

    /// <param name="room">The element whose width is the room for the row.</param>
    /// <param name="buttons">The panel holding the buttons, most important first, and then the menu.</param>
    /// <param name="menu">The menu that shows the More item.</param>
    /// <param name="more">The More item that receives the buttons that do not fit.</param>
    public ActionStrip(FrameworkElement room, Panel buttons, FrameworkElement menu, MenuItem more)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(buttons);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(more);
        _room = room;
        _buttons = buttons;
        _menu = menu;
        _more = more;
        _order = [.. buttons.Children.OfType<Button>()];
        // Language changes are applied after the new text is laid out.
        _settle = new DispatcherTimer(DispatcherPriority.Loaded, room.Dispatcher) { Interval = TimeSpan.Zero };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _kept = -1;
            Arrange();
        };
        room.SizeChanged += (_, args) =>
        {
            if (args.WidthChanged) Arrange();
        };
        room.Loaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged += TranslationChanged;
            Arrange();
        };
        room.Unloaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= TranslationChanged;
            _settle.Stop();
        };
    }

    private void TranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]" && !_settle.IsEnabled) _settle.Start();
    }

    private void Arrange()
    {
        double available = _room.ActualWidth;
        if (available <= 0 || !_room.IsLoaded) return;
        double[] widths = _order.Select(static button => Width(button)).ToArray();
        int kept = _order.Length;
        // A little slack absorbs rounding so the row never wraps by a pixel.
        if (widths.Sum() > available - 2)
        {
            double room = available - 2 - Width(_menu, ignoreVisibility: true);
            kept = 0;
            while (kept < _order.Length && room - widths[kept] >= 0)
            {
                room -= widths[kept];
                kept++;
            }
        }
        if (kept == _kept) return;
        _kept = kept;
        Place(kept);
    }

    private void Place(int kept)
    {
        // Only the buttons whose place changes move, so a focused button is never re-added.
        for (int index = 0; index < _order.Length; index++)
        {
            Button button = _order[index];
            bool shown = _buttons.Children.Contains(button);
            if (index < kept && !shown) _buttons.Children.Insert(_buttons.Children.IndexOf(_menu), button);
            else if (index >= kept && shown) _buttons.Children.Remove(button);
        }
        _more.Items.Clear();
        foreach (Button button in _order.Skip(kept))
        {
            var item = new MenuItem();
            item.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding(nameof(Button.Content)) { Source = button });
            item.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(UIElement.IsEnabled)) { Source = button });
            item.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(UIElement.Visibility)) { Source = button });
            item.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            _more.Items.Add(item);
        }
        _menu.Visibility = _order.Skip(kept).Any(static button => button.Visibility != Visibility.Collapsed)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // The measured width with margins, so a button in the menu still counts; a hidden one takes none.
    private static double Width(FrameworkElement element, bool ignoreVisibility = false)
    {
        if (!ignoreVisibility && element.Visibility == Visibility.Collapsed) return 0;
        Visibility visibility = element.Visibility;
        if (ignoreVisibility) element.Visibility = Visibility.Visible;
        element.Measure(Unbounded);
        double width = element.DesiredSize.Width;
        if (ignoreVisibility) element.Visibility = visibility;
        return width;
    }
}
