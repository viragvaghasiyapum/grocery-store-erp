using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Infrastructure;

// Schema 33 switches every stock writer together. It shares the runner transaction
// and the pre-migration backup. The ledger is the truth up to this point, so a lot
// whose documents disagree with it gets one dated CUTOVER_BALANCE correction.
internal static class DirectStockCutover
{
    internal static int Prepare(SqliteConnection connection, SqliteTransaction transaction)
    {
        // An unposted ledger count cannot be posted afterwards; the reviewed
        // Products count replaces it. Its lines stay readable.
        int closed = connection.Execute("""
            UPDATE stock_count SET status='CANCELLED',
                notes=trim(COALESCE(notes,'')||' [Closed by the direct-stock upgrade]')
            WHERE status='DRAFT'
            """, transaction: transaction);
        connection.Execute("""
            UPDATE setting SET value='0' WHERE key='maintenance.stock_rebuild_pending'
            """, transaction: transaction);
        return closed;
    }

    internal static void Finish(SqliteConnection connection, SqliteTransaction transaction, int closedCounts)
    {
        int negative = connection.Execute(
            "UPDATE product_lot SET qty_on_hand=0 WHERE qty_on_hand<0", transaction: transaction);
        List<LotSourceMismatch> mismatches =
            LotSourceReconciliation.CheckAll(connection, transaction).ToList();
        int repaired = 0;
        int corrected = 0;
        foreach (LotSourceMismatch mismatch in mismatches)
        {
            long ledger = Math.Max(0, connection.ExecuteScalar<long>(
                "SELECT COALESCE(SUM(qty),0) FROM stock_movement WHERE lot_id=@LotId",
                mismatch, transaction));
            if (ledger != mismatch.Actual)
            {
                connection.Execute("UPDATE product_lot SET qty_on_hand=@ledger WHERE id=@LotId",
                    new { ledger, mismatch.LotId }, transaction);
                repaired++;
            }
            if (ledger != mismatch.Expected)
                corrected += connection.Execute("""
                    INSERT INTO stock_correction(correction_date,product_id,lot_id,quantity_delta,
                        unit_cost,source_kind,reason)
                    SELECT date('now','localtime'),product_id,id,@delta,unit_cost,'CUTOVER_BALANCE',
                           'Direct-stock upgrade: source documents aligned with the stock ledger'
                    FROM product_lot WHERE id=@LotId
                    """, new { delta = ledger - mismatch.Expected, mismatch.LotId }, transaction);
        }
        foreach (LotSourceMismatch mismatch in mismatches)
            if (LotSourceReconciliation.CheckLot(connection, transaction, mismatch.LotId) is not null)
                throw new InvalidOperationException($"Lot {mismatch.LotId} did not reconcile at the direct-stock upgrade.");
        connection.Execute("""
            INSERT INTO audit_log(entity,entity_id,action,changes)
            VALUES ('direct_stock',0,'CUTOVER',@changes)
            """, new
        {
            changes = JsonSerializer.Serialize(new
            {
                movementHighWater = connection.ExecuteScalar<long>(
                    "SELECT movement_high_water FROM direct_stock_live", transaction: transaction),
                closedDraftCounts = closedCounts,
                zeroedNegativeLots = negative,
                ledgerCacheRepairs = repaired,
                balanceCorrections = corrected,
            }),
        }, transaction);
    }
}
