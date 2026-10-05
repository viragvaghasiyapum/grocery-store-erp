using System.Globalization;
using System.Windows;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class StockSourceWindow : Window
{
    public StockSourceWindow(StockSourceDocument document)
    {
        InitializeComponent();
        string kind = document.Type.ToLowerInvariant() switch
        {
            "purchase" => "Purchase",
            "sale" => "Sale",
            "sale_return" or "return" => "Return",
            "damage" => "Damage",
            "stock_count" or "count" => "Count",
            "opening_stock" or "opening" => "Opening",
            "vendor_return" or "vendorreturn" => "VendorReturn",
            _ => "Adjustment",
        };
        TitleText.Text = $"{TranslationSource.Instance[$"Stock_SourceDoc_{kind}"]}  {document.Number}";
        DateText.Text = document.Date.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("en-IN"));
        string? status = document.Status switch
        {
            "POSTED" => TranslationSource.Instance["Stock_Status_Posted"],
            "CANCELLED" => TranslationSource.Instance["Stock_Status_Cancelled"],
            "COMPLETED" => TranslationSource.Instance["Sales_Status_Completed"],
            "VOID" => TranslationSource.Instance["Sales_Status_Void"],
            _ => document.Status,
        };
        DetailText.Text = string.Join("  ·  ", new[] { document.Party, status, document.Notes }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        LinesGrid.ItemsSource = document.Lines;
    }
}
