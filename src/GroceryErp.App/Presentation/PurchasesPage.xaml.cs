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
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void VendorReturn_OnClick(object sender, RoutedEventArgs e)
    {
        if (PurchasesGrid.SelectedItem is not PurchaseListItem { Status: "POSTED" } purchase) return;
        var window = new VendorReturnWindow(_purchasing, purchase.Id) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        await RefreshAsync();
        StatusText.Text = TranslationSource.Instance.Format("Purchasing_ReturnPosted", window.DocumentNumber ?? "");
    }

    private async Task RefreshAsync()
    {
        try
        {
            PurchasesGrid.ItemsSource = await _purchasing.GetPurchasesAsync();
            VendorsGrid.ItemsSource = await _purchasing.GetVendorsAsync();
            StatusText.Text = $"{PurchasesGrid.Items.Count} / {VendorsGrid.Items.Count}";
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }
}
