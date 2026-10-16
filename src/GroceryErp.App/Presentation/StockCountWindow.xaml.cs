using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class StockCountWindow : Window
{
    private int PageSize => int.TryParse((PageSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int size) ? size : 50;
    private readonly InventoryService _inventory;
    private ScanResolver? _scanner;
    private readonly long? _existingCountId;
    private readonly IReadOnlyCollection<long>? _categoryIds;
    private readonly string? _notes;
    private long _countId;
    private bool _reviewed;
    private bool _loadingRows;
    private long? _reviewedMovementHighWaterMark;
    private readonly Stack<long?> _pageBack = new();
    private long? _pageAfter;
    private long? _pageNext;
    private long _totalLines;
    private long _savedCounted;
    public StockCountWindow(InventoryService inventory, long? countId = null, bool loadOnShow = true,
        IReadOnlyCollection<long>? categoryIds = null, string? notes = null)
    {
        InitializeComponent();
        _inventory = inventory;
        _existingCountId = countId;
        _categoryIds = categoryIds;
        _notes = notes;
        if (loadOnShow) Loaded += LoadAsync;
    }
    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            _scanner = ((App)Application.Current).Services.GetRequiredService<ScanResolver>();
            if (_existingCountId is long existing) _countId = existing;
            else
            {
                DateOnly date = ((App)Application.Current).Services.GetRequiredService<IClock>().BusinessDate;
                _countId = await _inventory.CreateStockCountAsync(date, _categoryIds, _notes);
            }
            await LoadPageAsync();
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }
    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_reviewed) throw new InvalidOperationException(TranslationSource.Instance["Stock_ReviewFirst"]);
            SetBusy(true);
            await SaveCountsAsync(requireComplete: true);
            await _inventory.PostReviewedStockCountAsync(_countId, OperationGuard.NewId(),
                _reviewedMovementHighWaterMark ?? throw new InvalidOperationException());
            DialogResult = true;
        }
        catch (DomainException exception) when (exception.Code == ErrorCode.StockCountReviewChanged)
        {
            InvalidateReview();
            try
            {
                await _inventory.RefreshStockCountLotsAsync(_countId);
                await LoadPageAsync();
            }
            catch (Exception reloadError) { ErrorText.Text = UserError.Message(reloadError); return; }
            ErrorText.Text = TranslationSource.Instance["Stock_MovementsChanged"];
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private async void Review_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            await SaveCountsAsync(requireComplete: true);
            int added = await _inventory.RefreshStockCountLotsAsync(_countId);
            if (added > 0)
            {
                await LoadPageAsync();
                InvalidateReview();
                ErrorText.Text = TranslationSource.Instance.Format("Stock_NewLotsAdded", added);
                return;
            }
            StockCountReview review = await _inventory.GetStockCountReviewAsync(_countId);
            if (review.Total == 0 || review.Counted != review.Total)
                throw new InvalidOperationException(TranslationSource.Instance["Stock_CompleteEveryLot"]);
            await LoadPageAsync();
            _reviewedMovementHighWaterMark = review.MovementHighWaterMark;
            _reviewed = true;
            PostButton.IsEnabled = true;
            var formatter = new DisplayFormatter();
            ReviewText.Text = TranslationSource.Instance.Format("Stock_ReviewSummary", review.Differences,
                formatter.Number(review.NetDifferenceValue.Value));
            ErrorText.Text = "";
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private void BindLines(IReadOnlyList<StockCountLine> lines)
    {
        _loadingRows = true;
        try
        {
            var entries = lines.Select(static line => new CountEditLine(line)).ToList();
            foreach (CountEditLine entry in entries) entry.PropertyChanged += CountEntry_OnPropertyChanged;
            CountGrid.ItemsSource = entries;
            UpdateProgress();
        }
        finally { _loadingRows = false; }
    }

    private async Task LoadPageAsync()
    {
        StockCountPage page = await _inventory.GetStockCountPageAsync(_countId, _pageAfter, PageSize);
        if (page.Status != "DRAFT") throw new InvalidOperationException("Stock count is already finalized.");
        _pageNext = page.NextLineId;
        _totalLines = page.Total;
        _savedCounted = page.Counted;
        BindLines(page.Lines);
        PreviousButton.IsEnabled = _pageBack.Count > 0;
        NextButton.IsEnabled = page.HasMore;
    }

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        if (_pageNext is null) return;
        try
        {
            SetBusy(true);
            await SaveCountsAsync(requireComplete: false);
            long? previous = _pageAfter;
            _pageAfter = _pageNext;
            try { await LoadPageAsync(); }
            catch { _pageAfter = previous; throw; }
            _pageBack.Push(previous);
            PreviousButton.IsEnabled = true;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private async void Previous_OnClick(object sender, RoutedEventArgs e)
    {
        if (_pageBack.Count == 0) return;
        try
        {
            SetBusy(true);
            await SaveCountsAsync(requireComplete: false);
            long? current = _pageAfter;
            _pageAfter = _pageBack.Peek();
            try { await LoadPageAsync(); _pageBack.Pop(); }
            catch { _pageAfter = current; throw; }
            PreviousButton.IsEnabled = _pageBack.Count > 0;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private void CountEntry_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CountEditLine.Counted)) InvalidateReview();
    }

    private async void PageSize_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _countId == 0) return;
        try
        {
            SetBusy(true);
            await SaveCountsAsync(requireComplete: false);
            _pageBack.Clear(); _pageAfter = null;
            await LoadPageAsync();
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private void InvalidateReview()
    {
        if (_loadingRows) return;
        _reviewed = false;
        _reviewedMovementHighWaterMark = null;
        PostButton.IsEnabled = false;
        ReviewText.Text = "";
        UpdateProgress();
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            await SaveCountsAsync(requireComplete: false);
            DialogResult = false;
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            await SaveCountsAsync(requireComplete: false);
            int added = await _inventory.RefreshStockCountLotsAsync(_countId);
            await LoadPageAsync();
            InvalidateReview();
            ErrorText.Text = TranslationSource.Instance.Format("Stock_NewLotsAdded", added);
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
        finally { SetBusy(false); }
    }

    private async Task SaveCountsAsync(bool requireComplete)
    {
        CountGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CountGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var entries = CountGrid.ItemsSource.Cast<CountEditLine>().ToList();
        if (requireComplete && entries.Any(static line => string.IsNullOrWhiteSpace(line.Counted)))
            throw new InvalidOperationException(TranslationSource.Instance["Stock_CompleteEveryLot"]);
        var values = new Dictionary<long, Quantity>();
        foreach (CountEditLine line in entries)
        {
            if (string.IsNullOrWhiteSpace(line.Counted)) continue;
            if (!decimal.TryParse(InputNormalizer.Normalize(line.Counted), NumberStyles.Number,
                    TranslationSource.FormatCulture, out decimal amount) || amount < 0)
                throw new InvalidOperationException(TranslationSource.Instance["Stock_InvalidCount"]);
            values.Add(line.LotId, new Quantity(amount));
        }
        if (values.Count > 0) await _inventory.SetCountedQuantitiesAsync(_countId, values);
        UpdateProgress();
    }

    private void SetBusy(bool busy) { SaveButton.IsEnabled = !busy; RefreshButton.IsEnabled = !busy; ReviewButton.IsEnabled = !busy; PostButton.IsEnabled = !busy && _reviewed; }
    private void CountGrid_OnCurrentCellChanged(object sender, EventArgs e) => UpdateProgress();
    private async void CountSearch_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await FindCountLineAsync();
    }
    private async void CountSearch_OnClick(object sender, RoutedEventArgs e) => await FindCountLineAsync();
    private async Task FindCountLineAsync()
    {
        string query = InputNormalizer.Normalize(CountSearchBox.Text).Trim();
        if (query.Length == 0 || CountGrid.ItemsSource is null) return;
        try
        {
            long? lineId = await _inventory.FindStockCountLineAsync(_countId, query);
            if (lineId is null && _scanner is not null)
            {
                ScanResult scan = await _scanner.ResolveAsync(query);
                lineId = await _inventory.FindStockCountLineAsync(_countId, query,
                    scan.LotId is null ? scan.ProductId : null, scan.LotId);
            }
            if (lineId is null)
            {
                ErrorText.Text = TranslationSource.Instance["Stock_CountLotNotFound"];
                return;
            }
            await SaveCountsAsync(requireComplete: false);
            _pageAfter = lineId.Value - 1;
            _pageBack.Clear();
            await LoadPageAsync();
            CountEditLine? line = CountGrid.ItemsSource.Cast<CountEditLine>()
                .FirstOrDefault(item => item.Id == lineId.Value);
            if (line is null) return;
            CountGrid.SelectedItem = line;
            CountGrid.ScrollIntoView(line);
            CountGrid.CurrentCell = new DataGridCellInfo(line, CountGrid.Columns[3]);
            CountGrid.Focus();
            CountGrid.BeginEdit();
            ErrorText.Text = "";
            CountSearchBox.Clear();
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }
    private void CountGrid_OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditingElement is TextBox box) box.Text = InputNormalizer.Normalize(box.Text);
    }
    private void UpdateProgress()
    {
        if (ProgressText is null || CountGrid?.ItemsSource is null) return;
        var entries = CountGrid.ItemsSource.Cast<CountEditLine>().ToList();
        ProgressText.Text = TranslationSource.Instance.Format("Stock_CountProgress",
            _savedCounted - entries.Count(static line => line.WasCounted) +
                entries.Count(static line => !string.IsNullOrWhiteSpace(line.Counted)), _totalLines);
    }

    private void Print_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        var document = new FlowDocument { FontFamily = new FontFamily("Nirmala UI"), FontSize = 11, PagePadding = new Thickness(36) };
        document.Blocks.Add(new Paragraph(new Run(TranslationSource.Instance["Stock_Count"])) { FontSize = 18, FontWeight = FontWeights.SemiBold });
        var table = new Table();
        table.Columns.Add(new TableColumn { Width = new GridLength(260) });
        table.Columns.Add(new TableColumn { Width = new GridLength(100) });
        table.Columns.Add(new TableColumn { Width = new GridLength(100) });
        table.Columns.Add(new TableColumn { Width = new GridLength(120) });
        var group = new TableRowGroup();
        group.Rows.Add(Row(TranslationSource.Instance["Catalog_Name"], TranslationSource.Instance["Stock_Expected"],
            TranslationSource.Instance["Stock_Counted"], TranslationSource.Instance["Purchasing_Notes"]));
        foreach (CountEditLine line in CountGrid.ItemsSource.Cast<CountEditLine>())
            group.Rows.Add(Row($"{line.ProductName} / {line.LotCode}",
                line.Expected.Value.ToString("0.000", TranslationSource.FormatCulture), "", ""));
        table.RowGroups.Add(group);
        document.Blocks.Add(table);
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "Stock count sheet");
    }

    private static TableRow Row(params string[] values)
    {
        var row = new TableRow();
        foreach (string value in values)
            row.Cells.Add(new TableCell(new Paragraph(new Run(value))) { Padding = new Thickness(4), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) });
        return row;
    }
    private async void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        if (_countId <= 0) { DialogResult = false; return; }
        if (MessageBox.Show(TranslationSource.Instance["Stock_DiscardConfirm"],
            TranslationSource.Instance["Stock_DiscardCount"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _inventory.CancelStockCountAsync(_countId); DialogResult = false; }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }
    private sealed class CountEditLine(StockCountLine line) : INotifyPropertyChanged
    {
        private string _counted = line.Counted?.Value.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        public long Id { get; } = line.Id;
        public bool WasCounted { get; } = line.Counted is not null;
        public long LotId { get; } = line.LotId;
        public long ProductId { get; } = line.ProductId;
        public string ProductName { get; } = line.ProductName;
        public string LotCode { get; } = line.LotCode;
        public Quantity Expected { get; } = line.Expected;
        public Quantity Movement { get; } = line.MovementSinceSnapshot;
        public Money UnitCost { get; } = line.UnitCost;
        public string Counted
        {
            get => _counted;
            set { _counted = value; OnPropertyChanged(); OnPropertyChanged(nameof(Difference)); OnPropertyChanged(nameof(DifferenceValue)); }
        }
        public Quantity? Difference => decimal.TryParse(InputNormalizer.Normalize(Counted), NumberStyles.Number,
            TranslationSource.FormatCulture, out decimal value)
            ? new Quantity(value - Expected.Value - Movement.Value) : null;
        public Money? DifferenceValue => Difference is Quantity difference ? UnitCost * difference : null;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
