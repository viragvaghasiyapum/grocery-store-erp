using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class PurchasesPage : Page
{
    private readonly PurchasingService _purchasing;

    public PurchasesPage()
    {
        InitializeComponent();
        _purchasing = ((App)Application.Current).Services.GetRequiredService<PurchasingService>();
        Loaded += async (_, _) => await RefreshAsync();
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
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private async void Payment_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new VendorPaymentWindow(_purchasing) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private async void CancelPurchase_OnClick(object sender, RoutedEventArgs e)
    {
        if (PurchasesGrid.SelectedItem is not PurchaseListItem purchase || purchase.Status != "POSTED") return;
        try
        {
            IServiceProvider services = ((App)Application.Current).Services;
            await _purchasing.CancelAsync(purchase.Id, services.GetRequiredService<IClock>().BusinessDate, OperationGuard.NewId());
            await RefreshAsync();
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
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
