using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using GroceryErp.Localization;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace GroceryErp.App.Presentation;

/// <summary>
/// The detail view every table page opens beside its list. The header holds the record's title,
/// its key facts, previous/next record arrows, the full-width switch and Close (Esc); under it are
/// the record's own actions, then the body, often tabs whose tables work like a page's. Built in
/// code so the content a page places in it keeps the page's names; <see cref="DetailSplit"/>
/// places it beside the list.
/// </summary>
[ContentProperty(nameof(Body))]
public sealed class DetailPanel : Border
{
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(DetailPanel),
        new PropertyMetadata(null, static (panel, args) => ((DetailPanel)panel)._body.Content = args.NewValue));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(DetailPanel),
        new PropertyMetadata(null, static (panel, args) =>
        {
            var self = (DetailPanel)panel;
            self._actions.Content = args.NewValue;
            self._actions.Visibility = args.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
        }));

    // Content controls, so the page's content is part of the panel's logical tree.
    private readonly ContentControl _body = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
        Focusable = false,
        IsTabStop = false,
    };
    private readonly ContentControl _actions = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed,
        Focusable = false,
        IsTabStop = false,
    };
    private readonly SymbolIcon _widthIcon = new() { Symbol = SymbolRegular.FullScreenMaximize20 };
    private Selector? _list;
    private bool _isFullWidth;

    public DetailPanel()
    {
        Padding = new Thickness(14, 10, 14, 10);
        BorderThickness = new Thickness(1, 0, 0, 0);
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        TitleText = new TextBlock { FontSize = 18, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        TitleText.SetResourceReference(StyleProperty, "FormTitle");
        FactsText = new TextBlock { Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
        FactsText.SetResourceReference(StyleProperty, "FormContext");
        SummaryText = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetLiveSetting(SummaryText, AutomationLiveSetting.Polite);

        PreviousButton = IconButton(SymbolRegular.ChevronUp20, "Common_PreviousRecord");
        NextButton = IconButton(SymbolRegular.ChevronDown20, "Common_NextRecord");
        PreviousButton.Visibility = NextButton.Visibility = Visibility.Collapsed;
        WidthButton = IconButton(SymbolRegular.FullScreenMaximize20, "Common_FullWidth");
        WidthButton.Content = _widthIcon;
        CloseButton = new Button { Margin = new Thickness(8, 0, 0, 0), ToolTip = "Esc" };
        CloseButton.SetResourceReference(StyleProperty, "ToolbarButton");
        CloseButton.SetBinding(ContentControl.ContentProperty, Text("Common_Close"));
        PreviousButton.Click += (_, _) => Move(-1);
        NextButton.Click += (_, _) => Move(1);
        WidthButton.Click += (_, _) =>
        {
            IsFullWidth = !IsFullWidth;
            FullWidthChanged?.Invoke(this, EventArgs.Empty);
        };
        CloseButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        foreach (Button button in new[] { PreviousButton, NextButton, WidthButton, CloseButton }) buttons.Children.Add(button);
        var texts = new StackPanel();
        texts.Children.Add(TitleText);
        texts.Children.Add(FactsText);
        texts.Children.Add(SummaryText);
        var header = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(texts);

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_actions, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(_actions);
        layout.Children.Add(_body);
        Child = layout;

        // Bubbling, so an open list or a cell being edited takes its own Esc first.
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || args.Handled) return;
            args.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? CloseRequested;

    /// <summary>Raised when the operator switches between beside the list and the full width.</summary>
    public event EventHandler? FullWidthChanged;

    /// <summary>Gets or sets the record's content, below the header and actions.</summary>
    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>Gets or sets the record's own actions, shown under the header.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public string Title
    {
        get => TitleText.Text;
        set
        {
            TitleText.Text = value;
            TitleText.ToolTip = value;
        }
    }

    /// <summary>Gets or sets the record's key facts, a line under the title.</summary>
    public string Facts
    {
        get => FactsText.Text;
        set => Show(FactsText, value);
    }

    /// <summary>Gets or sets an emphasized line under the facts, such as the stock on hand.</summary>
    public string Summary
    {
        get => SummaryText.Text;
        set => Show(SummaryText, value);
    }

    /// <summary>Gets or sets whether the panel offers the full-width switch; a narrow panel does not.</summary>
    public bool CanResize
    {
        get => WidthButton.Visibility == Visibility.Visible;
        set => WidthButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsFullWidth
    {
        get => _isFullWidth;
        set
        {
            _isFullWidth = value;
            _widthIcon.Symbol = value ? SymbolRegular.FullScreenMinimize20 : SymbolRegular.FullScreenMaximize20;
            string key = value ? "Common_BesideList" : "Common_FullWidth";
            WidthButton.SetBinding(ToolTipProperty, Text(key));
            WidthButton.SetBinding(AutomationProperties.NameProperty, Text(key));
        }
    }

    internal TextBlock TitleText { get; }

    internal TextBlock FactsText { get; }

    internal TextBlock SummaryText { get; }

    internal Button PreviousButton { get; }

    internal Button NextButton { get; }

    internal Button WidthButton { get; }

    internal Button CloseButton { get; }

    /// <summary>
    /// Shows the previous/next arrows for the list the panel shows a record of: they move its
    /// selection, and the page shows the newly selected record as it does for a click.
    /// </summary>
    public void Follow(Selector list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _list = list;
        PreviousButton.Visibility = NextButton.Visibility = Visibility.Visible;
        list.SelectionChanged += (_, _) => RefreshNavigation();
        RefreshNavigation();
    }

    /// <summary>Enables each arrow when the list has a record before or after the selected one.</summary>
    public void RefreshNavigation()
    {
        int index = _list?.SelectedIndex ?? -1;
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = _list is not null && index >= 0 && index < _list.Items.Count - 1;
    }

    private void Move(int step)
    {
        if (_list is null) return;
        int index = _list.SelectedIndex + step;
        if (index < 0 || index >= _list.Items.Count) return;
        _list.SelectedIndex = index;
        if (_list is System.Windows.Controls.DataGrid grid) grid.ScrollIntoView(grid.SelectedItem);
    }

    private static void Show(TextBlock line, string text)
    {
        line.Text = text;
        line.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Button IconButton(SymbolRegular symbol, string hintKey)
    {
        var button = new Button { Content = new SymbolIcon { Symbol = symbol }, Margin = new Thickness(4, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "PagerButton");
        button.SetBinding(ToolTipProperty, Text(hintKey));
        button.SetBinding(AutomationProperties.NameProperty, Text(hintKey));
        return button;
    }

    private static Binding Text(string key) => new($"[{key}]") { Source = TranslationSource.Instance, Mode = BindingMode.OneWay };
}
