-- The broad stock-ledger report orders by move_date,id. On the 5.39m-row
-- fixture the product/date index skip-scan needed a temporary sort (430 ms);
-- this access path returned the same first page in 0.6-1.4 ms.
CREATE INDEX IF NOT EXISTS ix_move_date_id ON stock_movement(move_date,id);
PRAGMA user_version = 23;
