using System.Windows;
using System.Windows.Controls;

namespace GroceryErp.App.Presentation;

/// <summary>
/// The totals of the table above it, in a footer that stays in view while the rows scroll: each
/// total's name, then its amount, in the normal text weight. Totals that do not fit one line wrap
/// onto the next instead of being cut off, and the bar hides while there are none. Built in code
/// like <see cref="PagerBar"/>.
/// </summary>
public sealed class TotalsBar : Border
{
    private readonly WrapPanel _items = new();

    public TotalsBar()
    {
        Focusable = false;
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 4, 0, 0);
        Padding = new Thickness(0, 6, 0, 0);
        BorderThickness = new Thickness(0, 1, 0, 0);
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Child = _items;
    }

    /// <summary>Gets the totals on one line, each name with its amount.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Shows the totals in the order given; none hides the bar.</summary>
    public void Show(IEnumerable<(string Label, string Value)> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        _items.Children.Clear();
        var parts = new List<string>();
        foreach ((string label, string value) in totals)
        {
            // Two text blocks rather than two runs of one: a run styled on its own drew smaller.
            var name = new TextBlock { Text = label, Margin = new Thickness(0, 0, 5, 0) };
            name.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 4) };
            item.Children.Add(name);
            item.Children.Add(new TextBlock { Text = value });
            _items.Children.Add(item);
            parts.Add($"{label} {value}");
        }
        Text = string.Join(" · ", parts);
        Visibility = parts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Clear() => Show([]);
}
