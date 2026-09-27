using System.Globalization;
using GroceryErp.Core.Numerics;

namespace GroceryErp.Catalog.Barcodes;

public enum BarcodeValueKind { Weight, Price }

public sealed record BarcodeRule(
    string Prefix,
    int TotalLength,
    int ItemStart,
    int ItemLength,
    int ValueStart,
    int ValueLength,
    BarcodeValueKind Kind,
    long ValueScale)
{
    public bool TryParse(string code, out string plu, out decimal value)
    {
        ArgumentNullException.ThrowIfNull(code);
        plu = string.Empty;
        value = 0;
        if (code.Length != TotalLength || !code.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (TotalLength == 13 && !Ean.IsValid(code)) return false;
        if (!ValidSlice(ItemStart, ItemLength) || !ValidSlice(ValueStart, ValueLength)) return false;

        ReadOnlySpan<char> item = code.AsSpan(ItemStart - 1, ItemLength);
        ReadOnlySpan<char> encodedValue = code.AsSpan(ValueStart - 1, ValueLength);
        if (!long.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            !long.TryParse(encodedValue, NumberStyles.None, CultureInfo.InvariantCulture, out long raw)) return false;

        plu = item.TrimStart('0').ToString();
        if (plu.Length == 0) plu = "0";
        value = Fx3.FromScaled(checked(raw * ValueScale));
        return true;
    }

    private bool ValidSlice(int start, int length) =>
        start > 0 && length > 0 && start - 1 + length <= TotalLength;
}
