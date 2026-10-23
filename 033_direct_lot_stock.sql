-- Phase 5 cutover: a lot's current quantity is authoritative and only the
-- document that causes a change updates it. stock_movement keeps its history for
-- reading until Phase 8 removes it; no new movement can be written.
-- MigrationRunner repairs ledger-cache drift and closes draft ledger counts before
-- this script, then records source-balance corrections after it, in one transaction.
ALTER TABLE product_lot ADD COLUMN quantity_version INTEGER NOT NULL DEFAULT 1;

CREATE TABLE direct_stock_live(
  id INTEGER PRIMARY KEY CHECK(id=1),
  activated_at TEXT NOT NULL DEFAULT (datetime('now','localtime')),
  movement_high_water INTEGER NOT NULL CHECK(movement_high_water>=0)
) STRICT;
INSERT INTO direct_stock_live(id,movement_high_water)
SELECT 1,COALESCE(MAX(id),0) FROM stock_movement;

-- Reviewed physical counts: a deficit becomes damage, a surplus a new direct lot.
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

-- Dated, reasoned and immutable stock corrections. Sales, purchases, returns,
-- damage and internal use keep their quantities on their own documents. The lot
-- ID is historical, so a zero lot can still be deleted.
CREATE TABLE stock_correction(
  id INTEGER PRIMARY KEY,
  correction_date TEXT NOT NULL CHECK(correction_date=date(correction_date)),
  product_id INTEGER NOT NULL,
  lot_id INTEGER NOT NULL,
  quantity_delta INTEGER NOT NULL CHECK(quantity_delta<>0),
  unit_cost INTEGER NOT NULL CHECK(unit_cost>=0),
  source_kind TEXT NOT NULL CHECK(source_kind IN
    ('LEGACY_MOVEMENT','CUTOVER_BALANCE','MANUAL','LOT_CORRECTION','DAMAGE_CORRECTION')),
  source_id INTEGER,
  reason TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_stock_correction_lot ON stock_correction(lot_id);
CREATE INDEX ix_stock_correction_date ON stock_correction(correction_date);
CREATE TRIGGER trg_stock_correction_no_update BEFORE UPDATE ON stock_correction
BEGIN SELECT RAISE(ABORT,'stock correction is immutable'); END;
CREATE TRIGGER trg_stock_correction_no_delete BEFORE DELETE ON stock_correction
BEGIN SELECT RAISE(ABORT,'stock correction is immutable'); END;
-- Legacy adjustments (manual, count, lot and damage corrections) carry forward so
-- each lot's documents still explain its quantity. Their P&L stays on the history.
INSERT INTO stock_correction(correction_date,product_id,lot_id,quantity_delta,unit_cost,
  source_kind,source_id,reason,created_at)
SELECT move_date,product_id,lot_id,qty,unit_cost,'LEGACY_MOVEMENT',id,note,moved_at
FROM stock_movement WHERE type_code='ADJUSTMENT';
CREATE INDEX ix_internal_use_lot ON internal_use_record(lot_id_snapshot);

DROP TRIGGER trg_move_apply;
CREATE TRIGGER trg_direct_no_movement BEFORE INSERT ON stock_movement
BEGIN SELECT RAISE(ABORT,'direct stock forbids movement writes'); END;
CREATE TRIGGER trg_direct_qty_nonnegative BEFORE UPDATE OF qty_on_hand ON product_lot
WHEN NEW.qty_on_hand<0
BEGIN SELECT RAISE(ABORT,'direct stock forbids negative stock'); END;
CREATE TRIGGER trg_direct_qty_version AFTER UPDATE OF qty_on_hand ON product_lot
WHEN NEW.qty_on_hand<>OLD.qty_on_hand AND NEW.quantity_version=OLD.quantity_version
BEGIN UPDATE product_lot SET quantity_version=OLD.quantity_version+1 WHERE id=NEW.id; END;

-- The ledger-cache comparison is meaningless once movements stop.
DROP VIEW v_lot_cache_mismatch;

DROP VIEW v_pnl_entry;
CREATE VIEW v_pnl_entry AS
SELECT s.sale_date AS entry_date, 'INCOME' AS section, 'SALES' AS head_code,
       NULL AS ref_id, s.taxable_total AS amount
  FROM sale s WHERE s.status = 'COMPLETED'
UNION ALL SELECT s.sale_date, 'INCOME', 'ROUND_OFF', NULL, s.round_off
  FROM sale s WHERE s.status = 'COMPLETED' AND s.round_off <> 0
UNION ALL SELECT r.return_date, 'INCOME', 'SALES_RETURNS', NULL, -r.taxable_total
  FROM sale_return r
UNION ALL SELECT s.sale_date, 'COGS', 'COGS', NULL, s.cost_total
  FROM sale s WHERE s.status = 'COMPLETED'
UNION ALL SELECT r.return_date, 'COGS', 'COGS', NULL, -r.cost_total
  FROM sale_return r
UNION ALL SELECT d.damage_date, 'EXPENSE', 'DAMAGES', NULL, d.cost_value - d.recovered_amount
  FROM damage d
UNION ALL SELECT dc.correction_date, 'EXPENSE', 'DAMAGES', NULL, dc.expense_delta
  FROM damage_correction dc
UNION ALL SELECT m.move_date, 'EXPENSE', 'STOCK_ADJUSTMENT', NULL,
       -CAST(ROUND(m.qty * m.unit_cost / 1000.0) AS INTEGER)
  FROM stock_movement m WHERE m.type_code = 'ADJUSTMENT'
    AND COALESCE(m.ref_type,'') <> 'damage_correction'
UNION ALL SELECT c.correction_date, 'EXPENSE', 'STOCK_ADJUSTMENT', NULL,
       -CAST(ROUND(c.quantity_delta * c.unit_cost / 1000.0) AS INTEGER)
  FROM stock_correction c WHERE c.source_kind IN ('MANUAL','LOT_CORRECTION')
UNION ALL SELECT e.expense_date, 'EXPENSE', 'EXPENSE_CATEGORY', e.category_id, e.amount
  FROM expense e
UNION ALL SELECT r.reversal_date, 'EXPENSE', 'EXPENSE_CATEGORY', e.category_id, -r.amount
  FROM purchase_expense_reversal r JOIN expense e ON e.id=r.expense_id
UNION ALL SELECT date(sp.period_month || '-01', '+1 month', '-1 day'), 'EXPENSE', 'SALARIES', NULL, sp.amount
  FROM salary_payment sp
UNION ALL SELECT sc.correction_date, 'EXPENSE', 'SALARIES', NULL, sc.amount_delta
  FROM salary_correction sc
UNION ALL SELECT date(ad.period_month || '-01', '+1 month', '-1 day'), 'EXPENSE', 'DEPRECIATION', NULL, ad.amount
  FROM asset_depreciation ad
UNION ALL SELECT a.disposed_date, 'OTHER', 'ASSET_DISPOSAL', a.id,
       a.disposal_value - COALESCE(
         (SELECT ad.closing_value FROM asset_depreciation ad
           WHERE ad.asset_id = a.id ORDER BY ad.period_month DESC LIMIT 1), a.cost)
  FROM asset a WHERE a.disposed_date IS NOT NULL
UNION ALL SELECT vr.return_date, 'OTHER', 'VENDOR_RETURN_VARIANCE', NULL,
       vr.credit_before_tax + CASE WHEN vr.tax_mode='REGULAR' THEN 0 ELSE vr.tax_credit END - vr.cost_total
  FROM vendor_return vr
UNION ALL SELECT c.cancellation_date, 'OTHER', 'VENDOR_RETURN_VARIANCE', NULL,
       -(vr.credit_before_tax + CASE WHEN vr.tax_mode='REGULAR' THEN 0 ELSE vr.tax_credit END - vr.cost_total)
  FROM vendor_return_cancellation c JOIN vendor_return vr ON vr.id=c.vendor_return_id;
