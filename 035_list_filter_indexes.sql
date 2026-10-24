-- Phase 7: indexes for list filters that otherwise scan a whole table on a full store.
-- Measured on the 47,003-product / 35,100-purchase rehearsal copy (warm p50):
--   purchase GRN or vendor-bill search, no match or exact   171 ms -> 0.07 ms
--   products, internal use only / archived only, no rows    150-165 ms -> under 0.01 ms
-- Together about 640 KB; purchases are rare writes, and the product indexes hold only
-- internal-use or archived rows, so a sale or ordinary product edit does not touch them.

-- The Purchases document search matches the GRN exactly or a vendor bill number by
-- prefix in any case; with both sides indexed SQLite answers the OR from two lookups.
CREATE INDEX ix_purchase_invoice_nocase ON purchase(vendor_invoice_no COLLATE NOCASE);

-- The internal-use and archived Products views, in list order.
CREATE INDEX ix_product_internal_name ON product(name COLLATE NOCASE) WHERE product_class='INTERNAL';
CREATE INDEX ix_product_archived_name ON product(name COLLATE NOCASE) WHERE is_active=0;
