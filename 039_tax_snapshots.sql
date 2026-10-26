-- Tax snapshots: every purchase line, lot and sale line keeps the tax label it was saved with and
-- one piece's purchase price before tax, its tax and its price including tax, so each row adds up
-- on screen. The booked cost columns (purchase_line.landed_unit_cost, product_lot.unit_cost,
-- sale_line.unit_cost) keep their meaning: before tax in Regular mode, including tax otherwise.
-- Nothing posted is backfilled; rows saved before this version show no values in the new columns.
-- Adding a column rewrites no rows, but SQLite checks every existing row of a table that has CHECK
-- constraints after each ADD COLUMN. On the full-size store (5.2 million sale lines) this step took
-- 37 s from a cold disk and 12 s cached, most of it for the three sale_line columns.

-- The draft's chosen tax rate (a live link) and, once posted, its label and per-piece values.
ALTER TABLE purchase_line ADD COLUMN tax_rate_id INTEGER;
ALTER TABLE purchase_line ADD COLUMN tax_label_snapshot TEXT;
ALTER TABLE purchase_line ADD COLUMN landed_unit_tax INTEGER;
ALTER TABLE purchase_line ADD COLUMN landed_unit_cost_incl_tax INTEGER;

-- A lot's purchase-side tax, saved when the lot is created and never changed.
ALTER TABLE product_lot ADD COLUMN tax_rate_id_snapshot INTEGER;
ALTER TABLE product_lot ADD COLUMN tax_label_snapshot TEXT;
ALTER TABLE product_lot ADD COLUMN tax_rate_pct_snapshot INTEGER;
ALTER TABLE product_lot ADD COLUMN unit_tax INTEGER;
ALTER TABLE product_lot ADD COLUMN unit_cost_incl_tax INTEGER;

-- The sale side's label (saved at completion) and the cost side copied from the lot.
ALTER TABLE sale_line ADD COLUMN tax_label_snapshot TEXT;
ALTER TABLE sale_line ADD COLUMN unit_cost_tax INTEGER;
ALTER TABLE sale_line ADD COLUMN unit_cost_incl_tax INTEGER;

-- Returns copy the label of the line they return.
ALTER TABLE sale_return_line ADD COLUMN tax_label_snapshot TEXT;
ALTER TABLE vendor_return_line ADD COLUMN tax_label_snapshot TEXT;

-- Both values or neither; the tax is never negative, and the booked cost is either the price
-- before tax (Regular) or the price including tax.
CREATE TRIGGER trg_lot_tax_insert_v39 BEFORE INSERT ON product_lot
WHEN (NEW.unit_tax IS NULL) <> (NEW.unit_cost_incl_tax IS NULL)
  OR NEW.unit_tax < 0
  OR (NEW.unit_tax IS NOT NULL AND NEW.unit_cost <> NEW.unit_cost_incl_tax - NEW.unit_tax
      AND NEW.unit_cost <> NEW.unit_cost_incl_tax)
BEGIN SELECT RAISE(ABORT,'lot tax values are inconsistent'); END;

CREATE TRIGGER trg_lot_cost_immutable_v39 BEFORE UPDATE OF
  unit_cost,tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,unit_tax,unit_cost_incl_tax
  ON product_lot
WHEN NEW.unit_cost IS NOT OLD.unit_cost
  OR NEW.tax_rate_id_snapshot IS NOT OLD.tax_rate_id_snapshot
  OR NEW.tax_label_snapshot IS NOT OLD.tax_label_snapshot
  OR NEW.tax_rate_pct_snapshot IS NOT OLD.tax_rate_pct_snapshot
  OR NEW.unit_tax IS NOT OLD.unit_tax
  OR NEW.unit_cost_incl_tax IS NOT OLD.unit_cost_incl_tax
BEGIN SELECT RAISE(ABORT,'lot cost and tax are fixed when the lot is created'); END;

-- Every selling price and MRP includes tax. Demo and test stores may hold products that excluded
-- it: their customer price grows by the tax a bill added (only Regular mode charged any), capped
-- at MRP, and the lot detail-version trigger counts the change as a price edit. Lines already on
-- held bills keep their own flag and still price as they were added.
-- Prices are fx3 and rates fx3 percent (5000 = 5%), so price × (1 + rate) rounds half up exactly
-- as a bill computed the tax it added.
UPDATE product_lot SET selling_price =
    CASE WHEN product_lot.mrp IS NOT NULL AND grown.price > product_lot.mrp
         THEN product_lot.mrp ELSE grown.price END
FROM (SELECT l.id AS lot_id,
             (l.selling_price * (100000 + COALESCE(t.rate_pct, 0)) + 50000) / 100000 AS price
      FROM product_lot l JOIN product p ON p.id=l.product_id
      LEFT JOIN tax_rate t ON t.id=p.tax_rate_id
           AND COALESCE((SELECT value FROM setting WHERE key='tax.mode'),'REGULAR')='REGULAR'
      WHERE p.price_includes_tax=0) grown
WHERE product_lot.id=grown.lot_id;

UPDATE product SET default_selling_price =
    CASE WHEN product.default_mrp IS NOT NULL AND grown.price > product.default_mrp
         THEN product.default_mrp ELSE grown.price END
FROM (SELECT p.id AS product_id,
             (p.default_selling_price * (100000 + COALESCE(t.rate_pct, 0)) + 50000) / 100000 AS price
      FROM product p LEFT JOIN tax_rate t ON t.id=p.tax_rate_id
           AND COALESCE((SELECT value FROM setting WHERE key='tax.mode'),'REGULAR')='REGULAR'
      WHERE p.price_includes_tax=0 AND p.default_selling_price IS NOT NULL) grown
WHERE product.id=grown.product_id;

UPDATE product SET price_includes_tax=1 WHERE price_includes_tax=0;

-- The column stays because shipped migrations and saved sale lines name it.
CREATE TRIGGER trg_product_prices_include_tax_v39 BEFORE INSERT ON product
WHEN NEW.price_includes_tax <> 1
BEGIN SELECT RAISE(ABORT,'product prices must include tax'); END;
CREATE TRIGGER trg_product_prices_include_tax_update_v39 BEFORE UPDATE OF price_includes_tax ON product
WHEN NEW.price_includes_tax <> 1
BEGIN SELECT RAISE(ABORT,'product prices must include tax'); END;
