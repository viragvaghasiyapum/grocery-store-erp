using Dapper;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Infrastructure;

// Schema 36 drops the stock ledger inside the runner transaction. Before the script,
// the ledger may hold nothing the documents lack: no movement after the direct-stock
// upgrade, and a matching carried-forward correction for each legacy adjustment.
// After it, every P&L day, head and category total must be exactly as before.
internal static class LedgerRemoval
{
    internal static IReadOnlyList<PnlTotal> Prepare(SqliteConnection connection, SqliteTransaction transaction)
    {
        long unexpected = connection.ExecuteScalar<long>("""
            SELECT (SELECT COALESCE(MAX(id),0) FROM stock_movement)-movement_high_water FROM direct_stock_live
            """, transaction: transaction);
        if (unexpected != 0)
            throw new InvalidOperationException(
                "A stock movement was written after the direct-stock upgrade; the ledger cannot be removed.");
        // Each LEGACY_MOVEMENT correction was copied from exactly one adjustment, so
        // equal counts mean every adjustment has its twin with the same values.
        long uncarried = connection.ExecuteScalar<long>("""
            SELECT (SELECT COUNT(*) FROM stock_movement WHERE type_code='ADJUSTMENT')
                 - (SELECT COUNT(DISTINCT c.source_id) FROM stock_correction c
                    JOIN stock_movement m ON m.id=c.source_id
                    WHERE c.source_kind='LEGACY_MOVEMENT' AND m.type_code='ADJUSTMENT'
                      AND c.correction_date=m.move_date AND c.quantity_delta=m.qty
                      AND c.unit_cost=m.unit_cost AND c.product_id=m.product_id AND c.lot_id=m.lot_id)
            """, transaction: transaction);
        if (uncarried != 0)
            throw new InvalidOperationException(
                $"{uncarried} legacy stock adjustments have no carried-forward correction; the ledger cannot be removed.");
        return Totals(connection, transaction);
    }

    internal static void Finish(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<PnlTotal> before)
    {
        List<PnlTotal> after = Totals(connection, transaction);
        if (!before.SequenceEqual(after))
            throw new InvalidOperationException("P&L totals changed while removing the stock ledger.");
        connection.Execute("""
            INSERT INTO audit_log(entity,entity_id,action,changes)
            VALUES ('direct_stock',0,'LEDGER_REMOVED',
                    json_object('legacyAdjustments',(SELECT COUNT(*) FROM legacy_adjustment_source),
                                'pnlRows',@rows))
            """, new { rows = after.Count }, transaction);
    }

    private static List<PnlTotal> Totals(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.Query<PnlTotal>("""
            SELECT entry_date AS Date,section AS Section,head_code AS Head,category_id AS CategoryId,
                   SUM(amount) AS Amount,SUM(source_count) AS Sources
            FROM v_pnl_entry GROUP BY entry_date,section,head_code,category_id
            ORDER BY entry_date,section,head_code,category_id
            """, transaction: transaction).ToList();

    // A record for value comparison; Dapper fills its init properties.
    internal sealed record PnlTotal
    {
        public string Date { get; init; } = "";
        public string Section { get; init; } = "";
        public string Head { get; init; } = "";
        public long CategoryId { get; init; }
        public long Amount { get; init; }
        public long Sources { get; init; }
    }
}
