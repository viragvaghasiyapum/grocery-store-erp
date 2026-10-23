-- Phase 5: immutable sales, refund states and customer-return dispositions.

-- Refund-status and history lookups stay bounded by sale.
CREATE INDEX ix_sale_return_sale_id ON sale_return(sale_id,id);
CREATE TABLE sale_snapshot_name_product(
  name TEXT NOT NULL COLLATE NOCASE,
  product_id INTEGER NOT NULL,
  PRIMARY KEY(name,product_id)
) STRICT, WITHOUT ROWID;
INSERT OR IGNORE INTO sale_snapshot_name_product(name,product_id)
SELECT product_name,product_id FROM sale_line;
CREATE TRIGGER trg_sale_snapshot_name_insert_v32 AFTER INSERT ON sale_line
BEGIN
  INSERT OR IGNORE INTO sale_snapshot_name_product(name,product_id)
  VALUES (NEW.product_name,NEW.product_id);
END;
CREATE TRIGGER trg_sale_snapshot_name_update_v32 AFTER UPDATE OF product_name,product_id ON sale_line
BEGIN
  INSERT OR IGNORE INTO sale_snapshot_name_product(name,product_id)
  VALUES (NEW.product_name,NEW.product_id);
END;

-- A resellable return names the lot it restocked: the original lot, or a new
-- CUSTOMER_RETURN lot. Damaged returns and legacy rows leave it NULL.
ALTER TABLE sale_return_line ADD COLUMN restock_lot_id INTEGER;
ALTER TABLE sale_return_line ADD COLUMN restock_lot_code_snapshot TEXT;
ALTER TABLE sale_return_line ADD COLUMN restock_product_id_snapshot INTEGER;
ALTER TABLE sale_return_line ADD COLUMN damage_id INTEGER;
CREATE INDEX ix_sale_return_line_restock ON sale_return_line(restock_lot_id)
  WHERE restock_lot_id IS NOT NULL;
-- Finds the product already recreated for a deleted original.
CREATE INDEX ix_sale_return_line_recreated ON sale_return_line(product_id_snapshot)
  WHERE restock_product_id_snapshot<>product_id_snapshot;

CREATE TABLE sale_refund_progress(
  sale_id INTEGER PRIMARY KEY REFERENCES sale(id),
  sold_qty INTEGER NOT NULL CHECK(sold_qty>0),
  returned_qty INTEGER NOT NULL CHECK(returned_qty>=0 AND returned_qty<=sold_qty),
  refunded_total INTEGER NOT NULL CHECK(refunded_total>=0)
) STRICT;
INSERT INTO sale_refund_progress(sale_id,sold_qty,returned_qty,refunded_total)
SELECT sl.sale_id,
       (SELECT SUM(all_sl.qty) FROM sale_line all_sl WHERE all_sl.sale_id=sl.sale_id),
       SUM(rl.qty),SUM(rl.refund_amount)
FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
GROUP BY sl.sale_id;
CREATE TRIGGER trg_return_progress_insert_v32 AFTER INSERT ON sale_return_line
BEGIN
  INSERT INTO sale_refund_progress(sale_id,sold_qty,returned_qty,refunded_total)
  SELECT sl.sale_id,
         (SELECT SUM(all_sl.qty) FROM sale_line all_sl WHERE all_sl.sale_id=sl.sale_id),
         NEW.qty,NEW.refund_amount
  FROM sale_line sl WHERE sl.id=NEW.sale_line_id
  ON CONFLICT(sale_id) DO UPDATE SET
    returned_qty=returned_qty+NEW.qty,
    refunded_total=refunded_total+NEW.refund_amount;
END;
CREATE TRIGGER trg_return_progress_update_v32 AFTER UPDATE OF qty,refund_amount ON sale_return_line
BEGIN
  UPDATE sale_refund_progress SET returned_qty=returned_qty+NEW.qty-OLD.qty,
    refunded_total=refunded_total+NEW.refund_amount-OLD.refund_amount
  WHERE sale_id=(SELECT sale_id FROM sale_line WHERE id=OLD.sale_line_id);
