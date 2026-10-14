-- Keep the same partial-index membership and date order. Including active
-- status lets the expired badge and dashboard count directly from the index.
DROP INDEX ix_lot_expiry;
CREATE INDEX ix_lot_expiry ON product_lot(expiry_date,is_active)
WHERE qty_on_hand>0 AND expiry_date IS NOT NULL;
