using System.Windows;
using System.Windows.Controls;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// A table page's status line and keyset previous/next arrows with the page number, placed in the
/// title row. The arrows show only when there is more than one page.
/// </summary>
public partial class PagerBar : UserControl
{
    private int _page = 1;
    private bool _canGoBack;
    private bool _canGoForward;

    public PagerBar()
    {
        InitializeComponent();
        Loaded += (_, _) => TranslationSource.Instance.PropertyChanged += OnTranslationChanged;
        Unloaded += (_, _) => TranslationSource.Instance.PropertyChanged -= OnTranslationChanged;
    }

    public event EventHandler? PreviousRequested;

    public event EventHandler? NextRequested;

    public string Status
    {
        get => StatusLine.Text;
        set => StatusLine.Text = value;
    }

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

    private void RenderPage()
    {
        bool paged = _page > 1 || _canGoForward;
        PageControls.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = paged ? TranslationSource.Instance.Format("Pager_Page", _page) : string.Empty;
    }

    private void OnTranslationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") RenderPage();
    }

    private void Previous_OnClick(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void Next_OnClick(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);
}
