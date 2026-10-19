-- Historical document fields retain original IDs. The migration runner rebuilds
-- their tables without live catalog/lot FKs before executing this script.
CREATE TABLE schema_migration_marker (
  version INTEGER PRIMARY KEY,
  completed_at TEXT NOT NULL,
  sale_lines INTEGER NOT NULL,
  return_lines INTEGER NOT NULL,
  purchase_lines INTEGER NOT NULL,
  lot_count INTEGER NOT NULL,
  sale_total INTEGER NOT NULL,
  return_total INTEGER NOT NULL,
  purchase_total INTEGER NOT NULL,
  receipt_bytes INTEGER NOT NULL
) STRICT;

ALTER TABLE product ADD COLUMN product_class TEXT NOT NULL DEFAULT 'SALEABLE'
  CHECK (product_class IN ('SALEABLE','INTERNAL'));
ALTER TABLE product_lot ADD COLUMN origin_kind TEXT NOT NULL DEFAULT 'DIRECT'
  CHECK (origin_kind IN ('PURCHASE_LINE','DIRECT','CUSTOMER_RETURN'));
ALTER TABLE product_lot ADD COLUMN origin_ref_id INTEGER;
ALTER TABLE product_lot ADD COLUMN detail_version INTEGER NOT NULL DEFAULT 1;

