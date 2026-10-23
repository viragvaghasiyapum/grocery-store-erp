-- Phase 6: one P&L source contract, independent calendar and fiscal annual
-- closes, dated money-out corrections and effective-dated payroll/asset terms.
-- A year is locked from the eighth date after it ends; the application derives
-- that from the business date, so the single legacy lock date is retired.
DELETE FROM setting WHERE key='books.locked_until';

-- Annual close snapshots replace the unused Phase 1 foundation table. A locked
-- year's report reads only these rows; the checksum covers the header and lines.
DROP TABLE annual_pnl_snapshot;
CREATE TABLE annual_pnl_close(
  period_kind TEXT NOT NULL CHECK(period_kind IN ('CALENDAR','FISCAL')),
  year_start TEXT NOT NULL CHECK(year_start=date(year_start)),
  year_end TEXT NOT NULL CHECK(year_end=date(year_end) AND year_end>year_start),
  edit_cutoff TEXT NOT NULL CHECK(edit_cutoff=date(edit_cutoff) AND edit_cutoff>year_end),
  generated_on TEXT NOT NULL CHECK(generated_on=date(generated_on)),
  generated_at TEXT NOT NULL,
  format_version INTEGER NOT NULL CHECK(format_version>0),
  line_count INTEGER NOT NULL CHECK(line_count>=0),
  net_profit INTEGER NOT NULL,
  checksum TEXT NOT NULL CHECK(length(checksum)=64),
  PRIMARY KEY(period_kind,year_start)
) STRICT, WITHOUT ROWID;
CREATE TABLE annual_pnl_line(
  period_kind TEXT NOT NULL,
  year_start TEXT NOT NULL,
  month TEXT NOT NULL CHECK(month GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]'),
  section TEXT NOT NULL CHECK(section IN ('INCOME','COGS','EXPENSE','OTHER')),
  head_code TEXT NOT NULL,
  category_id INTEGER NOT NULL DEFAULT 0,
  category_name TEXT,
  category_name_local TEXT,
  amount INTEGER NOT NULL,
  source_count INTEGER NOT NULL CHECK(source_count>=0),
  PRIMARY KEY(period_kind,year_start,month,section,head_code,category_id),
  FOREIGN KEY(period_kind,year_start) REFERENCES annual_pnl_close(period_kind,year_start)
) STRICT, WITHOUT ROWID;
CREATE TRIGGER trg_annual_close_no_update BEFORE UPDATE ON annual_pnl_close
BEGIN SELECT RAISE(ABORT,'finalized annual report is immutable'); END;
CREATE TRIGGER trg_annual_close_no_delete BEFORE DELETE ON annual_pnl_close
BEGIN SELECT RAISE(ABORT,'finalized annual report is immutable'); END;
CREATE TRIGGER trg_annual_line_no_update BEFORE UPDATE ON annual_pnl_line
BEGIN SELECT RAISE(ABORT,'finalized annual report is immutable'); END;
CREATE TRIGGER trg_annual_line_no_delete BEFORE DELETE ON annual_pnl_line
BEGIN SELECT RAISE(ABORT,'finalized annual report is immutable'); END;

-- A return refunds its share of the bill round-off, so that income reverses too.
ALTER TABLE pnl_day ADD COLUMN return_rounding INTEGER NOT NULL DEFAULT 0;
UPDATE pnl_day SET return_rounding=(
  SELECT COALESCE(SUM(r.refund_total-r.taxable_total-r.tax_total),0)
  FROM sale_return r WHERE r.return_date=pnl_day.day)
WHERE returns>0;
DROP TRIGGER trg_pnl_return_insert;
DROP TRIGGER trg_pnl_return_update;
DROP TRIGGER trg_pnl_return_delete;
CREATE TRIGGER trg_pnl_return_insert AFTER INSERT ON sale_return BEGIN
  INSERT INTO pnl_day(day,returns,return_taxable,return_cost,return_rounding)
  VALUES(NEW.return_date,1,NEW.taxable_total,NEW.cost_total,
         NEW.refund_total-NEW.taxable_total-NEW.tax_total)
  ON CONFLICT(day) DO UPDATE SET
    returns=returns+1,return_taxable=return_taxable+excluded.return_taxable,
    return_cost=return_cost+excluded.return_cost,
    return_rounding=return_rounding+excluded.return_rounding;