END;
CREATE TRIGGER trg_return_progress_delete_v32 AFTER DELETE ON sale_return_line
BEGIN
  UPDATE sale_refund_progress SET returned_qty=returned_qty-OLD.qty,
    refunded_total=refunded_total-OLD.refund_amount
  WHERE sale_id=(SELECT sale_id FROM sale_line WHERE id=OLD.sale_line_id);
  DELETE FROM sale_refund_progress
  WHERE sale_id=(SELECT sale_id FROM sale_line WHERE id=OLD.sale_line_id) AND returned_qty=0;
END;
CREATE TRIGGER trg_return_line_parent_immutable_v32 BEFORE UPDATE OF sale_line_id,sale_return_id ON sale_return_line
WHEN NEW.sale_line_id<>OLD.sale_line_id OR NEW.sale_return_id<>OLD.sale_return_id
BEGIN SELECT RAISE(ABORT,'return source cannot change'); END;

-- The sum of returned quantities and refunds can never exceed the original sale.
CREATE TRIGGER trg_return_line_limit_v32 BEFORE INSERT ON sale_return_line
WHEN (SELECT status FROM sale WHERE id=(SELECT sale_id FROM sale_line WHERE id=NEW.sale_line_id))<>'COMPLETED'
  OR NEW.refund_amount<0 OR NEW.taxable_amount<0 OR NEW.tax_amount<0 OR NEW.cost_amount<0
  OR NEW.qty+(SELECT COALESCE(SUM(qty),0) FROM sale_return_line WHERE sale_line_id=NEW.sale_line_id)
     >(SELECT qty FROM sale_line WHERE id=NEW.sale_line_id)
  OR NEW.refund_amount+(SELECT COALESCE(SUM(rl.refund_amount),0)
       FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
       WHERE sl.sale_id=(SELECT sale_id FROM sale_line WHERE id=NEW.sale_line_id))
     >(SELECT s.grand_total FROM sale s JOIN sale_line sl ON sl.sale_id=s.id
       WHERE sl.id=NEW.sale_line_id)
BEGIN SELECT RAISE(ABORT,'return exceeds original sale'); END;
CREATE TRIGGER trg_return_line_update_limit_v32 BEFORE UPDATE OF qty,refund_amount ON sale_return_line
WHEN NEW.refund_amount<0 OR NEW.qty<=0
  OR NEW.qty+(SELECT COALESCE(SUM(qty),0) FROM sale_return_line
                 WHERE sale_line_id=OLD.sale_line_id AND id<>OLD.id)
     >(SELECT qty FROM sale_line WHERE id=OLD.sale_line_id)
  OR NEW.refund_amount+(SELECT COALESCE(SUM(rl.refund_amount),0)
       FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
       WHERE sl.sale_id=(SELECT sale_id FROM sale_line WHERE id=OLD.sale_line_id)
         AND rl.id<>OLD.id)
     >(SELECT s.grand_total FROM sale s JOIN sale_line sl ON sl.sale_id=s.id
       WHERE sl.id=OLD.sale_line_id)
BEGIN SELECT RAISE(ABORT,'return exceeds original sale'); END;

-- A posted return, and every value of a completed sale, is final.
CREATE TRIGGER trg_return_header_immutable_v32 BEFORE UPDATE ON sale_return
WHEN OLD.receipt_snapshot IS NOT NULL AND (
  NEW.return_no IS NOT OLD.return_no OR NEW.sale_id IS NOT OLD.sale_id OR
  NEW.return_date IS NOT OLD.return_date OR NEW.reason IS NOT OLD.reason OR
  NEW.refund_mode IS NOT OLD.refund_mode OR NEW.taxable_total<>OLD.taxable_total OR
  NEW.tax_total<>OLD.tax_total OR NEW.refund_total<>OLD.refund_total OR
  NEW.cost_total<>OLD.cost_total OR NEW.receipt_snapshot IS NOT OLD.receipt_snapshot)
BEGIN SELECT RAISE(ABORT,'posted return is immutable'); END;

CREATE TRIGGER trg_return_line_no_insert_after_post_v32 BEFORE INSERT ON sale_return_line
WHEN (SELECT receipt_snapshot FROM sale_return WHERE id=NEW.sale_return_id) IS NOT NULL
BEGIN SELECT RAISE(ABORT,'posted return lines are immutable'); END;
CREATE TRIGGER trg_return_line_no_update_after_post_v32 BEFORE UPDATE ON sale_return_line
WHEN (SELECT receipt_snapshot FROM sale_return WHERE id=OLD.sale_return_id) IS NOT NULL
BEGIN SELECT RAISE(ABORT,'posted return lines are immutable'); END;
CREATE TRIGGER trg_return_line_no_delete_after_post_v32 BEFORE DELETE ON sale_return_line
WHEN (SELECT receipt_snapshot FROM sale_return WHERE id=OLD.sale_return_id) IS NOT NULL
BEGIN SELECT RAISE(ABORT,'posted return lines are immutable'); END;

