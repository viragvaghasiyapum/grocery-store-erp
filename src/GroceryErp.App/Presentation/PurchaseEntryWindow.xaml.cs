using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class PurchaseEntryWindow : Window
{
    private readonly PurchasingService _purchasing;
    private long? _purchaseId;
    public ObservableCollection<PurchaseEditLine> Lines { get; } = [];

    public PurchaseEntryWindow(PurchasingService purchasing)
    {
        InitializeComponent();
        _purchasing = purchasing;
        DataContext = this;
        Loaded += LoadAsync;
    }

    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        IServiceProvider services = ((App)Application.Current).Services;
        VendorBox.ItemsSource = await _purchasing.GetVendorsAsync();
        ProductColumn.ItemsSource = await services.GetRequiredService<CatalogService>().SearchAsync(new ProductSearchFilter(Limit: 5000));
        DateTime today = services.GetRequiredService<IClock>().BusinessDate.ToDateTime(TimeOnly.MinValue);
        InvoiceDateBox.SelectedDate = today;
        ReceivedDateBox.SelectedDate = today;
        Lines.Add(new());
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try { _purchaseId = await SaveAsync(); ErrorText.Text = ""; }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }

    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _purchaseId = await SaveAsync();
            PostedPurchase posted = await _purchasing.PostAsync(_purchaseId.Value, OperationGuard.NewId());
            if (!posted.WasReplay && posted.LotIds.Count > 0 && MessageBox.Show(
                TranslationSource.Instance["Catalog_PrintReceivedLabels"],
                TranslationSource.Instance["Catalog_PrintLabels"], MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                IServiceProvider services = ((App)Application.Current).Services;
                try
                {
                    new LabelBatchWindow(services.GetRequiredService<CatalogService>(),
                        services.GetRequiredService<InventoryService>(), services.GetRequiredService<ISettings>(),
                        lotIds: posted.LotIds)
                    { Owner = this }.ShowDialog();
                }
                catch (Exception labelError) { MessageBox.Show(labelError.Message); }
            }
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }

    private Task<long> SaveAsync()
    {
        long vendorId = VendorBox.SelectedValue is long value ? value : throw new InvalidOperationException("Select a vendor.");
        DateOnly received = DateOnly.FromDateTime(ReceivedDateBox.SelectedDate ?? throw new InvalidOperationException("Select a received date."));
        DateOnly? invoiceDate = InvoiceDateBox.SelectedDate is DateTime date ? DateOnly.FromDateTime(date) : null;
        List<PurchaseLineInput> lines = Lines.Where(static line => line.ProductId > 0).Select(line => line.ToInput()).ToList();
        Money? billTotal = ParseOptionalMoney(BillTotalBox.Text);
        var input = new PurchaseDraftInput(vendorId, InvoiceBox.Text, invoiceDate, received, billTotal,
            new Money(Parse(ChargesBox.Text)), NotesBox.Text, lines);
        return _purchasing.SaveDraftAsync(_purchaseId, input);
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private static decimal Parse(string value) => decimal.Parse(string.IsNullOrWhiteSpace(value) ? "0" : value, NumberStyles.Number, CultureInfo.InvariantCulture);
    private static Money? ParseOptionalMoney(string value) => string.IsNullOrWhiteSpace(value) ? null : new Money(Parse(value));

    public sealed class PurchaseEditLine
    {
        public long ProductId { get; set; }
        public string? Batch { get; set; }
        public string? Expiry { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal FreeQuantity { get; set; }
        public decimal Rate { get; set; }
        public decimal Discount { get; set; }
        public decimal TaxRate { get; set; }
        public decimal? Mrp { get; set; }
        public decimal SellingPrice { get; set; }
        public PurchaseLineInput ToInput() => new(ProductId, Batch, null,
            string.IsNullOrWhiteSpace(Expiry) ? null : DateOnly.ParseExact(Expiry, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            new Quantity(Quantity), new Quantity(FreeQuantity), new Money(Rate), new Money(Discount), new Percent(TaxRate),
            Mrp is null ? null : new Money(Mrp.Value), new Money(SellingPrice));
    }
}
