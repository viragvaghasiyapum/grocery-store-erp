-- Measured on the 5.18m-line/5.38m-movement fixture. These indexes align
-- saved-bill, ledger and history pages with their stable cursor order.
CREATE INDEX IF NOT EXISTS ix_sale_saved ON sale(created_at DESC,id DESC)
WHERE status IN ('HELD','DRAFT');
CREATE INDEX IF NOT EXISTS ix_move_product_id ON stock_movement(product_id,id DESC);
CREATE INDEX IF NOT EXISTS ix_sale_date_id ON sale(sale_date DESC,id DESC);
CREATE INDEX IF NOT EXISTS ix_return_line_return ON sale_return_line(sale_return_id);
CREATE INDEX IF NOT EXISTS ix_lot_vendor_date ON product_lot(vendor_id,received_date DESC,id DESC);
CREATE INDEX IF NOT EXISTS ix_audit_at_id ON audit_log(at,id DESC);
PRAGMA user_version = 13;
