using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using GroceryErp.Localization;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace GroceryErp.App.Presentation;

/// <summary>
/// A table page's footer, always in view under the table: the page's selection bar while rows are
/// checked, the status line, and keyset previous/next arrows with the page number. The arrows keep
/// the right edge and show only when there is more than one page; the status line trims to the
/// width left between. The fixed height fits the selection bar, so checking a row never changes
/// how many rows fit the table. Built in code so a selection bar placed in it keeps the page's names.
/// </summary>
public sealed class PagerBar : Border
{
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(SelectionBar), typeof(PagerBar),
        new PropertyMetadata(null, static (bar, args) => ((PagerBar)bar)._selection.Content = args.NewValue));

    private readonly ContentPresenter _selection = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 0, 12, 0) };
    private readonly StackPanel _pageControls = new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private int _page = 1;
    private bool _canGoBack;
    private bool _canGoForward;

    public PagerBar()
    {
        Focusable = false;
        Height = 40;
        Margin = new Thickness(0, 4, 0, 0);
        PreviousButton = Arrow("PreviousButton", SymbolRegular.ChevronLeft20, "Pager_PreviousHint");
        NextButton = Arrow("NextButton", SymbolRegular.ChevronRight20, "Pager_NextHint");
        PageText = new TextBlock { Margin = new Thickness(8, 0, 8, 0) };
        PageText.SetResourceReference(StyleProperty, "StatusText");
        PreviousButton.Click += (_, _) => PreviousRequested?.Invoke(this, EventArgs.Empty);
        NextButton.Click += (_, _) => NextRequested?.Invoke(this, EventArgs.Empty);
        _pageControls.Children.Add(PreviousButton);
        _pageControls.Children.Add(PageText);
        _pageControls.Children.Add(NextButton);

        _status.SetResourceReference(StyleProperty, "StatusText");
        _status.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = _status });
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_pageControls, Dock.Right);
        DockPanel.SetDock(_selection, Dock.Left);
        layout.Children.Add(_pageControls);
        layout.Children.Add(_selection);
        layout.Children.Add(_status);
        Child = layout;
        Loaded += (_, _) => TranslationSource.Instance.PropertyChanged += OnTranslationChanged;
        Unloaded += (_, _) => TranslationSource.Instance.PropertyChanged -= OnTranslationChanged;
    }

    public event EventHandler? PreviousRequested;

    public event EventHandler? NextRequested;

    public string Status
    {
        get => _status.Text;
        set => _status.Text = value;
    }

    /// <summary>Gets or sets the selection bar shown at the left of the footer while rows are checked.</summary>
    public SelectionBar? Selection
    {
        get => (SelectionBar?)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    internal Button PreviousButton { get; }

    internal Button NextButton { get; }

    internal TextBlock PageText { get; }

    /// <summary>Shows the page position; buttons stay disabled while a page is loading.</summary>
    public void Update(int page, bool canGoBack, bool canGoForward, bool loading = false)
    {
        _page = page;
        _canGoBack = canGoBack;
        _canGoForward = canGoForward;
        PreviousButton.IsEnabled = canGoBack && !loading;
        NextButton.IsEnabled = canGoForward && !loading;
        RenderPage();
    }

    public void SetLoading(bool loading)
    {
        PreviousButton.IsEnabled = _canGoBack && !loading;
        NextButton.IsEnabled = _canGoForward && !loading;
    }

    private static Button Arrow(string name, SymbolRegular symbol, string hintKey)
    {
        var button = new Button { Name = name, IsEnabled = false, Content = new SymbolIcon { Symbol = symbol } };
        button.SetResourceReference(StyleProperty, "PagerButton");
        button.SetBinding(ToolTipProperty, Text(hintKey));
        button.SetBinding(AutomationProperties.NameProperty, Text(hintKey));
        return button;
    }

    private void RenderPage()
    {
        bool paged = _page > 1 || _canGoForward;
        _pageControls.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = paged ? TranslationSource.Instance.Format("Pager_Page", _page) : string.Empty;
    }

    private void OnTranslationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") RenderPage();
    }

    private static Binding Text(string key) => new($"[{key}]") { Source = TranslationSource.Instance, Mode = BindingMode.OneWay };
}
