-- Phase 3 business documents remain ledger-backed until the single direct-stock cutover.
ALTER TABLE purchase_line ADD COLUMN line_kind TEXT NOT NULL DEFAULT 'PRODUCT'
  CHECK(line_kind IN ('PRODUCT','INTERNAL','EXPENSE'));
ALTER TABLE purchase ADD COLUMN cancelled_date TEXT CHECK(cancelled_date=date(cancelled_date));
ALTER TABLE product_lot ADD COLUMN direct_reason TEXT;
DROP TRIGGER trg_lot_origin_insert_v29;
CREATE TRIGGER trg_lot_origin_insert_v30 AFTER INSERT ON product_lot
BEGIN
  INSERT INTO direct_stock_origin(lot_id,product_id_snapshot,product_sku_snapshot,
    product_name_snapshot,product_name_local_snapshot,uom_snapshot,product_class_snapshot,
    lot_code_snapshot,batch_no_snapshot,expiry_date_snapshot,quantity,unit_cost,origin_date,
    reason,provenance)
  SELECT NEW.id,NEW.product_id,p.sku,p.name,p.name_local,u.code,p.product_class,
         NEW.lot_code,NEW.batch_no,NEW.expiry_date,NEW.qty_received,NEW.unit_cost,NEW.received_date,
         COALESCE(NULLIF(trim(NEW.direct_reason),''),'Direct or opening entry'),'DIRECT_ENTRY'
  FROM product p JOIN uom u ON u.id=p.uom_id
  WHERE NEW.purchase_line_id IS NULL AND NEW.origin_kind='DIRECT' AND p.id=NEW.product_id;
  UPDATE product_lot SET
    origin_kind=CASE WHEN NEW.purchase_line_id IS NULL THEN 'DIRECT' ELSE 'PURCHASE_LINE' END,
    origin_ref_id=CASE WHEN NEW.purchase_line_id IS NULL
      THEN (SELECT id FROM direct_stock_origin WHERE lot_id=NEW.id) ELSE NEW.purchase_line_id END
  WHERE id=NEW.id AND NEW.origin_kind<>'CUSTOMER_RETURN';
END;
CREATE INDEX ix_purchase_status_date_v30 ON purchase(status,received_date DESC,id DESC);
ALTER TABLE purchase_line ADD COLUMN expense_category_id INTEGER REFERENCES expense_category(id);
ALTER TABLE purchase_line ADD COLUMN expense_category_name_snapshot TEXT;
CREATE INDEX ix_purchase_line_kind ON purchase_line(line_kind,purchase_id);

ALTER TABLE expense ADD COLUMN purchase_line_id INTEGER REFERENCES purchase_line(id);
CREATE UNIQUE INDEX ux_expense_purchase_line ON expense(purchase_line_id)
  WHERE purchase_line_id IS NOT NULL;
CREATE TABLE purchase_expense_reversal(
  expense_id INTEGER PRIMARY KEY REFERENCES expense(id),
  purchase_id INTEGER NOT NULL REFERENCES purchase(id),
  reversal_date TEXT NOT NULL CHECK(reversal_date=date(reversal_date)),
  amount INTEGER NOT NULL CHECK(amount>0)
) STRICT;
CREATE TRIGGER trg_purchase_expense_no_update BEFORE UPDATE ON expense
  WHEN OLD.purchase_line_id IS NOT NULL
  BEGIN SELECT RAISE(ABORT,'purchase expense is immutable'); END;
CREATE TRIGGER trg_purchase_expense_no_delete BEFORE DELETE ON expense
  WHEN OLD.purchase_line_id IS NOT NULL
  BEGIN SELECT RAISE(ABORT,'purchase expense is immutable'); END;

CREATE TABLE vendor_return_cancellation(
  vendor_return_id INTEGER PRIMARY KEY REFERENCES vendor_return(id),
  operation_id TEXT NOT NULL UNIQUE,
  cancelled_at TEXT NOT NULL DEFAULT (datetime('now')),
  cancellation_date TEXT NOT NULL DEFAULT (date('now','localtime'))
    CHECK(cancellation_date=date(cancellation_date)),
  reason TEXT NOT NULL CHECK(length(trim(reason))>0)
) STRICT;
CREATE TRIGGER vendor_balance_return_cancel_insert AFTER INSERT ON vendor_return_cancellation BEGIN
  UPDATE vendor_balance_total SET returns=returns-(SELECT credit_total FROM vendor_return
    WHERE id=NEW.vendor_return_id)
  WHERE vendor_id=(SELECT vendor_id FROM vendor_return WHERE id=NEW.vendor_return_id);
END;

INSERT INTO stock_movement_type(code,name,direction)
VALUES ('VENDOR_RETURN_CANCEL','Vendor return cancelled',1),
       ('INTERNAL_USE','Consumed internally',-1),
       ('DIRECT_ENTRY','Direct lot addition',1);

-- An expense line is a financial source. Stock and its landed cost belong only
-- to product/internal lines. The purchase line itself remains the immutable link.
CREATE TRIGGER trg_purchase_line_expense_kind BEFORE INSERT ON purchase_line
  WHEN (NEW.line_kind='EXPENSE')<>(NEW.expense_category_id IS NOT NULL)
  BEGIN SELECT RAISE(ABORT,'expense category must match purchase line type'); END;
CREATE TRIGGER trg_purchase_line_expense_kind_update BEFORE UPDATE OF line_kind,expense_category_id ON purchase_line
  WHEN (NEW.line_kind='EXPENSE')<>(NEW.expense_category_id IS NOT NULL)
  BEGIN SELECT RAISE(ABORT,'expense category must match purchase line type'); END;

CREATE INDEX ix_vendor_return_cancel_date ON vendor_return_cancellation(cancellation_date);
CREATE INDEX ix_vendor_return_purchase_id_v30 ON vendor_return(purchase_id,id DESC);

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
