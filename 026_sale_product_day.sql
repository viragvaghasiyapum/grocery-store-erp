-- Low Stock needs gross sold quantity for an arbitrary 30-day window. Keep one
-- product/day row so a bounded page never revisits years of sale lines.
CREATE TABLE IF NOT EXISTS sale_product_day (
    product_id INTEGER NOT NULL,
    day TEXT NOT NULL,
    entries INTEGER NOT NULL,
    qty INTEGER NOT NULL,
    PRIMARY KEY(product_id,day)
) WITHOUT ROWID, STRICT;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_day_sale_update
AFTER UPDATE OF status,sale_date ON sale
WHEN OLD.status='COMPLETED' OR NEW.status='COMPLETED' BEGIN
    INSERT INTO sale_product_day(product_id,day,entries,qty)
    SELECT sl.product_id,OLD.sale_date,-COUNT(*),-SUM(sl.qty)
    FROM sale_line sl WHERE sl.sale_id=OLD.id AND OLD.status='COMPLETED'
    GROUP BY sl.product_id
    ON CONFLICT(product_id,day) DO UPDATE SET
      entries=entries+excluded.entries,qty=qty+excluded.qty;
    DELETE FROM sale_product_day WHERE entries=0 AND day IN (OLD.sale_date,NEW.sale_date)
      AND product_id IN (SELECT product_id FROM sale_line WHERE sale_id=NEW.id);
    INSERT INTO sale_product_day(product_id,day,entries,qty)
    SELECT sl.product_id,NEW.sale_date,COUNT(*),SUM(sl.qty)
    FROM sale_line sl WHERE sl.sale_id=NEW.id AND NEW.status='COMPLETED'
    GROUP BY sl.product_id
    ON CONFLICT(product_id,day) DO UPDATE SET
      entries=entries+excluded.entries,qty=qty+excluded.qty;
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_day_sale_delete
BEFORE DELETE ON sale WHEN OLD.status='COMPLETED' BEGIN
    INSERT INTO sale_product_day(product_id,day,entries,qty)
    SELECT sl.product_id,OLD.sale_date,-COUNT(*),-SUM(sl.qty)
    FROM sale_line sl WHERE sl.sale_id=OLD.id GROUP BY sl.product_id
    ON CONFLICT(product_id,day) DO UPDATE SET
      entries=entries+excluded.entries,qty=qty+excluded.qty;
    DELETE FROM sale_product_day WHERE entries=0 AND day=OLD.sale_date
      AND product_id IN (SELECT product_id FROM sale_line WHERE sale_id=OLD.id);
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_day_line_insert
AFTER INSERT ON sale_line
WHEN (SELECT status FROM sale WHERE id=NEW.sale_id)='COMPLETED' BEGIN
    INSERT INTO sale_product_day(product_id,day,entries,qty)
    SELECT NEW.product_id,s.sale_date,1,NEW.qty FROM sale s WHERE s.id=NEW.sale_id
    ON CONFLICT(product_id,day) DO UPDATE SET
      entries=entries+1,qty=qty+excluded.qty;
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_day_line_delete
AFTER DELETE ON sale_line
WHEN (SELECT status FROM sale WHERE id=OLD.sale_id)='COMPLETED' BEGIN
    INSERT INTO sale_product_day(product_id,day,entries,qty)
    SELECT OLD.product_id,s.sale_date,-1,-OLD.qty FROM sale s WHERE s.id=OLD.sale_id
    ON CONFLICT(product_id,day) DO UPDATE SET
      entries=entries-1,qty=qty+excluded.qty;
    DELETE FROM sale_product_day WHERE entries=0
      AND product_id=OLD.product_id AND day=(SELECT sale_date FROM sale WHERE id=OLD.sale_id);
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_product_day_line_update
AFTER UPDATE OF sale_id,product_id,qty ON sale_line
WHEN (SELECT status FROM sale WHERE id=OLD.sale_id)='COMPLETED'
  OR (SELECT status FROM sale WHERE id=NEW.sale_id)='COMPLETED' BEGIN
    INSERT INTO sale_product_day(product_id,day,entries,qty)
    SELECT OLD.product_id,s.sale_date,-1,-OLD.qty FROM sale s
    WHERE s.id=OLD.sale_id AND s.status='COMPLETED'
    ON CONFLICT(product_id,day) DO UPDATE SET
      entries=entries-1,qty=qty+excluded.qty;
    DELETE FROM sale_product_day WHERE entries=0
      AND product_id=OLD.product_id AND day=(SELECT sale_date FROM sale WHERE id=OLD.sale_id);
    INSERT INTO sale_product_day(product_id,day,entries,qty)
    SELECT NEW.product_id,s.sale_date,1,NEW.qty FROM sale s
    WHERE s.id=NEW.sale_id AND s.status='COMPLETED'
    ON CONFLICT(product_id,day) DO UPDATE SET
      entries=entries+1,qty=qty+excluded.qty;
END;

INSERT INTO sale_product_day(product_id,day,entries,qty)
SELECT sl.product_id,s.sale_date,COUNT(*),SUM(sl.qty)
FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
WHERE s.status='COMPLETED'
GROUP BY sl.product_id,s.sale_date;
PRAGMA user_version = 26;
