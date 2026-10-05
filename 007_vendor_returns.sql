-- Supplier credit notes are immutable documents. Stock, payable and variance post together.
CREATE TABLE vendor_return (
  id                 INTEGER PRIMARY KEY,
  doc_no             TEXT NOT NULL UNIQUE,
  vendor_id          INTEGER NOT NULL REFERENCES vendor(id),
  purchase_id        INTEGER NOT NULL REFERENCES purchase(id),
  return_date        TEXT NOT NULL CHECK (return_date = date(return_date)),
  vendor_credit_no   TEXT NOT NULL,
  reason             TEXT NOT NULL,
  tax_mode           TEXT NOT NULL CHECK (tax_mode IN ('REGULAR','COMPOSITION','NONE')),
  credit_before_tax  INTEGER NOT NULL CHECK (credit_before_tax >= 0),
  tax_credit         INTEGER NOT NULL CHECK (tax_credit >= 0),
  credit_total       INTEGER NOT NULL CHECK (credit_total = credit_before_tax + tax_credit),
  cost_total         INTEGER NOT NULL CHECK (cost_total >= 0),
  created_at         TEXT NOT NULL DEFAULT (datetime('now','localtime')),
  UNIQUE (vendor_id, vendor_credit_no)
) STRICT;
CREATE INDEX ix_vendor_return_vendor ON vendor_return(vendor_id, return_date);

CREATE TABLE vendor_return_line (
  id                 INTEGER PRIMARY KEY,
  vendor_return_id   INTEGER NOT NULL REFERENCES vendor_return(id),
  product_id         INTEGER NOT NULL REFERENCES product(id),
  lot_id             INTEGER NOT NULL REFERENCES product_lot(id),
  qty                INTEGER NOT NULL CHECK (qty > 0),
  credit_before_tax  INTEGER NOT NULL CHECK (credit_before_tax >= 0),
  tax_credit         INTEGER NOT NULL CHECK (tax_credit >= 0),
  cost_value         INTEGER NOT NULL CHECK (cost_value >= 0),
  UNIQUE (vendor_return_id, lot_id)
) STRICT;
CREATE INDEX ix_vendor_return_line_lot ON vendor_return_line(lot_id);

CREATE TRIGGER trg_vendor_return_lock_upd BEFORE UPDATE ON vendor_return
BEGIN SELECT RAISE(ABORT, 'vendor return is finalized'); END;
CREATE TRIGGER trg_vendor_return_lock_del BEFORE DELETE ON vendor_return
BEGIN SELECT RAISE(ABORT, 'vendor return is finalized'); END;
CREATE TRIGGER trg_vendor_return_line_lock_upd BEFORE UPDATE ON vendor_return_line
BEGIN SELECT RAISE(ABORT, 'vendor return line is finalized'); END;
CREATE TRIGGER trg_vendor_return_line_lock_del BEFORE DELETE ON vendor_return_line
BEGIN SELECT RAISE(ABORT, 'vendor return line is finalized'); END;

INSERT INTO stock_movement_type(code,name,direction)
VALUES ('VENDOR_RETURN','Returned to vendor',-1);

DROP VIEW v_vendor_balance;
CREATE VIEW v_vendor_balance AS
SELECT v.id AS vendor_id, v.name,
       v.opening_balance
       + COALESCE((SELECT SUM(p.grand_total) FROM purchase p
                    WHERE p.vendor_id=v.id AND p.status='POSTED'),0)
       - COALESCE((SELECT SUM(vp.amount) FROM vendor_payment vp WHERE vp.vendor_id=v.id),0)
       - COALESCE((SELECT SUM(vr.credit_total) FROM vendor_return vr WHERE vr.vendor_id=v.id),0)
         AS balance_due
FROM vendor v;

DROP VIEW v_pnl_entry;
CREATE VIEW v_pnl_entry AS
SELECT s.sale_date AS entry_date, 'INCOME' AS section, 'SALES' AS head_code,
       NULL AS ref_id, s.taxable_total AS amount
  FROM sale s WHERE s.status = 'COMPLETED'
UNION ALL
SELECT s.sale_date, 'INCOME', 'ROUND_OFF', NULL, s.round_off
  FROM sale s WHERE s.status = 'COMPLETED' AND s.round_off <> 0
UNION ALL
SELECT r.return_date, 'INCOME', 'SALES_RETURNS', NULL, -r.taxable_total
  FROM sale_return r
UNION ALL
SELECT s.sale_date, 'COGS', 'COGS', NULL, s.cost_total
  FROM sale s WHERE s.status = 'COMPLETED'
UNION ALL
SELECT r.return_date, 'COGS', 'COGS', NULL, -r.cost_total
  FROM sale_return r
UNION ALL
SELECT d.damage_date, 'EXPENSE', 'DAMAGES', NULL, d.cost_value - d.recovered_amount
  FROM damage d
UNION ALL
SELECT m.move_date, 'EXPENSE', 'STOCK_ADJUSTMENT', NULL,
       -CAST(ROUND(m.qty * m.unit_cost / 1000.0) AS INTEGER)
  FROM stock_movement m WHERE m.type_code = 'ADJUSTMENT'
UNION ALL
SELECT e.expense_date, 'EXPENSE', 'EXPENSE_CATEGORY', e.category_id, e.amount
  FROM expense e
UNION ALL
SELECT date(sp.period_month || '-01', '+1 month', '-1 day'), 'EXPENSE', 'SALARIES', NULL, sp.amount
  FROM salary_payment sp
UNION ALL
SELECT date(ad.period_month || '-01', '+1 month', '-1 day'), 'EXPENSE', 'DEPRECIATION', NULL, ad.amount
  FROM asset_depreciation ad
UNION ALL
SELECT a.disposed_date, 'OTHER', 'ASSET_DISPOSAL', a.id,
       a.disposal_value - COALESCE(
         (SELECT ad.closing_value FROM asset_depreciation ad
           WHERE ad.asset_id = a.id ORDER BY ad.period_month DESC LIMIT 1), a.cost)
  FROM asset a WHERE a.disposed_date IS NOT NULL
UNION ALL
SELECT vr.return_date, 'OTHER', 'VENDOR_RETURN_VARIANCE', NULL,
       vr.credit_before_tax + CASE WHEN vr.tax_mode='REGULAR' THEN 0 ELSE vr.tax_credit END - vr.cost_total
  FROM vendor_return vr;

PRAGMA user_version = 7;
