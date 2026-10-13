using System.Text.Json;
using Dapper;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Sales;

// A spooler acknowledgement does not prove that paper was produced. Interrupted jobs
// remain for operator review so restart cannot silently print a second copy.
public sealed class PrintJobJournal(SqliteConnectionFactory factory)
{
    private static readonly JsonSerializerOptions Json = PrintJobJson.Options;

    public IDisposable EnterPrintWork() => factory.EnterOperation();

    public int RecoverInterrupted()
    {
        using var connection = factory.Open();
        connection.Execute("UPDATE print_job SET state = 'REVIEW' WHERE state IN ('PENDING','SENDING')");
        return ReviewCount();
    }

    public int ReviewCount()
    {
        using var connection = factory.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM print_job WHERE state IN ('REVIEW','FAILED')");
    }

    public SavedPrintJob? OldestReview()
    {
        using var connection = factory.Open();
        PrintJobRow? row = connection.QuerySingleOrDefault<PrintJobRow>("""
            SELECT id, payload FROM print_job WHERE state IN ('REVIEW','FAILED') ORDER BY id LIMIT 1
            """);
        return row is null ? null : (JsonSerializer.Deserialize<SavedPrintJob>(row.Payload, Json)
            ?? throw new InvalidOperationException("Saved print job is empty.")) with
        { Id = row.Id };
    }

    public Task<long> AddAsync(SavedPrintJob job) => Task.Run(() =>
    {
        using var connection = factory.Open();
        return connection.ExecuteScalar<long>("""
            INSERT INTO print_job(state, document, payload)
            VALUES ('PENDING', @Document, @Payload);
            SELECT last_insert_rowid();
            """, new { job.Document, Payload = JsonSerializer.Serialize(job, Json) });
    });

    public SavedPrintJob? ClaimOldestReview(long? id = null)
    {
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        PrintJobRow? row = connection.QuerySingleOrDefault<PrintJobRow>("""
            SELECT id, payload FROM print_job
            WHERE state IN ('REVIEW','FAILED') AND (@id IS NULL OR id = @id)
            ORDER BY id LIMIT 1
            """, new { id }, transaction);
        if (row is null) return null;
        SavedPrintJob job = JsonSerializer.Deserialize<SavedPrintJob>(row.Payload, Json)
            ?? throw new InvalidOperationException("Saved print job is empty.");
        connection.Execute("UPDATE print_job SET state = 'PENDING' WHERE id = @Id", new { row.Id }, transaction);
        transaction.Commit();
        return job with { Id = row.Id };
    }

    public void MarkSending(long id) => SetState(id, "SENDING");
    public void MarkFailed(long id) => SetState(id, "FAILED");

    public void Remove(long id)
    {
        using var connection = factory.Open();
        connection.Execute("DELETE FROM print_job WHERE id = @id", new { id });
    }

    private void SetState(long id, string state)
    {
        using var connection = factory.Open();
        connection.Execute("UPDATE print_job SET state = @state WHERE id = @id", new { id, state });
    }

    private sealed record PrintJobRow(long Id, string Payload);
}

public sealed record SavedPrintJob(
    string Kind,
    string Printer,
    string Document,
    string Payload,
    string? Mode = null,
    int WidthDots = 0,
    bool BilingualNames = false)
{
    public long Id { get; init; }
}
