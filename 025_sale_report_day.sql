-- Compact daily outward movement totals shared by category and GST reports.
-- Category follows the product's current classification, matching the source report.
CREATE TABLE IF NOT EXISTS sale_report_day (
    day TEXT NOT NULL, category_id INTEGER NOT NULL, hsn TEXT NOT NULL,
    rate INTEGER NOT NULL, entries INTEGER NOT NULL, quantity INTEGER NOT NULL,
    revenue INTEGER NOT NULL, tax INTEGER NOT NULL, cost INTEGER NOT NULL,
    PRIMARY KEY(day,category_id,hsn,rate)
) WITHOUT ROWID, STRICT;
CREATE INDEX IF NOT EXISTS ix_sale_report_day_hsn ON sale_report_day(day,hsn,rate);

CREATE TRIGGER IF NOT EXISTS trg_sale_report_sale_insert AFTER INSERT ON sale
WHEN NEW.status='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT NEW.sale_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           COUNT(*),SUM(sl.qty),SUM(sl.taxable_amount),
           SUM(sl.tax_amount),SUM(sl.cost_amount)
    FROM sale_line sl JOIN product p ON p.id=sl.product_id
    WHERE sl.sale_id=NEW.id AND NEW.status='COMPLETED'
    GROUP BY p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (NEW.sale_date);
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_sale_update AFTER UPDATE OF status,sale_date ON sale
WHEN OLD.status='COMPLETED' OR NEW.status='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT OLD.sale_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -COUNT(*),-SUM(sl.qty),-SUM(sl.taxable_amount),
           -SUM(sl.tax_amount),-SUM(sl.cost_amount)
    FROM sale_line sl JOIN product p ON p.id=sl.product_id
    WHERE sl.sale_id=OLD.id AND OLD.status='COMPLETED'
    GROUP BY p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.sale_date,NEW.sale_date);
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT NEW.sale_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           COUNT(*),SUM(sl.qty),SUM(sl.taxable_amount),
           SUM(sl.tax_amount),SUM(sl.cost_amount)
    FROM sale_line sl JOIN product p ON p.id=sl.product_id
    WHERE sl.sale_id=NEW.id AND NEW.status='COMPLETED'
    GROUP BY p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.sale_date,NEW.sale_date);
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_sale_delete BEFORE DELETE ON sale
WHEN OLD.status='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT OLD.sale_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -COUNT(*),-SUM(sl.qty),-SUM(sl.taxable_amount),
           -SUM(sl.tax_amount),-SUM(sl.cost_amount)
    FROM sale_line sl JOIN product p ON p.id=sl.product_id
    WHERE sl.sale_id=OLD.id AND OLD.status='COMPLETED'
    GROUP BY p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.sale_date);
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_line_insert AFTER INSERT ON sale_line
WHEN (SELECT status FROM sale WHERE id=NEW.sale_id)='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT s.sale_date,p.category_id,COALESCE(NEW.hsn_code,''),NEW.tax_rate_pct,
           1,NEW.qty,NEW.taxable_amount,
           NEW.tax_amount,NEW.cost_amount
    FROM sale s JOIN product p ON p.id=NEW.product_id
    WHERE s.id=NEW.sale_id AND s.status='COMPLETED'
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT sale_date FROM sale WHERE id=NEW.sale_id));
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_line_delete AFTER DELETE ON sale_line
WHEN (SELECT status FROM sale WHERE id=OLD.sale_id)='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT s.sale_date,p.category_id,COALESCE(OLD.hsn_code,''),OLD.tax_rate_pct,
           -1,-OLD.qty,-OLD.taxable_amount,
           -OLD.tax_amount,-OLD.cost_amount
    FROM sale s JOIN product p ON p.id=OLD.product_id
    WHERE s.id=OLD.sale_id AND s.status='COMPLETED'
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT sale_date FROM sale WHERE id=OLD.sale_id));
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_line_update AFTER UPDATE ON sale_line
WHEN (SELECT status FROM sale WHERE id=OLD.sale_id)='COMPLETED' OR (SELECT status FROM sale WHERE id=NEW.sale_id)='COMPLETED' BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,p.category_id,COALESCE(OLD.hsn_code,''),OLD.tax_rate_pct,
           -COUNT(*),SUM(rl.qty),SUM(rl.taxable_amount),SUM(rl.tax_amount),SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
    JOIN product p ON p.id=OLD.product_id WHERE rl.sale_line_id=OLD.id
    GROUP BY r.return_date,p.category_id
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT s.sale_date,p.category_id,COALESCE(OLD.hsn_code,''),OLD.tax_rate_pct,
           -1,-OLD.qty,-OLD.taxable_amount,
           -OLD.tax_amount,-OLD.cost_amount
    FROM sale s JOIN product p ON p.id=OLD.product_id
    WHERE s.id=OLD.sale_id AND s.status='COMPLETED'
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT sale_date FROM sale WHERE id=OLD.sale_id),(SELECT sale_date FROM sale WHERE id=NEW.sale_id));
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT s.sale_date,p.category_id,COALESCE(NEW.hsn_code,''),NEW.tax_rate_pct,
           1,NEW.qty,NEW.taxable_amount,
           NEW.tax_amount,NEW.cost_amount
    FROM sale s JOIN product p ON p.id=NEW.product_id
    WHERE s.id=NEW.sale_id AND s.status='COMPLETED'
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,p.category_id,COALESCE(NEW.hsn_code,''),NEW.tax_rate_pct,
           COUNT(*),-SUM(rl.qty),-SUM(rl.taxable_amount),-SUM(rl.tax_amount),-SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
    JOIN product p ON p.id=NEW.product_id WHERE rl.sale_line_id=NEW.id
    GROUP BY r.return_date,p.category_id
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT sale_date FROM sale WHERE id=OLD.sale_id),(SELECT sale_date FROM sale WHERE id=NEW.sale_id));
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_return_insert AFTER INSERT ON sale_return_line
WHEN 1 BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           1,-NEW.qty,-NEW.taxable_amount,
           -NEW.tax_amount,-NEW.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=NEW.sale_line_id
    JOIN product p ON p.id=sl.product_id WHERE r.id=NEW.sale_return_id
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT return_date FROM sale_return WHERE id=NEW.sale_return_id));
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_return_delete AFTER DELETE ON sale_return_line
WHEN 1 BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -1,OLD.qty,OLD.taxable_amount,
           OLD.tax_amount,OLD.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=OLD.sale_line_id
    JOIN product p ON p.id=sl.product_id WHERE r.id=OLD.sale_return_id
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT return_date FROM sale_return WHERE id=OLD.sale_return_id));
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_return_update AFTER UPDATE ON sale_return_line
WHEN 1 BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -1,OLD.qty,OLD.taxable_amount,
           OLD.tax_amount,OLD.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=OLD.sale_line_id
    JOIN product p ON p.id=sl.product_id WHERE r.id=OLD.sale_return_id
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT return_date FROM sale_return WHERE id=OLD.sale_return_id),(SELECT return_date FROM sale_return WHERE id=NEW.sale_return_id));
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT r.return_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           1,-NEW.qty,-NEW.taxable_amount,
           -NEW.tax_amount,-NEW.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=NEW.sale_line_id
    JOIN product p ON p.id=sl.product_id WHERE r.id=NEW.sale_return_id
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN ((SELECT return_date FROM sale_return WHERE id=OLD.sale_return_id),(SELECT return_date FROM sale_return WHERE id=NEW.sale_return_id));
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_return_date AFTER UPDATE OF return_date ON sale_return
WHEN OLD.return_date IS NOT NEW.return_date BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT OLD.return_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -COUNT(*),SUM(rl.qty),SUM(rl.taxable_amount),
           SUM(rl.tax_amount),SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
    JOIN product p ON p.id=sl.product_id WHERE rl.sale_return_id=OLD.id
    GROUP BY p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.return_date,NEW.return_date);
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT NEW.return_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           COUNT(*),-SUM(rl.qty),-SUM(rl.taxable_amount),
           -SUM(rl.tax_amount),-SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
    JOIN product p ON p.id=sl.product_id WHERE rl.sale_return_id=NEW.id
    GROUP BY p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.return_date,NEW.return_date);
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_return_parent_delete BEFORE DELETE ON sale_return
WHEN 1 BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT OLD.return_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
           -COUNT(*),SUM(rl.qty),SUM(rl.taxable_amount),
           SUM(rl.tax_amount),SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
    JOIN product p ON p.id=sl.product_id WHERE rl.sale_return_id=OLD.id
    GROUP BY p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0 AND day IN (OLD.return_date);
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_report_product_category AFTER UPDATE OF category_id ON product
WHEN OLD.category_id IS NOT NEW.category_id BEGIN
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT day,OLD.category_id,hsn,rate,-COUNT(*),
           -SUM(quantity),-SUM(revenue),-SUM(tax),-SUM(cost)
    FROM (
      SELECT s.sale_date AS day,COALESCE(sl.hsn_code,'') AS hsn,sl.tax_rate_pct AS rate,
             sl.qty AS quantity,sl.taxable_amount AS revenue,sl.tax_amount AS tax,sl.cost_amount AS cost
      FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
      WHERE sl.product_id=OLD.id AND s.status='COMPLETED'
      UNION ALL
      SELECT r.return_date,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
             -rl.qty,-rl.taxable_amount,-rl.tax_amount,-rl.cost_amount
      FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
      JOIN sale_return r ON r.id=rl.sale_return_id WHERE sl.product_id=OLD.id
    ) GROUP BY day,hsn,rate
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0;
    INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
    SELECT day,NEW.category_id,hsn,rate,COUNT(*),
           SUM(quantity),SUM(revenue),SUM(tax),SUM(cost)
    FROM (
      SELECT s.sale_date AS day,COALESCE(sl.hsn_code,'') AS hsn,sl.tax_rate_pct AS rate,
             sl.qty AS quantity,sl.taxable_amount AS revenue,sl.tax_amount AS tax,sl.cost_amount AS cost
      FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
      WHERE sl.product_id=NEW.id AND s.status='COMPLETED'
      UNION ALL
      SELECT r.return_date,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
             -rl.qty,-rl.taxable_amount,-rl.tax_amount,-rl.cost_amount
      FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
      JOIN sale_return r ON r.id=rl.sale_return_id WHERE sl.product_id=NEW.id
    ) GROUP BY day,hsn,rate
    ON CONFLICT(day,category_id,hsn,rate) DO UPDATE SET
      entries=entries+excluded.entries,quantity=quantity+excluded.quantity,
      revenue=revenue+excluded.revenue,tax=tax+excluded.tax,cost=cost+excluded.cost;
    DELETE FROM sale_report_day WHERE entries=0;