END;
CREATE TRIGGER trg_pnl_return_update
AFTER UPDATE OF return_date,taxable_total,tax_total,refund_total,cost_total ON sale_return
WHEN OLD.return_date IS NOT NEW.return_date OR OLD.taxable_total IS NOT NEW.taxable_total OR
     OLD.tax_total IS NOT NEW.tax_total OR OLD.refund_total IS NOT NEW.refund_total OR
     OLD.cost_total IS NOT NEW.cost_total BEGIN
  UPDATE pnl_day SET returns=returns-1,
    return_taxable=return_taxable-OLD.taxable_total,return_cost=return_cost-OLD.cost_total,
    return_rounding=return_rounding-(OLD.refund_total-OLD.taxable_total-OLD.tax_total)
  WHERE day=OLD.return_date;
  INSERT INTO pnl_day(day,returns,return_taxable,return_cost,return_rounding)
  VALUES(NEW.return_date,1,NEW.taxable_total,NEW.cost_total,
         NEW.refund_total-NEW.taxable_total-NEW.tax_total)
  ON CONFLICT(day) DO UPDATE SET
    returns=returns+1,return_taxable=return_taxable+excluded.return_taxable,
    return_cost=return_cost+excluded.return_cost,
    return_rounding=return_rounding+excluded.return_rounding;
END;
CREATE TRIGGER trg_pnl_return_delete AFTER DELETE ON sale_return BEGIN
  UPDATE pnl_day SET returns=returns-1,
    return_taxable=return_taxable-OLD.taxable_total,return_cost=return_cost-OLD.cost_total,
    return_rounding=return_rounding-(OLD.refund_total-OLD.taxable_total-OLD.tax_total)
  WHERE day=OLD.return_date;
END;

DROP VIEW v_pnl_entry;

-- Corrections record the date their P&L difference belongs to: the original
-- date while both of its annual periods are open, otherwise the correction date.
CREATE TABLE damage_correction_v34(
  id INTEGER PRIMARY KEY,
  damage_id INTEGER NOT NULL REFERENCES damage(id),
  correction_date TEXT NOT NULL CHECK(correction_date=date(correction_date)),
  effective_date TEXT NOT NULL CHECK(effective_date=date(effective_date)),
  treatment TEXT NOT NULL CHECK(treatment IN ('OPEN_PERIOD','CURRENT_DATED')),
  previous_qty INTEGER NOT NULL CHECK(previous_qty>=0),
  new_qty INTEGER NOT NULL CHECK(new_qty>=0),
  previous_recovered INTEGER NOT NULL CHECK(previous_recovered>=0),
  new_recovered INTEGER NOT NULL CHECK(new_recovered>=0),
  new_reason TEXT NOT NULL CHECK(new_reason IN ('EXPIRED','BROKEN','SPOILED','PEST','LOST','CUSTOMER_RETURN','OTHER')),
  new_notes TEXT,
  expense_delta INTEGER NOT NULL,
  restored_lot_id INTEGER,
  correction_reason TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT(datetime('now','localtime'))
) STRICT;
INSERT INTO damage_correction_v34(id,damage_id,correction_date,effective_date,treatment,
  previous_qty,new_qty,previous_recovered,new_recovered,new_reason,new_notes,expense_delta,
  correction_reason,created_at)
SELECT id,damage_id,correction_date,correction_date,'CURRENT_DATED',previous_qty,new_qty,
  previous_recovered,new_recovered,new_reason,new_notes,expense_delta,correction_reason,created_at
