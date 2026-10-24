using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;
using GroceryErp.Sales;

namespace GroceryErp.App.Presentation;

public partial class SaleActionWindow : Window
{
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-IN");
    private readonly bool _isReturn;
    private readonly IReadOnlyList<ReturnEntry> _entries;

    public SaleActionWindow(bool isReturn, IReadOnlyList<ReturnableSaleLine>? lines = null, string? context = null)
    {
        InitializeComponent();
        _isReturn = isReturn;
        Title = TranslationSource.Instance[isReturn ? "Sales_Return" : "Sales_Void"];
        TitleText.Text = Title;
        ContextText.Text = context ?? string.Empty;
        ContextText.Visibility = context is null ? Visibility.Collapsed : Visibility.Visible;
        if (!isReturn)
        {
            ReturnFields.Visibility = Visibility.Collapsed;
            RefundModeField.Visibility = Visibility.Collapsed;
            TotalText.Visibility = Visibility.Collapsed;
            LayoutGrid.RowDefinitions[3].Height = new GridLength(0);
            // A void asks only for its reason; the window grows if the summary appears.
            Width = 520;
            MinWidth = 480;
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
        }
        _entries = lines?.Select(static line => new ReturnEntry(line)).ToArray() ?? [];
        foreach (ReturnEntry entry in _entries) entry.PropertyChanged += (_, _) => UpdateTotal();
        ReturnGrid.ItemsSource = _entries;
        UpdateTotal();
        Loaded += (_, _) => ReasonBox.Focus();
    }

    public string Reason => ReasonBox.Text.Trim();
    public IReadOnlyList<ReturnLineInput> ReturnLines { get; private set; } = [];
    public RefundMode SelectedRefundMode => ((ComboBoxItem)RefundModeBox.SelectedItem).Tag?.ToString() switch
    {
        "Upi" => RefundMode.Upi,
        "Card" => RefundMode.Card,
        _ => RefundMode.Cash,
    };

    private void ReturnAll_OnClick(object sender, RoutedEventArgs e)
    {
        ReturnGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (ReturnEntry entry in _entries) entry.QuantityText = entry.Remaining;
    }

    private void UpdateTotal()
    {
        if (!_isReturn) return;
        int lines = 0;
        decimal refund = 0;
        foreach (ReturnEntry entry in _entries)
            if (entry.Refund is { } amount)
            {
                lines++;
                refund += amount;
            }
        TotalText.Text = TranslationSource.Instance.Format("Sales_ReturnTotal",
            refund.ToString("0.00", Digits), lines);
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        if (form.Text(ReasonField, ReasonBox.Text, maxLength: 240) is null) form.AddKey(ReasonField, "Sales_ReasonRequired");
        bool linesValid = !_isReturn || TryCollectLines();
        if (!form.Apply(Fields, Summary,
                linesValid ? null : [TranslationSource.Instance["Sales_ReturnQuantityInvalid"]])) return;
        DialogResult = true;
    }

    // Selects and scrolls to the first line whose quantity cannot be returned.
    private bool TryCollectLines()
    {
        ReturnGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = new List<ReturnLineInput>();
        foreach (ReturnEntry entry in _entries)
        {
            string text = InputNormalizer.Normalize(entry.QuantityText).Trim();
            if (text.Length == 0) continue;
            if (!decimal.TryParse(text, NumberStyles.Number, Digits, out decimal value) ||
                value <= 0 || new Quantity(value) > entry.Line.Remaining)
            {
                ReturnGrid.SelectedItem = entry;
                ReturnGrid.ScrollIntoView(entry);
                return false;
            }
            selected.Add(new(entry.Line.Line.Id, new Quantity(value), entry.Resellable));
        }
        ReturnLines = selected;
        return selected.Count > 0;
    }

    // One sold line: what was sold and returned so far, what this return refunds,
    // and where its units go. An expired lot cannot take units back.
    private sealed class ReturnEntry(ReturnableSaleLine line) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private string _quantityText = "";
        private bool _resellable = !line.ResaleExpired;
        public ReturnableSaleLine Line { get; } = line;
        public string Name => Line.Line.ProductName;
        public string Lot => Line.Line.LotCode ?? Line.Line.BatchDisplay;
        public string Expiry => Line.Line.ExpiryDisplay;
        public string Sold => Format(Line.Line.Quantity.Value);
        public string AlreadyReturned => Format(Line.AlreadyReturned.Value);
        public string Remaining => Format(Line.Remaining.Value);
        public string QuantityText
        {
            get => _quantityText;
            set { _quantityText = value; Changed(); Changed(nameof(RefundPreview)); }
        }
        public bool Resellable
        {
            get => _resellable;
            set
            {
                _resellable = value && !Line.ResaleExpired;
                Changed();
                Changed(nameof(RestockDisposition));
            }
        }
        public string RestockDisposition => TranslationSource.Instance[
            Line.ResaleExpired ? "Sales_ReturnExpiredDamage" :
            !Resellable ? "Sales_Activity_RETURN_DAMAGED" :
            Line.OriginalLotAvailable ? "Sales_ReturnOriginalLot" : "Sales_ReturnNewLot"];
        // Mirrors the server: the last units of a line refund what is left of it.
        public decimal? Refund
        {
            get
            {
                if (!decimal.TryParse(InputNormalizer.Normalize(QuantityText), NumberStyles.Number,
                        Digits, out decimal amount) || amount <= 0 || amount > Line.Remaining.Value) return null;
                return amount == Line.Remaining.Value ? Line.RemainingRefund.Value :
                    decimal.Round(Line.Refundable.Value * amount / Line.Line.Quantity.Value, 3,
                        MidpointRounding.AwayFromZero);
            }
        }
        public string RefundPreview => Refund is { } refund ? Format(refund) : "—";
        private static string Format(decimal value) => value.ToString("0.###", Digits);
        private void Changed([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
