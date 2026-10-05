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
    private readonly InventoryService _inventory;
    private ScanResolver? _scanner;
    private readonly long? _existingCountId;
    private readonly IReadOnlyCollection<long>? _categoryIds;
    private readonly string? _notes;
    private long _countId;
    private bool _reviewed;
    private bool _loadingRows;
    private Dictionary<long, long> _reviewedMovements = [];
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
            StockCountDetails count = await _inventory.GetStockCountAsync(_countId);
            if (count.Status != "DRAFT") throw new InvalidOperationException("Stock count is already finalized.");
            BindLines(count.Lines);
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }
    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_reviewed) throw new InvalidOperationException(TranslationSource.Instance["Stock_ReviewFirst"]);
            SetBusy(true);
            await SaveCountsAsync(requireComplete: true);
            StockCountDetails latest = await _inventory.GetStockCountAsync(_countId);
            if (latest.Lines.Any(line => !_reviewedMovements.TryGetValue(line.LotId, out long quantity) ||
                                         quantity != line.MovementSinceSnapshot.Scaled))
            {
                BindLines(latest.Lines);
                InvalidateReview();
                throw new InvalidOperationException(TranslationSource.Instance["Stock_MovementsChanged"]);
            }
            await _inventory.PostStockCountAsync(_countId, OperationGuard.NewId(),
                reviewedMovements: _reviewedMovements);
            DialogResult = true;
        }
        catch (DomainException exception) when (exception.Code == ErrorCode.StockCountReviewChanged)
        {
            InvalidateReview();
            try
            {
                await _inventory.RefreshStockCountLotsAsync(_countId);
                StockCountDetails changed = await _inventory.GetStockCountAsync(_countId);
                BindLines(changed.Lines);
            }
            catch (Exception reloadError) { ErrorText.Text = reloadError.Message; return; }
            ErrorText.Text = TranslationSource.Instance["Stock_MovementsChanged"];
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
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
                BindLines((await _inventory.GetStockCountAsync(_countId)).Lines);
                InvalidateReview();
                ErrorText.Text = TranslationSource.Instance.Format("Stock_NewLotsAdded", added);
                return;
            }
            StockCountDetails current = await _inventory.GetStockCountAsync(_countId);
            BindLines(current.Lines.OrderByDescending(static line => Math.Abs(line.DifferenceValue.Value)).ToList());
            _reviewedMovements = current.Lines.ToDictionary(static line => line.LotId,
                static line => line.MovementSinceSnapshot.Scaled);
            _reviewed = true;
            PostButton.IsEnabled = true;
            int differences = current.Lines.Count(static line => line.Difference != Quantity.Zero);
            var formatter = new DisplayFormatter();
            Money net = current.Lines.Aggregate(Money.Zero, static (sum, line) => sum + line.DifferenceValue);
            ReviewText.Text = TranslationSource.Instance.Format("Stock_ReviewSummary", differences,
                formatter.Number(net.Value));
            ErrorText.Text = "";
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
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

    private void CountEntry_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CountEditLine.Counted)) InvalidateReview();
    }

    private void InvalidateReview()
    {
        if (_loadingRows) return;
        _reviewed = false;
        _reviewedMovements.Clear();
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
        catch (Exception exception) { ErrorText.Text = exception.Message; }
        finally { SetBusy(false); }
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            await SaveCountsAsync(requireComplete: false);
            int added = await _inventory.RefreshStockCountLotsAsync(_countId);
            BindLines((await _inventory.GetStockCountAsync(_countId)).Lines);
            InvalidateReview();
            ErrorText.Text = TranslationSource.Instance.Format("Stock_NewLotsAdded", added);
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
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
            List<CountEditLine> lines = CountGrid.ItemsSource.Cast<CountEditLine>().ToList();
            CountEditLine? line = lines.FirstOrDefault(item =>
                item.LotCode.Equals(query, StringComparison.OrdinalIgnoreCase))
                ?? lines.FirstOrDefault(item => item.ProductName.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (line is null && _scanner is not null)
            {
                ScanResult scan = await _scanner.ResolveAsync(query);
                line = scan.LotId is long lotId
                    ? lines.FirstOrDefault(item => item.LotId == lotId)
                    : lines.FirstOrDefault(item => item.ProductId == scan.ProductId);
            }
            if (line is null)
            {
                ErrorText.Text = TranslationSource.Instance["Stock_CountLotNotFound"];
                return;
            }
            CountGrid.SelectedItem = line;
            CountGrid.ScrollIntoView(line);
            CountGrid.CurrentCell = new DataGridCellInfo(line, CountGrid.Columns[3]);
            CountGrid.Focus();
            CountGrid.BeginEdit();
            ErrorText.Text = "";
            CountSearchBox.Clear();
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
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
            entries.Count(static line => !string.IsNullOrWhiteSpace(line.Counted)), entries.Count);
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
            group.Rows.Add(Row($"{line.ProductName} / {line.LotCode}", line.Expected.ToString(), "", ""));
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
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }
    private sealed class CountEditLine(StockCountLine line) : INotifyPropertyChanged
    {
        private string _counted = line.Counted?.Value.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
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
