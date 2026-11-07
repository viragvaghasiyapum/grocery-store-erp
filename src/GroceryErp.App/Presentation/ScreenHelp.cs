using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GroceryErp.Localization;
using Serilog;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Screen help. A page or dialog names its topic with <c>ScreenHelp.Topic</c> on its root; the
/// value flows down to every field. The ⓘ <see cref="HelpButton"/> beside the title, or F1 in any
/// field, opens that topic, and F1 also marks the row for the field, column or button that has
/// focus. Each topic is a Markdown file the user guide shares,
/// <c>docs/user-guide/help/{en,gu}/{topic}.md</c>, embedded in this assembly.
/// </summary>
public static class ScreenHelp
{
    public static readonly DependencyProperty TopicProperty = DependencyProperty.RegisterAttached(
        "Topic", typeof(string), typeof(ScreenHelp),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    private static readonly Dictionary<string, HelpDocument> Documents = new(StringComparer.Ordinal);

    public static string? GetTopic(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(TopicProperty);
    }

    public static void SetTopic(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TopicProperty, value);
    }

    /// <summary>Makes F1 (WPF's Help command) open the help of whichever screen has focus.</summary>
    internal static void Register() => CommandManager.RegisterClassCommandBinding(typeof(Window),
        new CommandBinding(ApplicationCommands.Help, OnHelp, OnCanHelp));

    /// <summary>
    /// Opens a topic's help over the window that holds <paramref name="source"/>, at the field's row
    /// or, without one, at the section for the open tab.
    /// </summary>
    internal static void Open(DependencyObject source, string topic, string? field)
    {
        HelpDocument? document = Read(topic, TranslationSource.Instance.UiCulture);
        if (document is null)
        {
            Log.Warning("No help text for topic {Topic}", topic);
            return;
        }
        var window = new HelpWindow(document, field, Tabs(source, everywhere: field is null)) { Owner = Window.GetWindow(source) };
        window.ShowDialog();
    }

    /// <summary>Gets a topic's help in the culture's language, or null when the topic has none.</summary>
    internal static HelpDocument? Read(string topic, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(culture);
        string language = string.Equals(culture.TwoLetterISOLanguageName, "gu", StringComparison.Ordinal) ? "gu" : "en";
        string name = $"GroceryErp.App.Help.{language}.{topic}.md";
        if (Documents.TryGetValue(name, out HelpDocument? cached)) return cached;
        using Stream? stream = typeof(ScreenHelp).Assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        HelpDocument document = HelpDocument.Parse(reader.ReadToEnd());
        Documents[name] = document;
        return document;
    }

    /// <summary>
    /// Gets the label of the field, column or button that has focus: a table cell's column header,
    /// else the nearest accessible name, button text or menu or tab header, up to the screen's root.
    /// </summary>
    internal static string? FieldLabel(DependencyObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        for (DependencyObject? current = source; current is not null; current = Parent(current))
        {
            string? label = current switch
            {
                DataGridCell { Column.Header: string column } => column,
                DataGridColumnHeader { Column.Header: string column } => column,
                _ => AutomationProperties.GetName(current) is { Length: > 0 } name ? name : current switch
                {
                    ButtonBase { Content: string text } => text,
                    HeaderedItemsControl { Header: string header } => header,
                    HeaderedContentControl { Header: string header } => header,
                    _ => null,
                },
            };
            if (!string.IsNullOrWhiteSpace(label)) return label;
            if (current.ReadLocalValue(TopicProperty) != DependencyProperty.UnsetValue) return null;
        }
        return null;
    }

    /// <summary>
    /// Gets the headers of the open tabs that hold <paramref name="source"/>, innermost first. When
    /// it is in no tab and <paramref name="everywhere"/> is set, as for the ⓘ beside a title, gets
    /// the open tab of every visible tab control on the screen instead.
    /// </summary>
    internal static IReadOnlyList<string> Tabs(DependencyObject source, bool everywhere)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tabs = new List<string>();
        DependencyObject? root = null;
        for (DependencyObject? current = source; current is not null; current = Parent(current))
        {
            if (current is TabControl control && Header(control) is { } header) tabs.Add(header);
            if (current.ReadLocalValue(TopicProperty) == DependencyProperty.UnsetValue) continue;
            root = current;
            break;
        }
        if (tabs.Count > 0 || !everywhere || root is null) return tabs;
        var pending = new Queue<DependencyObject>([root]);
        while (pending.TryDequeue(out DependencyObject? element))
        {
            if (element is TabControl { IsVisible: true } control && Header(control) is { } header) tabs.Add(header);
            if (element is not Visual) continue;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                pending.Enqueue(VisualTreeHelper.GetChild(element, index));
        }
        return tabs;
    }

    private static string? Header(TabControl control) => control.SelectedItem switch
    {
        TabItem { Header: string header } => header,
        TabItem item when AutomationProperties.GetName(item) is { Length: > 0 } name => name,
        _ => null,
    };

    private static void OnCanHelp(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || string.IsNullOrEmpty(GetTopic(source))) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private static void OnHelp(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || GetTopic(source) is not { Length: > 0 } topic) return;
        e.Handled = true;
        Open(source, topic, FieldLabel(source));
    }

    // A field in a drop-down or tooltip has no visual parent; its logical parent leads back to the screen.
    private static DependencyObject? Parent(DependencyObject element) =>
        (element is Visual ? VisualTreeHelper.GetParent(element) : null) ?? LogicalTreeHelper.GetParent(element);
}
