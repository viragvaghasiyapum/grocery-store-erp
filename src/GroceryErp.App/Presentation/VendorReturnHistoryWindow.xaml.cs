using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class VendorReturnHistoryWindow : Window
{
    private readonly PurchasingService _purchasing;
    private readonly long _purchaseId;
    private readonly Stack<long?> _previous = new();
    private long? _beforeId;
    private VendorReturnHistoryPage? _page;

    public VendorReturnHistoryWindow(PurchasingService purchasing, long purchaseId)
    {
        _purchasing = purchasing;
        _purchaseId = purchaseId;
        InitializeComponent();
        Loaded += async (_, _) => await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        try
        {
            _page = await _purchasing.GetVendorReturnHistoryPageAsync(_purchaseId, _beforeId);
            ReturnsGrid.ItemsSource = _page.Items;
            PreviousButton.IsEnabled = _previous.Count > 0;
            NextButton.IsEnabled = _page.HasMore;
            StatusText.Text = _page.Items.Count == 0 ? TranslationSource.Instance["Purchasing_NoReturns"] : "";
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void ReturnsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        CancelReturnButton.IsEnabled = ReturnsGrid.SelectedItem is VendorReturnHistoryItem { IsCancelled: false };

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        if (_page?.HasMore != true) return;
        _previous.Push(_beforeId);
        _beforeId = _page.NextId;
        await LoadPageAsync();
    }

    private async void Previous_OnClick(object sender, RoutedEventArgs e)
    {
        if (_previous.Count == 0) return;
        _beforeId = _previous.Pop();
        await LoadPageAsync();
    }

    private async void CancelReturn_OnClick(object sender, RoutedEventArgs e)
    {
        if (ReturnsGrid.SelectedItem is not VendorReturnHistoryItem { IsCancelled: false } item) return;
        string reason = ReasonBox.Text.Trim();
        if (reason.Length == 0)
        {
            StatusText.Text = TranslationSource.Instance["Purchasing_ReturnCancelReason"];
            return;
        }
        CancelReturnButton.IsEnabled = false;
        try
        {
            DateOnly date = ((App)Application.Current).Services.GetRequiredService<IClock>().BusinessDate;
            await _purchasing.CancelVendorReturnAsync(item.Id, date, reason, OperationGuard.NewId());
            ReasonBox.Clear();
            await LoadPageAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = UserError.Message(exception);
            CancelReturnButton.IsEnabled = true;
        }
    }
}