CREATE TRIGGER trg_sale_financial_immutable_v32 BEFORE UPDATE ON sale
WHEN OLD.status IN ('COMPLETED','VOID') AND (
  NEW.line_count<>OLD.line_count OR NEW.gross_total<>OLD.gross_total OR
  NEW.discount_total<>OLD.discount_total OR NEW.bill_discount<>OLD.bill_discount OR
  NEW.paid_total<>OLD.paid_total OR NEW.change_returned<>OLD.change_returned OR
  (OLD.receipt_snapshot IS NOT NULL AND NEW.receipt_snapshot IS NOT OLD.receipt_snapshot))
BEGIN SELECT RAISE(ABORT,'completed sale values are immutable'); END;

-- Hourly and per-product sales projections subtract returns incrementally.
CREATE TABLE sale_hour_return_day(
  day TEXT NOT NULL,weekday TEXT NOT NULL,hour INTEGER NOT NULL,
  return_count INTEGER NOT NULL,refund_total INTEGER NOT NULL,
  PRIMARY KEY(day,weekday,hour)
) WITHOUT ROWID, STRICT;
INSERT INTO sale_hour_return_day(day,weekday,hour,return_count,refund_total)
SELECT return_date,strftime('%w',created_at),CAST(strftime('%H',created_at) AS INTEGER),
       COUNT(*),SUM(refund_total)
FROM sale_return WHERE refund_total<>0
GROUP BY return_date,strftime('%w',created_at),strftime('%H',created_at);
CREATE TRIGGER trg_sale_hour_return_total_v32 AFTER UPDATE OF refund_total ON sale_return
WHEN NEW.refund_total<>OLD.refund_total
BEGIN
  INSERT INTO sale_hour_return_day(day,weekday,hour,return_count,refund_total)
  VALUES (NEW.return_date,strftime('%w',NEW.created_at),
          CAST(strftime('%H',NEW.created_at) AS INTEGER),
          CASE WHEN OLD.refund_total=0 THEN 1 ELSE 0 END,
          NEW.refund_total-OLD.refund_total)
  ON CONFLICT(day,weekday,hour) DO UPDATE SET
    return_count=return_count+excluded.return_count,
    refund_total=refund_total+excluded.refund_total;
END;

CREATE TABLE sale_product_return_day(
  product_id INTEGER NOT NULL,day TEXT NOT NULL,returned_qty INTEGER NOT NULL,
  PRIMARY KEY(product_id,day)
) WITHOUT ROWID, STRICT;
INSERT INTO sale_product_return_day(product_id,day,returned_qty)
SELECT sl.product_id,r.return_date,SUM(rl.qty)
FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
JOIN sale_line sl ON sl.id=rl.sale_line_id
GROUP BY sl.product_id,r.return_date;
CREATE TRIGGER trg_sale_product_return_insert_v32 AFTER INSERT ON sale_return_line
BEGIN
  INSERT INTO sale_product_return_day(product_id,day,returned_qty)
  SELECT sl.product_id,r.return_date,NEW.qty
  FROM sale_line sl JOIN sale_return r ON r.id=NEW.sale_return_id
  WHERE sl.id=NEW.sale_line_id
  ON CONFLICT(product_id,day) DO UPDATE SET returned_qty=returned_qty+excluded.returned_qty;
END;
CREATE TRIGGER trg_sale_product_return_update_v32 AFTER UPDATE OF qty ON sale_return_line
WHEN NEW.qty<>OLD.qty
BEGIN
  UPDATE sale_product_return_day SET returned_qty=returned_qty+NEW.qty-OLD.qty
  WHERE product_id=(SELECT product_id FROM sale_line WHERE id=OLD.sale_line_id)
    AND day=(SELECT return_date FROM sale_return WHERE id=OLD.sale_return_id);
