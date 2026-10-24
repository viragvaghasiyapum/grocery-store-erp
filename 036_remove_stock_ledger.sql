-- Phase 8: remove the stock ledger. Since schema 33 a lot quantity changes only with
-- the business document that causes it, and no movement could be written. Those
-- documents explain every lot, so the movement history and the old count sheets are
-- redundant here; the verified backup taken before this upgrade keeps them and is
-- exempt from backup rotation.
-- MigrationRunner checks before this script that no movement was written after the
-- direct-stock upgrade and that every legacy adjustment was carried forward, and after
-- it that every P&L day, head and category total is unchanged.

-- Schema 33 carried each legacy adjustment forward as a LEGACY_MOVEMENT correction.
-- The movement's reference decides whether it is a P&L stock adjustment (a damage
-- correction is already counted under Damages), so it stays with the correction.
CREATE TABLE legacy_adjustment_source(
  correction_id INTEGER PRIMARY KEY REFERENCES stock_correction(id),
  ref_type TEXT,
  ref_id INTEGER
) STRICT;
INSERT INTO legacy_adjustment_source(correction_id,ref_type,ref_id)
SELECT c.id,m.ref_type,m.ref_id
FROM stock_correction c JOIN stock_movement m ON m.id=c.source_id
WHERE c.source_kind='LEGACY_MOVEMENT';
CREATE TRIGGER trg_legacy_adjustment_no_update BEFORE UPDATE ON legacy_adjustment_source
BEGIN SELECT RAISE(ABORT,'legacy adjustment source is history'); END;
CREATE TRIGGER trg_legacy_adjustment_no_delete BEFORE DELETE ON legacy_adjustment_source
BEGIN SELECT RAISE(ABORT,'legacy adjustment source is history'); END;

-- The P&L contract of schema 34, with legacy adjustments read from their corrections.
DROP VIEW v_pnl_entry;
CREATE VIEW v_pnl_entry AS
SELECT day AS entry_date,'INCOME' AS section,'SALES' AS head_code,0 AS category_id,
       taxable AS amount,bills AS source_count
  FROM pnl_day WHERE bills>0
UNION ALL SELECT day,'INCOME','ROUND_OFF',0,rounding,bills FROM pnl_day WHERE bills>0 AND rounding<>0
UNION ALL SELECT day,'INCOME','SALES_RETURNS',0,-return_taxable,returns FROM pnl_day WHERE returns>0
UNION ALL SELECT day,'INCOME','ROUND_OFF',0,-return_rounding,0 FROM pnl_day WHERE returns>0 AND return_rounding<>0
UNION ALL SELECT day,'COGS','COGS',0,cost,bills FROM pnl_day WHERE bills>0
UNION ALL SELECT day,'COGS','COGS',0,-return_cost,returns FROM pnl_day WHERE returns>0
UNION ALL SELECT d.damage_date,'EXPENSE','DAMAGES',0,d.cost_value-d.recovered_amount,1
  FROM damage d WHERE COALESCE(d.product_class_snapshot,'SALEABLE')<>'INTERNAL'
UNION ALL SELECT dc.effective_date,'EXPENSE','DAMAGES',0,dc.expense_delta,1
  FROM damage_correction dc JOIN damage d ON d.id=dc.damage_id
  WHERE COALESCE(d.product_class_snapshot,'SALEABLE')<>'INTERNAL'
UNION ALL SELECT c.correction_date,'EXPENSE','STOCK_ADJUSTMENT',0,
       -CAST(ROUND(c.quantity_delta*c.unit_cost/1000.0) AS INTEGER),1
  FROM stock_correction c LEFT JOIN legacy_adjustment_source s ON s.correction_id=c.id
  WHERE c.source_kind IN ('MANUAL','LOT_CORRECTION')
     OR (c.source_kind='LEGACY_MOVEMENT' AND COALESCE(s.ref_type,'')<>'damage_correction')
UNION ALL SELECT e.expense_date,'EXPENSE','EXPENSE_CATEGORY',e.category_id,e.amount,1 FROM expense e
UNION ALL SELECT r.reversal_date,'EXPENSE','EXPENSE_CATEGORY',e.category_id,-r.amount,1
  FROM purchase_expense_reversal r JOIN expense e ON e.id=r.expense_id
UNION ALL SELECT c.reverse_date,'EXPENSE','EXPENSE_CATEGORY',c.previous_category_id,-c.previous_amount,1
  FROM expense_correction c
UNION ALL SELECT c.apply_date,'EXPENSE','EXPENSE_CATEGORY',c.new_category_id,c.new_amount,0
  FROM expense_correction c WHERE c.new_amount>0
UNION ALL SELECT date(sp.period_month||'-01','+1 month','-1 day'),'EXPENSE','SALARIES',0,sp.amount,1
  FROM salary_payment sp
UNION ALL SELECT sc.effective_date,'EXPENSE','SALARIES',0,sc.amount_delta,1 FROM salary_correction sc
UNION ALL SELECT COALESCE(ad.pnl_date,date(ad.period_month||'-01','+1 month','-1 day')),
       'EXPENSE','DEPRECIATION',0,ad.amount,1
  FROM asset_depreciation ad
UNION ALL SELECT a.disposed_date,'OTHER','ASSET_DISPOSAL',0,a.disposal_value-a.disposal_book_value,1
  FROM asset a WHERE a.disposed_date IS NOT NULL
UNION ALL SELECT vr.return_date,'OTHER','VENDOR_RETURN_VARIANCE',0,
       vl.credit_before_tax+CASE WHEN vr.tax_mode='REGULAR' THEN 0 ELSE vl.tax_credit END-vl.cost_value,1
  FROM vendor_return_line vl JOIN vendor_return vr ON vr.id=vl.vendor_return_id
  WHERE COALESCE(vl.product_class_snapshot,'SALEABLE')<>'INTERNAL'
UNION ALL SELECT c.cancellation_date,'OTHER','VENDOR_RETURN_VARIANCE',0,
       -(vl.credit_before_tax+CASE WHEN vr.tax_mode='REGULAR' THEN 0 ELSE vl.tax_credit END-vl.cost_value),1
  FROM vendor_return_cancellation c JOIN vendor_return vr ON vr.id=c.vendor_return_id
  JOIN vendor_return_line vl ON vl.vendor_return_id=vr.id
  WHERE COALESCE(vl.product_class_snapshot,'SALEABLE')<>'INTERNAL';

-- Nothing reads or writes the ledger now. Dropping a table also drops its indexes and
-- triggers. The old count sheets' posted results are the carried-forward corrections
-- above; schema 33 cancelled every unposted one.
DROP TABLE stock_movement;
DROP TABLE stock_movement_type;
DROP TABLE direct_stock_live;
DROP TABLE stock_count_scope;
DROP TABLE stock_count_line;
DROP TABLE stock_count;
DROP VIEW v_product_stock;

-- Settings of removed features: the negative-stock switch (gone since schema 33), the
-- ledger rebuild flag and the daily expiry popup.
DELETE FROM setting WHERE key IN ('stock.allow_negative','maintenance.stock_rebuild_pending',
  'expiry.popup_on_login','expiry.last_popup_date');
