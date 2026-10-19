using Dapper;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Inventory;

// An explicit opt-in conversion for disposable schema-31 copies. The production
// migration runner must not install this until every document writer is ported.
public static class DirectStockSandbox
{
    public static void Activate(DbTx tx)
    {
        if (tx.Connection.ExecuteScalar<long>("PRAGMA user_version", transaction: tx.Transaction) != 31)
            throw new InvalidOperationException("Direct stock requires a schema-31 disposable copy.");
        if (tx.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='direct_stock_sandbox'", transaction: tx.Transaction) != 0)
            throw new InvalidOperationException("Direct stock is already active.");
        tx.Connection.Execute("""
            ALTER TABLE product_lot ADD COLUMN quantity_version INTEGER NOT NULL DEFAULT 1;
            CREATE TABLE direct_stock_sandbox(activated_at TEXT NOT NULL DEFAULT (datetime('now')));
            INSERT INTO direct_stock_sandbox DEFAULT VALUES;
            -- Permanent idempotency marker, without quantity or stock deltas.
            -- operation rows can be purged; source claims cannot.
            CREATE TABLE direct_stock_claim(
              source_kind TEXT NOT NULL,
              source_id INTEGER NOT NULL,
              operation_id TEXT NOT NULL UNIQUE,
              PRIMARY KEY(source_kind,source_id)
            ) STRICT, WITHOUT ROWID;
            CREATE TABLE physical_count_result(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              operation_id TEXT NOT NULL UNIQUE,
              source_lot_id INTEGER NOT NULL,
              old_quantity INTEGER NOT NULL CHECK(old_quantity>=0),
              counted_quantity INTEGER NOT NULL CHECK(counted_quantity>=0),
              source_kind TEXT NOT NULL CHECK(source_kind IN ('DAMAGE','DIRECT_ORIGIN','MATCH')),
              source_id INTEGER,
              reason TEXT NOT NULL CHECK(length(trim(reason))>0),
              counted_at TEXT NOT NULL DEFAULT(datetime('now'))
            ) STRICT;
            CREATE INDEX ix_count_result_lot ON physical_count_result(source_lot_id);
            CREATE TABLE legacy_stock_correction(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              lot_id INTEGER NOT NULL REFERENCES product_lot(id),
              quantity_delta INTEGER NOT NULL CHECK(quantity_delta<>0),
              legacy_movement_id INTEGER NOT NULL UNIQUE,
              provenance TEXT NOT NULL DEFAULT 'LEGACY_ADJUSTMENT'
            ) STRICT;
            CREATE INDEX ix_legacy_correction_lot ON legacy_stock_correction(lot_id);
            CREATE INDEX ix_internal_use_lot_direct ON internal_use_record(lot_id_snapshot);
            INSERT INTO legacy_stock_correction(lot_id,quantity_delta,legacy_movement_id)
              SELECT lot_id,qty,id FROM stock_movement WHERE type_code='ADJUSTMENT';
            DROP TRIGGER trg_move_apply;
            CREATE TRIGGER trg_direct_no_movement BEFORE INSERT ON stock_movement
              BEGIN SELECT RAISE(ABORT,'direct stock forbids movement writes'); END;
            CREATE TRIGGER trg_direct_qty_nonnegative BEFORE UPDATE OF qty_on_hand ON product_lot
              WHEN NEW.qty_on_hand<0
              BEGIN SELECT RAISE(ABORT,'direct stock forbids negative stock'); END;
            CREATE TRIGGER trg_direct_qty_version AFTER UPDATE OF qty_on_hand ON product_lot
              WHEN NEW.qty_on_hand<>OLD.qty_on_hand AND NEW.quantity_version=OLD.quantity_version
              BEGIN UPDATE product_lot SET quantity_version=OLD.quantity_version+1 WHERE id=NEW.id; END;
            PRAGMA user_version=32;
            """, transaction: tx.Transaction);
    }
}
