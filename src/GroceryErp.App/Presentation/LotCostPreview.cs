using System.Globalization;
using System.Windows.Controls;
using GroceryErp.Catalog;
using GroceryErp.Core;
using GroceryErp.Core.Numerics;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Keeps a lot form's read-only Tax/unit and Purchase price incl. tax in step with its typed
/// purchase price (before tax, one piece) and chosen tax rate, as the direct lot form and a new
/// product's first lot show them. The service saves the same values when the lot is created.
/// </summary>
internal sealed class LotCostPreview
{
    private readonly TextBox _priceBox;
    private readonly ComboBox _taxBox;
    private readonly TextBox _unitTaxBox;
    private readonly TextBox _inclTaxBox;

    public LotCostPreview(TextBox priceBox, ComboBox taxBox, TextBox unitTaxBox, TextBox inclTaxBox)
    {
        _priceBox = priceBox;
        _taxBox = taxBox;
        _unitTaxBox = unitTaxBox;
        _inclTaxBox = inclTaxBox;
        _priceBox.TextChanged += (_, _) => Refresh();
        _taxBox.SelectionChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Offers the active rates and selects the product's rate when it is one of them.</summary>
    public void Load(IReadOnlyList<TaxRateItem> rates, long? productRateId)
    {
        ArgumentNullException.ThrowIfNull(rates);
        List<TaxRateItem> active = rates.Where(static rate => rate.IsActive).ToList();
        _taxBox.ItemsSource = active;
        _taxBox.SelectedValue = productRateId is { } id && active.Any(rate => rate.Id == id) ? id : null;
        Refresh();
    }

    public void Refresh()
    {
        string text = InputNormalizer.Normalize(_priceBox.Text ?? string.Empty).Trim();
        if (_taxBox.SelectedItem is not TaxRateItem rate ||
            !decimal.TryParse(text, NumberStyles.Number, TranslationSource.FormatCulture, out decimal typed) || typed < 0)
        {
            _unitTaxBox.Text = "—";
            _inclTaxBox.Text = "—";
            return;
        }
        PurchaseUnitCost cost = LandedCost.FromBeforeTax(new Money(typed), new Percent(rate.Rate), taxIsCost: false);
        _unitTaxBox.Text = AmountConverter.Text(TwoDecimals.Remainder(cost.InclTax, cost.BeforeTax));
        _inclTaxBox.Text = AmountConverter.Text(TwoDecimals.Round(cost.InclTax));
    }
}
