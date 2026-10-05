-- Retain count scope so new lots can be added to an unfinished sheet.
CREATE TABLE stock_count_scope (
  stock_count_id INTEGER NOT NULL REFERENCES stock_count(id) ON DELETE CASCADE,
  category_id INTEGER NOT NULL REFERENCES category(id),
  PRIMARY KEY (stock_count_id, category_id)
) STRICT;

PRAGMA user_version = 8;
