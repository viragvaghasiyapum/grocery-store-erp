-- Preserve the purchase tax treatment used to value its lots and later supplier credits.
ALTER TABLE purchase ADD COLUMN tax_mode TEXT
  CHECK (tax_mode IS NULL OR tax_mode IN ('REGULAR','COMPOSITION','NONE'));

PRAGMA user_version = 9;
