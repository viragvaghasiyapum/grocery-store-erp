using System.Windows;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

public partial class OpeningStockPreviewWindow : Window
{
    public OpeningStockPreviewWindow(OpeningStockImportPreview preview)
    {
        InitializeComponent();
        RowsGrid.ItemsSource = preview.Rows.Select(static row => new PreviewRow(row)).ToList();
        SummaryText.Text = TranslationSource.Instance.Format("Stock_OpeningPreviewSummary", preview.ValidCount,
            preview.ErrorCount);
        ImportButton.IsEnabled = preview.ValidCount > 0 && preview.ErrorCount == 0;
    }

    private void Import_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class PreviewRow(OpeningStockImportRow row)
    {
        public int RowNumber { get; } = row.RowNumber;
        public string Sku { get; } = row.Sku;
        public string Quantity { get; } = row.Line?.Quantity.ToString() ?? "";
        public string UnitCost { get; } = row.Line?.UnitCost.ToString() ?? "";
        public string SellingPrice { get; } = row.Line?.SellingPrice.ToString() ?? "";
        public string ErrorText { get; } = string.Join("; ", row.Errors);
    }
}
