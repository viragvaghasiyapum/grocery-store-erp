using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GroceryErp.Data.Services;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

// A narrow panel beside a money-out table: the selected entry's original posting and
// each correction, how an edit would be dated, and the Edit and Void actions.
public sealed class CorrectionDetailsPanel : Border
{
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _treatment = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    private readonly Button _edit = new() { MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _void = new() { MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _close = new() { MinWidth = 70 };
    private readonly ItemsControl _history = new();
    private readonly WrapPanel _actions = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _historyHeader = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

    public CorrectionDetailsPanel()
    {
        Width = 300;
        Margin = new Thickness(8, 0, 0, 0);
        Padding = new Thickness(12);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        Visibility = Visibility.Collapsed;
        _edit.Click += (_, _) => EditRequested?.Invoke(this, EventArgs.Empty);
        _void.Click += (_, _) => VoidRequested?.Invoke(this, EventArgs.Empty);
        _close.Click += (_, _) => Visibility = Visibility.Collapsed;
        _actions.Children.Add(_edit);
        _actions.Children.Add(_void);
        _actions.Children.Add(_close);
        var body = new DockPanel();
        DockPanel.SetDock(_title, Dock.Top);
        DockPanel.SetDock(_treatment, Dock.Top);
        DockPanel.SetDock(_actions, Dock.Top);
        DockPanel.SetDock(_historyHeader, Dock.Top);
        body.Children.Add(_title);
        body.Children.Add(_treatment);
        body.Children.Add(_actions);
        body.Children.Add(_historyHeader);
        body.Children.Add(new ScrollViewer { Content = _history, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Child = body;
        Localize();
        // A weak listener, so the shared translation source never keeps a closed page alive.
        System.ComponentModel.PropertyChangedEventManager.AddHandler(TranslationSource.Instance,
            (_, _) => { if (Visibility == Visibility.Visible && _shown is { } shown) shown(); }, string.Empty);
    }

    // Re-renders the visible entry after a language change.
    private Action? _shown;

    public event EventHandler? EditRequested;
    public event EventHandler? VoidRequested;

    public void Localize()
    {
        _edit.Content = T("MoneyOut_Edit");
        _void.Content = T("MoneyOut_Void");
        _close.Content = T("Common_Close");
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
        _title.Text = title;
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
