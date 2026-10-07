using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

public partial class SaleActionWindow : Window
{
    private readonly bool _isReturn;
    private readonly IReadOnlyList<ReturnEntry> _entries;

    public SaleActionWindow(bool isReturn, IReadOnlyList<ReturnableSaleLine>? lines = null)
    {
        InitializeComponent();
        _isReturn = isReturn;
        Title = TranslationSource.Instance[isReturn ? "Sales_Return" : "Sales_Void"];
        ReturnFields.Visibility = isReturn ? Visibility.Visible : Visibility.Collapsed;
        if (!isReturn)
        {
            LayoutGrid.RowDefinitions[2].Height = new GridLength(0);
            Width = 520;
            Height = 240;
            MinWidth = 480;
            MinHeight = 220;
        }
        _entries = lines?.Select(static line => new ReturnEntry(line)).ToArray() ?? [];
        ReturnGrid.ItemsSource = _entries;
    }

    public string Reason => ReasonBox.Text.Trim();
    public IReadOnlyList<ReturnLineInput> ReturnLines { get; private set; } = [];
    public RefundMode SelectedRefundMode => ((ComboBoxItem)RefundModeBox.SelectedItem).Tag?.ToString() switch
    {
        "Upi" => RefundMode.Upi,
        "Card" => RefundMode.Card,
        _ => RefundMode.Cash,
    };

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (Reason.Length == 0)
        {
            ErrorText.Text = TranslationSource.Instance["Sales_ReasonRequired"];
            return;
        }
        if (_isReturn)
        {
            var selected = new List<ReturnLineInput>();
            foreach (ReturnEntry entry in _entries)
            {
                string text = InputNormalizer.Normalize(entry.QuantityText).Trim();
                if (text.Length == 0) continue;
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("en-IN"), out decimal value) ||
                    value <= 0 || new Quantity(value) > entry.Line.Remaining)
                {
                    ErrorText.Text = TranslationSource.Instance["Sales_ReturnQuantityInvalid"];
                    return;
                }
                selected.Add(new(entry.Line.Line.Id, new Quantity(value), entry.Resellable));
            }
            if (selected.Count == 0)
            {
                ErrorText.Text = TranslationSource.Instance["Sales_ReturnQuantityInvalid"];
                return;
            }
            ReturnLines = selected;
        }
        DialogResult = true;
    }

    private sealed class ReturnEntry(ReturnableSaleLine line)
    {
        public ReturnableSaleLine Line { get; } = line;
        public string Name => Line.Line.ProductName;
        public string Remaining => Line.Remaining.Value.ToString("0.###", CultureInfo.GetCultureInfo("en-IN"));
        public string QuantityText { get; set; } = "";
        public bool Resellable { get; set; } = true;
    }
}
