using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// States a page's applied filters in words for a selection, a bulk review or an export. A filter
/// counts when it differs from its default: search text, a choice other than the first, a ticked
/// box, a date, or a typed value. Dates and typed values use the control's accessible name.
/// </summary>
internal static class FilterText
{
    public static string Describe(params FrameworkElement[] filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        string[] parts = filters.Select(Part).OfType<string>().ToArray();
        return parts.Length == 0 ? TranslationSource.Instance["Common_NoFilters"] : string.Join(" · ", parts);
    }

    /// <summary>Counts the filters that differ from their defaults.</summary>
    public static int Count(IEnumerable<FrameworkElement> filters) => filters.Count(static filter => Part(filter) is not null);

    private static string? Part(FrameworkElement filter)
    {
        string label = AutomationProperties.GetName(filter);
        return filter switch
        {
            Wpf.Ui.Controls.TextBox { Text: { } search } when !string.IsNullOrWhiteSpace(search) => $"“{search.Trim()}”",
            Wpf.Ui.Controls.TextBox => null,
            TextBox { Text: { } value } when !string.IsNullOrWhiteSpace(value) => $"{label} {value.Trim()}",
            ComboBox { SelectedIndex: > 0 } box when !string.IsNullOrEmpty(box.Text) => box.Text,
            CheckBox { IsChecked: true, Content: string text } => text,
            DatePicker { SelectedDate: { } date } => $"{label} {date.ToString("dd-MM-yyyy", TranslationSource.FormatCulture)}",
            _ => null,
        };
    }
}
