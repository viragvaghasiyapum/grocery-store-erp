using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Places a page's detail panel beside its list. Side by side, the panel takes a share of the page
/// and the list stays usable: picking another row shows that record, and the panel's arrows move
/// the list selection. Full width covers the list until the panel goes back or closes; the list is
/// hidden, not narrowed, so its column widths are never squeezed. Each screen remembers the choice
/// on this PC. The panel's column must start at zero width and the panel be the grid's last child.
/// </summary>
internal sealed class DetailSplit
{
    private static readonly string PreferencePath = TableColumnLayouts.PreferenceFile("detail-panels.json");
    private static readonly Dictionary<string, bool> FullWidth = Load();
    private readonly Grid _layout;
    private readonly FrameworkElement _host;
    private readonly DetailPanel _panel;
    private readonly string _screen;
    private readonly bool _fullByDefault;
    private readonly double _share;
    private readonly double _minimum;
    private readonly double _maximum;
    private readonly int _panelColumn;
    private readonly List<UIElement> _hidden = [];

    /// <param name="layout">The page grid whose columns hold the list and, in the host's column, the panel.</param>
    /// <param name="host">The element in the panel's column: the panel itself or a view wrapping it.</param>
    /// <param name="list">The table whose selection the panel follows.</param>
    /// <param name="screen">The name the full-width choice is remembered under.</param>
    /// <param name="fullByDefault">Whether the panel opens at full width until the operator chooses.</param>
    public DetailSplit(Grid layout, FrameworkElement host, DetailPanel panel, DataGrid list, string screen,
        bool fullByDefault = false, double share = 0.62, double minimum = 460, double maximum = 820)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(list);
        _layout = layout;
        _host = host;
        _panel = panel;
        _screen = screen;
        _fullByDefault = fullByDefault;
        _share = share;
        _minimum = minimum;
        _maximum = maximum;
        _panelColumn = Grid.GetColumn(host);
        panel.FullWidthChanged += (_, _) =>
        {
            FullWidth[_screen] = panel.IsFullWidth;
            Save();
            Arrange();
        };
        panel.Follow(list);
        layout.SizeChanged += (_, args) =>
        {
            if (args.WidthChanged && IsOpen && !panel.IsFullWidth) Arrange();
        };
    }

    public bool IsOpen => _host.Visibility == Visibility.Visible;

    /// <summary>Shows the panel at the screen's remembered width; an open panel only updates its arrows.</summary>
    public void Open()
    {
        if (IsOpen)
        {
            _panel.RefreshNavigation();
            return;
        }
        _panel.IsFullWidth = FullWidth.TryGetValue(_screen, out bool full) ? full : _fullByDefault;
        _host.Visibility = Visibility.Visible;
        Arrange();
        _panel.RefreshNavigation();
        if (!SystemParameters.ClientAreaAnimation) return;
        var slide = new TranslateTransform();
        _host.RenderTransform = slide;
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(48, 0, TimeSpan.FromMilliseconds(200))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Hides the panel and gives the list back its width.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        _host.Visibility = Visibility.Collapsed;
        Arrange();
    }

    private void Arrange()
    {
        bool full = IsOpen && _panel.IsFullWidth;
        Grid.SetColumn(_host, full ? 0 : _panelColumn);
        Grid.SetColumnSpan(_host, full ? _layout.ColumnDefinitions.Count : 1);
        _layout.ColumnDefinitions[_panelColumn].Width = IsOpen && !full
            ? new GridLength(Math.Clamp(_layout.ActualWidth * _share, _minimum, _maximum)) : new GridLength(0);
        if (full)
        {
            foreach (UIElement child in _layout.Children.OfType<UIElement>()
                .Where(child => child != _host && child.Visibility == Visibility.Visible))
            {
                child.Visibility = Visibility.Hidden;
                _hidden.Add(child);
            }
            return;
        }
        // Only what the split hid comes back; anything the page changed meanwhile keeps the page's state.
        foreach (UIElement child in _hidden.Where(static child => child.Visibility == Visibility.Hidden))
            child.Visibility = Visibility.Visible;
        _hidden.Clear();
    }

    private static Dictionary<string, bool> Load()
    {
        try
        {
            return File.Exists(PreferencePath)
                ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(PreferencePath)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            string temporaryPath = PreferencePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(FullWidth));
            File.Move(temporaryPath, PreferencePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only profile keeps the choice for this session only.
        }
    }
}
