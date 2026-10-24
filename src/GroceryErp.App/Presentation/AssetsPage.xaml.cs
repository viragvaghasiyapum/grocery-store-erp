using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GroceryErp.Assets;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Assets: search and keyset pages sized to the window, the asset and disposal forms as dialogs,
/// and a side panel with each asset's depreciation timeline and terms.
/// </summary>
public partial class AssetsPage : Page
{
    private const string StateKey = nameof(AssetsPage);
    private readonly AssetService _assets;
    private readonly IClock _clock;
    private readonly PurchasingService _purchasing;
    private readonly KeysetPager<AssetCursor> _pager = new();
    private readonly LatestRequest _requests = new();
    private readonly FitPageSize _fit;
    private readonly DispatcherTimer _searchDelay;
    private IReadOnlyList<DepreciationEntry> _timeline = [];
    private int _timelinePage;
    private const int TimelinePageSize = 50;
    private bool _restoring;

    private sealed record AssetCursor(bool Disposed, string Name, long Id);

    private sealed record State(string Search, bool IncludeDisposed);

    public AssetsPage() : this(null, null, null) { }

    public AssetsPage(AssetService? assets, IClock? clock, PurchasingService? purchasing, bool loadOnShow = true)
    {
        InitializeComponent();
        IServiceProvider? services = assets is null || clock is null || purchasing is null
            ? ((App)Application.Current).Services : null;
        _assets = assets ?? services!.GetRequiredService<AssetService>();
        _clock = clock ?? services!.GetRequiredService<IClock>();
        _purchasing = purchasing ?? services!.GetRequiredService<PurchasingService>();
        _fit = new FitPageSize(AssetGrid);
        // Method and status cells and the row count are set in code, so a reload re-renders them.
        LanguageRefresh.Attach(this, () => _ = LoadAsync(PageMove.Same));
        _fit.RowsChanged += async (_, _) => await LoadAsync(PageMove.Same);
        _searchDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await LoadAsync(PageMove.First);
        };
        Pager.PreviousRequested += async (_, _) => await LoadAsync(PageMove.Previous);
        Pager.NextRequested += async (_, _) => await LoadAsync(PageMove.Next);
        if (PageStateStore.Current?.Get<State>(StateKey) is { } state)
        {
            _restoring = true;
            AssetSearchBox.Text = state.Search;
            IncludeDisposedBox.IsChecked = state.IncludeDisposed;
            _restoring = false;
        }
        PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (args.Key == Key.F) { AssetSearchBox.Focus(); args.Handled = true; }
            else if (args.Key == Key.N) { NewAsset_OnClick(this, args); args.Handled = true; }
        };
        if (loadOnShow) Loaded += async (_, _) => await LoadAsync(PageMove.First);
        Unloaded += (_, _) =>
        {
            _searchDelay.Stop();
            _requests.Cancel();
            CloseTimeline();
            PageStateStore.Current?.Set(StateKey, new State(AssetSearchBox.Text ?? "", IncludeDisposedBox.IsChecked == true));
        };
    }

    private async Task LoadAsync(PageMove move)
    {
        if (!_pager.CanMove(move)) return;
        using var timing = UiRefreshProbe.Track("assets", this);
        using LatestRequest.Scope request = _requests.Begin();
        Pager.SetLoading(true);
        try
        {
            AssetCursor? cursor = _pager.CursorFor(move);
            AssetPage page = await _assets.GetAssetPageAsync(InputNormalizer.Normalize(AssetSearchBox.Text ?? string.Empty),
                IncludeDisposedBox.IsChecked == true, cursor?.Disposed, cursor?.Name, cursor?.Id, _fit.Take(), request.Token);
            if (!request.IsCurrent) return;
            _pager.Commit(move, page.HasMore, page is { NextDisposed: { } disposed, NextName: { } name, NextId: { } id }
                ? new AssetCursor(disposed, name, id) : null);
            CloseTimeline();
            timing.Bind(() => AssetGrid.ItemsSource = page.Items);
            EmptyText.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Pager.Status = TranslationSource.Instance.Format("MoneyOut_RowCount", page.Items.Count);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (request.IsCurrent) Pager.Status = UserError.Message(exception);
        }
        finally
        {
            if (request.IsCurrent) Pager.Update(_pager.PageNumber, _pager.CanGoBack, _pager.HasMore);
        }
    }

    private void Search_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _restoring) return;
        _requests.Cancel();
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private async void IncludeDisposed_OnClick(object sender, RoutedEventArgs e) => await LoadAsync(PageMove.First);

    private async void ClearAssetFilters_OnClick(object sender, RoutedEventArgs e)
    {
        _restoring = true;
        AssetSearchBox.Text = string.Empty;
        IncludeDisposedBox.IsChecked = true;
        _restoring = false;
        _searchDelay.Stop();
        await LoadAsync(PageMove.First);
    }

    private void AssetGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AssetItem? asset = AssetGrid.SelectedItem as AssetItem;
        EditAssetButton.IsEnabled = asset is not null;
        DisposeButton.IsEnabled = asset is { DisposedDate: null };
    }

    private void AssetGrid_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AssetGrid.SelectedItem is AssetItem) EditAsset_OnClick(sender, e);
    }

    private async void NewAsset_OnClick(object sender, RoutedEventArgs e) => await OpenFormAsync(null);

    private async void EditAsset_OnClick(object sender, RoutedEventArgs e)
    {
        if (AssetGrid.SelectedItem is AssetItem asset) await OpenFormAsync(asset);
    }

    private async Task OpenFormAsync(AssetItem? asset)
    {
        try
        {
            IReadOnlyList<VendorItem> vendors = await _purchasing.GetVendorsAsync(includeInactive: true);
            if (new AssetFormWindow(_assets, _clock, vendors, asset) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
            await LoadAsync(asset is null ? PageMove.First : PageMove.Same);
            Pager.Status = TranslationSource.Instance["MoneyOut_Saved"];
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async void Dispose_OnClick(object sender, RoutedEventArgs e)
    {
        if (AssetGrid.SelectedItem is not AssetItem { DisposedDate: null } asset) return;
        if (new AssetDisposalWindow(_assets, _clock, asset) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        await LoadAsync(PageMove.Same);
    }

    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DepreciationPostResult result = await _assets.PostDueDepreciationAsync();
            await LoadAsync(PageMove.Same);
            Pager.Status = TranslationSource.Instance.Format("Assets_PostResult", result.PostedCount, result.CurrentDatedPeriods.Count);
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private async void ViewTimeline_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AssetItem asset }) return;
        AssetGrid.SelectedItem = asset;
        try
        {
            IReadOnlyList<DepreciationEntry> schedule = await _assets.GetScheduleAsync(asset.Id);
            IReadOnlyList<AssetTermItem> terms = await _assets.GetTermsAsync(asset.Id);
            if (AssetGrid.SelectedItem is not AssetItem selected || selected.Id != asset.Id) return;
            TermsList.ItemsSource = terms.Select(static term => new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
                Text = TranslationSource.Instance.Format("Assets_TermLine", term.EffectiveMonth.ToString(),
                    term.Method == DepreciationMethod.Wdv ? "WDV" : "SLM",
                    term.Rate.Value.ToString("0.###", TranslationSource.FormatCulture),
                    term.SalvageValue.Value.ToString("N3", TranslationSource.FormatCulture),
                    term.Cost.Value.ToString("N3", TranslationSource.FormatCulture),
                    TranslationSource.Instance[$"MoneyOut_TermReason_{term.Reason}"],
                    term.CreatedAt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
            }).ToList();
            _timeline = schedule;
            _timelinePage = 0;
            TimelineHeading.Text = $"{asset.Name} · {TranslationSource.Instance["Assets_ViewTimeline"]}";
            RenderTimelinePage();
            SideOverlayMotion.Show(TimelineOverlay, TimelinePanel, TimelineBackdrop);
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private void CloseTimeline()
    {
        TimelineOverlay.Visibility = Visibility.Collapsed;
        _timeline = [];
        _timelinePage = 0;
        ScheduleGrid.ItemsSource = null;
    }

    private void CloseTimeline_OnClick(object sender, RoutedEventArgs e) => CloseTimeline();

    private void TimelineBackdrop_OnMouseDown(object sender, MouseButtonEventArgs e) => CloseTimeline();

    private void PreviousTimeline_OnClick(object sender, RoutedEventArgs e)
    {
        if (_timelinePage == 0) return;
        _timelinePage--;
        RenderTimelinePage();
    }

    private void NextTimeline_OnClick(object sender, RoutedEventArgs e)
    {
        if ((_timelinePage + 1) * TimelinePageSize >= _timeline.Count) return;
        _timelinePage++;
        RenderTimelinePage();
    }

    private void RenderTimelinePage()
    {
        int start = _timelinePage * TimelinePageSize;
        ScheduleGrid.ItemsSource = _timeline.Skip(start).Take(TimelinePageSize).ToArray();
        TimelinePageText.Text = _timeline.Count == 0 ? "0" :
            $"{start + 1}–{Math.Min(start + TimelinePageSize, _timeline.Count)} / {_timeline.Count}";
        PreviousTimelineButton.IsEnabled = start > 0;
        NextTimelineButton.IsEnabled = start + TimelinePageSize < _timeline.Count;
    }
}
