-- Stock totals and v_product_stock must include empty and inactive lots, so the
-- partial FEFO index cannot serve their product_id lookup.
CREATE INDEX ix_product_lot_product ON product_lot(product_id);
