-- Preserve issued document identity and print settings for future reprints.
ALTER TABLE sale ADD COLUMN receipt_snapshot TEXT;
ALTER TABLE sale_return ADD COLUMN receipt_snapshot TEXT;
