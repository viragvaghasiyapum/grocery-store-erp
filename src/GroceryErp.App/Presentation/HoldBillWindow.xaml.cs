using System.Windows;

namespace GroceryErp.App.Presentation;

public partial class HoldBillWindow : Window
{
    public HoldBillWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LabelBox.Focus();
    }

    public string? Label => string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim();

    private void Save_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
