using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Printing;

namespace GroceryErp.Sales;

public sealed class ReceiptPrinter(ISettings settings)
{
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-IN");

    public Task PrintAsync(ReceiptData receipt)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Print(receipt); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    public Task OpenDrawerAsync() => Task.Run(OpenDrawer);

    public Task<bool> IsReadyAsync() => Task.Run(() =>
    {
        string name = settings.Current.ReceiptPrinterName;
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            using var server = new LocalPrintServer();
            using PrintQueue queue = server.GetPrintQueue(name);
            queue.Refresh();
            return !queue.IsOffline && !queue.IsPaused && !queue.IsOutOfPaper && !queue.IsDoorOpened;
        }
        catch (PrintQueueException) { return false; }
        catch (PrintServerException) { return false; }
    });

    public Task PrintDayEndAsync(DayEndSummary summary)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                string printer = settings.Current.ReceiptPrinterName;
                if (string.IsNullOrWhiteSpace(printer))
                    throw new InvalidOperationException("A receipt printer has not been configured.");
                byte[] raster = RenderDayEnd(summary, settings.Current.ReceiptPrinterWidthDots,
                    settings.Current.StoreName, settings.Current.PrintLanguage);
                RawPrinter.Send(printer, raster, $"Day end {summary.Date:yyyy-MM-dd}");
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    public void Print(ReceiptData receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        string printer = settings.Current.ReceiptPrinterName;
        if (string.IsNullOrWhiteSpace(printer))
            throw new InvalidOperationException("A receipt printer has not been configured.");
        byte[] raster = Render(receipt,
            receipt.WidthDots > 0 ? receipt.WidthDots : settings.Current.ReceiptPrinterWidthDots,
            receipt.BilingualNames ?? settings.Current.PrintBilingualNames);
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
            "RETURN" => T("Receipt_Return"),
            _ => T("Receipt_Bill"),
        };
        Add(title, 23, FontWeights.Bold, TextAlignment.Center);
        if (receipt.Duplicate) Add(T("Receipt_Duplicate"), 20, FontWeights.Bold, TextAlignment.Center);
        Add($"{receipt.InvoiceNumber}   {receipt.CompletedAt.ToString("dd-MM-yyyy HH:mm", Digits)}");
        if (receipt.OriginalInvoiceNumber is { } original)
            Add($"{T("Receipt_OriginalInvoice")}: {original}");
        if (receipt.ReturnReason is { } reason)
            Add($"{T("Receipt_ReturnReason")}: {reason}");
        Add(receipt.CustomerName is null ? "" : $"{T("Receipt_Customer")}: {receipt.CustomerName}");
        Add(new string('-', 48));
        foreach (ReceiptLine line in receipt.Lines)
        {
            Add(line.Name, 19, FontWeights.SemiBold);
            if (bilingualNames) Add(line.LocalName ?? "", 17);
            Add($"{line.Quantity.Value.ToString("0.###", Digits)} × ₹{line.UnitPrice.Value.ToString("N3", Digits)}" +
                $"    ₹{line.Total.Value.ToString("N3", Digits)}");
            if (line.Mrp is { } mrp)
                Add($"MRP ₹{mrp.Value.ToString("N3", Digits)}", 15);
            if (line.Discount.Value > 0)
                Add($"{T("Receipt_Discount")} ₹{line.Discount.Value.ToString("N3", Digits)}", 16);
        }
        Add(new string('-', 48));
        Add($"{T("Receipt_Gross")}                 ₹{receipt.Gross.Value.ToString("N3", Digits)}");
        if (receipt.Discount.Value > 0) Add($"{T("Receipt_Discount")}             -₹{receipt.Discount.Value.ToString("N3", Digits)}");
        if (receipt.RoundOff != GroceryErp.Core.Numerics.Money.Zero)
            Add($"{T("Receipt_RoundOff")}              ₹{receipt.RoundOff.Value.ToString("N3", Digits)}");
        Add($"{T(receipt.TaxMode == "RETURN" ? "Receipt_RefundTotal" : "Receipt_Total")}                  ₹{receipt.GrandTotal.Value.ToString("N3", Digits)}", 25, FontWeights.Bold);
        if (receipt.Savings.Value > 0)
            Add($"{T("Receipt_Savings")} ₹{receipt.Savings.Value.ToString("N3", Digits)}", 19, FontWeights.SemiBold);
        if (receipt.TaxMode == "RETURN")
        {
            Add($"{T("Receipt_Taxable")}: ₹{receipt.Taxable.Value.ToString("N3", Digits)}");
            Add($"{T("Receipt_Gst")}: ₹{receipt.Tax.Value.ToString("N3", Digits)}");
            string modeKey = receipt.RefundMethod switch
            {
                "CASH" => "Receipt_RefundModeCash",
                "UPI" => "Receipt_RefundModeUpi",
                "CARD" => "Receipt_RefundModeCard",
                "CREDIT_NOTE" => "Receipt_RefundModeCreditNote",
                _ => "Receipt_RefundModeOther",
            };
            Add($"{T("Receipt_RefundBy")}: {T(modeKey)}");
        }
        else
            foreach (PaymentInput payment in receipt.Payments)
            {
                string modeKey = payment.Mode switch
                {
                    PaymentMode.Cash => "Receipt_RefundModeCash",
                    PaymentMode.Upi => "Receipt_RefundModeUpi",
                    PaymentMode.Card => "Receipt_RefundModeCard",
                    _ => "Receipt_RefundModeOther",
                };
                Add($"{T(modeKey)}: ₹{payment.Amount.Value.ToString("N3", Digits)}");
            }
        if (receipt.Change.Value > 0) Add($"{T("Receipt_Change")}: ₹{receipt.Change.Value.ToString("N3", Digits)}");
        if (receipt.TaxMode == "REGULAR")
        {
            Add(new string('-', 48));
            foreach (IGrouping<decimal, ReceiptLine> group in receipt.Lines.GroupBy(static line => line.TaxRate.Value))
            {
                var taxable = group.Sum(static line => line.Taxable.Value);
                var tax = group.Sum(static line => line.Tax.Value);
                var cgst = new GroceryErp.Core.Numerics.Money(tax / 2m);
                var sgst = new GroceryErp.Core.Numerics.Money(tax - cgst.Value);
                Add($"GST {group.Key.ToString("0.###", Digits)}%  {T("Receipt_Taxable")} ₹{taxable.ToString("N3", Digits)}  " +
                    $"CGST ₹{cgst.Value.ToString("N3", Digits)}  SGST ₹{sgst.Value.ToString("N3", Digits)}", 15);
            }
        }
        else if (receipt.TaxMode == "COMPOSITION")
            Add(T("Receipt_CompositionNotice"), 15);
        Add(receipt.Footer, 17, null, TextAlignment.Center);
        Add(receipt.InvoiceNumber, 19, FontWeights.Bold, TextAlignment.Center);

        return Rasterize(lines, widthDots, receipt.InvoiceNumber);
    }

    public static byte[] RenderDayEnd(DayEndSummary summary, int widthDots, string storeName, string printLanguage)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var lines = new List<(string Text, double Size, FontWeight Weight, TextAlignment Alignment)>();
        CultureInfo printCulture = CultureInfo.GetCultureInfo(printLanguage);
        string T(string key) => SharedStrings.ResourceManager.GetString(key, printCulture) ?? key;
        void Add(string text, double size = 18, FontWeight? weight = null,
            TextAlignment alignment = TextAlignment.Left) =>
            lines.Add((text, size, weight ?? FontWeights.Normal, alignment));
        string Amount(Money value) => $"₹{value.Value.ToString("N3", Digits)}";
        Add(storeName, 25, FontWeights.Bold, TextAlignment.Center);
        Add(T("Sales_DayEnd"), 23, FontWeights.Bold, TextAlignment.Center);
        Add(summary.Date.ToString("dd-MM-yyyy", Digits), 18, null, TextAlignment.Center);
        Add(new string('-', 48));
        Add($"{T("Sales_Bills")}: {summary.BillCount.ToString(Digits)}");
        Add($"{T("Sales_Gross")}: {Amount(summary.Gross)}");
        Add($"{T("Purchasing_Discount")}: {Amount(summary.Discounts)}");
        Add($"{T("Receipt_Taxable")}: {Amount(summary.Net)}");
        Add($"{T("Receipt_Gst")}: {Amount(summary.Tax)}");
        Add($"{T("Sales_RoundOff")}: {Amount(summary.RoundOff)}");
        Add($"{T("Sales_Payable")}: {Amount(summary.Payable)}", 20, FontWeights.Bold);
        Add(new string('-', 48));
        foreach (PaymentTotal payment in summary.Payments)
        {
            string key = payment.Mode switch
            {
                PaymentMode.Cash => "Receipt_RefundModeCash",
                PaymentMode.Upi => "Receipt_RefundModeUpi",
                PaymentMode.Card => "Receipt_RefundModeCard",
                _ => "Receipt_RefundModeOther",
            };
            Add($"{T(key)}: {Amount(payment.Amount)}");
        }
        Add($"{T("Sales_Voids")}: {summary.VoidCount.ToString(Digits)} / {Amount(summary.VoidValue)}");
        Add($"{T("Sales_ReturnsTab")}: {summary.ReturnCount.ToString(Digits)} / {Amount(summary.ReturnValue)}");
        Add(new string('-', 48));
        Add($"{T("Sales_CashSales")}: {Amount(summary.Payments.FirstOrDefault(static p => p.Mode == PaymentMode.Cash)?.Amount ?? Money.Zero)}");
        Add($"{T("Sales_CashChange")}: -{Amount(summary.CashChange)}");
        Add($"{T("Sales_CashRefunds")}: -{Amount(summary.CashRefunds)}");
        Add($"{T("Sales_CashExpenses")}: -{Amount(summary.CashExpenses)}");
        Add($"{T("Sales_CashExpected")}: {Amount(summary.CashExpected)}", 22, FontWeights.Bold);
        return Rasterize(lines, widthDots, null);
    }

    private static byte[] Rasterize(
        IReadOnlyList<(string Text, double Size, FontWeight Weight, TextAlignment Alignment)> lines,
        int widthDots, string? barcodeText)
    {
        if (widthDots is < 200 or > 2400) throw new ArgumentOutOfRangeException(nameof(widthDots));
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
            if (barcodeText is not null)
            {
                BitmapSource barcode = BarcodePreview.Create(barcodeText,
                    Math.Min(widthDots - 16, 400), 70);
                drawing.DrawImage(barcode, new Rect((widthDots - barcode.Width) / 2, y + 8, barcode.Width, barcode.Height));
                y += barcode.Height + 12;
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
