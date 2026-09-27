using System.Globalization;
using System.Text;

namespace GroceryErp.Printing;

public sealed record TsplLabel(
    string ProductName,
    string? LocalName,
    string Barcode,
    decimal Price,
    decimal? Mrp,
    int WidthMm = 60,
    int HeightMm = 40,
    int Copies = 1);

public static class TsplLabelBuilder
{
    public static byte[] Build(TsplLabel label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (label.WidthMm is < 20 or > 120 || label.HeightMm is < 15 or > 100)
            throw new ArgumentOutOfRangeException(nameof(label), "Label dimensions are outside the supported range.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(label.Copies);
        string name = Safe(label.ProductName, 34);
        string localName = Safe(label.LocalName, 34);
        string barcode = SafeBarcode(label.Barcode);
        string price = label.Price.ToString("0.00", CultureInfo.InvariantCulture);
        string mrp = label.Mrp is null ? string.Empty : $"MRP {label.Mrp.Value.ToString("0.00", CultureInfo.InvariantCulture)}";

        var command = new StringBuilder(256)
            .Append("SIZE ").Append(label.WidthMm).Append(" mm,").Append(label.HeightMm).Append(" mm\r\n")
            .Append("GAP 2 mm,0 mm\r\nDIRECTION 1\r\nCLS\r\n")
            .Append("TEXT 24,18,\"0\",0,1,1,\"").Append(name).Append("\"\r\n");
        if (localName.Length > 0)
            command.Append("TEXT 24,48,\"0\",0,1,1,\"").Append(localName).Append("\"\r\n");
        command.Append("BARCODE 24,82,\"128\",72,1,0,2,2,\"").Append(barcode).Append("\"\r\n")
            .Append("TEXT 24,170,\"0\",0,2,2,\"Rs ").Append(price).Append("\"\r\n");
        if (mrp.Length > 0)
            command.Append("TEXT 260,176,\"0\",0,1,1,\"").Append(mrp).Append("\"\r\n");
        command.Append("PRINT 1,").Append(label.Copies).Append("\r\n");
        return Encoding.UTF8.GetBytes(command.ToString());
    }

    private static string Safe(string? value, int maximum)
    {
        string safe = (value ?? string.Empty).Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length <= maximum ? safe : safe[..maximum];
    }

    private static string SafeBarcode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(static character => character is < ' ' or > '~' or '"'))
            throw new ArgumentException("Barcode contains unsupported printer characters.", nameof(value));
        return value;
    }
}
