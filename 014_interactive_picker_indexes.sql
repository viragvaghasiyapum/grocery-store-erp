-- Keep broad customer, saved count and vendor purchase pickers ordered by an index.
CREATE INDEX ix_customer_active_name ON customer(name COLLATE NOCASE,id)
WHERE is_active=1;
CREATE INDEX ix_stock_count_draft_id ON stock_count(id DESC)
WHERE status='DRAFT';
CREATE INDEX ix_purchase_vendor_posted_date ON purchase(vendor_id,received_date DESC,id DESC)
WHERE status='POSTED';
PRAGMA user_version = 14;
