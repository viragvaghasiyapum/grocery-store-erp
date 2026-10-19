using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class DirectCountReviewWindow : Window
{
    private readonly DirectStockCountService _service;
    private readonly long[] _productIds;
    private readonly IClock _clock;
    private long? _afterLotId;
    private long? _maxLotId;
    private long? _nextLotId;
    private bool _hasMore;
    private bool _posted;
    private bool _changed;
    private bool _busy;
    private int _pageNumber;
    private IReadOnlyList<DirectCountReview>? _review;

    public bool HasPosted => _changed;

    public DirectCountReviewWindow(DirectStockCountService service,
        IReadOnlyCollection<long> productIds, IClock clock, bool loadOnShow = true)
    {
        InitializeComponent();
        _service = service;
        _productIds = productIds.Distinct().ToArray();
        _clock = clock;
        if (loadOnShow) Loaded += async (_, _) => await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        try
        {
            SetBusy(true);
            DirectCountLotPage page = await _service.GetPageAsync(_productIds, _afterLotId, _maxLotId);
            _maxLotId = page.MaxLotId;
            _nextLotId = page.NextLotId;
            _hasMore = page.HasMore;
            _posted = false;
            _review = null;
            CountGrid.IsReadOnly = false;
            ReasonBox.IsEnabled = true;
            CategoryBox.IsEnabled = true;
            CountGrid.ItemsSource = page.Items.Select(static lot => new CountRow(lot)).ToArray();
            PageText.Text = TranslationSource.Instance.Format("Stock_CountPage", ++_pageNumber,
                page.Items.Count);
            StatusText.Text = page.Items.Count == 0 ? TranslationSource.Instance["Stock_CountNoLots"] : "";
            UpdateButtons();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private void Review_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CountGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            CountGrid.CommitEdit(DataGridEditingUnit.Row, true);
            string reason = ReasonBox.Text.Trim();
            if (reason.Length == 0) throw new InvalidOperationException(TranslationSource.Instance["Stock_ReasonRequired"]);
            if (CountGrid.ItemsSource is not CountRow[] rows || rows.Length == 0) return;
            string category = (CategoryBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "OTHER";
            var reviews = new List<DirectCountReview>(rows.Length);
            long differenceCount = 0;
            decimal netValue = 0;
            foreach (CountRow row in rows)
            {
                if (!decimal.TryParse(InputNormalizer.Normalize(row.Counted ?? ""), NumberStyles.Number,
                    TranslationSource.FormatCulture, out decimal counted) || counted < 0)
                    throw new InvalidOperationException(TranslationSource.Instance["Stock_InvalidCount"]);
                var quantity = new Quantity(counted);
                if (row.Lot.AllowFraction == 0 && !quantity.IsWhole)
                    throw new InvalidOperationException(TranslationSource.Instance["Stock_InvalidCount"]);
                long delta = checked(quantity.Scaled - row.Lot.OnHand);
                row.Difference = delta / 1000m;
                row.DifferenceValue = delta * row.Lot.UnitCost / 1000000m;
                if (delta != 0) differenceCount++;
                netValue += row.DifferenceValue.Value;
                reviews.Add(new(row.Lot.LotId, row.Lot.Version, row.Lot.OnHand, quantity.Scaled,
                    _clock.BusinessDate, reason, GroceryErp.Data.Services.OperationGuard.NewId(), category));
            }
            _review = reviews;
            CountGrid.Items.Refresh();
            CountGrid.IsReadOnly = true;
            ReasonBox.IsEnabled = false;
            CategoryBox.IsEnabled = false;
            StatusText.Text = TranslationSource.Instance.Format("Stock_ReviewSummary", differenceCount,
                netValue.ToString("0.000", TranslationSource.FormatCulture));
            UpdateButtons();
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void Edit_OnClick(object sender, RoutedEventArgs e)
    {
        _review = null;
        CountGrid.IsReadOnly = false;
        ReasonBox.IsEnabled = true;
        CategoryBox.IsEnabled = true;
        StatusText.Text = "";
        UpdateButtons();
    }

    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        if (_review is null || _busy) return;
        try
        {
            SetBusy(true);
            await _service.PostReviewedAsync(_review);
            _posted = true;
            _changed = true;
            StatusText.Text = TranslationSource.Instance["Stock_CountPagePosted"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_posted || !_hasMore || _nextLotId is null) return;
        _afterLotId = _nextLotId;
        await LoadPageAsync();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => DialogResult = _changed;

    private void CountGrid_OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditingElement is TextBox box) box.Text = InputNormalizer.Normalize(box.Text);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (ReviewButton is null) return;
        ReviewButton.IsEnabled = !_busy && !_posted && _review is null && CountGrid.Items.Count > 0;
        EditButton.IsEnabled = !_busy && !_posted && _review is not null;
        PostButton.IsEnabled = !_busy && !_posted && _review is not null;
        NextButton.IsEnabled = !_busy && _posted && _hasMore;
    }

    private sealed class CountRow(DirectCountLot lot)
    {
        public DirectCountLot Lot { get; } = lot;
        public string ProductName => Lot.ProductName;
        public string LotCode => Lot.LotCode;
        public decimal Expected => Lot.OnHand / 1000m;
        public string? Counted { get; set; }
        public decimal? Difference { get; set; }
        public decimal? DifferenceValue { get; set; }
    }
}
