-- Five sales summaries lost their last reader when Reports was cut to seven: Slow Movers
-- (sale_product_last), Low Stock and Reorder (sale_product_day, sale_product_return_day) and the
-- Hourly Sales heatmap (sale_hour_day, sale_hour_return_day). Their triggers still ran on every
-- sale, return and void, and a void recalculated the last sale date over every sale line of the
-- bill's products while holding the writer: 130 ms for a one-line bill of the busiest product on
-- the full-size store.
-- The triggers live on sale, sale_line and sale_return, so they go first; each would fail at its
-- next run once its table is gone. Nothing else names these tables. Dropping a table moves its
-- pages to the free list, and the next compaction returns them to the disk.

DROP TRIGGER IF EXISTS trg_sale_product_last_complete;
DROP TRIGGER IF EXISTS trg_sale_product_last_insert;
DROP TRIGGER IF EXISTS trg_sale_product_last_void;
DROP TRIGGER IF EXISTS trg_sale_product_last_date;
DROP TRIGGER IF EXISTS trg_sale_product_last_line_delete;
DROP TRIGGER IF EXISTS trg_sale_product_last_line_update;
DROP TRIGGER IF EXISTS trg_sale_product_last_sale_delete;

DROP TRIGGER IF EXISTS trg_sale_hour_insert;
DROP TRIGGER IF EXISTS trg_sale_hour_update;
DROP TRIGGER IF EXISTS trg_sale_hour_delete;
DROP TRIGGER IF EXISTS trg_sale_hour_return_total_v32;

DROP TRIGGER IF EXISTS trg_sale_product_day_sale_update;
DROP TRIGGER IF EXISTS trg_sale_product_day_sale_delete;
DROP TRIGGER IF EXISTS trg_sale_product_day_line_insert;
DROP TRIGGER IF EXISTS trg_sale_product_day_line_delete;
DROP TRIGGER IF EXISTS trg_sale_product_day_line_update;

DROP TRIGGER IF EXISTS trg_sale_product_return_insert_v32;
DROP TRIGGER IF EXISTS trg_sale_product_return_update_v32;
DROP TRIGGER IF EXISTS trg_sale_product_return_delete_v32;

DROP TABLE IF EXISTS sale_product_last;
DROP TABLE IF EXISTS sale_product_day;
DROP TABLE IF EXISTS sale_product_return_day;
DROP TABLE IF EXISTS sale_hour_day;
DROP TABLE IF EXISTS sale_hour_return_day;