END;
CREATE TRIGGER trg_sale_product_return_delete_v32 AFTER DELETE ON sale_return_line
BEGIN
  UPDATE sale_product_return_day SET returned_qty=returned_qty-OLD.qty
  WHERE product_id=(SELECT product_id FROM sale_line WHERE id=OLD.sale_line_id)
    AND day=(SELECT return_date FROM sale_return WHERE id=OLD.sale_return_id);
  DELETE FROM sale_product_return_day
  WHERE product_id=(SELECT product_id FROM sale_line WHERE id=OLD.sale_line_id)
    AND day=(SELECT return_date FROM sale_return WHERE id=OLD.sale_return_id)
    AND returned_qty=0;
END;

-- Category and GST rows use the category saved on the sale line, so a sale and
-- its later returns stay in the same bucket after a category edit or catalog
-- deletion. Legacy lines without a snapshot follow the live product, or 0 once
-- that product is deleted.
DROP TRIGGER trg_sale_report_sale_insert;
CREATE TRIGGER trg_sale_report_sale_insert AFTER INSERT ON sale
WHEN NEW.status='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT NEW.sale_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           COUNT(*),SUM(sl.qty),SUM(sl.taxable_amount),
           SUM(sl.tax_amount),SUM(sl.cost_amount)
    FROM sale_line sl LEFT JOIN product p ON p.id=sl.product_id
    WHERE sl.sale_id=NEW.id AND NEW.status='COMPLETED'
    GROUP BY COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (NEW.sale_date);
END;

DROP TRIGGER trg_sale_report_sale_update;
CREATE TRIGGER trg_sale_report_sale_update AFTER UPDATE OF status,sale_date ON sale
WHEN OLD.status='COMPLETED' OR NEW.status='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT OLD.sale_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -COUNT(*),-SUM(sl.qty),-SUM(sl.taxable_amount),
           -SUM(sl.tax_amount),-SUM(sl.cost_amount)
    FROM sale_line sl LEFT JOIN product p ON p.id=sl.product_id
    WHERE sl.sale_id=OLD.id AND OLD.status='COMPLETED'
    GROUP BY COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.sale_date,NEW.sale_date);
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT NEW.sale_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           COUNT(*),SUM(sl.qty),SUM(sl.taxable_amount),
           SUM(sl.tax_amount),SUM(sl.cost_amount)
    FROM sale_line sl LEFT JOIN product p ON p.id=sl.product_id
    WHERE sl.sale_id=NEW.id AND NEW.status='COMPLETED'
    GROUP BY COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.sale_date,NEW.sale_date);
END;

DROP TRIGGER trg_sale_report_sale_delete;
CREATE TRIGGER trg_sale_report_sale_delete BEFORE DELETE ON sale
WHEN OLD.status='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT OLD.sale_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -COUNT(*),-SUM(sl.qty),-SUM(sl.taxable_amount),
           -SUM(sl.tax_amount),-SUM(sl.cost_amount)
    FROM sale_line sl LEFT JOIN product p ON p.id=sl.product_id
    WHERE sl.sale_id=OLD.id AND OLD.status='COMPLETED'
    GROUP BY COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.sale_date);
END;

DROP TRIGGER trg_sale_report_line_insert;
CREATE TRIGGER trg_sale_report_line_insert AFTER INSERT ON sale_line
WHEN (SELECT status FROM sale WHERE id=NEW.sale_id)='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT s.sale_date,COALESCE(NEW.category_id_snapshot,
             (SELECT category_id FROM product WHERE id=NEW.product_id),0),
           COALESCE(NEW.hsn_code,''),NEW.tax_rate_pct,
           1,NEW.qty,NEW.taxable_amount,
           NEW.tax_amount,NEW.cost_amount
    FROM sale s WHERE s.id=NEW.sale_id AND s.status='COMPLETED'
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT sale_date FROM sale WHERE id=NEW.sale_id));
END;

DROP TRIGGER trg_sale_report_line_delete;
CREATE TRIGGER trg_sale_report_line_delete AFTER DELETE ON sale_line
WHEN (SELECT status FROM sale WHERE id=OLD.sale_id)='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT s.sale_date,COALESCE(OLD.category_id_snapshot,
             (SELECT category_id FROM product WHERE id=OLD.product_id),0),
           COALESCE(OLD.hsn_code,''),OLD.tax_rate_pct,
           -1,-OLD.qty,-OLD.taxable_amount,
           -OLD.tax_amount,-OLD.cost_amount
    FROM sale s WHERE s.id=OLD.sale_id AND s.status='COMPLETED'
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT sale_date FROM sale WHERE id=OLD.sale_id));
END;

