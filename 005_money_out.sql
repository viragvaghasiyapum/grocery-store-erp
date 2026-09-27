-- Phase 5: idempotent bulk write-offs and immutable posted money-out records.

ALTER TABLE damage ADD COLUMN bulk_operation_id TEXT;
CREATE INDEX ix_damage_bulk_operation ON damage(bulk_operation_id) WHERE bulk_operation_id IS NOT NULL;

INSERT INTO setting(key,value) VALUES ('expiry.popup_on_login','1')
ON CONFLICT(key) DO NOTHING;

CREATE UNIQUE INDEX ux_salary_regular_employee_month
ON salary_payment(employee_id, period_month) WHERE kind='SALARY';

CREATE TRIGGER trg_damage_no_update BEFORE UPDATE ON damage
BEGIN SELECT RAISE(ABORT, 'posted damage is read-only'); END;
CREATE TRIGGER trg_damage_no_delete BEFORE DELETE ON damage
BEGIN SELECT RAISE(ABORT, 'posted damage cannot be deleted'); END;

CREATE TRIGGER trg_asset_depreciation_no_update BEFORE UPDATE ON asset_depreciation
BEGIN SELECT RAISE(ABORT, 'posted depreciation is read-only'); END;
CREATE TRIGGER trg_asset_depreciation_no_delete BEFORE DELETE ON asset_depreciation
BEGIN SELECT RAISE(ABORT, 'posted depreciation cannot be deleted'); END;

-- The original expiry view predates lot blocking and local product names.
DROP VIEW v_expiry_alert;
CREATE VIEW v_expiry_alert AS
SELECT *,
       CASE WHEN days_left < 0  THEN 'EXPIRED'
            WHEN days_left <= 7 THEN 'CRITICAL'
            ELSE 'SOON' END AS bucket
FROM (
  SELECT l.id AS lot_id, l.lot_code, l.batch_no, l.vendor_id,
         p.id AS product_id, p.sku, p.name, p.name_local,
         l.expiry_date, l.qty_on_hand,
         CAST(julianday(l.expiry_date) - julianday('now','localtime','start of day') AS INTEGER) AS days_left,
         COALESCE(p.expiry_alert_days, c.expiry_alert_days, pc.expiry_alert_days,
                  CAST((SELECT value FROM setting WHERE key = 'expiry.default_alert_days') AS INTEGER),
                  30) AS alert_days,
         CAST(ROUND(l.qty_on_hand * l.unit_cost / 1000.0) AS INTEGER) AS value_at_cost
  FROM product_lot l
  JOIN product  p       ON p.id  = l.product_id
  JOIN category c       ON c.id  = p.category_id
  LEFT JOIN category pc ON pc.id = c.parent_id
  WHERE l.qty_on_hand > 0 AND l.is_active = 1 AND l.expiry_date IS NOT NULL
)
WHERE days_left <= alert_days;

PRAGMA user_version = 5;
