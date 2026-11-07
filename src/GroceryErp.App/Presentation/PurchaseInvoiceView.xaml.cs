using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;
using GroceryErp.Purchasing;

namespace GroceryErp.App.Presentation;

public partial class PurchaseInvoiceView : UserControl
{
    private PurchaseInvoice? _invoice;
    private Money? _draftTotal;

    public PurchaseInvoiceView()
    {
        InitializeComponent();
        // The totals' names and the tax mode are written here, so they are written again in the new
        // language. Loaded can come twice for one Unloaded, so the handler is removed first.
        Loaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= OnLanguageChanged;
            TranslationSource.Instance.PropertyChanged += OnLanguageChanged;
        };
        Unloaded += (_, _) => TranslationSource.Instance.PropertyChanged -= OnLanguageChanged;
    }

    /// <summary>Shows a posted or voided purchase's saved lines, notes and totals.</summary>
    public void Show(PurchaseInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        _invoice = invoice;
        _draftTotal = null;
        var text = TranslationSource.Instance;
        SetDraft(false);
        LinesGrid.ItemsSource = invoice.Lines;
        LegacySnapshotText.Text = invoice.Lines.Any(static line =>
            line.SnapshotProvenance == "LEGACY_MASTER_AT_MIGRATION")
            ? text["History_LegacyPurchaseSnapshot"] : "";
        PurchaseTotals totals = invoice.Totals;
        var shown = new List<(string, string)>
        {
            (text["Purchasing_Subtotal"], totals.Subtotal.ToString()),
            (text["Purchasing_Discount"], totals.Discount.ToString()),
            (text["Purchasing_TaxTotal"], totals.Tax.ToString()),
            (text["Purchasing_OtherCharges"], totals.OtherCharges.ToString()),
            (text["Purchasing_RoundOff"], totals.RoundOff.ToString()),
            (text["Purchasing_ComputedTotal"], totals.GrandTotal.ToString()),
        };
        if (totals.VendorBillTotal is { } billTotal) shown.Add((text["Purchasing_BillTotal"], billTotal.ToString()));
        if (totals.Difference is { } difference) shown.Add((text["Purchasing_Difference"], difference.ToString()));
        Totals.Show(shown);
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
        NotesText.Text = string.IsNullOrWhiteSpace(invoice.Notes) ? "" : $"{text["Purchasing_Notes"]}: {invoice.Notes}";
    }

    /// <summary>
    /// Shows that the purchase is a draft, which is finished in the purchase form, with the total
    /// it was saved with.
    /// </summary>
    public void ShowDraft(Money total)
    {
        _invoice = null;
        _draftTotal = total;
        LinesGrid.ItemsSource = null;
        SetDraft(true);
        Totals.Show([(TranslationSource.Instance["Purchasing_Total"], total.ToString())]);
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        if (_invoice is not null) Show(_invoice);
        else if (_draftTotal is { } total) ShowDraft(total);
    }

    private void SetDraft(bool draft)
    {
        DraftText.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;
        foreach (UIElement part in new UIElement[] { LinesGrid, NotesPanel })
            part.Visibility = draft ? Visibility.Collapsed : Visibility.Visible;
    }
}