DROP TRIGGER trg_sale_report_line_update;
CREATE TRIGGER trg_sale_report_line_update AFTER UPDATE ON sale_line
WHEN (SELECT status FROM sale WHERE id=OLD.sale_id)='COMPLETED' OR (SELECT status FROM sale WHERE id=NEW.sale_id)='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,COALESCE(OLD.category_id_snapshot,
             (SELECT category_id FROM product WHERE id=OLD.product_id),0),
           COALESCE(OLD.hsn_code,''),OLD.tax_rate_pct,
           -COUNT(*),SUM(rl.qty),SUM(rl.taxable_amount),SUM(rl.tax_amount),SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
    WHERE rl.sale_line_id=OLD.id
    GROUP BY r.return_date
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT s.sale_date,COALESCE(OLD.category_id_snapshot,
             (SELECT category_id FROM product WHERE id=OLD.product_id),0),
           COALESCE(OLD.hsn_code,''),OLD.tax_rate_pct,
           -1,-OLD.qty,-OLD.taxable_amount,
           -OLD.tax_amount,-OLD.cost_amount
    FROM sale s WHERE s.id=OLD.sale_id AND s.status='COMPLETED'
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT sale_date FROM sale WHERE id=OLD.sale_id),(SELECT sale_date FROM sale WHERE id=NEW.sale_id));
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT s.sale_date,COALESCE(NEW.category_id_snapshot,
             (SELECT category_id FROM product WHERE id=NEW.product_id),0),
           COALESCE(NEW.hsn_code,''),NEW.tax_rate_pct,
           1,NEW.qty,NEW.taxable_amount,
           NEW.tax_amount,NEW.cost_amount
    FROM sale s WHERE s.id=NEW.sale_id AND s.status='COMPLETED'
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,COALESCE(NEW.category_id_snapshot,
             (SELECT category_id FROM product WHERE id=NEW.product_id),0),
           COALESCE(NEW.hsn_code,''),NEW.tax_rate_pct,
           COUNT(*),-SUM(rl.qty),-SUM(rl.taxable_amount),-SUM(rl.tax_amount),-SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
    WHERE rl.sale_line_id=NEW.id
    GROUP BY r.return_date
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT sale_date FROM sale WHERE id=OLD.sale_id),(SELECT sale_date FROM sale WHERE id=NEW.sale_id));
END;

DROP TRIGGER trg_sale_report_return_insert;
CREATE TRIGGER trg_sale_report_return_insert AFTER INSERT ON sale_return_line
WHEN 1 BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           1,-NEW.qty,-NEW.taxable_amount,
           -NEW.tax_amount,-NEW.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=NEW.sale_line_id
    LEFT JOIN product p ON p.id=sl.product_id WHERE r.id=NEW.sale_return_id
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT return_date FROM sale_return WHERE id=NEW.sale_return_id));
END;

DROP TRIGGER trg_sale_report_return_delete;
CREATE TRIGGER trg_sale_report_return_delete AFTER DELETE ON sale_return_line
WHEN 1 BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -1,OLD.qty,OLD.taxable_amount,
           OLD.tax_amount,OLD.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=OLD.sale_line_id
    LEFT JOIN product p ON p.id=sl.product_id WHERE r.id=OLD.sale_return_id
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT return_date FROM sale_return WHERE id=OLD.sale_return_id));
END;

DROP TRIGGER trg_sale_report_return_update;
CREATE TRIGGER trg_sale_report_return_update AFTER UPDATE ON sale_return_line
WHEN 1 BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -1,OLD.qty,OLD.taxable_amount,
           OLD.tax_amount,OLD.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=OLD.sale_line_id
    LEFT JOIN product p ON p.id=sl.product_id WHERE r.id=OLD.sale_return_id
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT return_date FROM sale_return WHERE id=OLD.sale_return_id),(SELECT return_date FROM sale_return WHERE id=NEW.sale_return_id));
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           1,-NEW.qty,-NEW.taxable_amount,
           -NEW.tax_amount,-NEW.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=NEW.sale_line_id
    LEFT JOIN product p ON p.id=sl.product_id WHERE r.id=NEW.sale_return_id
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT return_date FROM sale_return WHERE id=OLD.sale_return_id),(SELECT return_date FROM sale_return WHERE id=NEW.sale_return_id));
END;

