-- The dashboard needs only its current 30-day product window. A single
-- aggregate per product/snapshot name avoids duplicating five years of lines.
CREATE TABLE IF NOT EXISTS dashboard_top_window (
    id INTEGER PRIMARY KEY CHECK(id=1),
    from_day TEXT NOT NULL,
    to_day TEXT NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS dashboard_top_summary (
    product_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    local_name TEXT NOT NULL,
    local_is_null INTEGER NOT NULL CHECK(local_is_null IN (0,1)),
    movements INTEGER NOT NULL,
    qty INTEGER NOT NULL,
    revenue INTEGER NOT NULL,
    cost INTEGER NOT NULL,
    PRIMARY KEY(product_id,name,local_name,local_is_null)
) WITHOUT ROWID, STRICT;

CREATE TRIGGER IF NOT EXISTS trg_dashboard_top_sale_status
AFTER UPDATE OF status,sale_date ON sale
WHEN OLD.status='COMPLETED' OR NEW.status='COMPLETED' BEGIN
    INSERT INTO dashboard_top_summary
      (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
    SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
           sl.product_name_local IS NULL,-COUNT(*),-SUM(sl.qty),
           -SUM(sl.taxable_amount),-SUM(sl.cost_amount)
    FROM sale_line sl JOIN dashboard_top_window w
      ON OLD.sale_date BETWEEN w.from_day AND w.to_day
    WHERE sl.sale_id=OLD.id AND OLD.status='COMPLETED'
    GROUP BY sl.product_id,sl.product_name,sl.product_name_local IS NULL,
             COALESCE(sl.product_name_local,'')
    ON CONFLICT(product_id,name,local_name,local_is_null) DO UPDATE SET
      movements=movements+excluded.movements,qty=qty+excluded.qty,
      revenue=revenue+excluded.revenue,cost=cost+excluded.cost;
    DELETE FROM dashboard_top_summary WHERE movements=0;
    INSERT INTO dashboard_top_summary
      (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
    SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
           sl.product_name_local IS NULL,COUNT(*),SUM(sl.qty),
           SUM(sl.taxable_amount),SUM(sl.cost_amount)
    FROM sale_line sl JOIN dashboard_top_window w
      ON NEW.sale_date BETWEEN w.from_day AND w.to_day
    WHERE sl.sale_id=NEW.id AND NEW.status='COMPLETED'
    GROUP BY sl.product_id,sl.product_name,sl.product_name_local IS NULL,
             COALESCE(sl.product_name_local,'')
    ON CONFLICT(product_id,name,local_name,local_is_null) DO UPDATE SET
      movements=movements+excluded.movements,qty=qty+excluded.qty,
      revenue=revenue+excluded.revenue,cost=cost+excluded.cost;
END;

CREATE TRIGGER IF NOT EXISTS trg_dashboard_top_return_line_insert
AFTER INSERT ON sale_return_line BEGIN
    INSERT INTO dashboard_top_summary
      (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
    SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
           sl.product_name_local IS NULL,1,-NEW.qty,-NEW.taxable_amount,-NEW.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=NEW.sale_line_id
    JOIN dashboard_top_window w ON r.return_date BETWEEN w.from_day AND w.to_day
    WHERE r.id=NEW.sale_return_id
    ON CONFLICT(product_id,name,local_name,local_is_null) DO UPDATE SET
      movements=movements+1,qty=qty+excluded.qty,
      revenue=revenue+excluded.revenue,cost=cost+excluded.cost;
END;

CREATE TRIGGER IF NOT EXISTS trg_dashboard_top_return_line_delete
AFTER DELETE ON sale_return_line BEGIN
    INSERT INTO dashboard_top_summary
      (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
    SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
           sl.product_name_local IS NULL,-1,OLD.qty,OLD.taxable_amount,OLD.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=OLD.sale_line_id
    JOIN dashboard_top_window w ON r.return_date BETWEEN w.from_day AND w.to_day
    WHERE r.id=OLD.sale_return_id
    ON CONFLICT(product_id,name,local_name,local_is_null) DO UPDATE SET
      movements=movements-1,qty=qty+excluded.qty,
      revenue=revenue+excluded.revenue,cost=cost+excluded.cost;
    DELETE FROM dashboard_top_summary WHERE movements=0;
END;

CREATE TRIGGER IF NOT EXISTS trg_dashboard_top_return_line_update
AFTER UPDATE OF sale_return_id,sale_line_id,qty,taxable_amount,cost_amount ON sale_return_line
WHEN OLD.sale_return_id IS NOT NEW.sale_return_id OR OLD.sale_line_id IS NOT NEW.sale_line_id
  OR OLD.qty IS NOT NEW.qty OR OLD.taxable_amount IS NOT NEW.taxable_amount
  OR OLD.cost_amount IS NOT NEW.cost_amount BEGIN
    INSERT INTO dashboard_top_summary
      (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
    SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
           sl.product_name_local IS NULL,-1,OLD.qty,OLD.taxable_amount,OLD.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=OLD.sale_line_id
    JOIN dashboard_top_window w ON r.return_date BETWEEN w.from_day AND w.to_day
    WHERE r.id=OLD.sale_return_id
    ON CONFLICT(product_id,name,local_name,local_is_null) DO UPDATE SET
      movements=movements-1,qty=qty+excluded.qty,
      revenue=revenue+excluded.revenue,cost=cost+excluded.cost;
    DELETE FROM dashboard_top_summary WHERE movements=0;
    INSERT INTO dashboard_top_summary
      (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
    SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
           sl.product_name_local IS NULL,1,-NEW.qty,-NEW.taxable_amount,-NEW.cost_amount
    FROM sale_return r JOIN sale_line sl ON sl.id=NEW.sale_line_id
    JOIN dashboard_top_window w ON r.return_date BETWEEN w.from_day AND w.to_day
    WHERE r.id=NEW.sale_return_id
    ON CONFLICT(product_id,name,local_name,local_is_null) DO UPDATE SET
      movements=movements+1,qty=qty+excluded.qty,
      revenue=revenue+excluded.revenue,cost=cost+excluded.cost;
END;

CREATE TRIGGER IF NOT EXISTS trg_dashboard_top_return_date
AFTER UPDATE OF return_date ON sale_return
WHEN OLD.return_date IS NOT NEW.return_date BEGIN
    INSERT INTO dashboard_top_summary
      (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
    SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
           sl.product_name_local IS NULL,-COUNT(*),SUM(rl.qty),
           SUM(rl.taxable_amount),SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
    JOIN dashboard_top_window w ON OLD.return_date BETWEEN w.from_day AND w.to_day
    WHERE rl.sale_return_id=OLD.id
    GROUP BY sl.product_id,sl.product_name,sl.product_name_local IS NULL,
             COALESCE(sl.product_name_local,'')
    ON CONFLICT(product_id,name,local_name,local_is_null) DO UPDATE SET
      movements=movements+excluded.movements,qty=qty+excluded.qty,
      revenue=revenue+excluded.revenue,cost=cost+excluded.cost;
    DELETE FROM dashboard_top_summary WHERE movements=0;
    INSERT INTO dashboard_top_summary
      (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
    SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
           sl.product_name_local IS NULL,COUNT(*),-SUM(rl.qty),
           -SUM(rl.taxable_amount),-SUM(rl.cost_amount)
    FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
    JOIN dashboard_top_window w ON NEW.return_date BETWEEN w.from_day AND w.to_day
    WHERE rl.sale_return_id=NEW.id
    GROUP BY sl.product_id,sl.product_name,sl.product_name_local IS NULL,
             COALESCE(sl.product_name_local,'')
    ON CONFLICT(product_id,name,local_name,local_is_null) DO UPDATE SET
      movements=movements+excluded.movements,qty=qty+excluded.qty,
      revenue=revenue+excluded.revenue,cost=cost+excluded.cost;
END;
