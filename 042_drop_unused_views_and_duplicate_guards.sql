-- Two views that nothing reads any more:
--   v_daily_sales (schema 1): the dashboard and reports read pnl_day and sale_report_day instead.
--   v_expiry_alert (schema 1, rebuilt in 5): every expiry list, tile and badge builds its SQL from
--   ExpiryAlert (Data/Infrastructure/ExpiryAlert.cs). The view also left out blocked expired lots,
--   which the Expired tile now counts, and dated itself from the machine clock, not the business date.
-- Three guards that newer triggers already enforce, with the same result for the operator:
--   trg_lot_no_negative (schema 1) looked up stock.allow_negative on every lot quantity change.
--   Schema 36 deleted that setting, so it always fired, exactly when trg_direct_qty_nonnegative
--   (schema 33) does; both map to ErrorCode.InsufficientStock.
--   trg_purchase_expense_no_update and trg_purchase_expense_no_delete (schema 30) protect the
--   expenses a purchase posts; trg_expense_no_update_v34 and trg_expense_no_delete_v34 (schema 34)
--   reject any change to any expense, theirs included.

DROP VIEW IF EXISTS v_daily_sales;
DROP VIEW IF EXISTS v_expiry_alert;

DROP TRIGGER IF EXISTS trg_lot_no_negative;
DROP TRIGGER IF EXISTS trg_purchase_expense_no_update;
DROP TRIGGER IF EXISTS trg_purchase_expense_no_delete;
