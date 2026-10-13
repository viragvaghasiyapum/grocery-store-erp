-- Keep the low-stock predicate indexable, including products with no lots.
ALTER TABLE product_stock_total ADD COLUMN is_low INTEGER NOT NULL DEFAULT 0
CHECK (is_low IN (0,1));

INSERT INTO product_stock_total(product_id,quantity,cost_value,is_low)
SELECT p.id,0,0,CASE WHEN p.reorder_level>0 THEN 1 ELSE 0 END
FROM product p WHERE NOT EXISTS (
    SELECT 1 FROM product_stock_total t WHERE t.product_id=p.id
);

UPDATE product_stock_total SET is_low=(
    SELECT CASE WHEN p.reorder_level>0 AND product_stock_total.quantity<=p.reorder_level
                THEN 1 ELSE 0 END FROM product p WHERE p.id=product_stock_total.product_id
);

CREATE INDEX ix_product_total_low ON product_stock_total(product_id) WHERE is_low=1;

DROP TRIGGER trg_lot_total_insert;
DROP TRIGGER trg_lot_total_delete;
DROP TRIGGER trg_lot_total_update;

CREATE TRIGGER trg_product_total_insert AFTER INSERT ON product BEGIN
    INSERT INTO product_stock_total(product_id,quantity,cost_value,is_low)
    VALUES (NEW.id,0,0,CASE WHEN NEW.reorder_level>0 THEN 1 ELSE 0 END);
END;

CREATE TRIGGER trg_product_total_reorder AFTER UPDATE OF reorder_level ON product BEGIN
    UPDATE product_stock_total SET is_low=
        CASE WHEN NEW.reorder_level>0 AND quantity<=NEW.reorder_level THEN 1 ELSE 0 END
    WHERE product_id=NEW.id;
END;

CREATE TRIGGER trg_lot_total_insert AFTER INSERT ON product_lot BEGIN
    INSERT INTO product_stock_total(product_id,quantity,cost_value,is_low)
    VALUES (NEW.product_id,NEW.qty_on_hand,
            CAST(ROUND(NEW.qty_on_hand*NEW.unit_cost/1000.0) AS INTEGER),
            (SELECT CASE WHEN p.reorder_level>0 AND NEW.qty_on_hand<=p.reorder_level
                         THEN 1 ELSE 0 END FROM product p WHERE p.id=NEW.product_id))
    ON CONFLICT(product_id) DO UPDATE SET
        quantity=quantity+excluded.quantity,
        cost_value=cost_value+excluded.cost_value,
        is_low=(SELECT CASE WHEN p.reorder_level>0 AND
                     product_stock_total.quantity+excluded.quantity<=p.reorder_level
                     THEN 1 ELSE 0 END FROM product p WHERE p.id=NEW.product_id);
END;

CREATE TRIGGER trg_lot_total_delete AFTER DELETE ON product_lot BEGIN
    UPDATE product_stock_total SET
        quantity=quantity-OLD.qty_on_hand,
        cost_value=cost_value-CAST(ROUND(OLD.qty_on_hand*OLD.unit_cost/1000.0) AS INTEGER),
        is_low=(SELECT CASE WHEN p.reorder_level>0 AND
                     product_stock_total.quantity-OLD.qty_on_hand<=p.reorder_level
                     THEN 1 ELSE 0 END FROM product p WHERE p.id=OLD.product_id)
    WHERE product_id=OLD.product_id;
END;

CREATE TRIGGER trg_lot_total_update AFTER UPDATE OF qty_on_hand,unit_cost,product_id ON product_lot BEGIN
    UPDATE product_stock_total SET
        quantity=quantity-OLD.qty_on_hand,
        cost_value=cost_value-CAST(ROUND(OLD.qty_on_hand*OLD.unit_cost/1000.0) AS INTEGER),
        is_low=(SELECT CASE WHEN p.reorder_level>0 AND
                     product_stock_total.quantity-OLD.qty_on_hand<=p.reorder_level
                     THEN 1 ELSE 0 END FROM product p WHERE p.id=OLD.product_id)
    WHERE product_id=OLD.product_id;
    INSERT INTO product_stock_total(product_id,quantity,cost_value,is_low)
    VALUES (NEW.product_id,NEW.qty_on_hand,
            CAST(ROUND(NEW.qty_on_hand*NEW.unit_cost/1000.0) AS INTEGER),
            (SELECT CASE WHEN p.reorder_level>0 AND NEW.qty_on_hand<=p.reorder_level
                         THEN 1 ELSE 0 END FROM product p WHERE p.id=NEW.product_id))
    ON CONFLICT(product_id) DO UPDATE SET
        quantity=quantity+excluded.quantity,
        cost_value=cost_value+excluded.cost_value,
        is_low=(SELECT CASE WHEN p.reorder_level>0 AND
                     product_stock_total.quantity+excluded.quantity<=p.reorder_level
                     THEN 1 ELSE 0 END FROM product p WHERE p.id=NEW.product_id);
END;

DROP VIEW v_product_stock;
CREATE VIEW v_product_stock AS
SELECT p.id AS product_id,p.sku,p.name,p.reorder_level,
       COALESCE(t.quantity,0) AS stock_qty,
       COALESCE(t.cost_value,0) AS stock_value,
       COALESCE(t.is_low,0) AS is_low
FROM product p LEFT JOIN product_stock_total t ON t.product_id=p.id
WHERE p.is_active=1;

PRAGMA user_version = 15;
