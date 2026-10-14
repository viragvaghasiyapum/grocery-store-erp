-- SQLite's default LIKE is ASCII case-insensitive. Keep that behavior while
-- making an invoice-prefix existence check seekable on large sale histories.
CREATE INDEX IF NOT EXISTS ix_sale_invoice_nocase ON sale(invoice_no COLLATE NOCASE);
