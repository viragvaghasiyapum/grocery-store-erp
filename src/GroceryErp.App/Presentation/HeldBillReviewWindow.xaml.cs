using System.Windows;
using GroceryErp.Core.Time;
using GroceryErp.Localization;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

public partial class HeldBillReviewWindow : Window
{
    public HeldBillReviewWindow(HeldBillReview review, IClock clock)
    {
        InitializeComponent();
        var rows = review.Lines.Select(line => new ReviewRow(line, clock.BusinessDate)).ToArray();
        ReviewGrid.ItemsSource = rows;
        bool unavailable = rows.Any(static row => !row.AvailableForSale);
        AcceptButton.IsEnabled = !unavailable;
        SummaryText.Text = unavailable
            ? TranslationSource.Instance["Sales_HeldUnavailable"]
            : rows.Any(static row => row.Line.PriceChanged)
                ? TranslationSource.Instance["Sales_HeldPriceChanged"]
                : TranslationSource.Instance["Sales_HeldNoChanges"];
    }

    private void Accept_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private sealed class ReviewRow(HeldBillReviewLine line, DateOnly today)
    {
        public HeldBillReviewLine Line { get; } = line;
        public string ProductName => Line.ProductName;
        public string Quantity => Line.Quantity.ToString();
        public string PreviousPrice => Line.PreviousPrice.ToString();
        public string CurrentPrice => Line.CurrentPrice.ToString();
        public string CurrentMrp => Line.CurrentMrp?.ToString() ?? "";
        public string Available => Line.Available.ToString();
        public bool AvailableForSale => Line.IsActive && Line.Available >= Line.Quantity &&
            (Line.ExpiryDate is null || Line.ExpiryDate >= today);
        public string Status => !AvailableForSale ? TranslationSource.Instance["Sales_HeldStockIssue"] :
            Line.PriceChanged ? TranslationSource.Instance["Sales_HeldChanged"] : TranslationSource.Instance["Sales_HeldUnchanged"];
    }
}