END;

-- Existing stores are backfilled once as part of the transactional migration.
DELETE FROM sale_report_day;
INSERT INTO sale_report_day(day,category_id,hsn,rate,entries,quantity,revenue,tax,cost)
SELECT day,category_id,hsn,rate,COUNT(*),SUM(quantity),SUM(revenue),SUM(tax),SUM(cost)
FROM (
  SELECT s.sale_date AS day,p.category_id,COALESCE(sl.hsn_code,'') AS hsn,
         sl.tax_rate_pct AS rate,sl.qty AS quantity,sl.taxable_amount AS revenue,
         sl.tax_amount AS tax,sl.cost_amount AS cost
  FROM sale_line sl JOIN sale s ON s.id=sl.sale_id JOIN product p ON p.id=sl.product_id
  WHERE s.status='COMPLETED'
  UNION ALL
  SELECT r.return_date,p.category_id,COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
         -rl.qty,-rl.taxable_amount,-rl.tax_amount,-rl.cost_amount
  FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
  JOIN sale_line sl ON sl.id=rl.sale_line_id JOIN product p ON p.id=sl.product_id
) GROUP BY day,category_id,hsn,rate;

-- Slow Movers needs one last completed-sale date per product. Completion is
-- incremental; a void recalculates only the affected products.
CREATE TABLE IF NOT EXISTS sale_product_last (
    product_id INTEGER PRIMARY KEY,
    last_day TEXT NOT NULL
) WITHOUT ROWID, STRICT;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_last_complete
AFTER UPDATE OF status ON sale WHEN NEW.status='COMPLETED' AND OLD.status<>'COMPLETED' BEGIN
    INSERT INTO sale_product_last(product_id,last_day)
    SELECT product_id,NEW.sale_date FROM sale_line WHERE sale_id=NEW.id GROUP BY product_id
    ON CONFLICT(product_id) DO UPDATE SET last_day=MAX(last_day,excluded.last_day);
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_last_insert
AFTER INSERT ON sale_line WHEN (SELECT status FROM sale WHERE id=NEW.sale_id)='COMPLETED' BEGIN
    INSERT INTO sale_product_last(product_id,last_day)
    SELECT NEW.product_id,s.sale_date FROM sale s WHERE s.id=NEW.sale_id
    ON CONFLICT(product_id) DO UPDATE SET last_day=MAX(last_day,excluded.last_day);
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_last_void
AFTER UPDATE OF status ON sale WHEN OLD.status='COMPLETED' AND NEW.status<>'COMPLETED' BEGIN
    DELETE FROM sale_product_last WHERE product_id IN
        (SELECT product_id FROM sale_line WHERE sale_id=NEW.id);
    INSERT INTO sale_product_last(product_id,last_day)
    SELECT sl.product_id,MAX(s.sale_date) FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
    WHERE sl.product_id IN (SELECT product_id FROM sale_line WHERE sale_id=NEW.id)
      AND s.status='COMPLETED' GROUP BY sl.product_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_last_date
