using System.Collections;
using System.Windows;

namespace GroceryErp.App.Presentation;

/// <summary>Asks for the one value a bulk change applies, such as a category or GST rate.</summary>
public partial class ChoiceWindow : Window
{
    public ChoiceWindow(string title, string context, string label, IEnumerable items,
        string displayMemberPath, string selectedValuePath)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        ContextText.Text = context;
        ChoiceField.Label = label;
        ChoiceBox.DisplayMemberPath = displayMemberPath;
        ChoiceBox.SelectedValuePath = selectedValuePath;
        ChoiceBox.ItemsSource = items;
        Loaded += (_, _) => ChoiceBox.Focus();
    }

    public object? SelectedValue { get; private set; }

    private void Continue_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        if (ChoiceBox.SelectedValue is null) form.AddKey(ChoiceField, "Form_Choose");
        if (!form.Apply(this, null)) return;
        SelectedValue = ChoiceBox.SelectedValue;
        DialogResult = true;
    }
}
