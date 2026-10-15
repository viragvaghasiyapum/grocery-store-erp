using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Sales;
using GroceryErp.Printing;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class PrintJobJournalTests
{
    private static readonly JsonSerializerOptions Json = PrintJobJson.Options;

    [Fact]
    public async Task ClearingAnotherStorePool_DoesNotInterruptJournalWork()
    {
        string printRoot = Path.Combine(Path.GetTempPath(), $"grocery-erp-print-{Guid.NewGuid():N}");
        string otherRoot = Path.Combine(Path.GetTempPath(), $"grocery-erp-other-{Guid.NewGuid():N}");
        try
        {
            var printFactory = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(printRoot)));
            var otherFactory = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(otherRoot)));
            new MigrationRunner(printFactory, NullMigrationBackup.Instance).Run();
            new MigrationRunner(otherFactory, NullMigrationBackup.Instance).Run();
            var journal = new PrintJobJournal(printFactory);
            var saved = new SavedPrintJob("RECEIPT", "Counter printer", "Concurrent journal", "{}");

            Task otherStore = Task.Run(() =>
            {
                for (int index = 0; index < 100; index++)
                {
                    using (var connection = otherFactory.Open())
                        connection.ExecuteScalar<int>("SELECT COUNT(*) FROM print_job").ShouldBe(0);
                    otherFactory.ClearPool();
                }
            }, TestContext.Current.CancellationToken);
            for (int index = 0; index < 100; index++)
            {
                long id = await journal.AddAsync(saved);
                journal.MarkSending(id);
                journal.RecoverInterrupted().ShouldBe(1);
                journal.ClaimOldestReview(id)!.Id.ShouldBe(id);
                journal.Remove(id);
            }
            await otherStore;
            journal.ReviewCount().ShouldBe(0);
        }
        finally
        {
            SqliteConnectionFactory.ClearPoolForDataRoot(printRoot);
            SqliteConnectionFactory.ClearPoolForDataRoot(otherRoot);
            if (Directory.Exists(printRoot)) Directory.Delete(printRoot, recursive: true);
            if (Directory.Exists(otherRoot)) Directory.Delete(otherRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedJobs_RemainForReview_AndRetryKeepsOriginalSnapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-print-{Guid.NewGuid():N}");
        try
        {
            var factory = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root)));
            new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
            var journal = new PrintJobJournal(factory);
            ReceiptData receipt = SampleReceipt();
            var saved = new SavedPrintJob("RECEIPT", "Counter printer", "Receipt S-001",
                JsonSerializer.Serialize(receipt, Json),
                "TEXT", 384);

            long id = await journal.AddAsync(saved);
            journal.MarkSending(id);
            journal.RecoverInterrupted().ShouldBe(1);
            SavedPrintJob recovered = journal.OldestReview()!;
            recovered.Id.ShouldBe(id);
            recovered.Printer.ShouldBe("Counter printer");
            recovered.Mode.ShouldBe("TEXT");
            recovered.WidthDots.ShouldBe(384);
            ReceiptData restored = JsonSerializer.Deserialize<ReceiptData>(recovered.Payload, Json)!;
            restored.InvoiceNumber.ShouldBe(receipt.InvoiceNumber);
            restored.GrandTotal.ShouldBe(receipt.GrandTotal);
            restored.Lines.Single().Quantity.ShouldBe(Quantity.One);

            journal.ClaimOldestReview()!.Id.ShouldBe(id);
            journal.ReviewCount().ShouldBe(0);
            journal.MarkFailed(id);
            journal.ReviewCount().ShouldBe(1);
            long anotherId = await journal.AddAsync(saved with { Document = "Receipt S-002" });
            journal.MarkFailed(anotherId);
            journal.ClaimOldestReview(anotherId)!.Id.ShouldBe(anotherId);
            journal.ClaimOldestReview(id)!.Id.ShouldBe(id);
            journal.Remove(id);
            journal.Remove(anotherId);
            journal.RecoverInterrupted().ShouldBe(0);
            using var connection = factory.Open();
            connection.ExecuteScalar<int>("SELECT COUNT(*) FROM print_job").ShouldBe(0);
        }
        finally
        {
            SqliteConnectionFactory.ClearPoolForDataRoot(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedReceipt_StaysReviewableAfterPrinterInstanceRestarts()
    {
        string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-print-{Guid.NewGuid():N}");
        try
        {
            var factory = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root)));
            new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
            var journal = new PrintJobJournal(factory);
            string missingPrinter = $"missing-printer-{Guid.NewGuid():N}";
            var appSettings = new AppSettings("Store", 4, 0, null, 15, 30, root)
            {
                Values = ImmutableDictionary<string, string>.Empty
                    .Add("printer.receipt.name", missingPrinter)
                    .Add("printer.receipt.mode", "TEXT")
                    .Add("printer.receipt.width_dots", "384"),
            };
            var printer = new ReceiptPrinter(new TestSettings(appSettings), journal);
            await Should.ThrowAsync<Exception>(() => printer.PrintAsync(SampleReceipt()));
            journal.ReviewCount().ShouldBe(1);

            var restarted = new ReceiptPrinter(new TestSettings(appSettings), journal);
            SavedPrintJob review = restarted.OldestReviewJob!;
            review.Document.ShouldBe("Receipt S-001");
            review.Printer.ShouldBe(missingPrinter);
            review.Mode.ShouldBe("TEXT");
            await Should.ThrowAsync<Exception>(() => restarted.RetryFailedAsync());
            journal.ReviewCount().ShouldBe(1);
        }
        finally
        {
            SqliteConnectionFactory.ClearPoolForDataRoot(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LargeLabelBatch_SavesEveryBoundedChunkBeforeFailedPrinter()
    {
        string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-label-{Guid.NewGuid():N}");
        try
        {
            var factory = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root)));
            new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
            var journal = new PrintJobJournal(factory);
            var printer = new ReceiptPrinter(new TestSettings(new AppSettings("Store", 4, 0,
                null, 15, 30, root)), journal);
            string missingPrinter = $"missing-printer-{Guid.NewGuid():N}";
            var label = new TsplLabel("Rice", null, "8901234567890", 42m, null, Copies: 250);

            await Should.ThrowAsync<Exception>(() =>
                printer.PrintLabelsAsync(missingPrinter, [label], useDriver: false));

            journal.ReviewCount().ShouldBe(3);
            SavedPrintJob saved = journal.OldestReview()!;
            saved.Document.ShouldBe("Product labels, chunk 1");
            TsplLabel[] chunk = JsonSerializer.Deserialize<TsplLabel[]>(saved.Payload, Json)!;
            chunk.Sum(static item => item.Copies).ShouldBe(100);
            TsplLabelBuilder.BuildBatch(chunk).Length.ShouldBeLessThan(256 * 1024);
        }
        finally
        {
            SqliteConnectionFactory.ClearPoolForDataRoot(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReceiptAndDrawer_StartBeforeNextLabelChunk()
    {
        using var firstChunkEntered = new ManualResetEventSlim();
        using var releaseFirstChunk = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();
        var appSettings = new AppSettings("Store", 4, 0, null, 15, 30, Path.GetTempPath())
        {
            Values = ImmutableDictionary<string, string>.Empty.Add("printer.receipt.name", "test-printer"),
        };
        var printer = new ReceiptPrinter(new TestSettings(appSettings), null,
            (job, _) =>
            {
                order.Enqueue(job.Kind);
                if (job.Kind == "TSPL_LABELS" && !firstChunkEntered.IsSet)
                {
                    firstChunkEntered.Set();
                    if (!releaseFirstChunk.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("The first label chunk was never released.");
                }
            }, () => order.Enqueue("DRAWER"));
        var labels = Enumerable.Range(0, 50)
            .Select(index => new TsplLabel($"Item {index}", null, $"89012345{index:D5}", 42m, null))
            .ToArray();
        Task labelTask = printer.PrintLabelsAsync("test-printer", labels, useDriver: false);
        Assert.True(await Task.Run(() => firstChunkEntered.Wait(TimeSpan.FromSeconds(5))));
        Task receiptTask = printer.PrintAsync(SampleReceipt());
        Task drawerTask = printer.OpenDrawerAsync();
        releaseFirstChunk.Set();
        await Task.WhenAll(labelTask, receiptTask, drawerTask);
        string[] sent = order.ToArray();
        sent.Length.ShouldBe(4);
        sent[0].ShouldBe("TSPL_LABELS");
        sent[1].ShouldBe("RECEIPT");
        sent[2].ShouldBe("DRAWER");
        sent[3].ShouldBe("TSPL_LABELS");
    }

    private sealed record TestSettings(AppSettings Current) : ISettings;

    private static ReceiptData SampleReceipt() => new(
        "Store", "Address", "Phone", "", "REGULAR", "S-001", new DateTime(2026, 10, 8, 10, 0, 0), null,
        [new ReceiptLine("Rice", "ચોખા", Quantity.One, new Money(42), null, Money.Zero,
            new Money(40), new Money(2), new Money(42), new Percent(5))],
        new Money(42), Money.Zero, new Money(40), new Money(2), Money.Zero, new Money(42),
        Money.Zero, [new PaymentInput(PaymentMode.Cash, new Money(42))], Money.Zero,
        "gu-IN", "Thanks", false);
}
