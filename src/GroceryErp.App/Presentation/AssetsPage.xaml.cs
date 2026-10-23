using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GroceryErp.Assets;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class AssetsPage : Page
{
    private int PageSize => int.TryParse((PageSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int size) ? size : 50;
    private readonly AssetService _assets;
    private readonly IClock _clock;
    private readonly PurchasingService _purchasing;
    private AssetItem? _editingAsset;
    private readonly Stack<(bool? Disposed, string? Name, long? Id)> _previousPages = new();
    private (bool? Disposed, string? Name, long? Id) _cursor;
    private AssetPage? _page;
    private bool _loadingPage;
    private bool _changingAssetSection;
    private IReadOnlyList<DepreciationEntry> _timeline = [];
    private bool _settingAsset;
    private int _previewVersion;
    private int _timelinePage;
    private const int TimelinePageSize = 50;

    public AssetsPage() : this(null, null, null) { }

    public AssetsPage(AssetService? assets, IClock? clock, PurchasingService? purchasing, bool loadOnShow = true)
    {
        InitializeComponent();
        IServiceProvider? services = assets is null || clock is null || purchasing is null
            ? ((App)Application.Current).Services : null;
        _assets = assets ?? services!.GetRequiredService<AssetService>();
        _clock = clock ?? services!.GetRequiredService<IClock>();
        _purchasing = purchasing ?? services!.GetRequiredService<PurchasingService>();
        AssetRate.Text = "10";
        DateTime today = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        PurchaseDate.SelectedDate = today;
        InUseDate.SelectedDate = today;
        DisposalDate.SelectedDate = today;
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
        Loaded += (_, _) => TranslationSource.Instance.PropertyChanged += TranslationChanged;
        Unloaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= TranslationChanged;
            CloseTimeline();
        };
    }

    private void TranslationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]" && IsLoaded)
            UpdateDisposalTarget(AssetGrid.SelectedItem as AssetItem);
    }

    private async void AddAsset_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ReadInput() is not { } input) return;
            if (_editingAsset is { } editing)
            {
                AssetChangePreview preview = await _assets.PreviewChangeAsync(editing.Id, input, EffectiveMonth());
                if (preview.Treatment == "BLOCKED")
                {
                    StatusText.Text = DescribePreview(preview);
                    return;
                }
                if (preview.Treatment != "DETAILS" && MessageBox.Show(DescribePreview(preview),
                        TranslationSource.Instance["Assets_ConfirmChange"], MessageBoxButton.OKCancel,
                        MessageBoxImage.Information) != MessageBoxResult.OK) return;
            }
            await _assets.SaveAssetAsync(_editingAsset?.Id, input, EffectiveMonth());
            NewAsset_OnClick(sender, e);
            AssetDetailsTab.IsChecked = false;
            await RefreshAsync();
        }
        catch (FormatException exception) { StatusText.Text = exception.Message; }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private AssetInput? ReadInput()
    {
        if (PurchaseDate.SelectedDate is not { } purchase || InUseDate.SelectedDate is not { } inUse) return null;
        string category = (AssetCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Other";
        string method = (AssetMethod.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "WDV";
        return new(AssetName.Text, category, DateOnly.FromDateTime(purchase), DateOnly.FromDateTime(inUse),
            new Money(Parse(AssetCost.Text)), AssetVendor.SelectedValue is long vendorId ? vendorId : null,
            method == "WDV" ? DepreciationMethod.Wdv : DepreciationMethod.Slm,
            new Percent(Parse(AssetRate.Text)), new Money(Parse(AssetSalvage.Text)), AssetNotes.Text);
    }

    private YearMonth? EffectiveMonth() =>
        AssetEffectivePanel.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(AssetEffectiveFrom.Text)
            ? YearMonth.Parse(AssetEffectiveFrom.Text.Trim()) : null;

    // Shows, before saving, which months a financial change recalculates and its effect.
    private async void AssetTerms_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _editingAsset is not { } asset || _settingAsset) return;
        int version = ++_previewVersion;
        try
        {
            await Task.Delay(250);
            if (version != _previewVersion || ReadInput() is not { } input) return;
            AssetChangePreview preview = await _assets.PreviewChangeAsync(asset.Id, input, EffectiveMonth());
            if (version != _previewVersion) return;
            bool terms = preview.Treatment == "TERMS";
            if (terms && AssetEffectivePanel.Visibility != Visibility.Visible)
            {
                AssetEffectivePanel.Visibility = Visibility.Visible;
                _settingAsset = true;
                AssetEffectiveFrom.Text = preview.EffectiveMonth?.ToString() ?? "";
                _settingAsset = false;
            }
            else if (!terms && AssetEffectivePanel.Visibility == Visibility.Visible && sender != AssetEffectiveFrom)
                AssetEffectivePanel.Visibility = Visibility.Collapsed;
            AssetFinancialLockText.Text = preview.Treatment == "DETAILS" ? "" : DescribePreview(preview);
            AssetFinancialLockText.Visibility = preview.Treatment == "DETAILS" ? Visibility.Collapsed : Visibility.Visible;
            SaveAssetButton.IsEnabled = preview.Treatment != "BLOCKED";
        }
        catch (FormatException)
        {
            if (version != _previewVersion) return;
            AssetFinancialLockText.Text = TranslationSource.Instance["Assets_PreviewNeedsValues"];
            AssetFinancialLockText.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            if (version != _previewVersion) return;
            AssetFinancialLockText.Text = UserError.Message(exception);
            AssetFinancialLockText.Visibility = Visibility.Visible;
        }
    }

    private static string DescribePreview(AssetChangePreview preview)
    {
        string Money(Money value) => value.Value.ToString("N3", TranslationSource.FormatCulture);
        return preview.Treatment switch
        {
            "BLOCKED" => TranslationSource.Instance.Format("Assets_PreviewBlocked", preview.FirstOpenMonth.ToString()),
            "RECALCULATION" => TranslationSource.Instance.Format("Assets_PreviewRecalculation",
                preview.EffectiveMonth?.ToString() ?? "", preview.MonthsRecalculated, Money(preview.DepreciationBefore),
                Money(preview.DepreciationAfter), Money(preview.BookValueBefore), Money(preview.BookValueAfter)),
            _ => TranslationSource.Instance.Format("Assets_PreviewTerms", preview.EffectiveMonth?.ToString() ?? "",
                preview.FirstOpenMonth.ToString(), preview.MonthsRecalculated, Money(preview.DepreciationBefore),
                Money(preview.DepreciationAfter), Money(preview.BookValueBefore), Money(preview.BookValueAfter)),
        };
    }

    private void AssetCategory_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || !AssetRate.IsEnabled || AssetCategory.SelectedItem is not ComboBoxItem item) return;
        AssetRate.Text = item.Tag?.ToString() switch
        {
            "Furniture" => "10",
            "Electronics" => "40",
            "Vehicle" => "15",
            _ => "15",
        };
    }

    private async void Dispose_OnClick(object sender, RoutedEventArgs e)
    {
        if (AssetGrid.SelectedItem is not AssetItem { DisposedDate: null } asset ||
            DisposalDate.SelectedDate is not { } date) return;
        if (MessageBox.Show(TranslationSource.Instance.Format("Assets_DisposeConfirm", asset.Name),
            TranslationSource.Instance["Assets_Dispose"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _assets.DisposeAsync(asset.Id, DateOnly.FromDateTime(date), new Money(Parse(DisposalValue.Text)), OperationGuard.NewId());
            AssetDisposalTab.IsChecked = false;
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DepreciationPostResult result = await _assets.PostDueDepreciationAsync();
            await RefreshAsync();
            StatusText.Text = TranslationSource.Instance.Format("Assets_PostResult", result.PostedCount, result.CurrentDatedPeriods.Count);
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void AssetGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CloseTimeline();
        AssetItem? asset = AssetGrid.SelectedItem as AssetItem;
        UpdateDisposalTarget(asset);
        if (asset is null)
        {
            SaveAssetButton.IsEnabled = _editingAsset is null;
            EditAssetButton.IsEnabled = false;
            return;
        }
        SaveAssetButton.IsEnabled = false;
        EditAssetButton.IsEnabled = false;
        _settingAsset = true;
        try
        {
            _editingAsset = asset;
            AssetName.Text = asset.Name;
            AssetCategory.SelectedItem = AssetCategory.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == asset.Category);
            PurchaseDate.SelectedDate = asset.PurchaseDate.ToDateTime(TimeOnly.MinValue);
            InUseDate.SelectedDate = asset.InUseFrom.ToDateTime(TimeOnly.MinValue);
            AssetCost.Text = asset.Cost.Value.ToString("0.000", TranslationSource.FormatCulture);
            AssetMethod.SelectedIndex = asset.Method == DepreciationMethod.Wdv ? 0 : 1;
            AssetRate.Text = asset.Rate.Value.ToString("0.000", TranslationSource.FormatCulture);
            AssetSalvage.Text = asset.SalvageValue.Value.ToString("0.000", TranslationSource.FormatCulture);
            AssetVendor.SelectedValue = asset.VendorId;
            AssetNotes.Text = asset.Notes ?? "";
            AssetEffectiveFrom.Text = "";
            AssetEffectivePanel.Visibility = Visibility.Collapsed;
            // A disposed asset keeps its final terms; others show a preview before saving.
            SetFinancialFieldsEnabled(asset.DisposedDate is null);
            SaveAssetButton.IsEnabled = true;
            EditAssetButton.IsEnabled = true;
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally { _settingAsset = false; }
    }

    private void UpdateDisposalTarget(AssetItem? asset)
    {
        if (DisposeButton is null) return;
        bool canDispose = asset is { DisposedDate: null };
        SelectedAssetText.Text = asset?.Name ?? TranslationSource.Instance["Assets_SelectToDispose"];
        DisposeButton.IsEnabled = canDispose;
        DisposalDate.IsEnabled = canDispose;
        DisposalValue.IsEnabled = canDispose;
    }

    private void NewAsset_OnClick(object sender, RoutedEventArgs e)
    {
        AssetGrid.SelectedItem = null;
        _editingAsset = null;
        AssetName.Clear(); AssetCost.Clear(); AssetNotes.Clear();
        AssetCategory.SelectedIndex = 0; AssetMethod.SelectedIndex = 0;
        AssetRate.Text = "10"; AssetSalvage.Text = "0";
        AssetVendor.SelectedIndex = -1;
        PurchaseDate.SelectedDate = _clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        InUseDate.SelectedDate = PurchaseDate.SelectedDate;
        SaveAssetButton.IsEnabled = true;
        EditAssetButton.IsEnabled = false;
        SetFinancialFieldsEnabled(true);
        UpdateDisposalTarget(null);
        CloseTimeline();
        AssetDetailsTab.IsChecked = true;
        AssetDisposalTab.IsChecked = false;
        AssetName.Focus();
    }

    private void EditAsset_OnClick(object sender, RoutedEventArgs e)
    {
        if (AssetGrid.SelectedItem is not AssetItem || !EditAssetButton.IsEnabled) return;
        AssetDetailsTab.IsChecked = true;
        AssetDisposalTab.IsChecked = false;
        AssetName.Focus();
    }

    private void AssetSection_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_changingAssetSection || AssetDetailsPanel is null || AssetDisposalPanel is null) return;
        _changingAssetSection = true;
        try
        {
            if (ReferenceEquals(sender, AssetDetailsTab) && AssetDetailsTab.IsChecked == true)
                AssetDisposalTab.IsChecked = false;
            else if (ReferenceEquals(sender, AssetDisposalTab) && AssetDisposalTab.IsChecked == true)
                AssetDetailsTab.IsChecked = false;
            SectionMotion.SetOpen(AssetDetailsPanel, AssetDetailsTab.IsChecked == true);
            SectionMotion.SetOpen(AssetDisposalPanel, AssetDisposalTab.IsChecked == true);
        }
        finally { _changingAssetSection = false; }
    }

    private void SetFinancialFieldsEnabled(bool enabled)
    {
        PurchaseDate.IsEnabled = enabled;
        InUseDate.IsEnabled = enabled;
        AssetCost.IsEnabled = enabled;
        AssetMethod.IsEnabled = enabled;
        AssetRate.IsEnabled = enabled;
        AssetSalvage.IsEnabled = enabled;
        AssetFinancialLockText.Text = enabled ? "" : TranslationSource.Instance["Assets_FinancialLocked"];
        AssetFinancialLockText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private Task RefreshAsync()
    {
        _previousPages.Clear();
        return LoadPageAsync(null, null, null);
    }

    private async Task LoadPageAsync(bool? disposed, string? name, long? id)
    {
        using var timing = UiRefreshProbe.Track("assets", this);
        if (_loadingPage) return;
        _loadingPage = true;
        try
        {
            long? selectedId = _editingAsset?.Id;
            AssetPage page = await _assets.GetAssetPageAsync(AssetSearchBox.Text,
                IncludeDisposedBox.IsChecked == true, disposed, name, id, PageSize);
            _page = page;
            _cursor = (disposed, name, id);
            AssetVendor.ItemsSource = await _purchasing.GetVendorsAsync(includeInactive: true);
            timing.Bind(() => AssetGrid.ItemsSource = page.Items);
            CloseTimeline();
            AssetGrid.SelectedItem = page.Items.FirstOrDefault(asset => asset.Id == selectedId);
            StatusText.Text = TranslationSource.Instance["MoneyOut_Ready"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _loadingPage = false;
            PreviousAssetButton.IsEnabled = _previousPages.Count > 0;
            NextAssetButton.IsEnabled = _page?.HasMore == true;
        }
    }

    private async void AssetSearch_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ClearAssetFilters_OnClick(object sender, RoutedEventArgs e)
    {
        AssetSearchBox.Clear();
        IncludeDisposedBox.IsChecked = true;
        await RefreshAsync();
    }

    private async void NextAsset_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingPage || _page is not { HasMore: true, NextDisposed: not null, NextName: not null, NextId: not null } page) return;
        (bool? Disposed, string? Name, long? Id) previous = _cursor;
        await LoadPageAsync(page.NextDisposed, page.NextName, page.NextId);
        if (_cursor == (page.NextDisposed, page.NextName, page.NextId)) _previousPages.Push(previous);
        PreviousAssetButton.IsEnabled = _previousPages.Count > 0;
    }

    private async void PreviousAsset_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingPage || _previousPages.Count == 0) return;
        (bool? Disposed, string? Name, long? Id) previous = _previousPages.Peek();
        await LoadPageAsync(previous.Disposed, previous.Name, previous.Id);
        if (_cursor == previous) _previousPages.Pop();
        PreviousAssetButton.IsEnabled = _previousPages.Count > 0;
    }

    private static decimal Parse(string? text)
    {
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("en-IN"), out decimal value))
            throw new FormatException(TranslationSource.Instance["MoneyOut_ValidNumber"]);
        return value;
    }

    private async void PageSize_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _previousPages.Clear();
        await LoadPageAsync(null, null, null);
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
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
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
