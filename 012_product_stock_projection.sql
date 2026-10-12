-- The lot cache is updated by stock_movement triggers. Keep a small per-product
-- projection so catalog filters and the dashboard do not regroup every lot.
CREATE TABLE product_stock_total (
    product_id INTEGER PRIMARY KEY REFERENCES product(id) ON DELETE CASCADE,
    quantity INTEGER NOT NULL DEFAULT 0,
    cost_value INTEGER NOT NULL DEFAULT 0
) STRICT;

INSERT INTO product_stock_total(product_id,quantity,cost_value)
SELECT product_id,SUM(qty_on_hand),
       SUM(CAST(ROUND(qty_on_hand * unit_cost / 1000.0) AS INTEGER))
FROM product_lot GROUP BY product_id;

CREATE TRIGGER trg_lot_total_insert AFTER INSERT ON product_lot BEGIN
    INSERT INTO product_stock_total(product_id,quantity,cost_value)
    VALUES (NEW.product_id,NEW.qty_on_hand,
            CAST(ROUND(NEW.qty_on_hand * NEW.unit_cost / 1000.0) AS INTEGER))
    ON CONFLICT(product_id) DO UPDATE SET
        quantity=quantity+excluded.quantity,
        cost_value=cost_value+excluded.cost_value;
END;

CREATE TRIGGER trg_lot_total_delete AFTER DELETE ON product_lot BEGIN
    UPDATE product_stock_total SET
        quantity=quantity-OLD.qty_on_hand,
        cost_value=cost_value-CAST(ROUND(OLD.qty_on_hand * OLD.unit_cost / 1000.0) AS INTEGER)
    WHERE product_id=OLD.product_id;
END;

CREATE TRIGGER trg_lot_total_update AFTER UPDATE OF qty_on_hand,unit_cost,product_id ON product_lot BEGIN
    UPDATE product_stock_total SET
        quantity=quantity-OLD.qty_on_hand,
        cost_value=cost_value-CAST(ROUND(OLD.qty_on_hand * OLD.unit_cost / 1000.0) AS INTEGER)
    WHERE product_id=OLD.product_id;
    INSERT INTO product_stock_total(product_id,quantity,cost_value)
    VALUES (NEW.product_id,NEW.qty_on_hand,
            CAST(ROUND(NEW.qty_on_hand * NEW.unit_cost / 1000.0) AS INTEGER))
    ON CONFLICT(product_id) DO UPDATE SET
        quantity=quantity+excluded.quantity,
        cost_value=cost_value+excluded.cost_value;
END;

DROP VIEW v_product_stock;
CREATE VIEW v_product_stock AS
SELECT p.id AS product_id,p.sku,p.name,p.reorder_level,
       COALESCE(t.quantity,0) AS stock_qty,
       COALESCE(t.cost_value,0) AS stock_value,
       (p.reorder_level>0 AND COALESCE(t.quantity,0)<=p.reorder_level) AS is_low
FROM product p LEFT JOIN product_stock_total t ON t.product_id=p.id
WHERE p.is_active=1;

PRAGMA user_version = 12;
