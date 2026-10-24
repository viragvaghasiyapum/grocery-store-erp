using System.ComponentModel;
using System.Globalization;
using System.Windows;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class VendorReturnWindow : Window
{
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-IN");
    private readonly PurchasingService _purchasing;
    private readonly long _purchaseId;
    private readonly string _operationId = OperationGuard.NewId();
    private ReturnEntry[] _entries = [];

    public VendorReturnWindow(PurchasingService purchasing, long purchaseId, bool loadOnShow = true,
        IClock? clock = null)
    {
        InitializeComponent();
        _purchasing = purchasing;
        _purchaseId = purchaseId;
        DatePicker.SelectedDate = (clock ?? ((App)Application.Current).Services.GetRequiredService<IClock>())
            .BusinessDate.ToDateTime(TimeOnly.MinValue);
        if (loadOnShow) Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _entries = (await _purchasing.GetVendorReturnLotsAsync(_purchaseId))
                .Select(lot => new ReturnEntry(lot)).ToArray();
            foreach (ReturnEntry entry in _entries) entry.PropertyChanged += (_, _) => UpdateTotals();
            ReturnGrid.ItemsSource = _entries;
            if (_entries.Length == 0)
            {
                PostButton.IsEnabled = false;
                Summary.ShowMessage(TranslationSource.Instance["Purchasing_NoReturnableLots"]);
            }
            UpdateTotals();
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); PostButton.IsEnabled = false; }
    }

    private static bool TryMoney(string raw, out Money money)
    {
        money = Money.Zero;
        if (!decimal.TryParse(InputNormalizer.Normalize(raw), NumberStyles.Number, Digits, out decimal value) || value < 0)
            return false;
        money = new Money(value);
        return true;
    }

    private bool TryCollect(out VendorReturnLineInput[] lines, out Money total)
    {
        var selected = new List<VendorReturnLineInput>();
        total = Money.Zero;
        foreach (ReturnEntry entry in _entries)
        {
            string raw = InputNormalizer.Normalize(entry.QuantityText).Trim();
            if (raw.Length == 0) continue;
            if (!decimal.TryParse(raw, NumberStyles.Number, Digits, out decimal value) || value <= 0 ||
                !TryMoney(entry.BeforeTaxText, out Money beforeTax) ||
                !TryMoney(entry.TaxText, out Money tax))
            {
                lines = [];
                return false;
            }
            selected.Add(new(entry.Lot.LotId, new Quantity(value), beforeTax, tax));
            total += beforeTax + tax;
        }
        lines = selected.ToArray();
        return lines.Length > 0;
    }

    private void UpdateTotals()
    {
        TotalsText.Text = TryCollect(out VendorReturnLineInput[] lines, out Money total)
            ? TranslationSource.Instance.Format("Purchasing_ReturnTotal", lines.Length,
                total.Value.ToString("N3", Digits))
            : TranslationSource.Instance["Purchasing_ReturnEnterLines"];
    }

    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        ReturnGrid.CommitEdit();
        var form = new FormValidator();
        DateOnly? date = form.Date(DateField, DatePicker.SelectedDate);
        string? reason = form.Text(ReasonField, ReasonBox.Text, required: true, maxLength: 500);
        bool linesValid = TryCollect(out VendorReturnLineInput[] lines, out Money total);
        if (!form.Apply(Fields, Summary, linesValid ? null : [TranslationSource.Instance["Purchasing_ReturnInvalid"]])) return;
        var input = new VendorReturnInput(_purchaseId, date!.Value,
            InputNormalizer.Normalize(CreditNumberBox.Text).Trim(), reason!, lines);
        if (MessageBox.Show(TranslationSource.Instance.Format("Purchasing_ReturnConfirm",
                lines.Length, total.Value.ToString("N3", Digits)),
            TranslationSource.Instance["Purchasing_VendorReturn"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        PostButton.IsEnabled = false;
        try
        {
            PostedVendorReturn posted = await _purchasing.PostVendorReturnAsync(input, _operationId);
            DocumentNumber = posted.DocumentNumber;
            DialogResult = true;
        }
        catch (Exception exception) { Summary.ShowMessage(UserError.Message(exception)); PostButton.IsEnabled = true; }
    }

    public string? DocumentNumber { get; private set; }

    private sealed class ReturnEntry : INotifyPropertyChanged
    {
        private string _quantityText = "";
        private string _beforeTaxText = "";
        private string _taxText = "";
        public ReturnEntry(VendorReturnLot lot) { Lot = lot; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public VendorReturnLot Lot { get; }
        public string ProductName => Lot.ProductName;
        public string LotCode => Lot.LotCode;
        public string Received => Lot.Received.Value.ToString("0.###", Digits);
        public string AlreadyReturned => Lot.AlreadyReturned.Value.ToString("0.###", Digits);
        public string OnHand => Lot.OnHand.Value.ToString("0.###", Digits);
        public string Returnable => Lot.Returnable.Value.ToString("0.###", Digits);
        public string QuantityText
        {
            get => _quantityText;
            set
            {
                _quantityText = value;
                PropertyChanged?.Invoke(this, new(nameof(QuantityText)));
                if (!decimal.TryParse(InputNormalizer.Normalize(value), NumberStyles.Number, Digits,
                        out decimal parsed) || parsed <= 0 || Lot.Received <= Quantity.Zero) return;
                Quantity quantity = new(parsed);
                bool final = quantity == Lot.Received - Lot.AlreadyReturned;
                Money remainingBefore = Lot.OriginalBeforeTax - Lot.CreditedBeforeTax;
                Money remainingTax = Lot.OriginalTax - Lot.CreditedTax;
                Money before = final ? remainingBefore :
                    new Money(Lot.OriginalBeforeTax.Value * quantity.Value / Lot.Received.Value);
                Money tax = final ? remainingTax :
                    new Money(Lot.OriginalTax.Value * quantity.Value / Lot.Received.Value);
                BeforeTaxText = (before > remainingBefore ? remainingBefore : before).Value.ToString("0.###", Digits);
                TaxText = (tax > remainingTax ? remainingTax : tax).Value.ToString("0.###", Digits);
            }
        }
        public string BeforeTaxText { get => _beforeTaxText; set { _beforeTaxText = value; PropertyChanged?.Invoke(this, new(nameof(BeforeTaxText))); } }
        public string TaxText { get => _taxText; set { _taxText = value; PropertyChanged?.Invoke(this, new(nameof(TaxText))); } }
    }
}
