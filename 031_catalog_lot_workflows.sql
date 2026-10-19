-- Phase 4: keep catalog timestamps current even for stock and import writes.
ALTER TABLE product_lot ADD COLUMN updated_at TEXT NOT NULL DEFAULT '';
UPDATE product_lot SET updated_at=created_at WHERE updated_at='';

-- Future sale returns can rebuild deleted masters from the saved sale line.
-- Existing lines remain explicitly incomplete and require operator review.
ALTER TABLE sale_line ADD COLUMN category_id_snapshot INTEGER;
ALTER TABLE sale_line ADD COLUMN category_name_snapshot TEXT;
ALTER TABLE sale_line ADD COLUMN uom_id_snapshot INTEGER;
ALTER TABLE sale_line ADD COLUMN tax_rate_id_snapshot INTEGER;
ALTER TABLE sale_line ADD COLUMN track_expiry_snapshot INTEGER;
ALTER TABLE sale_line ADD COLUMN is_loose_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN category_id_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN category_name_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN uom_id_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN tax_rate_id_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN track_expiry_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN is_loose_snapshot INTEGER;

CREATE TRIGGER trg_lot_created_at_v31 AFTER INSERT ON product_lot
BEGIN
  UPDATE product_lot SET updated_at=NEW.created_at WHERE id=NEW.id;
END;

CREATE TRIGGER trg_product_updated_at_v31 AFTER UPDATE ON product
WHEN NEW.updated_at=OLD.updated_at
BEGIN
  UPDATE product SET updated_at=strftime('%Y-%m-%d %H:%M:%f','now','localtime') WHERE id=NEW.id;
END;

CREATE TRIGGER trg_lot_updated_at_v31 AFTER UPDATE ON product_lot
WHEN NEW.updated_at=OLD.updated_at
BEGIN
  UPDATE product_lot SET updated_at=strftime('%Y-%m-%d %H:%M:%f','now','localtime') WHERE id=NEW.id;
END;

CREATE TRIGGER trg_lot_no_positive_delete_v31 BEFORE DELETE ON product_lot
WHEN OLD.qty_on_hand<>0
BEGIN SELECT RAISE(ABORT,'lot with stock cannot be deleted'); END;

CREATE TRIGGER trg_product_no_positive_delete_v31 BEFORE DELETE ON product
WHEN EXISTS(SELECT 1 FROM product_lot WHERE product_id=OLD.id AND qty_on_hand<>0)
BEGIN SELECT RAISE(ABORT,'product with stock cannot be deleted'); END;