DROP TRIGGER trg_sale_report_return_date;
CREATE TRIGGER trg_sale_report_return_date AFTER UPDATE OF return_date ON sale_return
WHEN OLD.return_date IS NOT NEW.return_date BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT OLD.return_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -COUNT(*),SUM(rl.qty),SUM(rl.taxable_amount),
           SUM(rl.tax_amount),SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
    LEFT JOIN product p ON p.id=sl.product_id WHERE rl.sale_return_id=OLD.id
    GROUP BY COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.return_date,NEW.return_date);
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT NEW.return_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           COUNT(*),-SUM(rl.qty),-SUM(rl.taxable_amount),
           -SUM(rl.tax_amount),-SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
    LEFT JOIN product p ON p.id=sl.product_id WHERE rl.sale_return_id=NEW.id
    GROUP BY COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.return_date,NEW.return_date);
END;

DROP TRIGGER trg_sale_report_return_parent_delete;
CREATE TRIGGER trg_sale_report_return_parent_delete BEFORE DELETE ON sale_return
WHEN 1 BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT OLD.return_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -COUNT(*),SUM(rl.qty),SUM(rl.taxable_amount),
           SUM(rl.tax_amount),SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
    LEFT JOIN product p ON p.id=sl.product_id WHERE rl.sale_return_id=OLD.id
    GROUP BY COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.return_date);
END;

-- Only legacy lines without a saved category follow a live category change.
DROP TRIGGER trg_sale_report_product_category;
CREATE TRIGGER trg_sale_report_product_category AFTER UPDATE OF category_id ON product
WHEN OLD.category_id IS NOT NEW.category_id AND EXISTS(
  SELECT 1 FROM sale_line WHERE product_id=NEW.id AND category_id_snapshot IS NULL) BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT day,OLD.category_id,hsn,rate,-COUNT(*),
           -SUM(quantity),-SUM(revenue),-SUM(tax),-SUM(cost)
    FROM (
      SELECT s.sale_date AS day,COALESCE(sl.hsn_code,'') AS hsn,sl.tax_rate_pct AS rate,
             sl.qty AS quantity,sl.taxable_amount AS revenue,sl.tax_amount AS tax,sl.cost_amount AS cost
      FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
      WHERE sl.product_id=OLD.id AND sl.category_id_snapshot IS NULL AND s.status='COMPLETED'
      UNION ALL
      SELECT r.return_date,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
             -rl.qty,-rl.taxable_amount,-rl.tax_amount,-rl.cost_amount
      FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
      JOIN sale_return r ON r.id=rl.sale_return_id
      WHERE sl.product_id=OLD.id AND sl.category_id_snapshot IS NULL
    ) GROUP BY day,hsn,rate
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND category_id=OLD.category_id AND day IN (
      SELECT s.sale_date FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
      WHERE sl.product_id=OLD.id AND sl.category_id_snapshot IS NULL
      UNION SELECT r.return_date FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
      JOIN sale_return r ON r.id=rl.sale_return_id
      WHERE sl.product_id=OLD.id AND sl.category_id_snapshot IS NULL);
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT day,NEW.category_id,hsn,rate,COUNT(*),
           SUM(quantity),SUM(revenue),SUM(tax),SUM(cost)
    FROM (
      SELECT s.sale_date AS day,COALESCE(sl.hsn_code,'') AS hsn,sl.tax_rate_pct AS rate,
             sl.qty AS quantity,sl.taxable_amount AS revenue,sl.tax_amount AS tax,sl.cost_amount AS cost
      FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
      WHERE sl.product_id=NEW.id AND sl.category_id_snapshot IS NULL AND s.status='COMPLETED'
      UNION ALL
      SELECT r.return_date,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
             -rl.qty,-rl.taxable_amount,-rl.tax_amount,-rl.cost_amount
      FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
      JOIN sale_return r ON r.id=rl.sale_return_id
      WHERE sl.product_id=NEW.id AND sl.category_id_snapshot IS NULL
    ) GROUP BY day,hsn,rate
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND category_id=NEW.category_id AND day IN (
      SELECT s.sale_date FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
      WHERE sl.product_id=NEW.id AND sl.category_id_snapshot IS NULL
      UNION SELECT r.return_date FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
      JOIN sale_return r ON r.id=rl.sale_return_id
      WHERE sl.product_id=NEW.id AND sl.category_id_snapshot IS NULL);
