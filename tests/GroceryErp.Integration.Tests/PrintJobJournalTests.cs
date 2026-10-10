using System.Collections.Immutable;
using System.Text.Json;
using Dapper;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Sales;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class PrintJobJournalTests
{
    private static readonly JsonSerializerOptions Json = PrintJobJson.Options;

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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
