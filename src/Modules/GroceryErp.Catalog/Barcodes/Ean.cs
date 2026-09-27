using System.Globalization;

namespace GroceryErp.Catalog.Barcodes;

public static class Ean
{
    public static int CheckDigit(ReadOnlySpan<char> body)
    {
        if (body.IsEmpty || !AllDigits(body))
            throw new ArgumentException("EAN bodies must contain only digits.", nameof(body));

        int sum = 0;
        for (int index = body.Length - 1, weight = 3; index >= 0; index--, weight = 4 - weight)
            sum += (body[index] - '0') * weight;
        return (10 - (sum % 10)) % 10;
    }

    public static bool IsValid(ReadOnlySpan<char> code) =>
        code.Length is 8 or 12 or 13 or 14 &&
        AllDigits(code) &&
        CheckDigit(code[..^1]) == code[^1] - '0';

    public static string Internal(string prefix, long serial)
    {
        if (prefix.Length != 2 || prefix[0] != '2' || !char.IsAsciiDigit(prefix[1]))
            throw new ArgumentException("Prefix must be between 20 and 29.", nameof(prefix));
        ArgumentOutOfRangeException.ThrowIfNegative(serial);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(serial, 9_999_999_999L);

        string body = prefix + serial.ToString("D10", CultureInfo.InvariantCulture);
        return body + CheckDigit(body).ToString(CultureInfo.InvariantCulture);
    }

    private static bool AllDigits(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
            if (!char.IsAsciiDigit(character)) return false;
        return true;
    }
}
