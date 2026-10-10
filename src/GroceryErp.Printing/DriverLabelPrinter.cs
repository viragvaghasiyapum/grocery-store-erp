using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GroceryErp.Printing;

/// <summary>Prints a TSPL label model through a Windows printer driver.</summary>
public static class DriverLabelPrinter
{
    public static void Print(string printerName, IReadOnlyList<TsplLabel> labels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Count == 0) throw new ArgumentException("Select at least one label.", nameof(labels));
        using var server = new LocalPrintServer();
        using PrintQueue queue = server.GetPrintQueue(printerName);
        var dialog = new PrintDialog { PrintQueue = queue };
        foreach (TsplLabel label in labels)
        {
            double width = label.WidthMm * 96.0 / 25.4;
            double height = label.HeightMm * 96.0 / 25.4;
            dialog.PrintTicket = new PrintTicket { PageMediaSize = new PageMediaSize(width, height) };
            DrawingVisual visual = Render(label, width, height);
            for (int copy = 0; copy < label.Copies; copy++)
                dialog.PrintVisual(visual, "Product label");
        }
    }

    public static DrawingVisual Render(TsplLabel label, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(label);
        var visual = new DrawingVisual();
        using DrawingContext drawing = visual.RenderOpen();
        drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        double inset = 8;
        void Text(string value, double y, double size, FontWeight weight)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var formatted = new FormattedText(value, CultureInfo.GetCultureInfo("en-IN"),
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Nirmala UI"), FontStyles.Normal, weight, FontStretches.Normal),
                size, Brushes.Black, 1)
            { MaxTextWidth = Math.Max(1, width - 2 * inset), MaxTextHeight = Math.Max(1, height - y - 2) };
            drawing.DrawText(formatted, new Point(inset, y));
        }
        Text(label.ProductName, 5, 11, FontWeights.SemiBold);
        Text(label.LocalName ?? "", 23, 10, FontWeights.Normal);
        double barcodeTop = label.Template == TsplLabelTemplate.ShelfEdge ? 63 : 44;
        double barcodeHeight = Math.Max(18, height - barcodeTop - 29);
        drawing.DrawImage(BarcodePreview.Create(label.Barcode, 360, 72),
            new Rect(inset, barcodeTop, width - 2 * inset, barcodeHeight));
        Text(label.Barcode, barcodeTop + barcodeHeight, 8, FontWeights.Normal);
        Text("Rs " + label.Price.ToString("0.00", CultureInfo.InvariantCulture),
            Math.Max(0, height - 20), 13, FontWeights.Bold);
        if (label.Mrp is { } mrp && width > 145)
        {
            var formatted = new FormattedText("MRP " + mrp.ToString("0.00", CultureInfo.InvariantCulture),
                CultureInfo.GetCultureInfo("en-IN"), FlowDirection.LeftToRight,
                new Typeface("Nirmala UI"), 8, Brushes.Black, 1);
            drawing.DrawText(formatted, new Point(Math.Max(inset, width - formatted.Width - inset),
                Math.Max(0, height - 17)));
        }
        return visual;
    }
}
