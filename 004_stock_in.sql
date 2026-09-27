-- Phase 3: stock-in workflow persistence.

ALTER TABLE purchase ADD COLUMN vendor_bill_total INTEGER;

CREATE TABLE stock_count (
  id                    INTEGER PRIMARY KEY,
  count_date            TEXT    NOT NULL CHECK (count_date = date(count_date)),
  status                TEXT    NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
  snapshot_movement_id  INTEGER NOT NULL,
  notes                 TEXT,
  created_at            TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  posted_at             TEXT
) STRICT;
CREATE INDEX ix_stock_count_date ON stock_count(count_date, status);

CREATE TABLE stock_count_line (
  id            INTEGER PRIMARY KEY,
  stock_count_id INTEGER NOT NULL REFERENCES stock_count(id) ON DELETE CASCADE,
  product_id    INTEGER NOT NULL,
  lot_id        INTEGER NOT NULL,
  expected_qty  INTEGER NOT NULL,
  counted_qty   INTEGER,
  unit_cost     INTEGER NOT NULL,
  UNIQUE (stock_count_id, lot_id),
  FOREIGN KEY (lot_id, product_id) REFERENCES product_lot(id, product_id)
) STRICT;
CREATE INDEX ix_stock_count_line_product ON stock_count_line(product_id);

CREATE TRIGGER trg_stock_count_line_lock_ins BEFORE INSERT ON stock_count_line
WHEN (SELECT status FROM stock_count WHERE id = NEW.stock_count_id) <> 'DRAFT'
BEGIN SELECT RAISE(ABORT, 'stock count is finalized; lines are read-only'); END;
CREATE TRIGGER trg_stock_count_line_lock_upd BEFORE UPDATE ON stock_count_line
WHEN (SELECT status FROM stock_count WHERE id = OLD.stock_count_id) <> 'DRAFT'
BEGIN SELECT RAISE(ABORT, 'stock count is finalized; lines are read-only'); END;
CREATE TRIGGER trg_stock_count_line_lock_del BEFORE DELETE ON stock_count_line
WHEN (SELECT status FROM stock_count WHERE id = OLD.stock_count_id) <> 'DRAFT'
BEGIN SELECT RAISE(ABORT, 'stock count is finalized; lines are read-only'); END;

PRAGMA user_version = 4;
