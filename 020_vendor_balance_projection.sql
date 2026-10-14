-- Keep the vendor picker and dashboard payable total independent of the
-- lifetime purchase/payment/return history. Every delta is in the same
-- transaction as its source document.
CREATE TABLE IF NOT EXISTS vendor_balance_total (
    vendor_id INTEGER PRIMARY KEY REFERENCES vendor(id) ON DELETE CASCADE,
    purchases INTEGER NOT NULL DEFAULT 0,
    payments INTEGER NOT NULL DEFAULT 0,
    returns INTEGER NOT NULL DEFAULT 0
) STRICT;

INSERT OR REPLACE INTO vendor_balance_total(vendor_id,purchases,payments,returns)
SELECT v.id,
       COALESCE((SELECT SUM(p.grand_total) FROM purchase p
                 WHERE p.vendor_id=v.id AND p.status='POSTED'),0),
       COALESCE((SELECT SUM(vp.amount) FROM vendor_payment vp WHERE vp.vendor_id=v.id),0),
       COALESCE((SELECT SUM(vr.credit_total) FROM vendor_return vr WHERE vr.vendor_id=v.id),0)
FROM vendor v;

CREATE TRIGGER IF NOT EXISTS vendor_balance_vendor_insert AFTER INSERT ON vendor BEGIN
    INSERT INTO vendor_balance_total(vendor_id) VALUES (NEW.id);
END;

CREATE TRIGGER IF NOT EXISTS vendor_balance_purchase_insert AFTER INSERT ON purchase
WHEN NEW.status='POSTED' BEGIN
    UPDATE vendor_balance_total SET purchases=purchases+NEW.grand_total WHERE vendor_id=NEW.vendor_id;
END;
CREATE TRIGGER IF NOT EXISTS vendor_balance_purchase_delete AFTER DELETE ON purchase
WHEN OLD.status='POSTED' BEGIN
    UPDATE vendor_balance_total SET purchases=purchases-OLD.grand_total WHERE vendor_id=OLD.vendor_id;
END;
CREATE TRIGGER IF NOT EXISTS vendor_balance_purchase_update
AFTER UPDATE OF vendor_id,status,grand_total ON purchase
WHEN OLD.vendor_id IS NOT NEW.vendor_id OR OLD.status IS NOT NEW.status
  OR OLD.grand_total IS NOT NEW.grand_total BEGIN
    UPDATE vendor_balance_total SET purchases=purchases-OLD.grand_total
    WHERE vendor_id=OLD.vendor_id AND OLD.status='POSTED';
    UPDATE vendor_balance_total SET purchases=purchases+NEW.grand_total
    WHERE vendor_id=NEW.vendor_id AND NEW.status='POSTED';
END;

CREATE TRIGGER IF NOT EXISTS vendor_balance_payment_insert AFTER INSERT ON vendor_payment BEGIN
    UPDATE vendor_balance_total SET payments=payments+NEW.amount WHERE vendor_id=NEW.vendor_id;
END;
CREATE TRIGGER IF NOT EXISTS vendor_balance_payment_delete AFTER DELETE ON vendor_payment BEGIN
    UPDATE vendor_balance_total SET payments=payments-OLD.amount WHERE vendor_id=OLD.vendor_id;
END;
CREATE TRIGGER IF NOT EXISTS vendor_balance_payment_update
AFTER UPDATE OF vendor_id,amount ON vendor_payment
WHEN OLD.vendor_id IS NOT NEW.vendor_id OR OLD.amount IS NOT NEW.amount BEGIN
    UPDATE vendor_balance_total SET payments=payments-OLD.amount WHERE vendor_id=OLD.vendor_id;
    UPDATE vendor_balance_total SET payments=payments+NEW.amount WHERE vendor_id=NEW.vendor_id;
END;

CREATE TRIGGER IF NOT EXISTS vendor_balance_return_insert AFTER INSERT ON vendor_return BEGIN
    UPDATE vendor_balance_total SET returns=returns+NEW.credit_total WHERE vendor_id=NEW.vendor_id;
END;
CREATE TRIGGER IF NOT EXISTS vendor_balance_return_delete AFTER DELETE ON vendor_return BEGIN
    UPDATE vendor_balance_total SET returns=returns-OLD.credit_total WHERE vendor_id=OLD.vendor_id;
END;
CREATE TRIGGER IF NOT EXISTS vendor_balance_return_update
AFTER UPDATE OF vendor_id,credit_total ON vendor_return
WHEN OLD.vendor_id IS NOT NEW.vendor_id OR OLD.credit_total IS NOT NEW.credit_total BEGIN
    UPDATE vendor_balance_total SET returns=returns-OLD.credit_total WHERE vendor_id=OLD.vendor_id;
    UPDATE vendor_balance_total SET returns=returns+NEW.credit_total WHERE vendor_id=NEW.vendor_id;
END;

DROP VIEW IF EXISTS v_vendor_balance;
CREATE VIEW v_vendor_balance AS
SELECT v.id AS vendor_id,v.name,
       v.opening_balance+t.purchases-t.payments-t.returns AS balance_due
FROM vendor v JOIN vendor_balance_total t ON t.vendor_id=v.id;