AFTER UPDATE OF sale_date ON sale
WHEN NEW.status='COMPLETED' AND OLD.sale_date IS NOT NEW.sale_date BEGIN
    DELETE FROM sale_product_last WHERE product_id IN
        (SELECT product_id FROM sale_line WHERE sale_id=NEW.id);
    INSERT INTO sale_product_last(product_id,last_day)
    SELECT sl.product_id,MAX(s.sale_date) FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
    WHERE sl.product_id IN (SELECT product_id FROM sale_line WHERE sale_id=NEW.id)
      AND s.status='COMPLETED' GROUP BY sl.product_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_last_line_delete
AFTER DELETE ON sale_line WHEN (SELECT status FROM sale WHERE id=OLD.sale_id)='COMPLETED' BEGIN
    DELETE FROM sale_product_last WHERE product_id=OLD.product_id;
    INSERT INTO sale_product_last(product_id,last_day)
    SELECT sl.product_id,MAX(s.sale_date) FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
    WHERE sl.product_id=OLD.product_id AND s.status='COMPLETED' GROUP BY sl.product_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_last_line_update
AFTER UPDATE OF product_id,sale_id ON sale_line
WHEN (SELECT status FROM sale WHERE id=OLD.sale_id)='COMPLETED'
  OR (SELECT status FROM sale WHERE id=NEW.sale_id)='COMPLETED' BEGIN
    DELETE FROM sale_product_last WHERE product_id IN (OLD.product_id,NEW.product_id);
    INSERT INTO sale_product_last(product_id,last_day)
    SELECT sl.product_id,MAX(s.sale_date) FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
    WHERE sl.product_id IN (OLD.product_id,NEW.product_id)
      AND s.status='COMPLETED' GROUP BY sl.product_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_last_sale_delete
