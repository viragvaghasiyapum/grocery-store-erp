-- 003_operations.sql
-- Applied by the migration runner inside one transaction; runner sets PRAGMA user_version = 3.
--
-- Duplicate-submission protection (idempotent commands).
-- Every command that finalizes something carries an op_id that the app generates
-- when the form/dialog/bill opens (Guid "N" format: 32 lowercase hex chars).
-- The service claims it FIRST, inside the same transaction as the work:
--
--   INSERT INTO operation(op_id, op_type) VALUES (?, ?) ON CONFLICT(op_id) DO NOTHING;
--     1 row  -> first time: do the work, then record ref_type/ref_id
--     0 rows -> already done: return the original result, do nothing again
--
-- Covered commands: SALE_COMPLETE, SALE_VOID, SALE_RETURN, PURCHASE_POST,
-- PURCHASE_CANCEL, VENDOR_PAYMENT, DAMAGE, DAMAGE_BULK, EXPENSE, SALARY_RUN,
-- STOCK_COUNT_POST, STOCK_ADJUSTMENT, PRICE_CHANGE, ASSET_DISPOSE,
-- OPENING_STOCK_IMPORT, PRODUCT_IMPORT (list lives in code: OpType).
--
-- Rows only matter for minutes; the app purges rows older than 30 days at startup.
-- This is a de-duplication register, not business history (audit_log is that).
CREATE TABLE operation (
  op_id       TEXT    PRIMARY KEY
              CHECK (length(op_id) = 32 AND op_id NOT GLOB '*[^0-9a-f]*'),
  op_type     TEXT    NOT NULL
              CHECK (op_type <> '' AND op_type NOT GLOB '*[^A-Z_]*'),
  ref_type    TEXT,                 -- 'sale', 'purchase', 'damage', 'expense', ...
  ref_id      INTEGER,              -- id of the document the operation created/changed
  created_at  TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT, WITHOUT ROWID;
