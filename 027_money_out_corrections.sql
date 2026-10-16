-- Posted money-out rows remain immutable. Corrections are dated, append-only entries.
CREATE TABLE IF NOT EXISTS damage_correction (
  id INTEGER PRIMARY KEY,
  damage_id INTEGER NOT NULL REFERENCES damage(id),
  correction_date TEXT NOT NULL CHECK (correction_date = date(correction_date)),
  previous_qty INTEGER NOT NULL CHECK (previous_qty >= 0),
  new_qty INTEGER NOT NULL CHECK (new_qty >= 0),
  previous_recovered INTEGER NOT NULL CHECK (previous_recovered >= 0),
  new_recovered INTEGER NOT NULL CHECK (new_recovered >= 0),
  new_reason TEXT NOT NULL CHECK (new_reason IN ('EXPIRED','BROKEN','SPOILED','PEST','LOST','CUSTOMER_RETURN','OTHER')),
  new_notes TEXT,
  expense_delta INTEGER NOT NULL,
  correction_reason TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
CREATE INDEX IF NOT EXISTS ix_damage_correction_damage ON damage_correction(damage_id,id DESC);
CREATE INDEX IF NOT EXISTS ix_damage_correction_date ON damage_correction(correction_date);

CREATE TABLE IF NOT EXISTS salary_correction (
  id INTEGER PRIMARY KEY,
  salary_payment_id INTEGER NOT NULL REFERENCES salary_payment(id),
  correction_date TEXT NOT NULL CHECK (correction_date = date(correction_date)),
  previous_amount INTEGER NOT NULL CHECK (previous_amount > 0),
  new_amount INTEGER NOT NULL CHECK (new_amount > 0),
  new_pay_date TEXT NOT NULL CHECK (new_pay_date = date(new_pay_date)),
  new_mode TEXT NOT NULL CHECK (new_mode IN ('CASH','UPI','BANK','OTHER')),
  new_notes TEXT,
  amount_delta INTEGER NOT NULL,
  correction_reason TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
CREATE INDEX IF NOT EXISTS ix_salary_correction_payment ON salary_correction(salary_payment_id,id DESC);
CREATE INDEX IF NOT EXISTS ix_salary_correction_date ON salary_correction(correction_date);

CREATE TRIGGER IF NOT EXISTS trg_damage_correction_no_update BEFORE UPDATE ON damage_correction
BEGIN SELECT RAISE(ABORT, 'posted damage correction is read-only'); END;
CREATE TRIGGER IF NOT EXISTS trg_damage_correction_no_delete BEFORE DELETE ON damage_correction
BEGIN SELECT RAISE(ABORT, 'posted damage correction cannot be deleted'); END;
CREATE TRIGGER IF NOT EXISTS trg_salary_correction_no_update BEFORE UPDATE ON salary_correction
BEGIN SELECT RAISE(ABORT, 'posted salary correction is read-only'); END;
CREATE TRIGGER IF NOT EXISTS trg_salary_correction_no_delete BEFORE DELETE ON salary_correction
BEGIN SELECT RAISE(ABORT, 'posted salary correction cannot be deleted'); END;

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
UNION ALL SELECT e.expense_date, 'EXPENSE', 'EXPENSE_CATEGORY', e.category_id, e.amount
  FROM expense e
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
  FROM vendor_return vr;
