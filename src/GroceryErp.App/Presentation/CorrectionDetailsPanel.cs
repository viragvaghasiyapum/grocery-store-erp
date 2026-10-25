using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GroceryErp.Data.Services;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

// The narrow detail panel beside a money-out table: the selected entry's original posting and
// each correction, how an edit would be dated, and the Edit and Void actions. It is the shared
// detail panel, so Close, Esc and the record arrows work as on every page; it has no full width.
public sealed class CorrectionDetailsPanel : Border
{
    private readonly DetailPanel _panel = new() { CanResize = false };
    private readonly TextBlock _treatment = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _edit = new();
    private readonly Button _void = new() { Margin = new Thickness(0) };
    private readonly ItemsControl _history = new();
    private readonly TextBlock _historyHeader = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

    public CorrectionDetailsPanel()
    {
        Width = 320;
        Margin = new Thickness(8, 0, 0, 0);
        Visibility = Visibility.Collapsed;
        // The panel is narrow, so a long entry title wraps instead of being cut short.
        _panel.TitleText.TextWrapping = TextWrapping.Wrap;
        _panel.TitleText.FontSize = 15;
        _edit.SetResourceReference(StyleProperty, "ToolbarButton");
        _void.SetResourceReference(StyleProperty, "ToolbarButton");
        _edit.Click += (_, _) => EditRequested?.Invoke(this, EventArgs.Empty);
        _void.Click += (_, _) => VoidRequested?.Invoke(this, EventArgs.Empty);
        _panel.CloseRequested += (_, _) => Visibility = Visibility.Collapsed;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_edit);
        actions.Children.Add(_void);
        _panel.Actions = actions;
        var body = new DockPanel();
        DockPanel.SetDock(_treatment, Dock.Top);
        DockPanel.SetDock(_historyHeader, Dock.Top);
        body.Children.Add(_treatment);
        body.Children.Add(_historyHeader);
        body.Children.Add(new ScrollViewer { Content = _history, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _panel.Body = body;
        Child = _panel;
        Localize();
        // A weak listener, so the shared translation source never keeps a closed page alive.
        System.ComponentModel.PropertyChangedEventManager.AddHandler(TranslationSource.Instance,
            (_, _) => { if (Visibility == Visibility.Visible && _shown is { } shown) shown(); }, string.Empty);
    }

    // Re-renders the visible entry after a language change.
    private Action? _shown;

    public event EventHandler? EditRequested;
    public event EventHandler? VoidRequested;

    /// <summary>Makes the record arrows move through the table beside the panel.</summary>
    public void Follow(Selector list) => _panel.Follow(list);

    public void Localize()
    {
        _edit.Content = T("MoneyOut_Edit");
        _void.Content = T("MoneyOut_Void");
        _historyHeader.Text = T("MoneyOut_History");
        AutomationProperties.SetName(this, T("MoneyOut_History"));
    }

    // Lines are localized as they are enumerated, so a lazy sequence over the loaded
    // history follows a later language change; numbers inside them use English digits.
    public void Show(string title, CorrectionDating? dating, bool canChange, IEnumerable<string> history,
        bool actions = true)
    {
        _shown = () => Show(title, dating, canChange, history, actions);
        Localize();
        _edit.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        _void.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        _panel.Title = title;
        _treatment.Text = dating is null ? "" : Describe(dating);
        _treatment.Visibility = dating is null ? Visibility.Collapsed : Visibility.Visible;
        _edit.IsEnabled = canChange;
        _void.IsEnabled = canChange;
        _history.ItemsSource = history.Select(static text => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        }).ToList();
        Visibility = Visibility.Visible;
    }

    public void Clear() => Visibility = Visibility.Collapsed;

    public static string Describe(CorrectionDating dating)
    {
        ArgumentNullException.ThrowIfNull(dating);
        string original = dating.OriginalDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        if (dating.Treatment == PeriodTreatment.OpenPeriod)
            return TranslationSource.Instance.Format("MoneyOut_TreatmentOpen", original);
        string locked = string.Join(", ", new[]
        {
            dating.CalendarLocked ? "CY " + dating.Calendar.Label : null,
            dating.FiscalLocked ? "FY " + dating.Fiscal.Label : null,
        }.Where(static value => value is not null));
        return TranslationSource.Instance.Format("MoneyOut_TreatmentCurrent", original, locked,
            dating.EffectiveDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture));
    }

    private static string T(string key) => TranslationSource.Instance[key];
}
