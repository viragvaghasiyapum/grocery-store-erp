using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class PurchasesPage : Page
{
    private readonly PurchasingService _purchasing;
    private readonly Stack<(DateOnly? Date, long? Id)> _previousPages = new();
    private DateOnly? _beforeDate;
    private long? _beforeId;
    private PurchasePage? _currentPage;
    private bool _loadingPage;

    public PurchasesPage() : this(null) { }

    public PurchasesPage(PurchasingService? purchasing, bool loadOnShow = true)
    {
        InitializeComponent();
        _purchasing = purchasing ?? ((App)Application.Current).Services.GetRequiredService<PurchasingService>();
        if (loadOnShow) Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void NewVendor_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new VendorEditorWindow(_purchasing) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private async void NewPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new PurchaseEntryWindow(_purchasing) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        await RefreshAsync();
    }

    private async void OpenDraft_OnClick(object sender, RoutedEventArgs e)
    {
        if (PurchasesGrid.SelectedItem is not PurchaseListItem { Status: "DRAFT" } purchase) return;
        var window = new PurchaseEntryWindow(_purchasing, purchase.Id) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        await RefreshAsync();
    }

    private void PurchasesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpenDraftButton is not null)
            OpenDraftButton.IsEnabled = PurchasesGrid.SelectedItem is PurchaseListItem { Status: "DRAFT" };
        if (CancelPurchaseButton is not null)
            CancelPurchaseButton.IsEnabled = PurchasesGrid.SelectedItem is PurchaseListItem { Status: "POSTED" };
        if (VendorReturnButton is not null)
            VendorReturnButton.IsEnabled = PurchasesGrid.SelectedItem is PurchaseListItem { Status: "POSTED" };
    }

    private void VendorsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool selected = VendorsGrid.SelectedItem is VendorItem;
        if (EditVendorButton is not null) EditVendorButton.IsEnabled = selected;
        if (VendorLedgerButton is not null) VendorLedgerButton.IsEnabled = selected;
    }

    private async void EditVendor_OnClick(object sender, RoutedEventArgs e)
    {
        if (VendorsGrid.SelectedItem is not VendorItem vendor) return;
        var window = new VendorEditorWindow(_purchasing, vendor.Id) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private void VendorLedger_OnClick(object sender, RoutedEventArgs e)
    {
        if (VendorsGrid.SelectedItem is not VendorItem vendor) return;
        new VendorLedgerWindow(_purchasing, vendor.Id, vendor.Name) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async void Payment_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new VendorPaymentWindow(_purchasing) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private async void CancelPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        if (PurchasesGrid.SelectedItem is not PurchaseListItem purchase || purchase.Status != "POSTED") return;
        if (MessageBox.Show(TranslationSource.Instance["Purchasing_CancelConfirm"],
            TranslationSource.Instance["Purchasing_CancelPurchase"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            IServiceProvider services = ((App)Application.Current).Services;
            await _purchasing.CancelAsync(purchase.Id, services.GetRequiredService<IClock>().BusinessDate, OperationGuard.NewId());
            await RefreshAsync();
        }
        catch (DomainException exception) when (exception.Code == ErrorCode.CancelNotAllowed)
        {
            StatusText.Text = TranslationSource.Instance["Purchasing_CancelBlocked"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private async void VendorReturn_OnClick(object sender, RoutedEventArgs e)
    {
        if (PurchasesGrid.SelectedItem is not PurchaseListItem { Status: "POSTED" } purchase) return;
        var window = new VendorReturnWindow(_purchasing, purchase.Id) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        await RefreshAsync();
        StatusText.Text = TranslationSource.Instance.Format("Purchasing_ReturnPosted", window.DocumentNumber ?? "");
    }

    private Task RefreshAsync() => LoadPageAsync(null, null, reset: true);

    private async Task LoadPageAsync(DateOnly? beforeDate, long? beforeId, bool reset)
    {
        using var timing = UiRefreshProbe.Track("purchases", this);
        if (_loadingPage) return;
        _loadingPage = true;
        PreviousPurchaseButton.IsEnabled = false;
        NextPurchaseButton.IsEnabled = false;
        try
        {
            PurchasePage page = await _purchasing.GetPurchasesPageAsync(beforeDate, beforeId);
            if (reset) _previousPages.Clear();
            _beforeDate = beforeDate;
            _beforeId = beforeId;
            _currentPage = page;
            timing.Bind(() => PurchasesGrid.ItemsSource = page.Items);
            VendorsGrid.ItemsSource = await _purchasing.GetVendorsAsync();
            StatusText.Text = $"{PurchasesGrid.Items.Count} / {VendorsGrid.Items.Count}";
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
        finally
        {
            _loadingPage = false;
            PreviousPurchaseButton.IsEnabled = _previousPages.Count > 0;
            NextPurchaseButton.IsEnabled = _currentPage?.HasMore == true;
        }
    }

    private async void NextPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingPage || _currentPage is not { HasMore: true, NextDate: not null, NextId: not null } page) return;
        DateOnly? oldDate = _beforeDate;
        long? oldId = _beforeId;
        await LoadPageAsync(page.NextDate, page.NextId, reset: false);
        if (_beforeDate == page.NextDate && _beforeId == page.NextId)
            _previousPages.Push((oldDate, oldId));
        PreviousPurchaseButton.IsEnabled = _previousPages.Count > 0;
    }

    private async void PreviousPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingPage || _previousPages.Count == 0) return;
        (DateOnly? date, long? id) = _previousPages.Peek();
        await LoadPageAsync(date, id, reset: false);
        if (_beforeDate == date && _beforeId == id) _previousPages.Pop();
        PreviousPurchaseButton.IsEnabled = _previousPages.Count > 0;
    }
}
