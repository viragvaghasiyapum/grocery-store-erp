-- Keep all four valuation figures together so category/product stock value
-- reads do not aggregate the full lot register on every screen refresh.
ALTER TABLE product_stock_total ADD COLUMN selling_value INTEGER NOT NULL DEFAULT 0;
ALTER TABLE product_stock_total ADD COLUMN mrp_value INTEGER NOT NULL DEFAULT 0;

UPDATE product_stock_total SET
  selling_value=COALESCE((SELECT SUM(CAST(ROUND(l.qty_on_hand*l.selling_price/1000.0) AS INTEGER))
                          FROM product_lot l WHERE l.product_id=product_stock_total.product_id),0),
  mrp_value=COALESCE((SELECT SUM(CAST(ROUND(l.qty_on_hand*COALESCE(l.mrp,l.selling_price)/1000.0) AS INTEGER))
                      FROM product_lot l WHERE l.product_id=product_stock_total.product_id),0);

DROP TRIGGER trg_lot_total_insert;
DROP TRIGGER trg_lot_total_delete;
DROP TRIGGER trg_lot_total_update;

CREATE TRIGGER trg_lot_total_insert AFTER INSERT ON product_lot BEGIN
  INSERT INTO product_stock_total(product_id,quantity,cost_value,is_low,selling_value,mrp_value)
  VALUES (NEW.product_id,NEW.qty_on_hand,
          CAST(ROUND(NEW.qty_on_hand*NEW.unit_cost/1000.0) AS INTEGER),
          (SELECT CASE WHEN p.reorder_level>0 AND NEW.qty_on_hand<=p.reorder_level
                       THEN 1 ELSE 0 END FROM product p WHERE p.id=NEW.product_id),
          CAST(ROUND(NEW.qty_on_hand*NEW.selling_price/1000.0) AS INTEGER),
          CAST(ROUND(NEW.qty_on_hand*COALESCE(NEW.mrp,NEW.selling_price)/1000.0) AS INTEGER))
  ON CONFLICT(product_id) DO UPDATE SET
    quantity=quantity+excluded.quantity,
    cost_value=cost_value+excluded.cost_value,
    selling_value=selling_value+excluded.selling_value,
    mrp_value=mrp_value+excluded.mrp_value,
    is_low=(SELECT CASE WHEN p.reorder_level>0 AND
                   product_stock_total.quantity+excluded.quantity<=p.reorder_level
                   THEN 1 ELSE 0 END FROM product p WHERE p.id=NEW.product_id);
END;

CREATE TRIGGER trg_lot_total_delete AFTER DELETE ON product_lot BEGIN
  UPDATE product_stock_total SET
    quantity=quantity-OLD.qty_on_hand,
    cost_value=cost_value-CAST(ROUND(OLD.qty_on_hand*OLD.unit_cost/1000.0) AS INTEGER),
    selling_value=selling_value-CAST(ROUND(OLD.qty_on_hand*OLD.selling_price/1000.0) AS INTEGER),
    mrp_value=mrp_value-CAST(ROUND(OLD.qty_on_hand*COALESCE(OLD.mrp,OLD.selling_price)/1000.0) AS INTEGER),
    is_low=(SELECT CASE WHEN p.reorder_level>0 AND
                   product_stock_total.quantity-OLD.qty_on_hand<=p.reorder_level
                   THEN 1 ELSE 0 END FROM product p WHERE p.id=OLD.product_id)
  WHERE product_id=OLD.product_id;
END;

CREATE TRIGGER trg_lot_total_update AFTER UPDATE OF qty_on_hand,unit_cost,product_id,selling_price,mrp ON product_lot BEGIN
  UPDATE product_stock_total SET
    quantity=quantity-OLD.qty_on_hand,
    cost_value=cost_value-CAST(ROUND(OLD.qty_on_hand*OLD.unit_cost/1000.0) AS INTEGER),
    selling_value=selling_value-CAST(ROUND(OLD.qty_on_hand*OLD.selling_price/1000.0) AS INTEGER),
    mrp_value=mrp_value-CAST(ROUND(OLD.qty_on_hand*COALESCE(OLD.mrp,OLD.selling_price)/1000.0) AS INTEGER),
    is_low=(SELECT CASE WHEN p.reorder_level>0 AND
                   product_stock_total.quantity-OLD.qty_on_hand<=p.reorder_level
                   THEN 1 ELSE 0 END FROM product p WHERE p.id=OLD.product_id)
  WHERE product_id=OLD.product_id;
  INSERT INTO product_stock_total(product_id,quantity,cost_value,is_low,selling_value,mrp_value)
  VALUES (NEW.product_id,NEW.qty_on_hand,
          CAST(ROUND(NEW.qty_on_hand*NEW.unit_cost/1000.0) AS INTEGER),
          (SELECT CASE WHEN p.reorder_level>0 AND NEW.qty_on_hand<=p.reorder_level
                       THEN 1 ELSE 0 END FROM product p WHERE p.id=NEW.product_id),
          CAST(ROUND(NEW.qty_on_hand*NEW.selling_price/1000.0) AS INTEGER),
          CAST(ROUND(NEW.qty_on_hand*COALESCE(NEW.mrp,NEW.selling_price)/1000.0) AS INTEGER))
  ON CONFLICT(product_id) DO UPDATE SET
    quantity=quantity+excluded.quantity,
    cost_value=cost_value+excluded.cost_value,
    selling_value=selling_value+excluded.selling_value,
    mrp_value=mrp_value+excluded.mrp_value,
    is_low=(SELECT CASE WHEN p.reorder_level>0 AND
                   product_stock_total.quantity+excluded.quantity<=p.reorder_level
                   THEN 1 ELSE 0 END FROM product p WHERE p.id=NEW.product_id);
END;

PRAGMA user_version = 24;
