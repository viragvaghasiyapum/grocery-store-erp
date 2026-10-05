using System.Text.Json;
using Dapper;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Data.Services;

public enum DocType { Sale, SaleReturn, Purchase, VendorReturn, Damage, Lot }

public sealed class DocNumberService(ISettings settings)
{
    public string Next(DbTx transaction, DocType type, DateOnly date)
    {
        string fiscalYear = type == DocType.Lot ? "*" : FiscalYear.Label(date, settings.Current.FyStartMonth);
        string prefix = type == DocType.Lot ? "L" : $"{Letter(type)}{FiscalYear.ShortCode(date, settings.Current.FyStartMonth)}-";
        string code = Code(type);
        transaction.Connection.Execute("""
            INSERT INTO doc_sequence(doc_type, fiscal_year, prefix, next_no)
            VALUES (@code, @fiscalYear, @prefix, 1)
            ON CONFLICT(doc_type, fiscal_year) DO NOTHING
            """, new { code, fiscalYear, prefix }, transaction.Transaction);
        var row = transaction.Connection.QuerySingle<SequenceResult>("""
            UPDATE doc_sequence SET next_no = next_no + 1
            WHERE doc_type = @code AND fiscal_year = @fiscalYear
            RETURNING prefix AS Prefix, next_no - 1 AS Number
            """, new { code, fiscalYear }, transaction.Transaction);
        return $"{row.Prefix}{row.Number:D6}";
    }

    private static string Letter(DocType type) => type switch
    {
        DocType.Sale => "S",
        DocType.SaleReturn => "R",
        DocType.Purchase => "P",
        DocType.VendorReturn => "V",
        DocType.Damage => "D",
        DocType.Lot => "L",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
    private static string Code(DocType type) => type switch
    {
        DocType.Sale => "SALE",
        DocType.SaleReturn => "SALE_RETURN",
        DocType.Purchase => "PURCHASE",
        DocType.VendorReturn => "VENDOR_RETURN",
        DocType.Damage => "DAMAGE",
        DocType.Lot => "LOT",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
    private sealed class SequenceResult { public string Prefix { get; init; } = ""; public long Number { get; init; } }
}

public interface IAuditLog
{
    void Write(DbTx transaction, string entity, long id, string action, object? changes);
}

public sealed class AuditLog : IAuditLog
{
    public void Write(DbTx transaction, string entity, long id, string action, object? changes) =>
        transaction.Connection.Execute(
            "INSERT INTO audit_log(entity, entity_id, action, changes) VALUES (@entity, @id, @action, @json)",
            new { entity, id, action, json = changes is null ? null : JsonSerializer.Serialize(changes) },
            transaction.Transaction);
}
