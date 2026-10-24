using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Keeps a table page's filter row on one line so the table keeps its height. The search box takes
/// the room the other controls leave, between a minimum and a maximum width. When even the minimum
/// does not fit, the registered filters move, lowest priority first, to the front of the More
/// filters row and come back when there is room. The More filters toggle shows how many filters
/// behind it differ from their defaults.
/// </summary>
internal sealed class FilterRow
{
    private const double SearchMinimum = 180;
    private const double SearchMaximum = 340;
    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);
    private readonly Panel _bar;
    private readonly FrameworkElement _search;
    private readonly ToggleButton? _more;
    private readonly Panel? _morePanel;
    private readonly List<UIElement> _barOrder;
    private readonly List<UIElement> _moreOrder;
    private readonly List<FrameworkElement[]> _units = [];
    private readonly DispatcherTimer _settle;
    private int _kept = -1;

    public FilterRow(Panel bar, FrameworkElement search, ToggleButton? more = null, Panel? morePanel = null)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(search);
        _bar = bar;
        _search = search;
        _more = more;
        _morePanel = morePanel;
        _barOrder = [.. bar.Children.Cast<UIElement>()];
        _moreOrder = morePanel is null ? [] : [.. morePanel.Children.Cast<UIElement>()];
        // Load and resize arrange at once, so a page sizes its first query to the final row height;
        // language and filter changes are coalesced into one pass after layout.
        _settle = new DispatcherTimer(DispatcherPriority.Loaded, bar.Dispatcher) { Interval = TimeSpan.Zero };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Arrange();
        };
        bar.SizeChanged += (_, args) =>
        {
            if (args.WidthChanged) Arrange();
        };
        bar.Loaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged += TranslationChanged;
            Arrange();
        };
        bar.Unloaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= TranslationChanged;
            _settle.Stop();
        };
        Panel[] panels = morePanel is null ? [bar] : [bar, morePanel];
        foreach (Panel panel in panels)
        {
            panel.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Schedule()));
            panel.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Schedule()));
            panel.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => Schedule()));
            panel.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Schedule()));
        }
    }

    /// <summary>
    /// Registers filters that may move behind More filters, together, such as a label and its box.
    /// Register the most important first; the last registered moves first.
    /// </summary>
    public FilterRow Fold(params FrameworkElement[] unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (_more is null || _morePanel is null) throw new InvalidOperationException("Folding needs a More filters row.");
        _units.Add(unit);
        return this;
    }

    private void TranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        // The toggle's own text changes now; widths settle after the new text is laid out.
        UpdateMoreLabel();
        Schedule();
    }

    private void Schedule()
    {
        if (!_settle.IsEnabled) _settle.Start();
    }

    private void Arrange()
    {
        double available = _bar.ActualWidth;
        if (available <= 0 || !_bar.IsLoaded) return;
        HashSet<UIElement> folding = [.. _units.SelectMany(static unit => unit)];
        double searchMargin = _search.Margin.Left + _search.Margin.Right;
        double[] unitWidths = _units.Select(static unit => unit.Sum(static element => Width(element))).ToArray();
        double room = 0;
        // The toggle's count can change its width, so a second pass settles the search width.
        for (int pass = 0; pass < 2; pass++)
        {
            double fixedWidth = _barOrder.Where(element => element != _search && !folding.Contains(element))
                .Sum(static element => Width(element));
            // Keep the longest prefix of units that still leaves the search box its minimum width;
            // a little slack absorbs rounding so the row never wraps by a pixel.
            room = available - fixedWidth - searchMargin - SearchMinimum - 4;
            int kept = 0;
            while (kept < _units.Count && room - unitWidths[kept] >= 0)
            {
                room -= unitWidths[kept];
                kept++;
            }
            if (kept != _kept)
            {
                _kept = kept;
                Place(kept);
            }
            if (!UpdateMoreLabel()) break;
        }
        _search.Width = Math.Clamp(SearchMinimum + room, SearchMinimum, SearchMaximum);
    }

    private void Place(int kept)
    {
        HashSet<UIElement> folded = [.. _units.Skip(kept).SelectMany(static unit => unit)];
        Replace(_bar, [.. _barOrder.Where(element => !folded.Contains(element))]);
        if (_morePanel is not null) Replace(_morePanel, [.. _barOrder.Where(folded.Contains), .. _moreOrder]);
    }

    // Moves only the controls whose place changes, so the focused search box is never re-added.
    private static void Replace(Panel panel, UIElement[] target)
    {
        for (int index = 0; index < target.Length; index++)
        {
            if (index < panel.Children.Count && panel.Children[index] == target[index]) continue;
            UIElement child = target[index];
            (System.Windows.Media.VisualTreeHelper.GetParent(child) as Panel)?.Children.Remove(child);
            panel.Children.Insert(index, child);
        }
        while (panel.Children.Count > target.Length) panel.Children.RemoveAt(target.Length);
    }

    /// <summary>Shows how many filters behind the toggle are set; returns whether the text changed.</summary>
    private bool UpdateMoreLabel()
    {
        if (_more is null || _morePanel is null) return false;
        string text = TranslationSource.Instance["Common_MoreFilters"];
        int active = FilterText.Count(_morePanel.Children.OfType<FrameworkElement>());
        string label = active > 0 ? $"{text} · {active}" : text;
        if (Equals(_more.Content, label)) return false;
        _more.Content = label;
        return true;
    }

    // A fixed width plus margins, or the measured width, so a folded or hidden control still counts.
    private static double Width(UIElement element)
    {
        if (element is not FrameworkElement framework || framework.Visibility == Visibility.Collapsed) return 0;
        if (!double.IsNaN(framework.Width)) return framework.Width + framework.Margin.Left + framework.Margin.Right;
        framework.Measure(Unbounded);
        return framework.DesiredSize.Width;
    }
}
