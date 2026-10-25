-- Purchase entry: a supplier bill may print its rates and discounts including GST. The operator
-- chooses per purchase whether the typed figures include GST; the choice is kept on the purchase
-- and offered again for that vendor's next bill. purchase_line.unit_cost and discount stay the
-- before-GST figures every total, report and return reads. entered_rate and entered_discount keep
-- the figures as typed (only when rates_include_tax = 1), so a reopened draft shows them unchanged.
-- Adding the columns rewrites nothing; SQLite only checks the new constraints against old rows.

ALTER TABLE purchase ADD COLUMN rates_include_tax INTEGER NOT NULL DEFAULT 0
  CHECK (rates_include_tax IN (0, 1));
ALTER TABLE purchase_line ADD COLUMN entered_rate INTEGER
  CHECK (entered_rate IS NULL OR entered_rate >= 0);
ALTER TABLE purchase_line ADD COLUMN entered_discount INTEGER
  CHECK (entered_discount IS NULL OR entered_discount >= 0);
