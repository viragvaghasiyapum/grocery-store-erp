using System.Windows;
using System.Windows.Controls;
using GroceryErp.Inventory;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Read-only stock movements recorded before the direct-stock cutover, for one product. Phase 8
/// removes this view together with the movement table.
/// </summary>
public partial class ProductLedgerView : UserControl
{
    private readonly InventoryService _inventory;
    private readonly long _productId;
    private readonly KeysetPager<long?> _pager = new();
    private readonly LatestRequest _requests = new();
    private readonly FitPageSize _fit;

    public ProductLedgerView(InventoryService inventory, long productId, bool loadOnShow = true)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        _inventory = inventory;
        _productId = productId;
        InitializeComponent();
        _fit = new FitPageSize(LedgerGrid, 12);
        _fit.RowsChanged += async (_, _) => await LoadAsync(PageMove.Same);
        Pager.PreviousRequested += async (_, _) => await LoadAsync(PageMove.Previous);
        Pager.NextRequested += async (_, _) => await LoadAsync(PageMove.Next);
        Unloaded += (_, _) => _requests.Cancel();
        if (loadOnShow) Loaded += async (_, _) =>
        {
            if (LedgerGrid.ItemsSource is null) await LoadAsync(PageMove.First);
        };
    }

    private async Task LoadAsync(PageMove move)
    {
        if (!_pager.CanMove(move)) return;
        using LatestRequest.Scope request = _requests.Begin();
        Pager.SetLoading(true);
        try
        {
            LedgerPage page = await _inventory.GetLedgerPageAsync(_productId, DateOnly.MinValue, DateOnly.MaxValue,
                _pager.CursorFor(move), limit: _fit.Take(), cancellationToken: request.Token);
            if (!request.IsCurrent) return;
            _pager.Commit(move, page.HasMore, page.NextId);
            LedgerGrid.ItemsSource = page.Items;
            Pager.Status = string.Empty;
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

    private void LedgerGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OpenSourceButton.IsEnabled = LedgerGrid.SelectedItem is LedgerItem;

    private async void OpenSource_OnClick(object sender, RoutedEventArgs e)
    {
        if (LedgerGrid.SelectedItem is not LedgerItem movement) return;
        try
        {
            StockSourceDocument source = await _inventory.GetSourceDocumentAsync(movement);
            new StockSourceWindow(source) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception exception) { Pager.Status = UserError.Message(exception); }
    }
}
