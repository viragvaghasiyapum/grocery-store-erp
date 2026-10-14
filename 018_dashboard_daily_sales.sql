-- Reuse the narrow P&L day projection for dashboard sales and trend reads.
-- The sale total is deliberately gross completed-sale revenue; returns retain
-- their own effective-date columns and do not change v_daily_sales semantics.
ALTER TABLE pnl_day ADD COLUMN sales INTEGER NOT NULL DEFAULT 0;

UPDATE pnl_day SET sales=COALESCE((
    SELECT SUM(s.grand_total) FROM sale s
    WHERE s.status='COMPLETED' AND s.sale_date=pnl_day.day
),0);

DROP TRIGGER IF EXISTS trg_pnl_sale_insert;
DROP TRIGGER IF EXISTS trg_pnl_sale_update;
DROP TRIGGER IF EXISTS trg_pnl_sale_delete;

CREATE TRIGGER trg_pnl_sale_insert AFTER INSERT ON sale
WHEN NEW.status='COMPLETED' BEGIN
    INSERT INTO pnl_day(day,bills,taxable,rounding,cost,sales)
    VALUES(NEW.sale_date,1,NEW.taxable_total,NEW.round_off,NEW.cost_total,NEW.grand_total)
    ON CONFLICT(day) DO UPDATE SET
        bills=bills+1,taxable=taxable+excluded.taxable,
        rounding=rounding+excluded.rounding,cost=cost+excluded.cost,
        sales=sales+excluded.sales;
END;

CREATE TRIGGER trg_pnl_sale_update
AFTER UPDATE OF status,sale_date,taxable_total,round_off,cost_total,grand_total ON sale
WHEN (OLD.status='COMPLETED' OR NEW.status='COMPLETED') AND
     (OLD.status IS NOT NEW.status OR OLD.sale_date IS NOT NEW.sale_date OR
      OLD.taxable_total IS NOT NEW.taxable_total OR OLD.round_off IS NOT NEW.round_off OR
      OLD.cost_total IS NOT NEW.cost_total OR OLD.grand_total IS NOT NEW.grand_total) BEGIN
    UPDATE pnl_day SET bills=bills-1,taxable=taxable-OLD.taxable_total,
        rounding=rounding-OLD.round_off,cost=cost-OLD.cost_total,
        sales=sales-OLD.grand_total
    WHERE day=OLD.sale_date AND OLD.status='COMPLETED';
    INSERT INTO pnl_day(day,bills,taxable,rounding,cost,sales)
    SELECT NEW.sale_date,1,NEW.taxable_total,NEW.round_off,NEW.cost_total,NEW.grand_total
    WHERE NEW.status='COMPLETED'
    ON CONFLICT(day) DO UPDATE SET
        bills=bills+1,taxable=taxable+excluded.taxable,
        rounding=rounding+excluded.rounding,cost=cost+excluded.cost,
        sales=sales+excluded.sales;
END;

CREATE TRIGGER trg_pnl_sale_delete AFTER DELETE ON sale
WHEN OLD.status='COMPLETED' BEGIN
    UPDATE pnl_day SET bills=bills-1,taxable=taxable-OLD.taxable_total,
        rounding=rounding-OLD.round_off,cost=cost-OLD.cost_total,
        sales=sales-OLD.grand_total
    WHERE day=OLD.sale_date;
END;
