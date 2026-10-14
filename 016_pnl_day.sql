-- Only the costly sale and return totals are projected. All other P&L
-- branches continue to read their authoritative rows.
CREATE TABLE IF NOT EXISTS pnl_day (
    day TEXT PRIMARY KEY,
    bills INTEGER NOT NULL DEFAULT 0,
    taxable INTEGER NOT NULL DEFAULT 0,
    rounding INTEGER NOT NULL DEFAULT 0,
    cost INTEGER NOT NULL DEFAULT 0,
    returns INTEGER NOT NULL DEFAULT 0,
    return_taxable INTEGER NOT NULL DEFAULT 0,
    return_cost INTEGER NOT NULL DEFAULT 0
) WITHOUT ROWID;

CREATE TRIGGER IF NOT EXISTS trg_pnl_sale_insert AFTER INSERT ON sale
WHEN NEW.status='COMPLETED' BEGIN
    INSERT INTO pnl_day(day,bills,taxable,rounding,cost)
    VALUES(NEW.sale_date,1,NEW.taxable_total,NEW.round_off,NEW.cost_total)
    ON CONFLICT(day) DO UPDATE SET
        bills=bills+1,taxable=taxable+excluded.taxable,
        rounding=rounding+excluded.rounding,cost=cost+excluded.cost;
END;

CREATE TRIGGER IF NOT EXISTS trg_pnl_sale_update
AFTER UPDATE OF status,sale_date,taxable_total,round_off,cost_total ON sale
WHEN (OLD.status='COMPLETED' OR NEW.status='COMPLETED') AND
     (OLD.status IS NOT NEW.status OR OLD.sale_date IS NOT NEW.sale_date OR
      OLD.taxable_total IS NOT NEW.taxable_total OR OLD.round_off IS NOT NEW.round_off OR
      OLD.cost_total IS NOT NEW.cost_total) BEGIN
    UPDATE pnl_day SET bills=bills-1,taxable=taxable-OLD.taxable_total,
        rounding=rounding-OLD.round_off,cost=cost-OLD.cost_total
    WHERE day=OLD.sale_date AND OLD.status='COMPLETED';
    INSERT INTO pnl_day(day,bills,taxable,rounding,cost)
    SELECT NEW.sale_date,1,NEW.taxable_total,NEW.round_off,NEW.cost_total
    WHERE NEW.status='COMPLETED'
    ON CONFLICT(day) DO UPDATE SET
        bills=bills+1,taxable=taxable+excluded.taxable,
        rounding=rounding+excluded.rounding,cost=cost+excluded.cost;
END;

CREATE TRIGGER IF NOT EXISTS trg_pnl_sale_delete AFTER DELETE ON sale
WHEN OLD.status='COMPLETED' BEGIN
    UPDATE pnl_day SET bills=bills-1,taxable=taxable-OLD.taxable_total,
        rounding=rounding-OLD.round_off,cost=cost-OLD.cost_total
    WHERE day=OLD.sale_date;
END;

CREATE TRIGGER IF NOT EXISTS trg_pnl_return_insert AFTER INSERT ON sale_return BEGIN
    INSERT INTO pnl_day(day,returns,return_taxable,return_cost)
    VALUES(NEW.return_date,1,NEW.taxable_total,NEW.cost_total)
    ON CONFLICT(day) DO UPDATE SET
        returns=returns+1,return_taxable=return_taxable+excluded.return_taxable,
        return_cost=return_cost+excluded.return_cost;
END;

CREATE TRIGGER IF NOT EXISTS trg_pnl_return_update
AFTER UPDATE OF return_date,taxable_total,cost_total ON sale_return
WHEN OLD.return_date IS NOT NEW.return_date OR
     OLD.taxable_total IS NOT NEW.taxable_total OR OLD.cost_total IS NOT NEW.cost_total BEGIN
    UPDATE pnl_day SET returns=returns-1,
        return_taxable=return_taxable-OLD.taxable_total,
        return_cost=return_cost-OLD.cost_total WHERE day=OLD.return_date;
    INSERT INTO pnl_day(day,returns,return_taxable,return_cost)
    VALUES(NEW.return_date,1,NEW.taxable_total,NEW.cost_total)
    ON CONFLICT(day) DO UPDATE SET
        returns=returns+1,return_taxable=return_taxable+excluded.return_taxable,
        return_cost=return_cost+excluded.return_cost;
END;

CREATE TRIGGER IF NOT EXISTS trg_pnl_return_delete AFTER DELETE ON sale_return BEGIN
    UPDATE pnl_day SET returns=returns-1,
        return_taxable=return_taxable-OLD.taxable_total,
        return_cost=return_cost-OLD.cost_total WHERE day=OLD.return_date;
END;
