using System.Text.RegularExpressions;

namespace GroceryErp.Core.Validation;

public static partial class IndianIds
{
    [GeneratedRegex("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$", RegexOptions.CultureInvariant)]
    private static partial Regex GstinPattern();

    [GeneratedRegex("^[A-Z]{5}[0-9]{4}[A-Z]$", RegexOptions.CultureInvariant)]
    private static partial Regex PanPattern();

    [GeneratedRegex("^[1-9][0-9]{5}$", RegexOptions.CultureInvariant)]
    private static partial Regex PinPattern();

    public static bool IsGstin(string value) => !string.IsNullOrWhiteSpace(value) && GstinPattern().IsMatch(value);
    public static bool IsPan(string value) => !string.IsNullOrWhiteSpace(value) && PanPattern().IsMatch(value);
    public static bool IsPincode(string value) => !string.IsNullOrWhiteSpace(value) && PinPattern().IsMatch(value);
}
