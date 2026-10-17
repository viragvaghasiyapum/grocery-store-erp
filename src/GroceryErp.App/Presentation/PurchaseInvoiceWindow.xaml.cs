using System.Globalization;
using System.Windows;
using GroceryErp.Localization;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

public partial class PurchaseInvoiceWindow : Window
{
    public PurchaseInvoiceWindow(PurchaseInvoice invoice)
    {
        InitializeComponent();
        var text = TranslationSource.Instance;
        TitleText.Text = $"{text["Purchasing_ViewInvoice"]}  {invoice.DocumentNumber ?? invoice.VendorInvoiceNumber ?? invoice.Id.ToString(CultureInfo.InvariantCulture)}  ·  {invoice.Status}";
        VendorText.Text = invoice.VendorName;
        InvoiceNumberText.Text = invoice.VendorInvoiceNumber ?? "—";
        InvoiceDateText.Text = FormatDate(invoice.VendorInvoiceDate);
        ReceivedDateText.Text = FormatDate(invoice.ReceivedDate);
        LinesGrid.ItemsSource = invoice.Lines;
        SubtotalText.Text = invoice.Totals.Subtotal.ToString();
        DiscountText.Text = invoice.Totals.Discount.ToString();
        TaxText.Text = invoice.Totals.Tax.ToString();
        ChargesText.Text = invoice.Totals.OtherCharges.ToString();
        RoundOffText.Text = invoice.Totals.RoundOff.ToString();
        TotalText.Text = invoice.Totals.GrandTotal.ToString();
        BillTotalText.Text = $"{text["Purchasing_BillTotal"]}: {invoice.Totals.VendorBillTotal?.ToString() ?? "—"}";
        DifferenceText.Text = invoice.Totals.Difference is { } difference
            ? $"{text["Purchasing_Difference"]}: {difference}"
            : "";
        NotesText.Text = string.IsNullOrWhiteSpace(invoice.Notes) ? "" : $"{text["Purchasing_Notes"]}: {invoice.Notes}";
    }

    private static string FormatDate(DateOnly? date) =>
        date?.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("en-IN")) ?? "—";
}