FROM damage_correction;
DROP TABLE damage_correction;
ALTER TABLE damage_correction_v34 RENAME TO damage_correction;
CREATE INDEX ix_damage_correction_damage ON damage_correction(damage_id,id DESC);
CREATE INDEX ix_damage_correction_effective ON damage_correction(effective_date);
CREATE TRIGGER trg_damage_correction_no_update BEFORE UPDATE ON damage_correction
BEGIN SELECT RAISE(ABORT,'posted damage correction is read-only'); END;
CREATE TRIGGER trg_damage_correction_no_delete BEFORE DELETE ON damage_correction
BEGIN SELECT RAISE(ABORT,'posted damage correction cannot be deleted'); END;

-- A salary payment entered by mistake is voided by a correction to zero.
CREATE TABLE salary_correction_v34(
  id INTEGER PRIMARY KEY,
  salary_payment_id INTEGER NOT NULL REFERENCES salary_payment(id),
  correction_date TEXT NOT NULL CHECK(correction_date=date(correction_date)),
  effective_date TEXT NOT NULL CHECK(effective_date=date(effective_date)),
  treatment TEXT NOT NULL CHECK(treatment IN ('OPEN_PERIOD','CURRENT_DATED')),
  kind TEXT NOT NULL CHECK(kind IN ('EDIT','VOID')),
  previous_amount INTEGER NOT NULL CHECK(previous_amount>0),
  new_amount INTEGER NOT NULL CHECK(new_amount>=0 AND (kind='VOID')=(new_amount=0)),
  new_pay_date TEXT NOT NULL CHECK(new_pay_date=date(new_pay_date)),
  new_mode TEXT NOT NULL CHECK(new_mode IN ('CASH','UPI','BANK','OTHER')),
  new_notes TEXT,
  amount_delta INTEGER NOT NULL,
  correction_reason TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT(datetime('now','localtime'))
) STRICT;
INSERT INTO salary_correction_v34(id,salary_payment_id,correction_date,effective_date,treatment,
  kind,previous_amount,new_amount,new_pay_date,new_mode,new_notes,amount_delta,correction_reason,created_at)
SELECT id,salary_payment_id,correction_date,correction_date,'CURRENT_DATED','EDIT',previous_amount,
  new_amount,new_pay_date,new_mode,new_notes,amount_delta,correction_reason,created_at
FROM salary_correction;
DROP TABLE salary_correction;
ALTER TABLE salary_correction_v34 RENAME TO salary_correction;
CREATE INDEX ix_salary_correction_payment ON salary_correction(salary_payment_id,id DESC);
CREATE INDEX ix_salary_correction_effective ON salary_correction(effective_date);
CREATE TRIGGER trg_salary_correction_no_update BEFORE UPDATE ON salary_correction
BEGIN SELECT RAISE(ABORT,'posted salary correction is read-only'); END;
CREATE TRIGGER trg_salary_correction_no_delete BEFORE DELETE ON salary_correction
BEGIN SELECT RAISE(ABORT,'posted salary correction cannot be deleted'); END;

