-- A deleted lot keeps its purchase-side tax, so a customer return of units sold from it restocks
-- them under the rate, label and percent they were bought at. A sale line saves the lot's tax per
-- piece but its own label is the sale side: none in Composition and No-GST modes, and the
-- product's rate, which need not be the rate the lot was bought at.
-- Lot IDs are never reused (product_lot is AUTOINCREMENT since schema 29), so the ID keys the row.
-- Only lots saved with tax values (schema 39 onward) have any to keep. Nothing is backfilled:
-- a lot deleted before this version is traced through its purchase line where it had one.
-- The table starts empty and gets one small row per deleted lot, so this step takes no time.

CREATE TABLE deleted_lot_tax (
  lot_id INTEGER PRIMARY KEY,
  tax_rate_id_snapshot INTEGER,
  tax_label_snapshot TEXT,
  tax_rate_pct_snapshot INTEGER,
  unit_tax INTEGER NOT NULL,
  unit_cost_incl_tax INTEGER NOT NULL
) STRICT;

-- Every deletion path (one zero lot, zero lots in bulk, a deleted product's lots) passes here.
CREATE TRIGGER trg_lot_delete_keeps_tax_v40 AFTER DELETE ON product_lot
WHEN OLD.unit_tax IS NOT NULL
BEGIN
  INSERT INTO deleted_lot_tax(lot_id,tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,
    unit_tax,unit_cost_incl_tax)
  VALUES (OLD.id,OLD.tax_rate_id_snapshot,OLD.tax_label_snapshot,OLD.tax_rate_pct_snapshot,
    OLD.unit_tax,OLD.unit_cost_incl_tax);
END;

CREATE TRIGGER trg_deleted_lot_tax_immutable_v40 BEFORE UPDATE ON deleted_lot_tax
BEGIN SELECT RAISE(ABORT,'deleted lot tax is immutable'); END;
CREATE TRIGGER trg_deleted_lot_tax_no_delete_v40 BEFORE DELETE ON deleted_lot_tax
BEGIN SELECT RAISE(ABORT,'deleted lot tax is immutable'); END;
