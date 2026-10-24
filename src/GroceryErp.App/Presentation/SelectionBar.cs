using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Appears in place of the filter row while rows are checked: the exact selected count, a way to
/// widen a fully checked page to every row matching the filters, Clear, and the page's bulk
/// actions. It covers the filters rather than pushing the table down, so the page size and the
/// rows stay put; changing a filter would clear the selection anyway. Built in code so the
/// actions placed in it keep the page's names.
/// </summary>
[ContentProperty(nameof(Actions))]
public sealed class SelectionBar : Border
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(SelectionBar),
        new PropertyMetadata(null, static (bar, args) => ((SelectionBar)bar)._actions.Content = args.NewValue));

    /// <summary>The filter row the bar stands in for; it is hidden, keeping its height, while rows are checked.</summary>
    public static readonly DependencyProperty CoversProperty = DependencyProperty.Register(
        nameof(Covers), typeof(UIElement), typeof(SelectionBar), new PropertyMetadata(null));


    private readonly TextBlock _count = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 12, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly Button _allMatching = new() { Visibility = Visibility.Collapsed };
    private readonly Button _clear = new();
    private readonly ContentPresenter _actions = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

    public SelectionBar()
    {
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 0, 0, 4);
        VerticalAlignment = VerticalAlignment.Top;
        Panel.SetZIndex(this, 1);
        Padding = new Thickness(8, 2, 8, 2);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "SelectionSurfaceBrush");
        SetResourceReference(BorderBrushProperty, "SelectionStrokeBrush");
        _allMatching.SetResourceReference(StyleProperty, "ToolbarButton");
        _clear.SetResourceReference(StyleProperty, "ToolbarButton");
        _allMatching.SetBinding(ContentControl.ContentProperty, Text("Selection_AllMatching"));
        _clear.SetBinding(ContentControl.ContentProperty, Text("Selection_Clear"));
        _allMatching.Click += (_, _) => AllMatchingRequested?.Invoke(this, EventArgs.Empty);
        _clear.Click += (_, _) => ClearRequested?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetLiveSetting(_count, AutomationLiveSetting.Polite);
        // Actions sit at the right, then Clear and "all matching"; the count and filters take
        // the rest and trim rather than wrap, so the bar stays one line high.
        var layout = new DockPanel { LastChildFill = true };
        foreach (FrameworkElement docked in new FrameworkElement[] { _actions, _clear, _allMatching })
        {
            DockPanel.SetDock(docked, Dock.Right);
            layout.Children.Add(docked);
        }
        _clear.Margin = _allMatching.Margin = new Thickness(6, 0, 0, 0);
        layout.Children.Add(_count);
        Child = layout;
    }

    public event EventHandler? AllMatchingRequested;

    public event EventHandler? ClearRequested;

    /// <summary>Gets or sets the bulk action controls shown at the right of the bar.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public UIElement? Covers
    {
        get => (UIElement?)GetValue(CoversProperty);
        set => SetValue(CoversProperty, value);
    }

    /// <summary>
    /// Shows the selection; offers "all matching" when a full page is checked and more pages exist.
    /// An all-matching selection also states the filters it was made under.
    /// </summary>
    public void Show(int count, bool allMatching, bool pageFullySelected, bool morePages, string? filters = null)
    {
        bool visible = count > 0;
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (Covers is not null) Covers.Visibility = visible ? Visibility.Hidden : Visibility.Visible;
        string text = allMatching
            ? TranslationSource.Instance.Format("Selection_AllMatchingCount", count)
            : TranslationSource.Instance.Format("Selection_Count", count);
        _count.Text = allMatching && !string.IsNullOrEmpty(filters) ? $"{text} · {filters}" : text;
        _count.ToolTip = _count.Text;
        _allMatching.Visibility = !allMatching && pageFullySelected && morePages ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Binding Text(string key) => new($"[{key}]") { Source = TranslationSource.Instance, Mode = BindingMode.OneWay };
}
