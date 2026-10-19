using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Infrastructure;

// Schema 29 rebuilds only the tables whose historical foreign keys block future
// catalog deletion. SQLite cannot drop a foreign key with ALTER TABLE. All copies,
// snapshot backfills, checks, and the version marker share the runner transaction.
internal sealed class PhaseOneMigration
{
    private static readonly string[] RebuiltTables =
    [
        "product", "product_lot", "purchase_line", "sale_line", "stock_movement",
        "stock_count_line", "damage", "vendor_return_line",
    ];

    private readonly List<SchemaObject> _objects = [];
    private readonly List<SchemaObject> _views = [];
    private readonly Dictionary<string, long> _counts = [];
    private readonly long _saleTotal;
    private readonly long _returnTotal;
    private readonly long _purchaseTotal;
    private readonly long _receiptBytes;

    private PhaseOneMigration(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (string table in RebuiltTables)
            _counts[table] = connection.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}", transaction: transaction);
        _counts["sale_return_line"] = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sale_return_line", transaction: transaction);
        _saleTotal = connection.ExecuteScalar<long>(
            "SELECT COALESCE(SUM(grand_total),0) FROM sale WHERE status IN ('COMPLETED','VOID')",
            transaction: transaction);
        _returnTotal = connection.ExecuteScalar<long>(
            "SELECT COALESCE(SUM(refund_total),0) FROM sale_return", transaction: transaction);
        _purchaseTotal = connection.ExecuteScalar<long>(
            "SELECT COALESCE(SUM(grand_total),0) FROM purchase WHERE status<>'DRAFT'",
            transaction: transaction);
        _receiptBytes = connection.ExecuteScalar<long>("""
            SELECT COALESCE((SELECT SUM(length(receipt_snapshot)) FROM sale),0)
                 + COALESCE((SELECT SUM(length(receipt_snapshot)) FROM sale_return),0)
            """, transaction: transaction);
    }

    internal static PhaseOneMigration Prepare(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (connection.QueryFirstOrDefault("PRAGMA foreign_key_check", transaction: transaction) is not null)
            throw new InvalidOperationException("Phase 1 source foreign-key check failed.");
        var migration = new PhaseOneMigration(connection, transaction);
        migration._views.AddRange(connection.Query<SchemaObject>("""
            SELECT name AS Name,sql AS Sql FROM sqlite_schema WHERE type='view' ORDER BY rowid
            """, transaction: transaction));
        migration._objects.AddRange(connection.Query<SchemaObject>("""
            SELECT name AS Name,sql AS Sql FROM sqlite_schema WHERE type='trigger' ORDER BY rowid
            """, transaction: transaction));
        foreach (SchemaObject view in migration._views)
            connection.Execute($"DROP VIEW \"{view.Name}\"", transaction: transaction);
        foreach (SchemaObject trigger in migration._objects)
            connection.Execute($"DROP TRIGGER \"{trigger.Name}\"", transaction: transaction);
        foreach (string table in RebuiltTables)
            migration.Rebuild(connection, transaction, table);
        return migration;
    }

    private void Rebuild(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        string create = connection.ExecuteScalar<string>(
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name=@table",
            new { table }, transaction) ?? throw new InvalidOperationException($"Missing table {table}.");
        _objects.AddRange(connection.Query<SchemaObject>("""
            SELECT name AS Name,sql AS Sql FROM sqlite_schema
            WHERE tbl_name=@table AND type='index' AND sql IS NOT NULL
            ORDER BY name
            """, new { table }, transaction).Where(static item => item.Name != "ix_lot_fefo"));

        string updated = table switch
        {
            "product" or "product_lot" => Regex.Replace(create,
                @"\bid\s+INTEGER\s+PRIMARY\s+KEY\b", "id INTEGER PRIMARY KEY AUTOINCREMENT",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "purchase_line" => RemoveInlineReference(create, "product_id", "product"),
            "vendor_return_line" => RemoveInlineReference(
                RemoveInlineReference(create, "product_id", "product"), "lot_id", "product_lot"),
            "sale_line" or "stock_movement" or "stock_count_line" or "damage" =>
                RemoveLotReference(create),
            _ => throw new InvalidOperationException(table),
        };
        if (updated == create)
            throw new InvalidOperationException($"Schema {table} did not have the expected Phase 1 change.");
        string shadow = "__phase1_" + table;
        updated = Regex.Replace(updated, @"^CREATE\s+TABLE\s+" + table + @"\b",
            "CREATE TABLE " + shadow, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!updated.StartsWith("CREATE TABLE " + shadow, StringComparison.Ordinal))
            throw new InvalidOperationException($"Could not rebuild {table}.");
        connection.Execute(updated, transaction: transaction);
        connection.Execute($"INSERT INTO {shadow} SELECT * FROM {table}", transaction: transaction);
        connection.Execute($"DROP TABLE {table}", transaction: transaction);
        connection.Execute($"ALTER TABLE {shadow} RENAME TO {table}", transaction: transaction);
    }

    private static string RemoveInlineReference(string create, string column, string parent) =>
        Regex.Replace(create, @"(\b" + column + @"\s+INTEGER\s+NOT\s+NULL)\s+REFERENCES\s+" +
            parent + @"\s*\(\s*id\s*\)", "$1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string RemoveLotReference(string create) => Regex.Replace(create,
        @",\s*FOREIGN\s+KEY\s*\(\s*lot_id\s*,\s*product_id\s*\)\s*" +
        @"REFERENCES\s+product_lot\s*\(\s*id\s*,\s*product_id\s*\)", string.Empty,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal void Finish(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (SchemaObject view in _views)
            connection.Execute(view.Sql, transaction: transaction);
        foreach (SchemaObject item in _objects)
            connection.Execute(item.Sql, transaction: transaction);
        foreach ((string table, long count) in _counts)
            if (connection.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}", transaction: transaction) != count)
                throw new InvalidOperationException($"Phase 1 changed the row count of {table}.");
        if (connection.ExecuteScalar<long>(
                "SELECT COALESCE(SUM(grand_total),0) FROM sale WHERE status IN ('COMPLETED','VOID')",
                transaction: transaction) != _saleTotal ||
            connection.ExecuteScalar<long>(
                "SELECT COALESCE(SUM(refund_total),0) FROM sale_return", transaction: transaction) != _returnTotal ||
            connection.ExecuteScalar<long>(
                "SELECT COALESCE(SUM(grand_total),0) FROM purchase WHERE status<>'DRAFT'",
                transaction: transaction) != _purchaseTotal ||
            connection.ExecuteScalar<long>("""
                SELECT COALESCE((SELECT SUM(length(receipt_snapshot)) FROM sale),0)
                     + COALESCE((SELECT SUM(length(receipt_snapshot)) FROM sale_return),0)
                """, transaction: transaction) != _receiptBytes)
            throw new InvalidOperationException("Phase 1 document or receipt parity failed.");
        if (connection.QueryFirstOrDefault("PRAGMA foreign_key_check", transaction: transaction) is not null)
            throw new InvalidOperationException("Phase 1 foreign-key check failed.");
        if (connection.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM product_lot
                WHERE origin_ref_id IS NULL OR
                  (origin_kind='PURCHASE_LINE' AND origin_ref_id<>purchase_line_id) OR
                  (origin_kind='DIRECT' AND NOT EXISTS
                    (SELECT 1 FROM direct_stock_origin d WHERE d.id=product_lot.origin_ref_id
                     AND d.lot_id=product_lot.id))
                """, transaction: transaction) != 0)
            throw new InvalidOperationException("Phase 1 lot origins are incomplete.");
        connection.Execute("""
            INSERT INTO schema_migration_marker(version,completed_at,sale_lines,return_lines,purchase_lines,
                lot_count,sale_total,return_total,purchase_total,receipt_bytes)
            VALUES (29,datetime('now'),@saleLines,@returnLines,@purchaseLines,@lotCount,
                @saleTotal,@returnTotal,@purchaseTotal,@receiptBytes)
            """, new
        {
            saleLines = _counts["sale_line"], returnLines = _counts["sale_return_line"],
            purchaseLines = _counts["purchase_line"], lotCount = _counts["product_lot"],
            saleTotal = _saleTotal, returnTotal = _returnTotal,
            purchaseTotal = _purchaseTotal, receiptBytes = _receiptBytes,
        }, transaction);
    }

    private sealed class SchemaObject
    {
        public string Name { get; init; } = "";
        public string Sql { get; init; } = "";
    }
}
