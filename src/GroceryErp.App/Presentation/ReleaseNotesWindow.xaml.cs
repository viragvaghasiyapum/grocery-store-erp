using System.Windows;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Shows a version's release notes once after sign-in. The window is never taller than the
/// screen's work area: the notes scroll and Close stays in view at any display scaling.
/// </summary>
public partial class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow(string notes) : this(notes, SystemParameters.WorkArea.Height) { }

    internal ReleaseNotesWindow(string notes, double workAreaHeight)
    {
        ArgumentNullException.ThrowIfNull(notes);
        InitializeComponent();
        string[] parts = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', 2);
        VersionText.Text = parts[0].Trim();
        NotesText.Text = parts.Length > 1 ? parts[1].Trim() : "";
        MaxHeight = Math.Max(240, workAreaHeight - 24);
        Loaded += (_, _) => CloseButton.Focus();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
