using System.Globalization;
using System.IO;
using System.Text;

namespace GroceryErp.Printing;

public enum TsplLabelTemplate { Price, BarcodePrice, ShelfEdge }

public sealed record TsplLabel(
    string ProductName,
    string? LocalName,
    string Barcode,
    decimal Price,
    decimal? Mrp,
    int WidthMm = 60,
    int HeightMm = 40,
    int Copies = 1,
    TsplLabelTemplate Template = TsplLabelTemplate.BarcodePrice);

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
        using var output = new MemoryStream();
        output.Write(Encoding.UTF8.GetBytes(command.ToString()));
        if (localName.Length > 0) output.Write(TsplRasterText.Render(localName, 24, 48));
        command.Clear();
        switch (label.Template)
        {
            case TsplLabelTemplate.Price:
                command.Append("BARCODE 24,82,\"128\",48,1,0,2,2,\"").Append(barcode).Append("\"\r\n")
                    .Append("TEXT 24,145,\"0\",0,3,3,\"Rs ").Append(price).Append("\"\r\n");
                if (mrp.Length > 0) command.Append("TEXT 260,182,\"0\",0,1,1,\"").Append(mrp).Append("\"\r\n");
                break;
            case TsplLabelTemplate.ShelfEdge:
                command.Append("TEXT 24,88,\"0\",0,2,2,\"Rs ").Append(price).Append("\"\r\n")
                    .Append("BARCODE 24,130,\"128\",46,1,0,2,2,\"").Append(barcode).Append("\"\r\n");
                if (mrp.Length > 0) command.Append("TEXT 260,182,\"0\",0,1,1,\"").Append(mrp).Append("\"\r\n");
                break;
            case TsplLabelTemplate.BarcodePrice:
                command.Append("BARCODE 24,82,\"128\",72,1,0,2,2,\"").Append(barcode).Append("\"\r\n")
                    .Append("TEXT 24,170,\"0\",0,2,2,\"Rs ").Append(price).Append("\"\r\n");
                if (mrp.Length > 0) command.Append("TEXT 260,176,\"0\",0,1,1,\"").Append(mrp).Append("\"\r\n");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(label), "Unsupported label template.");
        }
        command.Append("PRINT 1,").Append(label.Copies).Append("\r\n");
        output.Write(Encoding.UTF8.GetBytes(command.ToString()));
        return output.ToArray();
    }

    public static byte[] BuildBatch(IEnumerable<TsplLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        using var output = new MemoryStream();
        int count = 0;
        foreach (TsplLabel label in labels)
        {
            int remaining = label.Copies;
            if (remaining <= 0) throw new ArgumentOutOfRangeException(nameof(labels), "Copies must be positive.");
            while (remaining > 0)
            {
                int copies = Math.Min(remaining, 999);
                output.Write(Build(label with { Copies = copies }));
                remaining -= copies;
            }
            count++;
        }
        if (count == 0) throw new ArgumentException("Select at least one label.", nameof(labels));
        return output.ToArray();
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
