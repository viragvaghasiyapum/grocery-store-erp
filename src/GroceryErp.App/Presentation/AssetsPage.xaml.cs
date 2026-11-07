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
/// and a detail panel beside the list with each asset's depreciation timeline and terms.
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
    private readonly DetailSplit _timelineSplit;
    private AssetItem? _openAsset;
    private bool _rebinding;
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
        _ = new ActionStrip(TitleRow, TitleActions, MoreActionsMenu, MoreActions, ActionStrip.TitleRow(TitleHeader));
        _fit = new FitPageSize(AssetGrid);
        // Method and status cells and the row count are set in code, so a reload re-renders them.
        LanguageRefresh.Attach(this, () => _ = LoadAsync(PageMove.Same));
        _fit.RowsChanged += async (_, _) => await LoadAsync(PageMove.Same);
        _timelineSplit = new DetailSplit(Layout, Timeline, Timeline, AssetGrid, "Assets");
        TimelinePager.PreviousRequested += (_, _) => MoveTimelinePage(-1);
        TimelinePager.NextRequested += (_, _) => MoveTimelinePage(1);
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
            long? openId = _timelineSplit.IsOpen ? _openAsset?.Id : null;
            _rebinding = true;
            try { timing.Bind(() => AssetGrid.ItemsSource = page.Items); }
            finally { _rebinding = false; }
            // An asset still listed keeps its timeline open, refreshed after an edit or disposal.
            if (openId is { } openAssetId && page.Items.FirstOrDefault(item => item.Id == openAssetId) is { } open) AssetGrid.SelectedItem = open;
            else CloseTimeline();
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

    // While the timeline is open it follows the selected asset.
    private async void AssetGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AssetItem? asset = AssetGrid.SelectedItem as AssetItem;
        EditAssetButton.IsEnabled = TimelineEditButton.IsEnabled = asset is not null;
        TimelineButton.IsEnabled = asset is not null;
        DisposeButton.IsEnabled = TimelineDisposeButton.IsEnabled = asset is { DisposedDate: null };
        if (_rebinding || !_timelineSplit.IsOpen) return;
        if (asset is not null) await ShowTimelineAsync(asset);
        else CloseTimeline();
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
        if (AssetGrid.SelectedItem is AssetItem asset) await ShowTimelineAsync(asset);
    }

    private async Task ShowTimelineAsync(AssetItem asset)
    {
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
            _openAsset = asset;
            _timeline = schedule;
            _timelinePage = 0;
            Timeline.Title = asset.Name;
            Timeline.Facts = string.Join(" · ", new[]
            {
                asset.Category,
                $"{(asset.Method == DepreciationMethod.Wdv ? "WDV" : "SLM")} {asset.Rate.Value.ToString("0.###", TranslationSource.FormatCulture)}%",
                $"{TranslationSource.Instance["Assets_BookValue"]} {asset.BookValue.Value.ToString("N3", TranslationSource.FormatCulture)}",
                asset.DisposedDate is { } disposed
                    ? $"{TranslationSource.Instance["Assets_Disposed"]} {disposed.ToString("dd MMM yyyy", TranslationSource.FormatCulture)}" : null,
            }.Where(static part => !string.IsNullOrEmpty(part)));
            RenderTimelinePage();
            ShowRecordActions(false);
            _timelineSplit.Open();
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }

    private void CloseTimeline()
    {
        _timelineSplit.Close();
        ShowRecordActions(true);
        _openAsset = null;
        _timeline = [];
        _timelinePage = 0;
        ScheduleGrid.ItemsSource = null;
    }

    // The asset's own actions are in the open panel, so the title row keeps only the page's.
    private void ShowRecordActions(bool show)
    {
        Visibility visibility = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button button in new[] { EditAssetButton, TimelineButton, DisposeButton }) button.Visibility = visibility;
    }

    private void Timeline_OnCloseRequested(object? sender, EventArgs e)
    {
        CloseTimeline();
        AssetGrid.Focus();
    }

    private void MoveTimelinePage(int step)
    {
        int page = _timelinePage + step;
        if (page < 0 || page * TimelinePageSize >= _timeline.Count) return;
        _timelinePage = page;
        RenderTimelinePage();
    }

    private void RenderTimelinePage()
    {
        int start = _timelinePage * TimelinePageSize;
        ScheduleGrid.ItemsSource = _timeline.Skip(start).Take(TimelinePageSize).ToArray();
        TimelinePager.Status = _timeline.Count == 0 ? "0" :
            $"{start + 1}–{Math.Min(start + TimelinePageSize, _timeline.Count)} / {_timeline.Count}";
        TimelinePager.Update(_timelinePage + 1, start > 0, start + TimelinePageSize < _timeline.Count);
    }
}
