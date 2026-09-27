using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Printing;

namespace GroceryErp.Sales;

public sealed class ReceiptPrinter(ISettings settings)
{
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-IN");

    public void Print(ReceiptData receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        string printer = settings.Current.ReceiptPrinterName;
        if (string.IsNullOrWhiteSpace(printer))
            throw new InvalidOperationException("A receipt printer has not been configured.");
        byte[] raster = Render(receipt, settings.Current.ReceiptPrinterWidthDots,
            settings.Current.PrintBilingualNames);
        RawPrinter.Send(printer, raster, $"Receipt {receipt.InvoiceNumber}");
    }

    public void OpenDrawer()
    {
        string printer = settings.Current.ReceiptPrinterName;
        if (string.IsNullOrWhiteSpace(printer))
            throw new InvalidOperationException("A receipt printer has not been configured.");
        RawPrinter.Send(printer, [0x1B, 0x70, 0x00, 0x19, 0xFA], "Open cash drawer");
    }

    public static byte[] Render(ReceiptData receipt, int widthDots, bool bilingualNames)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (widthDots is < 200 or > 2400) throw new ArgumentOutOfRangeException(nameof(widthDots));
        var lines = new List<(string Text, double Size, FontWeight Weight, TextAlignment Alignment)>();
        CultureInfo printCulture = CultureInfo.GetCultureInfo(receipt.PrintLanguage);
        string T(string key) => SharedStrings.ResourceManager.GetString(key, printCulture) ?? key;
        void Add(string text, double size = 18, FontWeight? weight = null,
            TextAlignment alignment = TextAlignment.Left)
        {
            if (!string.IsNullOrWhiteSpace(text)) lines.Add((text, size, weight ?? FontWeights.Normal, alignment));
        }
        Add(receipt.StoreName, 27, FontWeights.Bold, TextAlignment.Center);
        Add(receipt.StoreAddress, 16, null, TextAlignment.Center);
        Add(receipt.StorePhone, 16, null, TextAlignment.Center);
        Add(string.IsNullOrWhiteSpace(receipt.StoreGstin) ? "" : $"GSTIN: {receipt.StoreGstin}", 16, null, TextAlignment.Center);
        string title = receipt.TaxMode switch
        {
            "REGULAR" => T("Receipt_TaxInvoice"),
            "COMPOSITION" => T("Receipt_BillOfSupply"),
            _ => T("Receipt_Bill"),
        };
        Add(title, 23, FontWeights.Bold, TextAlignment.Center);
        if (receipt.Duplicate) Add(T("Receipt_Duplicate"), 20, FontWeights.Bold, TextAlignment.Center);
        Add($"{receipt.InvoiceNumber}   {receipt.CompletedAt.ToString("dd-MM-yyyy HH:mm", Digits)}");
        Add(receipt.CustomerName is null ? "" : $"{T("Receipt_Customer")}: {receipt.CustomerName}");
        Add(new string('-', 48));
        foreach (ReceiptLine line in receipt.Lines)
        {
            Add(line.Name, 19, FontWeights.SemiBold);
            if (bilingualNames) Add(line.LocalName ?? "", 17);
            Add($"{line.Quantity.Value.ToString("0.###", Digits)} × ₹{line.UnitPrice.Value.ToString("N3", Digits)}" +
                $"    ₹{line.Total.Value.ToString("N3", Digits)}");
            if (line.Discount.Value > 0)
                Add($"{T("Receipt_Discount")} ₹{line.Discount.Value.ToString("N3", Digits)}", 16);
        }
        Add(new string('-', 48));
        Add($"{T("Receipt_Gross")}                 ₹{receipt.Gross.Value.ToString("N3", Digits)}");
        if (receipt.Discount.Value > 0) Add($"{T("Receipt_Discount")}             -₹{receipt.Discount.Value.ToString("N3", Digits)}");
        if (receipt.RoundOff != GroceryErp.Core.Numerics.Money.Zero)
            Add($"{T("Receipt_RoundOff")}              ₹{receipt.RoundOff.Value.ToString("N3", Digits)}");
        Add($"{T("Receipt_Total")}                  ₹{receipt.GrandTotal.Value.ToString("N3", Digits)}", 25, FontWeights.Bold);
        if (receipt.Savings.Value > 0)
            Add($"{T("Receipt_Savings")} ₹{receipt.Savings.Value.ToString("N3", Digits)}", 19, FontWeights.SemiBold);
        foreach (PaymentInput payment in receipt.Payments)
            Add($"{payment.Mode}: ₹{payment.Amount.Value.ToString("N3", Digits)}");
        if (receipt.Change.Value > 0) Add($"{T("Receipt_Change")}: ₹{receipt.Change.Value.ToString("N3", Digits)}");
        if (receipt.TaxMode == "REGULAR")
        {
            Add(new string('-', 48));
            foreach (IGrouping<decimal, ReceiptLine> group in receipt.Lines.GroupBy(static line => line.TaxRate.Value))
            {
                var taxable = group.Sum(static line => line.Taxable.Value);
                var tax = group.Sum(static line => line.Tax.Value);
                Add($"GST {group.Key.ToString("0.###", Digits)}%  Taxable ₹{taxable.ToString("N3", Digits)}  " +
                    $"CGST ₹{(tax / 2m).ToString("N3", Digits)}  SGST ₹{(tax - tax / 2m).ToString("N3", Digits)}", 15);
            }
        }
        else if (receipt.TaxMode == "COMPOSITION")
            Add(T("Receipt_CompositionNotice"), 15);
        Add(receipt.Footer, 17, null, TextAlignment.Center);
        Add(receipt.InvoiceNumber, 19, FontWeights.Bold, TextAlignment.Center);

        const double padding = 8;
        var typeface = new Typeface(new FontFamily("Nirmala UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var visual = new DrawingVisual();
        double y = padding;
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, widthDots, 10000));
            foreach ((string text, double size, FontWeight weight, TextAlignment alignment) in lines)
            {
                var formatted = new FormattedText(text, Digits, FlowDirection.LeftToRight,
                    new Typeface(typeface.FontFamily, typeface.Style, weight, typeface.Stretch),
                    size, Brushes.Black, 1)
                {
                    MaxTextWidth = widthDots - padding * 2,
                    TextAlignment = alignment,
                };
                drawing.DrawText(formatted, new Point(padding, y));
                y += formatted.Height + 3;
            }
        }
        int height = checked((int)Math.Ceiling(y + 24));
        var bitmap = new RenderTargetBitmap(widthDots, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        int stride = widthDots * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        int bytesWide = (widthDots + 7) / 8;
        byte[] command = new byte[8 + bytesWide * height + 6];
        command[0] = 0x1D; command[1] = 0x76; command[2] = 0x30; command[3] = 0;
        command[4] = (byte)(bytesWide & 0xFF); command[5] = (byte)(bytesWide >> 8);
        command[6] = (byte)(height & 0xFF); command[7] = (byte)(height >> 8);
        for (int row = 0; row < height; row++)
            for (int column = 0; column < widthDots; column++)
            {
                int pixel = row * stride + column * 4;
                if (pixels[pixel] < 160)
                    command[8 + row * bytesWide + column / 8] |= (byte)(0x80 >> column % 8);
            }
        command[^6] = 0x1B; command[^5] = 0x64; command[^4] = 0x04;
        command[^3] = 0x1D; command[^2] = 0x56; command[^1] = 0x00;
        return command;
    }
}