CREATE TABLE direct_stock_origin (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  lot_id INTEGER NOT NULL UNIQUE,
  product_id_snapshot INTEGER NOT NULL,
  product_sku_snapshot TEXT,
  product_name_snapshot TEXT,
  product_name_local_snapshot TEXT,
  uom_snapshot TEXT,
  product_class_snapshot TEXT,
  lot_code_snapshot TEXT,
  batch_no_snapshot TEXT,
  expiry_date_snapshot TEXT,
  quantity INTEGER NOT NULL CHECK (quantity>=0),
  unit_cost INTEGER NOT NULL CHECK (unit_cost>=0),
  origin_date TEXT NOT NULL CHECK (origin_date=date(origin_date)),
  reason TEXT NOT NULL,
  provenance TEXT NOT NULL CHECK (provenance IN ('DIRECT_ENTRY','LEGACY_UNKNOWN')),
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
INSERT INTO direct_stock_origin(lot_id,product_id_snapshot,product_sku_snapshot,
  product_name_snapshot,product_name_local_snapshot,uom_snapshot,product_class_snapshot,
  lot_code_snapshot,batch_no_snapshot,expiry_date_snapshot,quantity,unit_cost,origin_date,
  reason,provenance)
SELECT l.id,l.product_id,p.sku,p.name,p.name_local,u.code,p.product_class,
       l.lot_code,l.batch_no,l.expiry_date,l.qty_received,l.unit_cost,l.received_date,
       'Legacy direct/opening lot','LEGACY_UNKNOWN'
FROM product_lot l LEFT JOIN product p ON p.id=l.product_id
LEFT JOIN uom u ON u.id=p.uom_id WHERE l.purchase_line_id IS NULL;
UPDATE product_lot SET origin_kind='PURCHASE_LINE',origin_ref_id=purchase_line_id
WHERE purchase_line_id IS NOT NULL;
UPDATE product_lot SET origin_kind='DIRECT',
  origin_ref_id=(SELECT d.id FROM direct_stock_origin d WHERE d.lot_id=product_lot.id)
WHERE purchase_line_id IS NULL;

ALTER TABLE sale_line ADD COLUMN product_sku_snapshot TEXT;
ALTER TABLE sale_line ADD COLUMN uom_snapshot TEXT;
ALTER TABLE sale_line ADD COLUMN product_class_snapshot TEXT;
ALTER TABLE sale_line ADD COLUMN lot_code_snapshot TEXT;
ALTER TABLE sale_line ADD COLUMN batch_no_snapshot TEXT;
ALTER TABLE sale_line ADD COLUMN expiry_date_snapshot TEXT;
ALTER TABLE sale_line ADD COLUMN lot_version_snapshot INTEGER;
ALTER TABLE sale_line ADD COLUMN tax_mode_snapshot TEXT;
ALTER TABLE sale_line ADD COLUMN snapshot_provenance TEXT NOT NULL DEFAULT 'DRAFT';
ALTER TABLE sale_line ADD COLUMN legacy_unknown_fields TEXT;
-- Compact flags keep field-level disclosure affordable across millions of
-- historical lines: S=SKU,U=unit,C=class,L=lot code,B=batch,E=expiry,
-- V=lot version,T=tax mode,D=exact discount allocation.
UPDATE sale_line SET snapshot_provenance='LEGACY_SALE_LINE',
  legacy_unknown_fields='SUCLBEVT'
WHERE sale_id IN (SELECT id FROM sale WHERE status IN ('COMPLETED','VOID'));

ALTER TABLE sale_return_line ADD COLUMN product_id_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN lot_id_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN product_sku_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN product_name_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN product_name_local_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN uom_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN product_class_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN lot_code_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN batch_no_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN expiry_date_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN lot_version_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN tax_mode_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN price_includes_tax_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN tax_rate_pct_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN mrp_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN unit_price_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN unit_cost_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN discount_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN snapshot_provenance TEXT;
ALTER TABLE sale_return_line ADD COLUMN legacy_unknown_fields TEXT;
UPDATE sale_return_line SET
  product_id_snapshot=(SELECT sl.product_id FROM sale_line sl WHERE sl.id=sale_line_id),
  lot_id_snapshot=(SELECT sl.lot_id FROM sale_line sl WHERE sl.id=sale_line_id),
  product_name_snapshot=(SELECT sl.product_name FROM sale_line sl WHERE sl.id=sale_line_id),
  product_name_local_snapshot=(SELECT sl.product_name_local FROM sale_line sl WHERE sl.id=sale_line_id),
  price_includes_tax_snapshot=(SELECT sl.price_includes_tax FROM sale_line sl WHERE sl.id=sale_line_id),
  tax_rate_pct_snapshot=(SELECT sl.tax_rate_pct FROM sale_line sl WHERE sl.id=sale_line_id),
  mrp_snapshot=(SELECT sl.mrp FROM sale_line sl WHERE sl.id=sale_line_id),
  unit_price_snapshot=(SELECT sl.unit_price FROM sale_line sl WHERE sl.id=sale_line_id),
  unit_cost_snapshot=(SELECT sl.unit_cost FROM sale_line sl WHERE sl.id=sale_line_id),
  discount_snapshot=(SELECT CAST(ROUND(sl.discount*1.0*sale_return_line.qty/sl.qty) AS INTEGER)
                     FROM sale_line sl WHERE sl.id=sale_line_id),
  snapshot_provenance='LEGACY_SALE_LINE',
  legacy_unknown_fields='SUCLBEVTD';

ALTER TABLE purchase_line ADD COLUMN product_name_local_snapshot TEXT;
ALTER TABLE purchase_line ADD COLUMN uom_snapshot TEXT;
ALTER TABLE purchase_line ADD COLUMN product_class_snapshot TEXT;
ALTER TABLE purchase_line ADD COLUMN tax_mode_snapshot TEXT;
ALTER TABLE purchase_line ADD COLUMN lot_code_snapshot TEXT;
ALTER TABLE purchase_line ADD COLUMN lot_id_snapshot INTEGER;
ALTER TABLE purchase_line ADD COLUMN snapshot_provenance TEXT;
ALTER TABLE purchase_line ADD COLUMN legacy_unknown_fields TEXT;
UPDATE purchase_line SET snapshot_provenance='LEGACY_MASTER_AT_MIGRATION',
  legacy_unknown_fields='historical_product_name,historical_sku,local_name,unit,classification',
  tax_mode_snapshot=(SELECT p.tax_mode FROM purchase p WHERE p.id=purchase_id),
  lot_code_snapshot=(SELECT l.lot_code FROM product_lot l WHERE l.purchase_line_id=purchase_line.id),
  lot_id_snapshot=(SELECT l.id FROM product_lot l WHERE l.purchase_line_id=purchase_line.id)
WHERE purchase_id IN (SELECT id FROM purchase WHERE status<>'DRAFT');

ALTER TABLE vendor_return_line ADD COLUMN purchase_line_id_snapshot INTEGER;
ALTER TABLE vendor_return_line ADD COLUMN product_name_snapshot TEXT;
ALTER TABLE vendor_return_line ADD COLUMN product_sku_snapshot TEXT;
ALTER TABLE vendor_return_line ADD COLUMN product_name_local_snapshot TEXT;
ALTER TABLE vendor_return_line ADD COLUMN uom_snapshot TEXT;
ALTER TABLE vendor_return_line ADD COLUMN product_class_snapshot TEXT;
ALTER TABLE vendor_return_line ADD COLUMN lot_code_snapshot TEXT;
ALTER TABLE vendor_return_line ADD COLUMN batch_no_snapshot TEXT;
ALTER TABLE vendor_return_line ADD COLUMN expiry_date_snapshot TEXT;
ALTER TABLE vendor_return_line ADD COLUMN unit_cost_snapshot INTEGER;
ALTER TABLE vendor_return_line ADD COLUMN tax_mode_snapshot TEXT;
ALTER TABLE vendor_return_line ADD COLUMN snapshot_provenance TEXT;
ALTER TABLE vendor_return_line ADD COLUMN legacy_unknown_fields TEXT;
UPDATE vendor_return_line SET
  purchase_line_id_snapshot=(SELECT l.purchase_line_id FROM product_lot l WHERE l.id=lot_id),
  product_name_snapshot=(SELECT pl.product_name_snapshot FROM product_lot l
    JOIN purchase_line pl ON pl.id=l.purchase_line_id WHERE l.id=lot_id),
  product_sku_snapshot=(SELECT pl.product_sku_snapshot FROM product_lot l
    JOIN purchase_line pl ON pl.id=l.purchase_line_id WHERE l.id=lot_id),
  lot_code_snapshot=(SELECT l.lot_code FROM product_lot l WHERE l.id=lot_id),
  batch_no_snapshot=(SELECT pl.batch_no FROM product_lot l
    JOIN purchase_line pl ON pl.id=l.purchase_line_id WHERE l.id=lot_id),
  expiry_date_snapshot=(SELECT pl.expiry_date FROM product_lot l
    JOIN purchase_line pl ON pl.id=l.purchase_line_id WHERE l.id=lot_id),
  unit_cost_snapshot=(SELECT l.unit_cost FROM product_lot l WHERE l.id=lot_id),
  tax_mode_snapshot=(SELECT vr.tax_mode FROM vendor_return vr WHERE vr.id=vendor_return_id),
  snapshot_provenance='LEGACY_PURCHASE_OR_LOT',
  legacy_unknown_fields='historical_name,historical_sku,local_name,unit,classification,lot_code_if_edited';

ALTER TABLE damage ADD COLUMN product_name_snapshot TEXT;
ALTER TABLE damage ADD COLUMN product_sku_snapshot TEXT;
ALTER TABLE damage ADD COLUMN product_name_local_snapshot TEXT;
ALTER TABLE damage ADD COLUMN uom_snapshot TEXT;
ALTER TABLE damage ADD COLUMN product_class_snapshot TEXT;
ALTER TABLE damage ADD COLUMN lot_code_snapshot TEXT;
ALTER TABLE damage ADD COLUMN batch_no_snapshot TEXT;
ALTER TABLE damage ADD COLUMN expiry_date_snapshot TEXT;
ALTER TABLE damage ADD COLUMN snapshot_provenance TEXT;
ALTER TABLE damage ADD COLUMN legacy_unknown_fields TEXT;
UPDATE damage SET
  product_name_snapshot=(SELECT p.name FROM product p WHERE p.id=damage.product_id),
  product_sku_snapshot=(SELECT p.sku FROM product p WHERE p.id=damage.product_id),
  product_name_local_snapshot=(SELECT p.name_local FROM product p WHERE p.id=damage.product_id),
  uom_snapshot=(SELECT u.code FROM product p JOIN uom u ON u.id=p.uom_id WHERE p.id=damage.product_id),
  lot_code_snapshot=(SELECT l.lot_code FROM product_lot l WHERE l.id=damage.lot_id),
  batch_no_snapshot=(SELECT l.batch_no FROM product_lot l WHERE l.id=damage.lot_id),
  expiry_date_snapshot=(SELECT l.expiry_date FROM product_lot l WHERE l.id=damage.lot_id),
  snapshot_provenance='LEGACY_MASTER_AT_MIGRATION',
  legacy_unknown_fields='historical_name,historical_sku,historical_unit,historical_batch,historical_expiry,classification';

ALTER TABLE stock_count_line ADD COLUMN product_name_snapshot TEXT;
ALTER TABLE stock_count_line ADD COLUMN lot_code_snapshot TEXT;
ALTER TABLE stock_count_line ADD COLUMN snapshot_provenance TEXT;
UPDATE stock_count_line SET
  product_name_snapshot=(SELECT p.name FROM product p WHERE p.id=stock_count_line.product_id),
  lot_code_snapshot=(SELECT l.lot_code FROM product_lot l WHERE l.id=stock_count_line.lot_id),
  snapshot_provenance='LEGACY_MASTER_AT_MIGRATION';

CREATE TABLE internal_use_record (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  product_id_snapshot INTEGER NOT NULL,
  lot_id_snapshot INTEGER NOT NULL,
  product_sku_snapshot TEXT,
  product_name_snapshot TEXT NOT NULL,
  uom_snapshot TEXT,
  lot_code_snapshot TEXT,
  batch_no_snapshot TEXT,
  expiry_date_snapshot TEXT,
  quantity INTEGER NOT NULL CHECK (quantity>0),
  unit_cost INTEGER NOT NULL CHECK (unit_cost>=0),
  used_date TEXT NOT NULL CHECK (used_date=date(used_date)),
  reason TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;

-- Phase 6 fills these rows when an annual period locks. The composite primary
-- key serves exact-year lookup and bounded month/head drill-down without a
-- second index or nullable-key duplicates.
CREATE TABLE annual_pnl_snapshot (
  period_kind TEXT NOT NULL CHECK (period_kind IN ('CALENDAR','FISCAL')),
  year_start TEXT NOT NULL CHECK (year_start=date(year_start)),
  month_number INTEGER NOT NULL CHECK (month_number BETWEEN 0 AND 12),
  section TEXT NOT NULL,
  head_code TEXT NOT NULL,
  category_id_snapshot INTEGER NOT NULL DEFAULT 0,
  amount INTEGER NOT NULL,
  source_count INTEGER NOT NULL DEFAULT 0,
  closed_at TEXT NOT NULL,
  PRIMARY KEY (period_kind,year_start,month_number,section,head_code,category_id_snapshot)
) STRICT, WITHOUT ROWID;

CREATE INDEX ix_lot_fefo ON product_lot(product_id,(expiry_date IS NULL),expiry_date,received_date,id)
  WHERE qty_on_hand>0 AND is_active=1;
CREATE UNIQUE INDEX ix_purchase_line_lot_snapshot_v29 ON purchase_line(lot_id_snapshot)
  WHERE lot_id_snapshot IS NOT NULL;

CREATE TRIGGER trg_lot_origin_insert_v29 AFTER INSERT ON product_lot
BEGIN
  INSERT INTO direct_stock_origin(lot_id,product_id_snapshot,product_sku_snapshot,
    product_name_snapshot,product_name_local_snapshot,uom_snapshot,product_class_snapshot,
    lot_code_snapshot,batch_no_snapshot,expiry_date_snapshot,quantity,unit_cost,origin_date,
    reason,provenance)
  SELECT NEW.id,NEW.product_id,p.sku,p.name,p.name_local,u.code,p.product_class,
         NEW.lot_code,NEW.batch_no,NEW.expiry_date,NEW.qty_received,NEW.unit_cost,NEW.received_date,
         'Direct or opening entry','DIRECT_ENTRY'
  FROM product p JOIN uom u ON u.id=p.uom_id
  WHERE NEW.purchase_line_id IS NULL AND NEW.origin_kind='DIRECT' AND p.id=NEW.product_id;
  UPDATE product_lot SET
    origin_kind=CASE WHEN NEW.purchase_line_id IS NULL THEN 'DIRECT' ELSE 'PURCHASE_LINE' END,
    origin_ref_id=CASE WHEN NEW.purchase_line_id IS NULL
      THEN (SELECT id FROM direct_stock_origin WHERE lot_id=NEW.id) ELSE NEW.purchase_line_id END
  WHERE id=NEW.id AND NEW.origin_kind<>'CUSTOMER_RETURN';
END;
CREATE TRIGGER trg_lot_origin_immutable_v29 BEFORE UPDATE OF origin_kind,origin_ref_id ON product_lot
WHEN OLD.origin_ref_id IS NOT NULL AND
  (NEW.origin_kind<>OLD.origin_kind OR NEW.origin_ref_id<>OLD.origin_ref_id)
BEGIN SELECT RAISE(ABORT,'lot origin is immutable'); END;
CREATE TRIGGER trg_lot_purchase_source_immutable_v29 BEFORE UPDATE OF purchase_line_id ON product_lot
WHEN OLD.origin_ref_id IS NOT NULL AND NEW.purchase_line_id IS NOT OLD.purchase_line_id
BEGIN SELECT RAISE(ABORT,'lot purchase source is immutable'); END;
CREATE TRIGGER trg_direct_origin_immutable_v29 BEFORE UPDATE ON direct_stock_origin
BEGIN SELECT RAISE(ABORT,'direct origin is immutable'); END;
CREATE TRIGGER trg_direct_origin_no_delete_v29 BEFORE DELETE ON direct_stock_origin
BEGIN SELECT RAISE(ABORT,'direct origin cannot be deleted'); END;
CREATE TRIGGER trg_lot_detail_version_v29 AFTER UPDATE OF
  product_id,batch_no,expiry_date,unit_cost,mrp,selling_price,is_active ON product_lot
WHEN NEW.detail_version=OLD.detail_version AND
  (NEW.product_id IS NOT OLD.product_id OR NEW.batch_no IS NOT OLD.batch_no OR
   NEW.expiry_date IS NOT OLD.expiry_date OR NEW.unit_cost IS NOT OLD.unit_cost OR
   NEW.mrp IS NOT OLD.mrp OR NEW.selling_price IS NOT OLD.selling_price OR
   NEW.is_active IS NOT OLD.is_active)
BEGIN
  UPDATE product_lot SET detail_version=OLD.detail_version+1 WHERE id=NEW.id;
END;
CREATE TRIGGER trg_lot_barcode_detach_v29 BEFORE DELETE ON product_lot
BEGIN UPDATE product_barcode SET lot_id=NULL WHERE lot_id=OLD.id; END;

-- Historical IDs no longer have live FKs, but every new document must point to
-- a real catalog item/lot at posting time.
CREATE TRIGGER trg_purchase_line_live_product_v29 BEFORE INSERT ON purchase_line
WHEN NOT EXISTS (SELECT 1 FROM product WHERE id=NEW.product_id)
BEGIN SELECT RAISE(ABORT,'purchase line product is missing'); END;
CREATE TRIGGER trg_purchase_line_live_product_update_v29 BEFORE UPDATE OF product_id ON purchase_line
WHEN NOT EXISTS (SELECT 1 FROM product WHERE id=NEW.product_id)
BEGIN SELECT RAISE(ABORT,'purchase line product is missing'); END;
CREATE TRIGGER trg_sale_line_live_lot_v29 BEFORE INSERT ON sale_line
WHEN NOT EXISTS (SELECT 1 FROM product_lot WHERE id=NEW.lot_id AND product_id=NEW.product_id)
BEGIN SELECT RAISE(ABORT,'sale line lot is missing'); END;
CREATE TRIGGER trg_sale_line_live_lot_update_v29 BEFORE UPDATE OF lot_id,product_id ON sale_line
WHEN NOT EXISTS (SELECT 1 FROM product_lot WHERE id=NEW.lot_id AND product_id=NEW.product_id)
BEGIN SELECT RAISE(ABORT,'sale line lot is missing'); END;
CREATE TRIGGER trg_movement_live_lot_v29 BEFORE INSERT ON stock_movement
WHEN NOT EXISTS (SELECT 1 FROM product_lot WHERE id=NEW.lot_id AND product_id=NEW.product_id)
BEGIN SELECT RAISE(ABORT,'stock movement lot is missing'); END;
CREATE TRIGGER trg_damage_live_lot_v29 BEFORE INSERT ON damage
WHEN NOT EXISTS (SELECT 1 FROM product_lot WHERE id=NEW.lot_id AND product_id=NEW.product_id)
BEGIN SELECT RAISE(ABORT,'damage lot is missing'); END;
CREATE TRIGGER trg_vendor_return_line_live_lot_v29 BEFORE INSERT ON vendor_return_line
WHEN NOT EXISTS (SELECT 1 FROM product_lot WHERE id=NEW.lot_id AND product_id=NEW.product_id)
BEGIN SELECT RAISE(ABORT,'vendor return lot is missing'); END;
CREATE TRIGGER trg_count_line_live_lot_v29 BEFORE INSERT ON stock_count_line
WHEN NOT EXISTS (SELECT 1 FROM product_lot WHERE id=NEW.lot_id AND product_id=NEW.product_id)
BEGIN SELECT RAISE(ABORT,'stock count lot is missing'); END;
