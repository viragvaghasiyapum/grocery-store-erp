using System.Windows;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

public partial class VendorLedgerWindow : Window
{
    private readonly PurchasingService _purchasing;
    private readonly long _vendorId;

    public VendorLedgerWindow(PurchasingService purchasing, long vendorId, string vendorName, bool loadOnShow = true)
    {
        InitializeComponent();
        _purchasing = purchasing;
        _vendorId = vendorId;
        VendorTitle.Text = vendorName;
        if (loadOnShow) Loaded += LoadAsync;
    }

    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<VendorLedgerItem> ledger = await _purchasing.GetVendorLedgerAsync(_vendorId);
            LedgerGrid.ItemsSource = ledger;
            PurchasesGrid.ItemsSource = ledger.Where(static row => row.Kind == "PURCHASE").ToList();
            PaymentsGrid.ItemsSource = ledger.Where(static row => row.Kind == "PAYMENT").ToList();
            ReturnsGrid.ItemsSource = ledger.Where(static row => row.Kind == "VENDOR_RETURN").ToList();
            LotsGrid.ItemsSource = await _purchasing.GetVendorLotsAsync(_vendorId);
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
