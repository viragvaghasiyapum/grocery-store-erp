using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Printing;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Printing;

namespace GroceryErp.Sales;

public sealed class ReceiptPrinter
{
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-IN");
    private static readonly JsonSerializerOptions JobJson = PrintJobJson.Options;
    private readonly Channel<PrintJob> jobs = Channel.CreateBounded<PrintJob>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly ConcurrentQueue<PrintJob> failed = new();
    private readonly ISettings settings;
    private readonly PrintJobJournal? journal;

    public ReceiptPrinter(ISettings settings, PrintJobJournal? journal = null)
    {
        this.settings = settings;
        this.journal = journal;
        journal?.RecoverInterrupted();
        var worker = new Thread(ProcessJobs) { IsBackground = true, Name = "Printer job queue" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public bool HasFailedJobs => !failed.IsEmpty || journal?.ReviewCount() > 0;
    public SavedPrintJob? OldestReviewJob => journal?.OldestReview();

    public async Task PrintLabelsAsync(string printerName, IReadOnlyList<TsplLabel> labels, bool useDriver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        ArgumentNullException.ThrowIfNull(labels);
        TsplLabel[] snapshot = labels.ToArray();
        if (snapshot.Length == 0) throw new ArgumentException("Select at least one label.", nameof(labels));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new SavedPrintJob(useDriver ? "DRIVER_LABELS" : "TSPL_LABELS",
            printerName, "Product labels", JsonSerializer.Serialize(snapshot, JobJson));
        await EnqueueSavedAsync(saved, completion);
    }

    public Task QueueReceiptAsync(ReceiptData receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        EnsureConfigured();
        return EnqueueSavedAsync(SaveReceipt(receipt), null);
    }

    public Task QueueDrawerAsync()
    {
        EnsureConfigured();
        return jobs.Writer.WriteAsync(new PrintJob(OpenDrawer, null, Retryable: false)).AsTask();
    }

    public async Task PrintAsync(ReceiptData receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        EnsureConfigured();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueSavedAsync(SaveReceipt(receipt), completion);
    }

    public async Task OpenDrawerAsync()
    {
        EnsureConfigured();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await jobs.Writer.WriteAsync(new PrintJob(OpenDrawer, completion, Retryable: false));
        await completion.Task;
    }

    public async Task<bool> RetryFailedAsync(long? reviewJobId = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (journal?.ClaimOldestReview(reviewJobId) is { } saved)
            await jobs.Writer.WriteAsync(new PrintJob(() => SendSaved(saved, retry: true), completion, saved.Id));
        else if (reviewJobId is not null) return false;
        else if (failed.TryDequeue(out PrintJob? previous))
            await jobs.Writer.WriteAsync(new PrintJob(previous.Action, completion));
        else return false;
        await completion.Task;
        return true;
    }

    private async Task EnqueueSavedAsync(SavedPrintJob saved, TaskCompletionSource? completion)
    {
        long id = journal is null ? 0 : await journal.AddAsync(saved);
        await jobs.Writer.WriteAsync(new PrintJob(() => SendSaved(saved), completion, id));
        if (completion is not null) await completion.Task;
    }

    private void ProcessJobs()
    {
        while (jobs.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            while (jobs.Reader.TryRead(out PrintJob? job))
            {
                PerformanceMetrics.PrintQueueMilliseconds.Record(
                    Stopwatch.GetElapsedTime(job.EnqueuedAt).TotalMilliseconds);
                try
                {
                    using IDisposable? printWork = journal?.EnterPrintWork();
                    if (job.JournalId != 0) journal!.MarkSending(job.JournalId);
                    job.Action();
                    if (job.JournalId != 0) journal!.Remove(job.JournalId);
                    job.Completion?.TrySetResult();
                }
                catch (Exception exception)
                {
                    if (job.Retryable)
                    {
                        if (job.JournalId != 0)
                        {
                            try { journal!.MarkFailed(job.JournalId); }
                            catch (Exception journalError) { System.Diagnostics.Trace.TraceError("Could not mark print job failed: {0}", journalError); }
                        }
                        else failed.Enqueue(job);
                    }
                    job.Completion?.TrySetException(exception);
                    System.Diagnostics.Trace.TraceError("Print job failed: {0}", exception);
                }
            }
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(settings.Current.ReceiptPrinterName))
            throw new InvalidOperationException("A receipt printer has not been configured.");
    }

    private sealed record PrintJob(Action Action, TaskCompletionSource? Completion, long JournalId = 0,
        bool Retryable = true)
    {
        public long EnqueuedAt { get; } = Stopwatch.GetTimestamp();
    }

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

    public async Task PrintDayEndAsync(DayEndSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        EnsureConfigured();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueSavedAsync(new SavedPrintJob("DAY_END", settings.Current.ReceiptPrinterName,
            $"Day end {summary.Date:yyyy-MM-dd}",
            JsonSerializer.Serialize(new DayEndPrint(summary, settings.Current.StoreName, settings.Current.PrintLanguage), JobJson),
            WidthDots: settings.Current.ReceiptPrinterWidthDots), completion);
    }

    private SavedPrintJob SaveReceipt(ReceiptData receipt)
    {
        return new SavedPrintJob("RECEIPT", settings.Current.ReceiptPrinterName,
            $"Receipt {receipt.InvoiceNumber}", JsonSerializer.Serialize(receipt, JobJson),
            settings.Current.ReceiptPrinterMode,
            receipt.WidthDots > 0 ? receipt.WidthDots : settings.Current.ReceiptPrinterWidthDots,
            receipt.BilingualNames ?? settings.Current.PrintBilingualNames);
    }

    private static void SendSaved(SavedPrintJob saved, bool retry = false)
    {
        byte[] output = saved.Kind switch
        {
            "RECEIPT" => RenderSavedReceipt(saved, retry),
            "DAY_END" => RenderSavedDayEnd(saved),
            "TSPL_LABELS" => TsplLabelBuilder.BuildBatch(JsonSerializer.Deserialize<TsplLabel[]>(saved.Payload, JobJson)
                ?? throw new InvalidOperationException("Saved labels are empty.")),
            "DRIVER_LABELS" => PrintDriverLabels(saved),
            _ => throw new InvalidOperationException("Unknown saved print job type."),
        };
        if (output.Length > 0) RawPrinter.Send(saved.Printer, output, saved.Document);
    }

    private static byte[] RenderSavedReceipt(SavedPrintJob saved, bool retry)
    {
        ReceiptData receipt = JsonSerializer.Deserialize<ReceiptData>(saved.Payload, JobJson)
            ?? throw new InvalidOperationException("Saved receipt is empty.");
        if (retry) receipt = receipt with { Duplicate = true };
        return string.Equals(saved.Mode, "TEXT", StringComparison.OrdinalIgnoreCase)
            ? RenderText(receipt, saved.WidthDots)
            : Render(receipt, saved.WidthDots, saved.BilingualNames);
    }

    private static byte[] RenderSavedDayEnd(SavedPrintJob saved)
    {
        DayEndPrint dayEnd = JsonSerializer.Deserialize<DayEndPrint>(saved.Payload, JobJson)
            ?? throw new InvalidOperationException("Saved day end is empty.");
        return RenderDayEnd(dayEnd.Summary, saved.WidthDots, dayEnd.StoreName, dayEnd.Language);
    }

    private static byte[] PrintDriverLabels(SavedPrintJob saved)
    {
        DriverLabelPrinter.Print(saved.Printer, JsonSerializer.Deserialize<TsplLabel[]>(saved.Payload, JobJson)
            ?? throw new InvalidOperationException("Saved labels are empty."));
        return [];
    }

    private sealed record DayEndPrint(DayEndSummary Summary, string StoreName, string Language);

    private void OpenDrawer()
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
        Add(string.IsNullOrWhiteSpace(receipt.StoreGstin) ? "" : $"{T("Receipt_Gstin")}: {receipt.StoreGstin}", 16, null, TextAlignment.Center);
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
                Add($"{T("Receipt_Mrp")} ₹{mrp.Value.ToString("N3", Digits)}", 15);
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
                Add($"{T("Receipt_Gst")} {group.Key.ToString("0.###", Digits)}%  {T("Receipt_Taxable")} ₹{taxable.ToString("N3", Digits)}  " +
                    $"{T("Receipt_Cgst")} ₹{cgst.Value.ToString("N3", Digits)}  {T("Receipt_Sgst")} ₹{sgst.Value.ToString("N3", Digits)}", 15);
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

    public static byte[] RenderText(ReceiptData receipt, int widthDots)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        int columns = widthDots <= 384 ? 32 : 48;
        CultureInfo english = CultureInfo.GetCultureInfo("en-IN");
        string T(string key) => SharedStrings.ResourceManager.GetString(key, english) ?? key;
        var output = new EscPos().Init();
        void Add(string value)
        {
            foreach (string part in value.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
            {
                string remaining = part;
                while (remaining.Length > columns)
                {
                    output.Text(remaining[..columns]);
                    remaining = remaining[columns..];
                }
                output.Text(remaining);
            }
        }
        string Amount(Money amount) => "Rs " + amount.Value.ToString("N3", Digits);
        output.Align(1).Bold(true);
        Add(receipt.StoreName);
        output.Bold(false);
        Add(receipt.StoreAddress);
        Add(receipt.StorePhone);
        if (!string.IsNullOrWhiteSpace(receipt.StoreGstin)) Add(T("Receipt_Gstin") + ": " + receipt.StoreGstin);
        Add(receipt.TaxMode switch
        {
            "REGULAR" => T("Receipt_TaxInvoice"),
            "COMPOSITION" => T("Receipt_BillOfSupply"),
            "RETURN" => T("Receipt_Return"),
            _ => T("Receipt_Bill"),
        });
        if (receipt.Duplicate) Add(T("Receipt_Duplicate"));
        output.Align(0);
        Add(receipt.InvoiceNumber + "  " + receipt.CompletedAt.ToString("dd-MM-yyyy HH:mm", Digits));
        if (receipt.OriginalInvoiceNumber is { } original)
            Add(T("Receipt_OriginalInvoice") + ": " + original);
        if (receipt.CustomerName is { } customer) Add(T("Receipt_Customer") + ": " + customer);
        Add(new string('-', columns));
        foreach (ReceiptLine line in receipt.Lines)
        {
            Add(line.Name);
            Add(line.Quantity.Value.ToString("0.###", Digits) + " x " + Amount(line.UnitPrice)
                + "  " + Amount(line.Total));
            if (line.Mrp is { } mrp) Add(T("Receipt_Mrp") + " " + Amount(mrp));
            if (line.Discount > Money.Zero) Add(T("Receipt_Discount") + " " + Amount(line.Discount));
        }
        Add(new string('-', columns));
        Add(T("Receipt_Gross") + " " + Amount(receipt.Gross));
        if (receipt.Discount > Money.Zero) Add(T("Receipt_Discount") + " -" + Amount(receipt.Discount));
        Add(T("Receipt_Taxable") + " " + Amount(receipt.Taxable));
        Add(T("Receipt_Gst") + " " + Amount(receipt.Tax));
        if (receipt.RoundOff != Money.Zero) Add(T("Receipt_RoundOff") + " " + Amount(receipt.RoundOff));
        output.Bold(true);
        Add(T(receipt.TaxMode == "RETURN" ? "Receipt_RefundTotal" : "Receipt_Total") + " " + Amount(receipt.GrandTotal));
        output.Bold(false);
        foreach (PaymentInput payment in receipt.Payments)
            Add(payment.Mode + " " + Amount(payment.Amount));
        if (receipt.Change > Money.Zero) Add(T("Receipt_Change") + " " + Amount(receipt.Change));
        output.Align(1);
        Add(receipt.Footer);
        return output.Feed(4).Cut().ToArray();
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
        byte[] bits = new byte[bytesWide * height];
        for (int row = 0; row < height; row++)
            for (int column = 0; column < widthDots; column++)
            {
                int pixel = row * stride + column * 4;
                if (pixels[pixel] < 160)
                    bits[row * bytesWide + column / 8] |= (byte)(0x80 >> column % 8);
            }
        return new EscPos().Init().Raster(bits, bytesWide, height).Feed(4).Cut().ToArray();
    }
}
