using System.ComponentModel;
using System.Windows;
using GroceryErp.Localization;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Asks, when Billing opens on an unfinished bill, whether to continue it or discard it. Billing
/// keeps only that one bill, so the question has no close button and Esc and Alt+F4 do nothing.
/// </summary>
public partial class SavedBillWindow : Window
{
    private bool _answered;

    public SavedBillWindow(SavedBill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        InitializeComponent();
        string details = TranslationSource.Instance.Format("Sales_SavedBillDetails",
            bill.CreatedAt, bill.ItemCount, bill.Total.Value);
        SummaryText.Text = bill.CustomerName is { } customer
            ? details + Environment.NewLine + TranslationSource.Instance.Format("Sales_SavedBillCustomer", customer)
            : details;
    }

    /// <summary>True to carry on with the bill; false to discard it.</summary>
    public bool Continue { get; private set; }

    private void Continue_OnClick(object sender, RoutedEventArgs e) => Answer(true);

    private void Discard_OnClick(object sender, RoutedEventArgs e) => Answer(false);

    private void Answer(bool continueBill)
    {
        Continue = continueBill;
        _answered = true;
        Close();
    }

    // Closing the app still closes it; WPF ignores a cancel while the application shuts down.
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_answered) e.Cancel = true;
        base.OnClosing(e);
    }
}
