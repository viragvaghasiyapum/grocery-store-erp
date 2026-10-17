ALTER TABLE purchase ADD COLUMN vendor_name_snapshot TEXT;
ALTER TABLE purchase_line ADD COLUMN product_name_snapshot TEXT;
ALTER TABLE purchase_line ADD COLUMN product_sku_snapshot TEXT;

-- Existing invoices have no historical names; retain the names available at migration.
UPDATE purchase SET vendor_name_snapshot =
    (SELECT name FROM vendor WHERE vendor.id = purchase.vendor_id)
WHERE status <> 'DRAFT';
DROP TRIGGER trg_purchase_line_lock_upd;
UPDATE purchase_line SET product_name_snapshot =
    (SELECT name FROM product WHERE product.id = purchase_line.product_id),
    product_sku_snapshot =
    (SELECT sku FROM product WHERE product.id = purchase_line.product_id)
WHERE purchase_id IN (SELECT id FROM purchase WHERE status <> 'DRAFT');
CREATE TRIGGER trg_purchase_line_lock_upd BEFORE UPDATE ON purchase_line
WHEN (SELECT status FROM purchase WHERE id = OLD.purchase_id) <> 'DRAFT'
BEGIN SELECT RAISE(ABORT, 'purchase is posted/cancelled; lines are read-only'); END;
