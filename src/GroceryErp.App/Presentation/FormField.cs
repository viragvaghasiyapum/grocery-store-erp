using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace GroceryErp.App.Presentation;

/// <summary>Labels one input with a required marker, an optional hint and its inline error.</summary>
public sealed class FormField : ContentControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(FormField), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsRequiredProperty = DependencyProperty.Register(
        nameof(IsRequired), typeof(bool), typeof(FormField), new PropertyMetadata(false));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(FormField), new PropertyMetadata(null));

    public static readonly DependencyProperty ErrorProperty = DependencyProperty.Register(
        nameof(Error), typeof(string), typeof(FormField),
        new PropertyMetadata(null, static (field, args) =>
            field.SetValue(HasErrorKey, !string.IsNullOrEmpty(args.NewValue as string))));

    private static readonly DependencyPropertyKey HasErrorKey = DependencyProperty.RegisterReadOnly(
        nameof(HasError), typeof(bool), typeof(FormField), new PropertyMetadata(false));

    public static readonly DependencyProperty HasErrorProperty = HasErrorKey.DependencyProperty;

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsRequired
    {
        get => (bool)GetValue(IsRequiredProperty);
        set => SetValue(IsRequiredProperty, value);
    }

    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string? Error
    {
        get => (string?)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    public bool HasError => (bool)GetValue(HasErrorProperty);

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is not DependencyObject input) return;
        // Screen readers announce the visible label, the required state and the current error.
        BindingOperations.SetBinding(input, AutomationProperties.NameProperty,
            new Binding(nameof(Label)) { Source = this });
        BindingOperations.SetBinding(input, AutomationProperties.IsRequiredForFormProperty,
            new Binding(nameof(IsRequired)) { Source = this });
        BindingOperations.SetBinding(input, AutomationProperties.HelpTextProperty,
            new Binding(nameof(Error)) { Source = this });
    }

    /// <summary>Moves keyboard focus to the first focusable input inside the field.</summary>
    public bool FocusInput()
    {
        if (Content is not DependencyObject root) return false;
        if (FindFocusable(root) is not UIElement target) return false;
        BringIntoView();
        return target.Focus() || Keyboard.Focus(target) is not null;
    }

    private static UIElement? FindFocusable(DependencyObject element)
    {
        if (element is Control { Focusable: true, IsEnabled: true, IsVisible: true } candidate) return candidate;
        int count = element is Visual ? VisualTreeHelper.GetChildrenCount(element) : 0;
        for (int index = 0; index < count; index++)
            if (FindFocusable(VisualTreeHelper.GetChild(element, index)) is { } found) return found;
        return null;
    }
}
