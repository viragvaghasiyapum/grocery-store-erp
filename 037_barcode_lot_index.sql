-- Phase 9: deleting a lot first detaches its lot-specific barcodes (trg_lot_barcode_detach_v29)
-- and then checks the barcode foreign key on (lot_id, product_id); both scanned every barcode.
-- Measured on the 47,010-product / 27,000-barcode / 223,272-lot rehearsal copy, deleting 50 zero lots
-- in one transaction: 73-79 ms before (1.43 ms p50 each), 2.4-4.9 ms after (0.04-0.07 ms p50).
-- Only barcodes that name a lot are indexed, so the index is a single page there (no barcode
-- names a lot) and an ordinary barcode write does not touch it.

CREATE INDEX ix_product_barcode_lot ON product_barcode(lot_id, product_id) WHERE lot_id IS NOT NULL;
