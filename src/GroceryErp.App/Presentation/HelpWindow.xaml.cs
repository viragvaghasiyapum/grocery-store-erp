using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Shows one screen's help: what the screen is for, then its fields, columns and buttons. Opened by
/// F1 in a field, it marks that field's row and scrolls it to the top; on a screen with tabs, a
/// field's rows in the open tab's sections come first, and without a field it opens at that tab's
/// first section. Like the release notes, it is never taller than the screen's work area: the help
/// scrolls and Close stays in view.
/// </summary>
public partial class HelpWindow : Window
{
    private readonly List<Border> _marked = [];
    private FrameworkElement? _opened;

    internal HelpWindow(HelpDocument document, string? field, IReadOnlyList<string> tabs)
        : this(document, field, tabs, SystemParameters.WorkArea.Height) { }

    /// <param name="tabs">The headers of the open tabs, innermost first (see <see cref="ScreenHelp.Tabs"/>).</param>
    internal HelpWindow(HelpDocument document, string? field, IReadOnlyList<string> tabs, double workAreaHeight)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(tabs);
        InitializeComponent();
        Title = TranslationSource.Instance.Format("Help_Title", document.Title);
        TitleText.Text = document.Title;
        SummaryText.Text = document.Summary;
        // "Name" can be on several tabs; the open one's row is the one meant.
        string? tab = tabs.FirstOrDefault(tab => document.Sections.Any(section => section.Covers(tab)));
        bool inTab = field is not null && document.Sections.Any(section =>
            section.Covers(tab) && section.Rows.Any(row => row.Describes(field)));
        foreach (HelpSection section in document.Sections)
        {
            if (section.Heading.Length > 0)
            {
                var heading = new TextBlock { Text = section.Heading, TextWrapping = TextWrapping.Wrap };
                heading.SetResourceReference(StyleProperty, "FormSection");
                heading.Margin = new Thickness(0, 14, 0, 6);
                Body.Children.Add(heading);
                if (_opened is null && section.Covers(tab))
                {
                    _opened = heading;
                    OpenedSection = section.Heading;
                }
            }
            string? marks = inTab && !section.Covers(tab) ? null : field;
            foreach (HelpBlock block in section.Blocks) Body.Children.Add(Render(block, marks));
        }
        MaxHeight = Math.Max(320, workAreaHeight - 24);
        Loaded += (_, _) =>
        {
            Scroll.Focus();
            if (_marked.Count > 0 || _opened is not null) Dispatcher.BeginInvoke(ScrollToMarked, DispatcherPriority.Loaded);
        };
    }

    /// <summary>Gets the rows that describe the field F1 was pressed in.</summary>
    internal IReadOnlyList<Border> MarkedRows => _marked;

    /// <summary>Gets the heading of the open tab's first section, or null when no section covers it.</summary>
    internal string? OpenedSection { get; }

    private FrameworkElement Render(HelpBlock block, string? field) => block switch
    {
        HelpParagraph paragraph => Spaced(Text(paragraph.Text)),
        HelpList list => List(list),
        HelpTable table => Table(table, field),
        _ => throw new ArgumentOutOfRangeException(nameof(block)),
    };

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 0, 0, 8);
        return element;
    }

    private static StackPanel List(HelpList list)
    {
        var items = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        int number = 0;
        foreach (IReadOnlyList<HelpSpan> item in list.Items)
        {
            number++;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock
            {
                Text = list.Ordered ? number.ToString(CultureInfo.InvariantCulture) + "." : "•",
            });
            TextBlock text = Text(item);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            items.Children.Add(row);
        }
        return items;
    }

    private StackPanel Table(HelpTable table, string? field)
    {
        var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        Border header = Row(
            new TextBlock { Text = table.TermHeading, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = table.MeaningHeading, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (TextBlock heading in ((Grid)header.Child).Children.OfType<TextBlock>())
            heading.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        rows.Children.Add(header);
        foreach (HelpRow help in table.Rows)
        {
            Border row = Row(Text(help.Term), Text(help.Meaning));
            if (field is not null && help.Describes(field))
            {
                row.SetResourceReference(Border.BackgroundProperty, "SelectionSurfaceBrush");
                row.SetResourceReference(Border.BorderBrushProperty, "SelectionStrokeBrush");
                row.BorderThickness = new Thickness(1);
                _marked.Add(row);
            }
            rows.Children.Add(row);
        }
        return rows;
    }

    private static Border Row(TextBlock term, TextBlock meaning)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        term.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(meaning, 1);
        grid.Children.Add(term);
        grid.Children.Add(meaning);
        var row = new Border { Padding = new Thickness(6, 6, 6, 6), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        row.SetResourceReference(Border.BorderBrushProperty, "TableRuleBrush");
        return row;
    }

    private static TextBlock Text(IEnumerable<HelpSpan> spans)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        foreach (HelpSpan span in spans)
            text.Inlines.Add(span.Bold ? new Run(span.Text) { FontWeight = FontWeights.SemiBold } : new Run(span.Text));
        return text;
    }

    // The field's row, else the open tab's section, goes to the top.
    private void ScrollToMarked()
    {
        FrameworkElement target = _marked.Count > 0 ? _marked[0] : _opened!;
        Point top = target.TransformToAncestor(Body).Transform(new Point(0, 0));
        Scroll.ScrollToVerticalOffset(Math.Max(0, top.Y - 8));
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
