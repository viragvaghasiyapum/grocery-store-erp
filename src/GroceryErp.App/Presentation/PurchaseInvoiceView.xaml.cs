using System.Windows;
using System.Windows.Controls;
using GroceryErp.Localization;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

public partial class PurchaseInvoiceView : UserControl
{
    public PurchaseInvoiceView() => InitializeComponent();

    /// <summary>Shows a posted or voided purchase's saved lines, totals and notes.</summary>
    public void Show(PurchaseInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var text = TranslationSource.Instance;
        SetDraft(false);
        LinesGrid.ItemsSource = invoice.Lines;
        LegacySnapshotText.Text = invoice.Lines.Any(static line =>
            line.SnapshotProvenance == "LEGACY_MASTER_AT_MIGRATION")
            ? text["History_LegacyPurchaseSnapshot"] : "";
        SubtotalText.Text = invoice.Totals.Subtotal.ToString();
        DiscountText.Text = invoice.Totals.Discount.ToString();
        TaxText.Text = invoice.Totals.Tax.ToString();
        ChargesText.Text = invoice.Totals.OtherCharges.ToString();
        RoundOffText.Text = invoice.Totals.RoundOff.ToString();
        TotalText.Text = invoice.Totals.GrandTotal.ToString();
        // The mode saved with the purchase, named as Setup names it.
        string taxMode = invoice.TaxMode switch
        {
            "REGULAR" => text["Setup_TaxRegular"],
            "COMPOSITION" => text["Setup_TaxComposition"],
            "NONE" => text["Setup_TaxNone"],
            null => "—",
            { } code => code,
        };
        TaxModeText.Text = $"{text["Purchasing_TaxMode"]}: {taxMode}";
        BillTotalText.Text = $"{text["Purchasing_BillTotal"]}: {invoice.Totals.VendorBillTotal?.ToString() ?? "—"}";
        DifferenceText.Text = invoice.Totals.Difference is { } difference
            ? $"{text["Purchasing_Difference"]}: {difference}"
            : "";
        NotesText.Text = string.IsNullOrWhiteSpace(invoice.Notes) ? "" : $"{text["Purchasing_Notes"]}: {invoice.Notes}";
    }

    /// <summary>Shows that the purchase is a draft, which is finished in the purchase form.</summary>
    public void ShowDraft()
    {
        LinesGrid.ItemsSource = null;
        SetDraft(true);
    }

    private void SetDraft(bool draft)
    {
        DraftText.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;
        foreach (UIElement part in new UIElement[] { LinesGrid, TotalsRow, NotesPanel })
            part.Visibility = draft ? Visibility.Collapsed : Visibility.Visible;
    }
}
