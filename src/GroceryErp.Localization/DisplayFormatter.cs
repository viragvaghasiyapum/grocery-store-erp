using System.Globalization;

namespace GroceryErp.Localization;

/// <summary>Formats Phase 0 demonstration values using English digits and Indian grouping.</summary>
public sealed class DisplayFormatter
{
    private readonly CultureInfo _formatCulture = TranslationSource.FormatCulture;
    private readonly CultureInfo _invariantCulture = CultureInfo.InvariantCulture;

    /// <summary>Formats a decimal value with three places and Indian grouping.</summary>
    public string Number(decimal value) => value.ToString("N3", _formatCulture);

    /// <summary>Formats a date with invariant English digits.</summary>
    public string Date(DateOnly value) => value.ToString("dd-MM-yyyy", _invariantCulture);

    /// <summary>Formats a localized month name while keeping the year in English digits.</summary>
    public string MonthYear(DateOnly value, CultureInfo uiCulture) =>
        $"{uiCulture.DateTimeFormat.GetAbbreviatedMonthName(value.Month)} {value.Year.ToString(_invariantCulture)}";
}
