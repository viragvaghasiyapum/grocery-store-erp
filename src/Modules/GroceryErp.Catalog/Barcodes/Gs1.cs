using System.Globalization;

namespace GroceryErp.Catalog.Barcodes;

public sealed record Gs1Data(string? Gtin, DateOnly? Expiry, string? Batch, string? Serial);

public static class Gs1
{
    private const char GroupSeparator = '\u001d';

    public static bool TryParse(string raw, out Gs1Data result)
    {
        result = new(null, null, null, null);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string value = raw.Trim();
        if (value.StartsWith(']'))
        {
            if (!(value.StartsWith("]d2", StringComparison.Ordinal) ||
                  value.StartsWith("]C1", StringComparison.Ordinal) ||
                  value.StartsWith("]Q3", StringComparison.Ordinal))) return false;
            value = value[3..];
        }
        bool parenthesized = value.StartsWith("(01)", StringComparison.Ordinal);
        if (!parenthesized && (value.Length < 16 || !value.StartsWith("01", StringComparison.Ordinal))) return false;

        string? gtin = null;
        DateOnly? expiry = null;
        string? batch = null;
        string? serial = null;
        int position = 0;
        while (position < value.Length)
        {
            string? ai = ReadAi(value, ref position, parenthesized);
            if (ai is null) return false;
            switch (ai)
            {
                case "01":
                    if (!ReadFixed(value, ref position, 14, out gtin) || !Ean.IsValid(gtin)) return false;
                    break;
                case "17":
                    if (!ReadFixed(value, ref position, 6, out string? date) || date is null || !TryDate(date, out expiry)) return false;
                    break;
                case "10":
                    batch = ReadVariable(value, ref position, parenthesized, 20);
                    if (batch.Length == 0) return false;
                    break;
                case "21":
                    serial = ReadVariable(value, ref position, parenthesized, 20);
                    if (serial.Length == 0) return false;
                    break;
                default:
                    return false;
            }
        }

        if (gtin is null) return false;
        result = new(gtin, expiry, batch, serial);
        return true;
    }

    private static string? ReadAi(string value, ref int position, bool parenthesized)
    {
        if (parenthesized)
        {
            if (position + 4 > value.Length || value[position] != '(' || value[position + 3] != ')') return null;
            string ai = value.Substring(position + 1, 2);
            position += 4;
            return ai;
        }
        if (position + 2 > value.Length) return null;
        string rawAi = value.Substring(position, 2);
        position += 2;
        return rawAi;
    }

    private static bool ReadFixed(string value, ref int position, int length, out string? field)
    {
        field = null;
        if (position + length > value.Length) return false;
        field = value.Substring(position, length);
        if (field.Any(static character => !char.IsAsciiDigit(character))) return false;
        position += length;
        return true;
    }

    private static string ReadVariable(string value, ref int position, bool parenthesized, int maximum)
    {
        int start = position;
        int length;
        if (parenthesized)
        {
            int next = value.IndexOf('(', start);
            length = (next < 0 ? value.Length : next) - start;
        }
        else
        {
            int separator = value.IndexOf(GroupSeparator, start);
            length = (separator < 0 ? value.Length : separator) - start;
        }
        if (length > maximum) return string.Empty;
        position += length;
        if (!parenthesized && position < value.Length && value[position] == GroupSeparator) position++;
        return value.Substring(start, length);
    }

    private static bool TryDate(string value, out DateOnly? date)
    {
        date = null;
        if (!int.TryParse(value.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
            !int.TryParse(value.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int month) ||
            !int.TryParse(value.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int day) ||
            month is < 1 or > 12) return false;
        int fullYear = 2000 + year;
        if (day == 0) day = DateTime.DaysInMonth(fullYear, month);
        if (day > DateTime.DaysInMonth(fullYear, month)) return false;
        date = new DateOnly(fullYear, month, day);
        return true;
    }
}
