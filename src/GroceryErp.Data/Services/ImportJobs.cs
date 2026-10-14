using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Data.Services;

public sealed record ImportJobState(bool Completed, long ResultId, int Created, int Updated);

public static class ImportJobs
{
    public static string Fingerprint<T>(T payload) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));

    public static string OperationIdFor<T>(string kind, T payload) =>
        Fingerprint(new { kind, payload })[..32];

    public static ImportJobState Start(DbTx tx, string operationId, string kind, string fingerprint, int batchCount)
    {
        if (operationId.Length != 32 || operationId.Any(static c => !char.IsAsciiHexDigitLower(c)))
            throw new DomainException(ErrorCode.Validation, "Operation ID must be 32 lowercase hexadecimal characters.");
        tx.Connection.Execute("""
            INSERT INTO import_job(op_id,kind,payload_sha,batch_count)
            VALUES (@operationId,@kind,@fingerprint,@batchCount)
            ON CONFLICT(op_id) DO NOTHING
            """, new { operationId, kind, fingerprint, batchCount }, tx.Transaction);
        var row = tx.Connection.QuerySingle<JobRow>("""
            SELECT kind AS Kind,payload_sha AS Fingerprint,batch_count AS BatchCount,
                   completed AS Completed,result_id AS ResultId
            FROM import_job WHERE op_id=@operationId
            """, new { operationId }, tx.Transaction);
        if (row.Kind != kind || row.Fingerprint != fingerprint || row.BatchCount != batchCount)
            throw new DomainException(ErrorCode.Validation, "An import operation ID was reused with different data.");
        (int created, int updated) = Totals(tx, operationId);
        return new(row.Completed, row.ResultId, created, updated);
    }

    public static bool HasBatch(DbTx tx, string operationId, int batchNo) =>
        tx.Connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM import_batch WHERE op_id=@operationId AND batch_no=@batchNo
            """, new { operationId, batchNo }, tx.Transaction) != 0;

    public static void RecordBatch(DbTx tx, string operationId, int batchNo, int created = 0,
        int updated = 0, long firstLot = 0) => tx.Connection.Execute("""
            INSERT INTO import_batch(op_id,batch_no,created,updated,first_lot)
            VALUES (@operationId,@batchNo,@created,@updated,@firstLot)
            """, new { operationId, batchNo, created, updated, firstLot }, tx.Transaction);

    public static ImportJobState Complete(DbTx tx, string operationId, int batchCount, bool openingStock)
    {
        long done = tx.Connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM import_batch WHERE op_id=@operationId
            """, new { operationId }, tx.Transaction);
        if (done != batchCount) throw new InvalidOperationException("Import batches are incomplete.");
        if (batchCount >= 10)
            tx.Connection.Execute("PRAGMA optimize", transaction: tx.Transaction);
        long resultId = openingStock
            ? tx.Connection.ExecuteScalar<long>("""
                SELECT first_lot FROM import_batch WHERE op_id=@operationId
                ORDER BY batch_no LIMIT 1
                """, new { operationId }, tx.Transaction)
            : tx.Connection.ExecuteScalar<long>("""
                SELECT COALESCE(SUM(created+updated),0) FROM import_batch WHERE op_id=@operationId
                """, new { operationId }, tx.Transaction);
        tx.Connection.Execute("""
            UPDATE import_job SET completed=1,result_id=@resultId WHERE op_id=@operationId
            """, new { operationId, resultId }, tx.Transaction);
        (int created, int updated) = Totals(tx, operationId);
        return new(true, resultId, created, updated);
    }

    private static (int Created, int Updated) Totals(DbTx tx, string operationId) =>
        tx.Connection.QuerySingle<(int Created, int Updated)>("""
            SELECT COALESCE(SUM(created),0) AS Created,COALESCE(SUM(updated),0) AS Updated
            FROM import_batch WHERE op_id=@operationId
            """, new { operationId }, tx.Transaction);

    private sealed class JobRow
    {
        public string Kind { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        public int BatchCount { get; init; }
        public bool Completed { get; init; }
        public long ResultId { get; init; }
    }
}