END;

CREATE TRIGGER trg_sale_report_product_delete_v32 AFTER DELETE ON product
WHEN EXISTS(SELECT 1 FROM sale_line WHERE product_id=OLD.id AND category_id_snapshot IS NULL) BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT day,category_id,hsn,rate,SUM(entries),SUM(quantity),SUM(revenue),SUM(tax),SUM(cost)
    FROM (
      SELECT s.sale_date AS day,OLD.category_id AS category_id,COALESCE(sl.hsn_code,'') AS hsn,
             sl.tax_rate_pct AS rate,-1 AS entries,-sl.qty AS quantity,-sl.taxable_amount AS revenue,
             -sl.tax_amount AS tax,-sl.cost_amount AS cost
      FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
      WHERE sl.product_id=OLD.id AND sl.category_id_snapshot IS NULL AND s.status='COMPLETED'
      UNION ALL
      SELECT s.sale_date,0,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,1,sl.qty,sl.taxable_amount,
             sl.tax_amount,sl.cost_amount
      FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
      WHERE sl.product_id=OLD.id AND sl.category_id_snapshot IS NULL AND s.status='COMPLETED'
      UNION ALL
      SELECT r.return_date,OLD.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,-1,rl.qty,
             rl.taxable_amount,rl.tax_amount,rl.cost_amount
      FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
      JOIN sale_return r ON r.id=rl.sale_return_id
      WHERE sl.product_id=OLD.id AND sl.category_id_snapshot IS NULL
      UNION ALL
      SELECT r.return_date,0,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,1,-rl.qty,
             -rl.taxable_amount,-rl.tax_amount,-rl.cost_amount
      FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
      JOIN sale_return r ON r.id=rl.sale_return_id
      WHERE sl.product_id=OLD.id AND sl.category_id_snapshot IS NULL
    ) GROUP BY day,category_id,hsn,rate
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND category_id IN (OLD.category_id,0) AND day IN (
      SELECT s.sale_date FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
      WHERE sl.product_id=OLD.id AND sl.category_id_snapshot IS NULL
      UNION SELECT r.return_date FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
      JOIN sale_return r ON r.id=rl.sale_return_id
      WHERE sl.product_id=OLD.id AND sl.category_id_snapshot IS NULL);
END;

-- Rebuild once under the sale-time definition; earlier rows followed the
-- product's category at the time of each incremental write.
DELETE FROM sale_report_day;
INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
SELECT day,category_id,hsn,rate,COUNT(*),SUM(quantity),SUM(revenue),SUM(tax),SUM(cost)
FROM (
  SELECT s.sale_date AS day,COALESCE(sl.category_id_snapshot,p.category_id,0) AS category_id,
         COALESCE(sl.hsn_code,'') AS hsn,sl.tax_rate_pct AS rate,sl.qty AS quantity,
         sl.taxable_amount AS revenue,sl.tax_amount AS tax,sl.cost_amount AS cost
  FROM sale_line sl JOIN sale s ON s.id=sl.sale_id LEFT JOIN product p ON p.id=sl.product_id
  WHERE s.status='COMPLETED'
  UNION ALL
  SELECT r.return_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),
         sl.tax_rate_pct,-rl.qty,-rl.taxable_amount,-rl.tax_amount,-rl.cost_amount
  FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
  JOIN sale_line sl ON sl.id=rl.sale_line_id LEFT JOIN product p ON p.id=sl.product_id
) GROUP BY day,category_id,hsn,rate;

-- A damaged customer return is recorded against the sold lot's historical ID,
-- even after that zero lot was deleted; it never changes saleable stock.
DROP TRIGGER trg_damage_live_lot_v29;
CREATE TRIGGER trg_damage_live_lot_v29 BEFORE INSERT ON damage
WHEN NEW.reason<>'CUSTOMER_RETURN'
  AND NOT EXISTS (SELECT 1 FROM product_lot WHERE id=NEW.lot_id AND product_id=NEW.product_id)
BEGIN SELECT RAISE(ABORT,'damage lot is missing'); END;
