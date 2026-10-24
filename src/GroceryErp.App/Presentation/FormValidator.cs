using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Shows the problems of a failed submit above the form; each line focuses its field.</summary>
public sealed class FormErrorSummary : Border
{
    private readonly TextBlock _heading = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _lines = new() { Margin = new Thickness(0, 4, 0, 0) };

    public FormErrorSummary()
    {
        Visibility = Visibility.Collapsed;
        Padding = new Thickness(10, 8, 10, 8);
        Margin = new Thickness(0, 8, 0, 4);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "ErrorSurfaceBrush");
        SetResourceReference(BorderBrushProperty, "ErrorStrokeBrush");
        _heading.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        Child = new StackPanel { Children = { _heading, _lines } };
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
    }

    public bool HasMessage => Visibility == Visibility.Visible;

    /// <summary>Lists field errors (each focuses its field) and any errors that belong to no single field.</summary>
    public void Show(IReadOnlyList<(FormField Field, string Message)> errors, IReadOnlyList<string>? otherErrors = null)
    {
        ArgumentNullException.ThrowIfNull(errors);
        otherErrors ??= [];
        _lines.Children.Clear();
        int count = errors.Count + otherErrors.Count;
        if (count == 0)
        {
            Clear();
            return;
        }
        _heading.Text = TranslationSource.Instance.Format(count == 1 ? "Form_FixProblem_One" : "Form_FixProblem_Other", count);
        foreach ((FormField field, string message) in errors)
        {
            var link = new Hyperlink(new Run(string.IsNullOrWhiteSpace(field.Label) ? message : $"{field.Label}: {message}"));
            link.Click += (_, _) => field.FocusInput();
            _lines.Children.Add(new TextBlock(link) { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        }
        foreach (string message in otherErrors)
            _lines.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Visibility = Visibility.Visible;
    }

    /// <summary>Shows a message that belongs to the whole form, such as a service rejection.</summary>
    public void ShowMessage(string message)
    {
        _lines.Children.Clear();
        _heading.Text = message;
        Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Clear()
    {
        _heading.Text = string.Empty;
        _lines.Children.Clear();
        Visibility = Visibility.Collapsed;
    }
}

/// <summary>Collects field errors for one submit, then shows them inline, in the summary and focuses the first.</summary>
internal sealed class FormValidator
{
    private readonly List<(FormField Field, string Message)> _errors = [];

    public bool IsValid => _errors.Count == 0;

    public IReadOnlyList<(FormField Field, string Message)> Errors => _errors;

    public void Add(FormField field, string message)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (_errors.Any(error => ReferenceEquals(error.Field, field))) return;
        _errors.Add((field, message));
    }

    public void AddKey(FormField field, string key, params object[] arguments) =>
        Add(field, arguments.Length == 0 ? TranslationSource.Instance[key] : TranslationSource.Instance.Format(key, arguments));

    public string? Text(FormField field, string? value, bool required = false, int maxLength = int.MaxValue)
    {
        string text = InputNormalizer.Normalize(value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            if (required) AddKey(field, "Form_Required");
            return null;
        }
        if (text.Length > maxLength) AddKey(field, "Form_TooLong", maxLength);
        return text;
    }

    public long? Choice(FormField field, object? selectedValue, bool required = true)
    {
        if (selectedValue is long id) return id;
        if (required) AddKey(field, "Form_Choose");
        return null;
    }

    public decimal? Number(FormField field, string? text, bool required = false, decimal? minimum = null,
        bool positive = false)
    {
        string value = InputNormalizer.Normalize(text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            if (required) AddKey(field, "Form_Required");
            return null;
        }
        if (!decimal.TryParse(value, NumberStyles.Number, TranslationSource.FormatCulture, out decimal number))
        {
            AddKey(field, "Form_InvalidNumber");
            return null;
        }
        if (number.Scale > 3 && number != decimal.Round(number, 3, MidpointRounding.AwayFromZero))
        {
            AddKey(field, "Form_TooManyDecimals");
            return null;
        }
        if (positive && number <= 0)
        {
            AddKey(field, "Form_MustBePositive");
            return null;
        }
        if (minimum is { } min && number < min)
        {
            AddKey(field, "Form_AtLeast", min.ToString("0.###", TranslationSource.FormatCulture));
            return null;
        }
        return number;
    }

    public Money? Money(FormField field, string? text, bool required = false, bool positive = false) =>
        Number(field, text, required, positive ? null : 0m, positive) is { } value ? new Money(value) : null;

    public Quantity? Quantity(FormField field, string? text, bool required = true, bool positive = true) =>
        Number(field, text, required, positive ? null : 0m, positive) is { } value ? new Quantity(value) : null;

    public int? WholeNumber(FormField field, string? text, bool required = false, int minimum = 0)
    {
        string value = InputNormalizer.Normalize(text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            if (required) AddKey(field, "Form_Required");
            return null;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < minimum)
        {
            AddKey(field, "Form_WholeNumberAtLeast", minimum);
            return null;
        }
        return number;
    }

    public DateOnly? Date(FormField field, DateTime? value, bool required = true)
    {
        if (value is { } date) return DateOnly.FromDateTime(date);
        if (required) AddKey(field, "Form_Required");
        return null;
    }

    /// <summary>Clears earlier errors under <paramref name="root"/>, shows this submit's errors and focuses the first.</summary>
    public bool Apply(DependencyObject root, FormErrorSummary? summary, IReadOnlyList<string>? otherErrors = null)
    {
        Clear(root, summary);
        foreach ((FormField field, string message) in _errors) field.Error = message;
        if (IsValid && otherErrors is not { Count: > 0 }) return true;
        summary?.Show(_errors, otherErrors);
        if (_errors.Count > 0) _errors[0].Field.FocusInput();
        return false;
    }

    public static void Clear(DependencyObject root, FormErrorSummary? summary = null)
    {
        foreach (FormField field in Fields(root)) field.Error = null;
        summary?.Clear();
    }

    private static IEnumerable<FormField> Fields(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            if (element is FormField field) yield return field;
            foreach (FormField nested in Fields(element)) yield return nested;
        }
    }
}