-- Expenses keep their original row; edits and voids are dated corrections. Each
-- correction reverses the previous value on reverse_date and applies the new one
-- on apply_date, so a category or date change moves exactly what it should.
CREATE TABLE expense_correction(
  id INTEGER PRIMARY KEY,
  expense_id INTEGER NOT NULL REFERENCES expense(id),
  correction_date TEXT NOT NULL CHECK(correction_date=date(correction_date)),
  treatment TEXT NOT NULL CHECK(treatment IN ('OPEN_PERIOD','CURRENT_DATED')),
  kind TEXT NOT NULL CHECK(kind IN ('EDIT','VOID')),
  previous_date TEXT NOT NULL CHECK(previous_date=date(previous_date)),
  previous_category_id INTEGER NOT NULL REFERENCES expense_category(id),
  previous_amount INTEGER NOT NULL CHECK(previous_amount>0),
  new_date TEXT NOT NULL CHECK(new_date=date(new_date)),
  new_category_id INTEGER NOT NULL REFERENCES expense_category(id),
  new_amount INTEGER NOT NULL CHECK(new_amount>=0 AND (kind='VOID')=(new_amount=0)),
  new_mode TEXT NOT NULL CHECK(new_mode IN ('CASH','UPI','BANK','CARD','OTHER')),
  new_paid_to TEXT,
  new_reference TEXT,
  new_notes TEXT,
  reverse_date TEXT NOT NULL CHECK(reverse_date=date(reverse_date)),
  apply_date TEXT NOT NULL CHECK(apply_date=date(apply_date)),
  correction_reason TEXT NOT NULL CHECK(length(trim(correction_reason))>0),
  created_at TEXT NOT NULL DEFAULT(datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_expense_correction_expense ON expense_correction(expense_id,id DESC);
CREATE INDEX ix_expense_correction_new_date ON expense_correction(new_date,expense_id);
CREATE INDEX ix_expense_correction_reverse ON expense_correction(reverse_date);
CREATE INDEX ix_expense_correction_apply ON expense_correction(apply_date);
CREATE TRIGGER trg_expense_correction_no_update BEFORE UPDATE ON expense_correction
BEGIN SELECT RAISE(ABORT,'posted expense correction is read-only'); END;
CREATE TRIGGER trg_expense_correction_no_delete BEFORE DELETE ON expense_correction
BEGIN SELECT RAISE(ABORT,'posted expense correction cannot be deleted'); END;
CREATE TRIGGER trg_expense_no_update_v34 BEFORE UPDATE ON expense
BEGIN SELECT RAISE(ABORT,'posted expense is read-only; post a correction'); END;
CREATE TRIGGER trg_expense_no_delete_v34 BEFORE DELETE ON expense
BEGIN SELECT RAISE(ABORT,'posted expense cannot be deleted'); END;

-- Monthly salary is a dated term. The newest term effective on or before a month
-- pre-fills that month's salary run; posted payments never change with it.
CREATE TABLE employee_salary_term(
  id INTEGER PRIMARY KEY,
  employee_id INTEGER NOT NULL REFERENCES employee(id),
  effective_month TEXT NOT NULL CHECK(effective_month GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]'),
  monthly_salary INTEGER NOT NULL CHECK(monthly_salary>=0),
  reason TEXT NOT NULL CHECK(reason IN ('INITIAL','CHANGE','RECALCULATION')),
  created_at TEXT NOT NULL DEFAULT(datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_employee_salary_term ON employee_salary_term(employee_id,effective_month,id);
INSERT INTO employee_salary_term(employee_id,effective_month,monthly_salary,reason)
SELECT id,COALESCE(substr(join_date,1,7),'2000-01'),monthly_salary,'INITIAL' FROM employee;
CREATE TRIGGER trg_salary_term_no_update BEFORE UPDATE ON employee_salary_term
BEGIN SELECT RAISE(ABORT,'salary terms are history'); END;
CREATE TRIGGER trg_salary_term_no_delete BEFORE DELETE ON employee_salary_term
BEGIN SELECT RAISE(ABORT,'salary terms are history'); END;

-- Depreciation terms are dated in the same way. The newest term effective on or
-- before a month sets its method, rate and salvage; a cost or in-use correction
-- starts a fresh schedule only while every posted month is still open.
CREATE TABLE asset_term(
  id INTEGER PRIMARY KEY,
  asset_id INTEGER NOT NULL REFERENCES asset(id),
  effective_month TEXT NOT NULL CHECK(effective_month GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]'),
  cost INTEGER NOT NULL CHECK(cost>0),
  in_use_from TEXT NOT NULL CHECK(in_use_from=date(in_use_from)),
  method TEXT NOT NULL CHECK(method IN ('WDV','SLM')),
  rate_pct INTEGER NOT NULL CHECK(rate_pct BETWEEN 0 AND 100000),
  salvage_value INTEGER NOT NULL CHECK(salvage_value>=0 AND salvage_value<=cost),
  reason TEXT NOT NULL CHECK(reason IN ('INITIAL','CHANGE','RECALCULATION')),
  created_at TEXT NOT NULL DEFAULT(datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_asset_term ON asset_term(asset_id,effective_month,id);
INSERT INTO asset_term(asset_id,effective_month,cost,in_use_from,method,rate_pct,salvage_value,reason)
SELECT id,substr(in_use_from,1,7),cost,in_use_from,method,rate_pct,salvage_value,'INITIAL' FROM asset;
CREATE TRIGGER trg_asset_term_no_update BEFORE UPDATE ON asset_term
BEGIN SELECT RAISE(ABORT,'asset terms are history'); END;
CREATE TRIGGER trg_asset_term_no_delete BEFORE DELETE ON asset_term
BEGIN SELECT RAISE(ABORT,'asset terms are history'); END;

-- A month posted after its year locked keeps its place in the schedule, but its
-- charge belongs to the open period in which it was posted.
ALTER TABLE asset_depreciation ADD COLUMN pnl_date TEXT CHECK(pnl_date IS NULL OR pnl_date=date(pnl_date));
-- Disposal saves the book value it was measured against.
ALTER TABLE asset ADD COLUMN disposal_book_value INTEGER;
UPDATE asset SET disposal_book_value=COALESCE((SELECT d.closing_value FROM asset_depreciation d
  WHERE d.asset_id=asset.id ORDER BY d.period_month DESC LIMIT 1),cost)
WHERE disposed_date IS NOT NULL;
-- An open month may be recalculated after a term change; a finalized one may not.
DROP TRIGGER trg_asset_depreciation_no_delete;
CREATE TRIGGER trg_asset_depreciation_finalized_delete BEFORE DELETE ON asset_depreciation
WHEN OLD.pnl_date IS NOT NULL OR EXISTS(SELECT 1 FROM annual_pnl_close c
  WHERE date(OLD.period_month||'-01','+1 month','-1 day') BETWEEN c.year_start AND c.year_end)
  OR EXISTS(SELECT 1 FROM asset a WHERE a.id=OLD.asset_id AND a.disposed_date IS NOT NULL)
BEGIN SELECT RAISE(ABORT,'posted depreciation of a finalized or disposed period cannot change'); END;

-- The one P&L source contract. Sale and return heads come from their bounded
-- daily projection of saved snapshots; every other head reads its dated source.
-- Internal-use stock has no automatic head. Reports filter on entry_date, which
-- SQLite pushes into each branch.
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
UNION ALL SELECT m.move_date,'EXPENSE','STOCK_ADJUSTMENT',0,
       -CAST(ROUND(m.qty*m.unit_cost/1000.0) AS INTEGER),1
  FROM stock_movement m WHERE m.type_code='ADJUSTMENT' AND COALESCE(m.ref_type,'')<>'damage_correction'
UNION ALL SELECT c.correction_date,'EXPENSE','STOCK_ADJUSTMENT',0,
       -CAST(ROUND(c.quantity_delta*c.unit_cost/1000.0) AS INTEGER),1
  FROM stock_correction c WHERE c.source_kind IN ('MANUAL','LOT_CORRECTION')
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

-- Lots that differed from their source documents in the newest full check. Weekly
-- maintenance replaces the rows; the reconciliation report rechecks only these lots
-- instead of rescanning all sale history interactively.
CREATE TABLE lot_source_issue(
  lot_id INTEGER PRIMARY KEY,
  product_id INTEGER NOT NULL,
  actual INTEGER NOT NULL,
  expected INTEGER NOT NULL,
  found_on TEXT NOT NULL CHECK(found_on=date(found_on))
) STRICT;
CREATE INDEX ix_lot_source_issue_product ON lot_source_issue(product_id,lot_id);

-- Date lookups used by the P&L contract and annual close bounds.
CREATE INDEX ix_purchase_expense_reversal_date ON purchase_expense_reversal(reversal_date);
CREATE INDEX ix_vendor_return_date ON vendor_return(return_date);
CREATE INDEX ix_asset_disposed ON asset(disposed_date) WHERE disposed_date IS NOT NULL;
