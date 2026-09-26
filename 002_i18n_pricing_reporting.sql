-- 002_i18n_pricing_reporting.sql
-- Applied by the migration runner inside one transaction; runner sets PRAGMA user_version = 2.
-- Gujarati names (name_local) for user-maintained masters
ALTER TABLE category         ADD COLUMN name_local TEXT;
ALTER TABLE expense_category ADD COLUMN name_local TEXT;
ALTER TABLE uom              ADD COLUMN name_local TEXT;

-- Snapshots on bill lines: bilingual reprints + GST HSN summary
ALTER TABLE sale_line ADD COLUMN product_name_local TEXT;
ALTER TABLE sale_line ADD COLUMN hsn_code           TEXT;

-- Pricing INPUTS persisted on drafts, so completion can recompute everything
ALTER TABLE sale      ADD COLUMN bill_discount      INTEGER NOT NULL DEFAULT 0;  -- fx3, as entered
ALTER TABLE sale_line ADD COLUMN line_discount      INTEGER NOT NULL DEFAULT 0;  -- fx3, as entered
ALTER TABLE sale_line ADD COLUMN price_includes_tax INTEGER NOT NULL DEFAULT 1
                      CHECK (price_includes_tax IN (0,1));                       -- snapshot

-- Stock adjustments must reach the P&L; this index serves that branch
CREATE INDEX ix_move_type_date ON stock_movement(type_code, move_date);

INSERT INTO setting(key, value) VALUES
  ('ui.language',             'en-IN'),   -- en-IN (default) | gu-IN. Numbers are always English digits (fixed en-IN format, not a setting)
  ('print.language',          'en-IN'),   -- en-IN (default) | gu-IN: labels on printed bills/labels
  ('print.bilingual_names',   '1'),       -- print the Gujarati name (name_local) under the English name
  ('books.locked_until',      ''),        -- 'YYYY-MM-DD': no entries on/before this date
  ('business.day_start_hour', '0')        -- bills before this hour belong to the previous day
ON CONFLICT(key) DO NOTHING;

-- P&L view now returns CODES instead of English text; the UI translates.
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
       -CAST(ROUND(m.qty * m.unit_cost / 1000.0) AS INTEGER)   -- shortage = expense, excess = negative expense
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
SELECT a.disposed_date, 'INCOME', 'ASSET_DISPOSAL', a.id,
       a.disposal_value - COALESCE(
         (SELECT ad.closing_value FROM asset_depreciation ad
           WHERE ad.asset_id = a.id ORDER BY ad.period_month DESC LIMIT 1), a.cost)
  FROM asset a WHERE a.disposed_date IS NOT NULL;