BEFORE DELETE ON sale WHEN OLD.status='COMPLETED' BEGIN
    DELETE FROM sale_product_last WHERE product_id IN
        (SELECT product_id FROM sale_line WHERE sale_id=OLD.id);
    INSERT INTO sale_product_last(product_id,last_day)
    SELECT sl.product_id,MAX(s.sale_date) FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
    WHERE sl.product_id IN (SELECT product_id FROM sale_line WHERE sale_id=OLD.id)
      AND s.status='COMPLETED' AND s.id<>OLD.id GROUP BY sl.product_id;
END;

DELETE FROM sale_product_last;
INSERT INTO sale_product_last(product_id,last_day)
SELECT sl.product_id,MAX(s.sale_date) FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
WHERE s.status='COMPLETED' GROUP BY sl.product_id;

-- Preserve the sale-date filter while grouping checkout timestamps by weekday/hour.
CREATE TABLE IF NOT EXISTS sale_hour_day (
    day TEXT NOT NULL, weekday TEXT NOT NULL, hour INTEGER NOT NULL,
    bills INTEGER NOT NULL, revenue INTEGER NOT NULL,
    PRIMARY KEY(day,weekday,hour)
) WITHOUT ROWID, STRICT;

CREATE TRIGGER IF NOT EXISTS trg_sale_hour_insert AFTER INSERT ON sale
WHEN NEW.status='COMPLETED' AND NEW.completed_at IS NOT NULL BEGIN
    INSERT INTO sale_hour_day(day,weekday,hour,bills,revenue)
    VALUES(NEW.sale_date,strftime('%w',NEW.completed_at),CAST(strftime('%H',NEW.completed_at) AS INTEGER),1,NEW.grand_total)
    ON CONFLICT(day,weekday,hour) DO UPDATE SET
      bills=bills+1,revenue=revenue+excluded.revenue;
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_hour_update
AFTER UPDATE OF status,sale_date,completed_at,grand_total ON sale
WHEN (OLD.status='COMPLETED' AND OLD.completed_at IS NOT NULL)
  OR (NEW.status='COMPLETED' AND NEW.completed_at IS NOT NULL) BEGIN
    INSERT INTO sale_hour_day(day,weekday,hour,bills,revenue)
    SELECT OLD.sale_date,strftime('%w',OLD.completed_at),
           CAST(strftime('%H',OLD.completed_at) AS INTEGER),-1,-OLD.grand_total
    WHERE OLD.status='COMPLETED' AND OLD.completed_at IS NOT NULL
    ON CONFLICT(day,weekday,hour) DO UPDATE SET
      bills=bills-1,revenue=revenue+excluded.revenue;
    INSERT INTO sale_hour_day(day,weekday,hour,bills,revenue)
    SELECT NEW.sale_date,strftime('%w',NEW.completed_at),
           CAST(strftime('%H',NEW.completed_at) AS INTEGER),1,NEW.grand_total
    WHERE NEW.status='COMPLETED' AND NEW.completed_at IS NOT NULL
    ON CONFLICT(day,weekday,hour) DO UPDATE SET
      bills=bills+1,revenue=revenue+excluded.revenue;
    DELETE FROM sale_hour_day WHERE bills=0 AND day IN (OLD.sale_date,NEW.sale_date);
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_hour_delete BEFORE DELETE ON sale
WHEN OLD.status='COMPLETED' AND OLD.completed_at IS NOT NULL BEGIN
    UPDATE sale_hour_day SET bills=bills-1,revenue=revenue-OLD.grand_total
    WHERE day=OLD.sale_date AND weekday=strftime('%w',OLD.completed_at)
      AND hour=CAST(strftime('%H',OLD.completed_at) AS INTEGER);
    DELETE FROM sale_hour_day WHERE bills=0 AND day=OLD.sale_date;
END;

DELETE FROM sale_hour_day;
INSERT INTO sale_hour_day(day,weekday,hour,bills,revenue)
SELECT sale_date,strftime('%w',completed_at),CAST(strftime('%H',completed_at) AS INTEGER),
       COUNT(*),SUM(grand_total)
FROM sale WHERE status='COMPLETED' AND completed_at IS NOT NULL
GROUP BY sale_date,strftime('%w',completed_at),strftime('%H',completed_at);
