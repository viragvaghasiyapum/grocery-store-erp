-- =====================================================================
--  GROCERY ERP · SQLite schema v1
--  Offline, single user, single Windows PC.
--  Requires SQLite >= 3.37 (STRICT tables) with FTS5 (trigram tokenizer).
--  rusqlite with the "bundled" feature ships a suitable SQLite.
-- =====================================================================
--
--  CONVENTIONS
--  fx3        Fixed-point, 3 decimals, stored as INTEGER x 1000.
--             Rs 12.345 -> 12345 | 1.250 kg -> 1250 | 18.000 % -> 18000
--             Money, quantities and percentages are NEVER stored as REAL.
--             All arithmetic happens in ONE Fx3 type in the app layer
--             (integer math, explicit round-half-up) - never in the UI.
--  Dates      TEXT 'YYYY-MM-DD'           business date, local time
--  Timestamps TEXT 'YYYY-MM-DD HH:MM:SS'  local time
--  Flags      INTEGER 0/1
--  Base unit  Every quantity is in the product's base unit (product.uom_id).
--
--  RULES THE SCHEMA ENFORCES
--  * Finalized documents are never deleted: sales are voided, purchases
--    cancelled, masters archived (is_active = 0).
--  * Stock changes ONLY through stock_movement (append-only ledger).
--    product_lot.qty_on_hand is a cache maintained by trigger.
--  * Documents snapshot names, prices, tax rates and costs, so reprints
--    and P&L never depend on today's master data.
--
--  PRAGMAS - run on EVERY connection open (foreign_keys is OFF by default!)
--    PRAGMA foreign_keys = ON;
--    PRAGMA journal_mode = WAL;
--    PRAGMA synchronous  = FULL;     -- survives power cuts; negligible cost at POS volume
--    PRAGMA busy_timeout = 5000;
--    PRAGMA temp_store   = MEMORY;
--    PRAGMA cache_size   = -64000;   -- ~64 MB page cache
-- =====================================================================


-- =====================================================================
-- 1. SYSTEM
-- =====================================================================

CREATE TABLE app_user (
  id             INTEGER PRIMARY KEY CHECK (id = 1),   -- single user, by design
  username       TEXT    NOT NULL,
  password_hash  TEXT    NOT NULL,                     -- Argon2id; never plain text
  created_at     TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  last_login_at  TEXT
) STRICT;

CREATE TABLE setting (
  key    TEXT PRIMARY KEY,
  value  TEXT NOT NULL
) STRICT, WITHOUT ROWID;

-- Gapless numbering per document type per financial year.
-- Increment inside the same transaction that finalizes the document.
-- Keep prefix + number <= 16 chars (GST invoice-number limit).
CREATE TABLE doc_sequence (
  doc_type     TEXT    NOT NULL,     -- 'SALE','SALE_RETURN','PURCHASE','DAMAGE'
  fiscal_year  TEXT    NOT NULL,     -- '2026-27'
  prefix       TEXT    NOT NULL,     -- 'S2627-'  -> S2627-000123
  next_no      INTEGER NOT NULL DEFAULT 1 CHECK (next_no > 0),
  PRIMARY KEY (doc_type, fiscal_year)
) STRICT, WITHOUT ROWID;

-- Answers "who changed this price / why is this different" months later.
CREATE TABLE audit_log (
  id         INTEGER PRIMARY KEY,
  at         TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  entity     TEXT    NOT NULL,       -- 'product','product_lot','sale',...
  entity_id  INTEGER NOT NULL,
  action     TEXT    NOT NULL,       -- 'CREATE','UPDATE','ARCHIVE','VOID','PRICE_CHANGE'
  changes    TEXT                    -- JSON: {"selling_price":[48000,45000]}
) STRICT;
CREATE INDEX ix_audit_entity ON audit_log(entity, entity_id);


-- =====================================================================
-- 2. CATALOG
-- =====================================================================

CREATE TABLE uom (
  id              INTEGER PRIMARY KEY,
  code            TEXT    NOT NULL UNIQUE COLLATE NOCASE,   -- PCS, KG, L, ...
  name            TEXT    NOT NULL,
  allow_fraction  INTEGER NOT NULL DEFAULT 0 CHECK (allow_fraction IN (0,1))
                  -- KG = 1 (0.250 kg ok); PCS = 0 (qty must be a multiple of 1000)
) STRICT;

CREATE TABLE category (
  id                 INTEGER PRIMARY KEY,
  parent_id          INTEGER REFERENCES category(id),   -- Staples > Flour & Atta
  name               TEXT    NOT NULL COLLATE NOCASE,
  expiry_alert_days  INTEGER CHECK (expiry_alert_days >= 0),  -- NULL = inherit
  sort_order         INTEGER NOT NULL DEFAULT 0,
  is_active          INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
) STRICT;
CREATE UNIQUE INDEX ux_category_name ON category(COALESCE(parent_id, 0), name);

CREATE TABLE brand (
  id         INTEGER PRIMARY KEY,
  name       TEXT    NOT NULL UNIQUE COLLATE NOCASE,
  is_active  INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
) STRICT;

-- Rates are DATA, not constants: when the government changes a slab you
-- edit rows, not code. Old bills keep their own snapshot of the rate.
CREATE TABLE tax_rate (
  id         INTEGER PRIMARY KEY,
  name       TEXT    NOT NULL UNIQUE,                  -- 'GST 5%'
  rate_pct   INTEGER NOT NULL CHECK (rate_pct >= 0),   -- fx3: 5.000% -> 5000
  is_active  INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
) STRICT;

CREATE TABLE product (
  id                       INTEGER PRIMARY KEY,
  sku                      TEXT    NOT NULL UNIQUE COLLATE NOCASE,  -- short code typed at the counter
  name                     TEXT    NOT NULL,                        -- 'Aashirvaad Atta 5kg'
  name_local               TEXT,                                    -- Gujarati/Hindi name
  search_terms             TEXT,                                    -- aliases: 'gehu lot, ghau no lot'
  category_id              INTEGER NOT NULL REFERENCES category(id),
  brand_id                 INTEGER REFERENCES brand(id),
  uom_id                   INTEGER NOT NULL REFERENCES uom(id),     -- base unit for ALL qty
  hsn_code                 TEXT,
  tax_rate_id              INTEGER REFERENCES tax_rate(id),
  price_includes_tax       INTEGER NOT NULL DEFAULT 1 CHECK (price_includes_tax IN (0,1)),
  is_loose                 INTEGER NOT NULL DEFAULT 0 CHECK (is_loose IN (0,1)),  -- sold by weight
  plu_code                 TEXT    UNIQUE,          -- item code programmed into the weighing scale
  track_expiry             INTEGER NOT NULL DEFAULT 1 CHECK (track_expiry IN (0,1)),
  default_shelf_life_days  INTEGER CHECK (default_shelf_life_days > 0),  -- bread = 4: auto-fills lot expiry
  expiry_alert_days        INTEGER CHECK (expiry_alert_days >= 0),       -- NULL = inherit from category
  reorder_level            INTEGER NOT NULL DEFAULT 0 CHECK (reorder_level >= 0),  -- fx3
  default_selling_price    INTEGER,                 -- fx3, pre-fills new lots
  default_mrp              INTEGER,                 -- fx3
  is_active                INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
  created_at               TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  updated_at               TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_product_category ON product(category_id);
CREATE INDEX ix_product_brand    ON product(brand_id);
CREATE INDEX ix_product_name     ON product(name COLLATE NOCASE);   -- 1-2 char prefix search fallback

-- Weighing-scale (variable-measure) labels, e.g. 27 00123 01250 5
--   prefix 27 | PLU 00123 (pos 3-7) | weight 01250 g (pos 8-12) | check digit 5
-- Configure to match whatever your scale prints.
CREATE TABLE barcode_rule (
  id            INTEGER PRIMARY KEY,
  name          TEXT    NOT NULL,
  prefix        TEXT    NOT NULL UNIQUE,           -- must not collide with internal prefixes in settings
  total_length  INTEGER NOT NULL DEFAULT 13,
  item_start    INTEGER NOT NULL,                  -- 1-based position of the PLU
  item_length   INTEGER NOT NULL,
  value_start   INTEGER NOT NULL,
  value_length  INTEGER NOT NULL,
  value_kind    TEXT    NOT NULL CHECK (value_kind IN ('WEIGHT','PRICE')),
  value_scale   INTEGER NOT NULL DEFAULT 1,        -- raw digits x scale = fx3
                                                   -- grams -> fx3 kg: 1 | paise -> fx3 rupees: 10
  is_active     INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
) STRICT;


-- =====================================================================
-- 3. VENDORS & PURCHASING   (lots are born here)
-- =====================================================================

CREATE TABLE vendor (
  id                  INTEGER PRIMARY KEY,
  name                TEXT    NOT NULL COLLATE NOCASE,
  contact_person      TEXT,
  phone               TEXT,
  alt_phone           TEXT,
  email               TEXT,
  address             TEXT,
  city                TEXT,
  state               TEXT,              -- decides CGST+SGST vs IGST on purchases
  pincode             TEXT,
  gstin               TEXT UNIQUE,
  pan                 TEXT,
  upi_id              TEXT,
  bank_details        TEXT,
  payment_terms_days  INTEGER NOT NULL DEFAULT 0,
  opening_balance     INTEGER NOT NULL DEFAULT 0,   -- fx3, what you owed them on go-live
  notes               TEXT,
  is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
  created_at          TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_vendor_name ON vendor(name);

CREATE TABLE purchase (
  id                   INTEGER PRIMARY KEY,
  doc_no               TEXT    UNIQUE,                   -- internal GRN no., assigned on POST
  vendor_id            INTEGER NOT NULL REFERENCES vendor(id),
  vendor_invoice_no    TEXT,
  vendor_invoice_date  TEXT CHECK (vendor_invoice_date = date(vendor_invoice_date)),
  received_date        TEXT    NOT NULL CHECK (received_date = date(received_date)),
  status               TEXT    NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
  subtotal             INTEGER NOT NULL DEFAULT 0,       -- fx3 sum qty x unit_cost
  discount_total       INTEGER NOT NULL DEFAULT 0,       -- fx3
  tax_total            INTEGER NOT NULL DEFAULT 0,       -- fx3
  other_charges        INTEGER NOT NULL DEFAULT 0,       -- fx3 freight/loading -> spread into lot cost
  round_off            INTEGER NOT NULL DEFAULT 0,       -- fx3
  grand_total          INTEGER NOT NULL DEFAULT 0,       -- fx3 = amount payable to vendor
  notes                TEXT,
  created_at           TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  posted_at            TEXT,
  UNIQUE (vendor_id, vendor_invoice_no),                 -- blocks entering the same bill twice
  CHECK (status <> 'POSTED' OR doc_no IS NOT NULL)
) STRICT;
CREATE INDEX ix_purchase_date   ON purchase(received_date);
CREATE INDEX ix_purchase_vendor ON purchase(vendor_id, status);

-- One purchase line -> exactly one lot (created when the purchase is POSTED).
CREATE TABLE purchase_line (
  id                INTEGER PRIMARY KEY,
  purchase_id       INTEGER NOT NULL REFERENCES purchase(id) ON DELETE CASCADE,  -- drafts only
  line_no           INTEGER NOT NULL,
  product_id        INTEGER NOT NULL REFERENCES product(id),
  batch_no          TEXT,
  mfg_date          TEXT CHECK (mfg_date = date(mfg_date)),
  expiry_date       TEXT CHECK (expiry_date = date(expiry_date)),
  qty               INTEGER NOT NULL CHECK (qty > 0),              -- fx3 billed qty
  free_qty          INTEGER NOT NULL DEFAULT 0 CHECK (free_qty >= 0),  -- fx3 scheme, e.g. "10+1"
  unit_cost         INTEGER NOT NULL CHECK (unit_cost >= 0),       -- fx3 vendor rate, ex-GST
  discount          INTEGER NOT NULL DEFAULT 0 CHECK (discount >= 0),  -- fx3 amount
  tax_rate_pct      INTEGER NOT NULL DEFAULT 0,                    -- fx3 snapshot
  tax_amount        INTEGER NOT NULL DEFAULT 0,                    -- fx3
  line_total        INTEGER NOT NULL,                              -- fx3 incl. tax
  -- landed_unit_cost = (qty x unit_cost - discount + share of other_charges
  --                     [+ tax_amount if tax.mode <> REGULAR, i.e. no input credit])
  --                    / (qty + free_qty)
  landed_unit_cost  INTEGER NOT NULL CHECK (landed_unit_cost >= 0),  -- fx3 -> product_lot.unit_cost
  mrp               INTEGER,                                       -- fx3 per base unit
  selling_price     INTEGER NOT NULL CHECK (selling_price >= 0),   -- fx3 per base unit
  UNIQUE (purchase_id, line_no)
) STRICT;
CREATE INDEX ix_purchase_line_product ON purchase_line(product_id);

CREATE TABLE vendor_payment (
  id           INTEGER PRIMARY KEY,
  vendor_id    INTEGER NOT NULL REFERENCES vendor(id),
  purchase_id  INTEGER REFERENCES purchase(id),          -- optional: against a specific bill
  pay_date     TEXT    NOT NULL CHECK (pay_date = date(pay_date)),
  amount       INTEGER NOT NULL CHECK (amount > 0),      -- fx3
  mode         TEXT    NOT NULL CHECK (mode IN ('CASH','UPI','BANK','CHEQUE','OTHER')),
  reference    TEXT,
  notes        TEXT,
  created_at   TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_vendor_payment_vendor ON vendor_payment(vendor_id, pay_date);

-- Posted purchase lines are read-only (cancel the purchase instead).
CREATE TRIGGER trg_purchase_line_lock_ins BEFORE INSERT ON purchase_line
WHEN (SELECT status FROM purchase WHERE id = NEW.purchase_id) <> 'DRAFT'
BEGIN SELECT RAISE(ABORT, 'purchase is posted/cancelled; lines are read-only'); END;
CREATE TRIGGER trg_purchase_line_lock_upd BEFORE UPDATE ON purchase_line
WHEN (SELECT status FROM purchase WHERE id = OLD.purchase_id) <> 'DRAFT'
BEGIN SELECT RAISE(ABORT, 'purchase is posted/cancelled; lines are read-only'); END;
CREATE TRIGGER trg_purchase_line_lock_del BEFORE DELETE ON purchase_line
WHEN (SELECT status FROM purchase WHERE id = OLD.purchase_id) <> 'DRAFT'
BEGIN SELECT RAISE(ABORT, 'purchase is posted/cancelled; lines are read-only'); END;


-- =====================================================================
-- 4. INVENTORY   (the lot is the unit of stock, price, cost and expiry)
-- =====================================================================

-- Why price lives on the LOT: the same product arrives with different
-- MRP (old stock Rs 45, new stock Rs 48), different cost and different
-- expiry. Selling from a specific lot gives exact COGS and exact expiry.
CREATE TABLE product_lot (
  id                INTEGER PRIMARY KEY,
  product_id        INTEGER NOT NULL REFERENCES product(id),
  lot_code          TEXT    NOT NULL UNIQUE,              -- internal, human-readable 'L000123'
  batch_no          TEXT,                                 -- manufacturer batch on the pack
  purchase_line_id  INTEGER UNIQUE REFERENCES purchase_line(id),  -- NULL = opening stock
  vendor_id         INTEGER REFERENCES vendor(id),
  received_date     TEXT    NOT NULL CHECK (received_date = date(received_date)),
  mfg_date          TEXT CHECK (mfg_date = date(mfg_date)),
  expiry_date       TEXT CHECK (expiry_date = date(expiry_date)),  -- NULL if not tracked
  unit_cost         INTEGER NOT NULL CHECK (unit_cost >= 0),       -- fx3 landed cost per base unit
  mrp               INTEGER,                                       -- fx3 per base unit
  selling_price     INTEGER NOT NULL CHECK (selling_price >= 0),   -- fx3 per base unit
  qty_received      INTEGER NOT NULL DEFAULT 0,                    -- fx3 incl. free qty (informational)
  qty_on_hand       INTEGER NOT NULL DEFAULT 0,                    -- fx3 CACHE of SUM(stock_movement.qty)
                                                                   -- written ONLY by trg_move_apply
  is_active         INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),  -- 0 = blocked (recall)
  created_at        TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  UNIQUE (id, product_id),              -- target for composite FKs: "this lot belongs to this product"
  CHECK (mrp IS NULL OR selling_price <= mrp)   -- selling above MRP is illegal
) STRICT;
-- FEFO pick: WHERE product_id=? AND qty_on_hand>0 AND is_active=1
--            ORDER BY expiry_date NULLS LAST, received_date, id
CREATE INDEX ix_lot_fefo   ON product_lot(product_id, expiry_date) WHERE qty_on_hand > 0;
CREATE INDEX ix_lot_expiry ON product_lot(expiry_date) WHERE qty_on_hand > 0 AND expiry_date IS NOT NULL;

-- Movement types are rows (not a CHECK) so a new module can add its own
-- type without rebuilding the biggest table in the database.
CREATE TABLE stock_movement_type (
  code       TEXT    PRIMARY KEY,
  name       TEXT    NOT NULL,
  direction  INTEGER NOT NULL CHECK (direction IN (-1, 0, 1))   -- 0 = either sign
) STRICT, WITHOUT ROWID;

-- The inventory LEDGER. Append-only. Stock on any date = SUM(qty) up to that date.
CREATE TABLE stock_movement (
  id          INTEGER PRIMARY KEY,
  moved_at    TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  move_date   TEXT    NOT NULL CHECK (move_date = date(move_date)),  -- business date
  product_id  INTEGER NOT NULL,                  -- denormalized for fast per-product ledgers
  lot_id      INTEGER NOT NULL,
  type_code   TEXT    NOT NULL REFERENCES stock_movement_type(code),
  qty         INTEGER NOT NULL CHECK (qty <> 0), -- fx3 signed: + in, - out
  unit_cost   INTEGER NOT NULL,                  -- fx3 lot cost at the time
  ref_type    TEXT,                              -- 'purchase_line','sale_line','damage',...
  ref_id      INTEGER,
  note        TEXT,
  FOREIGN KEY (lot_id, product_id) REFERENCES product_lot(id, product_id)
) STRICT;
CREATE INDEX ix_move_lot          ON stock_movement(lot_id);
CREATE INDEX ix_move_product_date ON stock_movement(product_id, move_date);
CREATE INDEX ix_move_ref          ON stock_movement(ref_type, ref_id);

CREATE TRIGGER trg_move_direction BEFORE INSERT ON stock_movement
WHEN (SELECT direction FROM stock_movement_type WHERE code = NEW.type_code) * NEW.qty < 0
BEGIN SELECT RAISE(ABORT, 'stock_movement: qty sign does not match movement type'); END;

CREATE TRIGGER trg_move_apply AFTER INSERT ON stock_movement
BEGIN
  UPDATE product_lot SET qty_on_hand = qty_on_hand + NEW.qty WHERE id = NEW.lot_id;
END;

CREATE TRIGGER trg_move_no_update BEFORE UPDATE ON stock_movement
BEGIN SELECT RAISE(ABORT, 'stock_movement is append-only; post a reversing movement'); END;
CREATE TRIGGER trg_move_no_delete BEFORE DELETE ON stock_movement
BEGIN SELECT RAISE(ABORT, 'stock_movement is append-only; post a reversing movement'); END;

-- Blocks overselling unless setting stock.allow_negative = '1'.
CREATE TRIGGER trg_lot_no_negative BEFORE UPDATE OF qty_on_hand ON product_lot
WHEN NEW.qty_on_hand < 0
 AND COALESCE((SELECT value FROM setting WHERE key = 'stock.allow_negative'), '0') <> '1'
BEGIN SELECT RAISE(ABORT, 'insufficient stock in lot'); END;


-- =====================================================================
-- 5. BARCODES
-- =====================================================================
-- The database stores the barcode TEXT, never an image. Images are
-- generated only when printing a label.
--
-- One product can have many barcodes (unit EAN, carton EAN, your own label).
-- A barcode with lot_id set identifies one exact lot (price/MRP/expiry).
-- A barcode without lot_id -> the POS picks a lot by FEFO.
--
-- Scan resolution order:
--   1. exact match here (unique index)          -> product / lot, x pack_qty
--   2. starts with a barcode_rule prefix         -> parse PLU + weight/price
--   3. GS1 DataMatrix / GS1-128 (2D scanners)    -> GTIN (01), batch (10), expiry (17)
--   4. no match                                  -> quick-add product dialog
CREATE TABLE product_barcode (
  id          INTEGER PRIMARY KEY,
  barcode     TEXT    NOT NULL UNIQUE,              -- exactly what the scanner sends
  product_id  INTEGER NOT NULL REFERENCES product(id),
  lot_id      INTEGER,                              -- set only for lot-specific labels
  pack_qty    INTEGER NOT NULL DEFAULT 1000 CHECK (pack_qty > 0),  -- fx3 base units per scan
                                                    -- carton of 24 -> 24000
  symbology   TEXT    NOT NULL DEFAULT 'EAN13'
              CHECK (symbology IN ('EAN13','EAN8','UPCA','CODE128','OTHER')),
  source      TEXT    NOT NULL DEFAULT 'MANUFACTURER' CHECK (source IN ('MANUFACTURER','INTERNAL')),
  is_primary  INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0,1)),
  created_at  TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  FOREIGN KEY (lot_id, product_id) REFERENCES product_lot(id, product_id)
) STRICT;
CREATE INDEX ix_barcode_product ON product_barcode(product_id);
CREATE UNIQUE INDEX ux_barcode_primary ON product_barcode(product_id) WHERE is_primary = 1;


-- =====================================================================
-- 6. SALES & INVOICING
-- =====================================================================

-- Optional: walk-in sales have customer_id NULL. Needed for credit
-- (udhaar/khata) sales and B2B buyers who need a GSTIN on the bill.
CREATE TABLE customer (
  id          INTEGER PRIMARY KEY,
  name        TEXT    NOT NULL,
  phone       TEXT    UNIQUE,
  address     TEXT,
  gstin       TEXT,
  notes       TEXT,
  is_active   INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
  created_at  TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;

-- Lifecycle: DRAFT/HELD (lines saved on every scan -> survives power cuts,
-- no stock effect) -> COMPLETED (invoice_no assigned, SALE movements posted,
-- one transaction) -> VOID (SALE_VOID movements reverse the stock).
CREATE TABLE sale (
  id               INTEGER PRIMARY KEY,
  invoice_no       TEXT    UNIQUE,                  -- assigned only on COMPLETE -> no gaps
  sale_date        TEXT    NOT NULL CHECK (sale_date = date(sale_date)),
  customer_id      INTEGER REFERENCES customer(id),
  status           TEXT    NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','HELD','COMPLETED','VOID')),
  line_count       INTEGER NOT NULL DEFAULT 0,
  gross_total      INTEGER NOT NULL DEFAULT 0,      -- fx3 sum qty x unit_price
  discount_total   INTEGER NOT NULL DEFAULT 0,      -- fx3 line + bill discounts
  taxable_total    INTEGER NOT NULL DEFAULT 0,      -- fx3 net revenue ex-GST
  tax_total        INTEGER NOT NULL DEFAULT 0,      -- fx3
  round_off        INTEGER NOT NULL DEFAULT 0,      -- fx3 +/- (setting bill.round_to)
  grand_total      INTEGER NOT NULL DEFAULT 0,      -- fx3 payable
  cost_total       INTEGER NOT NULL DEFAULT 0,      -- fx3 COGS snapshot -> fast P&L
  paid_total       INTEGER NOT NULL DEFAULT 0,      -- fx3
  change_returned  INTEGER NOT NULL DEFAULT 0,      -- fx3
  notes            TEXT,
  void_reason      TEXT,
  created_at       TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  completed_at     TEXT,
  voided_at        TEXT,
  CHECK (grand_total = taxable_total + tax_total + round_off),
  CHECK (status NOT IN ('COMPLETED','VOID') OR invoice_no IS NOT NULL),
  CHECK (status <> 'VOID' OR void_reason IS NOT NULL)
) STRICT;
CREATE INDEX ix_sale_date     ON sale(sale_date, status);
CREATE INDEX ix_sale_customer ON sale(customer_id) WHERE customer_id IS NOT NULL;

-- One line per LOT. If a scanned qty spans two lots (FEFO), it becomes two lines.
-- Bill-level discount is allocated proportionally into line.discount so
-- each line's GST is computed on the discounted value.
CREATE TABLE sale_line (
  id               INTEGER PRIMARY KEY,
  sale_id          INTEGER NOT NULL REFERENCES sale(id) ON DELETE CASCADE,  -- drafts only
  line_no          INTEGER NOT NULL,
  product_id       INTEGER NOT NULL,
  lot_id           INTEGER NOT NULL,
  product_name     TEXT    NOT NULL,                 -- snapshot for exact reprints
  scanned_barcode  TEXT,
  qty              INTEGER NOT NULL CHECK (qty > 0), -- fx3
  unit_price       INTEGER NOT NULL,                 -- fx3 per base unit
  mrp              INTEGER,                          -- fx3 snapshot (printed on bill)
  discount         INTEGER NOT NULL DEFAULT 0,       -- fx3 line + allocated bill discount
  tax_rate_pct     INTEGER NOT NULL DEFAULT 0,       -- fx3 snapshot
  taxable_amount   INTEGER NOT NULL,                 -- fx3
  tax_amount       INTEGER NOT NULL,                 -- fx3
  line_total       INTEGER NOT NULL,                 -- fx3
  unit_cost        INTEGER NOT NULL,                 -- fx3 lot cost snapshot
  cost_amount      INTEGER NOT NULL,                 -- fx3 COGS of this line
  UNIQUE (sale_id, line_no),
  CHECK (line_total = taxable_amount + tax_amount),
  FOREIGN KEY (lot_id, product_id) REFERENCES product_lot(id, product_id)
) STRICT;
CREATE INDEX ix_sale_line_product ON sale_line(product_id);
CREATE INDEX ix_sale_line_lot     ON sale_line(lot_id);

-- Split payments: part cash, part UPI.
CREATE TABLE sale_payment (
  id         INTEGER PRIMARY KEY,
  sale_id    INTEGER NOT NULL REFERENCES sale(id) ON DELETE CASCADE,
  mode       TEXT    NOT NULL CHECK (mode IN ('CASH','UPI','CARD','CREDIT','OTHER')),
  amount     INTEGER NOT NULL CHECK (amount > 0),    -- fx3
  reference  TEXT,                                   -- UPI txn id / card last 4
  paid_at    TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_sale_payment_sale ON sale_payment(sale_id);
CREATE INDEX ix_sale_payment_mode ON sale_payment(mode, paid_at);

CREATE TABLE sale_return (
  id             INTEGER PRIMARY KEY,
  return_no      TEXT    UNIQUE,
  sale_id        INTEGER NOT NULL REFERENCES sale(id),
  return_date    TEXT    NOT NULL CHECK (return_date = date(return_date)),
  reason         TEXT,
  refund_mode    TEXT    NOT NULL CHECK (refund_mode IN ('CASH','UPI','CARD','CREDIT_NOTE','OTHER')),
  taxable_total  INTEGER NOT NULL DEFAULT 0,   -- fx3
  tax_total      INTEGER NOT NULL DEFAULT 0,   -- fx3
  refund_total   INTEGER NOT NULL DEFAULT 0,   -- fx3
  cost_total     INTEGER NOT NULL DEFAULT 0,   -- fx3 COGS reversed
  created_at     TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_sale_return_date ON sale_return(return_date);

-- Always posts SALE_RETURN (+qty) into the ORIGINAL lot. If resellable = 0,
-- the app immediately records a damage (reason CUSTOMER_RETURN), so the
-- loss is visible under Damages instead of silently hiding in COGS.
CREATE TABLE sale_return_line (
  id              INTEGER PRIMARY KEY,
  sale_return_id  INTEGER NOT NULL REFERENCES sale_return(id),
  sale_line_id    INTEGER NOT NULL REFERENCES sale_line(id),
  qty             INTEGER NOT NULL CHECK (qty > 0),   -- fx3 (app checks <= sold - already returned)
  taxable_amount  INTEGER NOT NULL,                   -- fx3
  tax_amount      INTEGER NOT NULL,                   -- fx3
  refund_amount   INTEGER NOT NULL,                   -- fx3
  cost_amount     INTEGER NOT NULL,                   -- fx3
  resellable      INTEGER NOT NULL DEFAULT 1 CHECK (resellable IN (0,1))
) STRICT;
CREATE INDEX ix_sale_return_line_sl ON sale_return_line(sale_line_id);

-- Integrity: a completed/void bill can't be edited or deleted.
CREATE TRIGGER trg_sale_status_flow BEFORE UPDATE OF status ON sale
WHEN OLD.status = 'VOID'
  OR (OLD.status = 'COMPLETED' AND NEW.status NOT IN ('COMPLETED','VOID'))
BEGIN SELECT RAISE(ABORT, 'invalid sale status change'); END;

CREATE TRIGGER trg_sale_completed_readonly BEFORE UPDATE ON sale
WHEN OLD.status IN ('COMPLETED','VOID') AND (
       NEW.invoice_no  IS NOT OLD.invoice_no  OR NEW.sale_date     IS NOT OLD.sale_date
    OR NEW.customer_id IS NOT OLD.customer_id OR NEW.grand_total   <> OLD.grand_total
    OR NEW.taxable_total <> OLD.taxable_total OR NEW.tax_total     <> OLD.tax_total
    OR NEW.cost_total    <> OLD.cost_total    OR NEW.round_off     <> OLD.round_off)
BEGIN SELECT RAISE(ABORT, 'completed sale is read-only; void it instead'); END;

CREATE TRIGGER trg_sale_no_delete BEFORE DELETE ON sale
WHEN OLD.status IN ('COMPLETED','VOID')
BEGIN SELECT RAISE(ABORT, 'completed sales cannot be deleted; void instead'); END;

CREATE TRIGGER trg_sale_line_lock_ins BEFORE INSERT ON sale_line
WHEN (SELECT status FROM sale WHERE id = NEW.sale_id) IN ('COMPLETED','VOID')
BEGIN SELECT RAISE(ABORT, 'sale is finalized; lines are read-only'); END;
CREATE TRIGGER trg_sale_line_lock_upd BEFORE UPDATE ON sale_line
WHEN (SELECT status FROM sale WHERE id = OLD.sale_id) IN ('COMPLETED','VOID')
BEGIN SELECT RAISE(ABORT, 'sale is finalized; lines are read-only'); END;
CREATE TRIGGER trg_sale_line_lock_del BEFORE DELETE ON sale_line
WHEN (SELECT status FROM sale WHERE id = OLD.sale_id) IN ('COMPLETED','VOID')
BEGIN SELECT RAISE(ABORT, 'sale is finalized; lines are read-only'); END;


-- =====================================================================
-- 7. DAMAGES   (valued at COST, not selling price)
-- =====================================================================

CREATE TABLE damage (
  id                INTEGER PRIMARY KEY,
  doc_no            TEXT    UNIQUE,
  damage_date       TEXT    NOT NULL CHECK (damage_date = date(damage_date)),
  product_id        INTEGER NOT NULL,
  lot_id            INTEGER NOT NULL,
  qty               INTEGER NOT NULL CHECK (qty > 0),          -- fx3
  unit_cost         INTEGER NOT NULL,                          -- fx3 snapshot of lot cost
  cost_value        INTEGER NOT NULL,                          -- fx3 qty x unit_cost = the loss
  recovered_amount  INTEGER NOT NULL DEFAULT 0 CHECK (recovered_amount >= 0),  -- fx3 vendor credit/scrap
  reason            TEXT    NOT NULL CHECK (reason IN
                      ('EXPIRED','BROKEN','SPOILED','PEST','LOST','CUSTOMER_RETURN','OTHER')),
  notes             TEXT,
  created_at        TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  FOREIGN KEY (lot_id, product_id) REFERENCES product_lot(id, product_id)
) STRICT;
CREATE INDEX ix_damage_date ON damage(damage_date);
CREATE INDEX ix_damage_lot  ON damage(lot_id);


-- =====================================================================
-- 8. EXPENSES & SALARY
-- =====================================================================

CREATE TABLE expense_category (
  id         INTEGER PRIMARY KEY,
  name       TEXT    NOT NULL UNIQUE COLLATE NOCASE,
  is_active  INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
) STRICT;

CREATE TABLE expense (
  id            INTEGER PRIMARY KEY,
  expense_date  TEXT    NOT NULL CHECK (expense_date = date(expense_date)),
  category_id   INTEGER NOT NULL REFERENCES expense_category(id),
  amount        INTEGER NOT NULL CHECK (amount > 0),    -- fx3
  mode          TEXT    NOT NULL CHECK (mode IN ('CASH','UPI','BANK','CARD','OTHER')),
  paid_to       TEXT,
  reference     TEXT,
  notes         TEXT,
  created_at    TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_expense_date ON expense(expense_date);

CREATE TABLE employee (
  id              INTEGER PRIMARY KEY,
  name            TEXT    NOT NULL,
  phone           TEXT,
  role            TEXT,
  join_date       TEXT CHECK (join_date = date(join_date)),
  leave_date      TEXT CHECK (leave_date = date(leave_date)),
  monthly_salary  INTEGER NOT NULL DEFAULT 0,     -- fx3, pre-fills salary entries
  is_active       INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
  notes           TEXT
) STRICT;

-- Salary is charged to the month it is FOR (period_month), not the day it
-- was paid: August salary paid on 5 Sept is an August expense.
CREATE TABLE salary_payment (
  id            INTEGER PRIMARY KEY,
  employee_id   INTEGER NOT NULL REFERENCES employee(id),
  period_month  TEXT    NOT NULL CHECK (period_month GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]'),
  pay_date      TEXT    NOT NULL CHECK (pay_date = date(pay_date)),
  kind          TEXT    NOT NULL DEFAULT 'SALARY' CHECK (kind IN ('SALARY','BONUS','OVERTIME','OTHER')),
  amount        INTEGER NOT NULL CHECK (amount > 0),   -- fx3
  mode          TEXT    NOT NULL CHECK (mode IN ('CASH','UPI','BANK','OTHER')),
  notes         TEXT,
  created_at    TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
) STRICT;
CREATE INDEX ix_salary_period   ON salary_payment(period_month);
CREATE INDEX ix_salary_employee ON salary_payment(employee_id, period_month);


-- =====================================================================
-- 9. ASSETS & DEPRECIATION
-- =====================================================================
-- Buying an asset is NOT a P&L expense; its depreciation is.
-- The app posts one asset_depreciation row per asset per elapsed month
-- (on app start). Posted rows are history and never recomputed.
--   WDV: yearly = book value at FY start x rate; month = yearly x days_in_use / days_in_FY
--   SLM: month  = (cost - salvage) x rate x days_in_use / days_in_year
--   First month prorated from in_use_from; never below salvage; stops at disposal.
CREATE TABLE asset (
  id              INTEGER PRIMARY KEY,
  name            TEXT    NOT NULL,          -- 'Visi-cooler 300L'
  category        TEXT,                      -- Furniture / Equipment / Electronics / Vehicle
  purchase_date   TEXT    NOT NULL CHECK (purchase_date = date(purchase_date)),
  in_use_from     TEXT    NOT NULL CHECK (in_use_from = date(in_use_from)),
  cost            INTEGER NOT NULL CHECK (cost > 0),                        -- fx3
  vendor_id       INTEGER REFERENCES vendor(id),
  method          TEXT    NOT NULL DEFAULT 'WDV' CHECK (method IN ('WDV','SLM')),
  rate_pct        INTEGER NOT NULL CHECK (rate_pct BETWEEN 0 AND 100000),   -- fx3 per year
  salvage_value   INTEGER NOT NULL DEFAULT 0 CHECK (salvage_value >= 0),    -- fx3
  disposed_date   TEXT CHECK (disposed_date = date(disposed_date)),
  disposal_value  INTEGER,                                                  -- fx3 proceeds
  notes           TEXT,
  created_at      TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  CHECK (salvage_value <= cost)
) STRICT;

CREATE TABLE asset_depreciation (
  id             INTEGER PRIMARY KEY,
  asset_id       INTEGER NOT NULL REFERENCES asset(id),
  period_month   TEXT    NOT NULL CHECK (period_month GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]'),
  opening_value  INTEGER NOT NULL,   -- fx3
  amount         INTEGER NOT NULL CHECK (amount >= 0),   -- fx3 charged to P&L
  closing_value  INTEGER NOT NULL,   -- fx3
  posted_at      TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
  UNIQUE (asset_id, period_month),
  CHECK (closing_value = opening_value - amount)
) STRICT;


-- =====================================================================
-- 10. SEARCH   (FTS5 trigram: substring match, works for Gujarati/Hindi too)
-- =====================================================================
-- rowid = product.id.  Query terms need >= 3 characters; for 1-2 chars use
--   SELECT ... FROM product WHERE name LIKE :q || '%'   (uses ix_product_name)
-- Example:
--   SELECT p.* FROM product_fts f JOIN product p ON p.id = f.rowid
--   WHERE product_fts MATCH '"atta" "aash"' AND p.is_active = 1
--   ORDER BY bm25(product_fts, 10.0, 5.0, 4.0, 3.0, 1.0) LIMIT 30;
CREATE VIRTUAL TABLE product_fts USING fts5(
  sku, name, name_local, search_terms, brand,
  tokenize = 'trigram'
);

CREATE TRIGGER trg_product_fts_ins AFTER INSERT ON product BEGIN
  INSERT INTO product_fts(rowid, sku, name, name_local, search_terms, brand)
  VALUES (NEW.id, NEW.sku, NEW.name, NEW.name_local, NEW.search_terms,
          (SELECT name FROM brand WHERE id = NEW.brand_id));
END;
CREATE TRIGGER trg_product_fts_upd AFTER UPDATE OF sku, name, name_local, search_terms, brand_id ON product BEGIN
  DELETE FROM product_fts WHERE rowid = OLD.id;
  INSERT INTO product_fts(rowid, sku, name, name_local, search_terms, brand)
  VALUES (NEW.id, NEW.sku, NEW.name, NEW.name_local, NEW.search_terms,
          (SELECT name FROM brand WHERE id = NEW.brand_id));
END;
CREATE TRIGGER trg_product_fts_del AFTER DELETE ON product BEGIN   -- only never-used products
  DELETE FROM product_fts WHERE rowid = OLD.id;
END;
CREATE TRIGGER trg_brand_fts_upd AFTER UPDATE OF name ON brand BEGIN
  UPDATE product_fts SET brand = NEW.name
  WHERE rowid IN (SELECT id FROM product WHERE brand_id = NEW.id);
END;


-- =====================================================================
-- 11. REPORTING VIEWS
-- =====================================================================

-- Current stock and value per product.
CREATE VIEW v_product_stock AS
SELECT p.id AS product_id, p.sku, p.name, p.reorder_level,
       COALESCE(SUM(l.qty_on_hand), 0)                                        AS stock_qty,    -- fx3
       COALESCE(SUM(CAST(ROUND(l.qty_on_hand * l.unit_cost / 1000.0) AS INTEGER)), 0) AS stock_value,  -- fx3 at cost
       (p.reorder_level > 0 AND COALESCE(SUM(l.qty_on_hand), 0) <= p.reorder_level) AS is_low
FROM product p
LEFT JOIN product_lot l ON l.product_id = p.id AND l.qty_on_hand <> 0
WHERE p.is_active = 1
GROUP BY p.id;

-- Expiry notification area. Alert window: product -> category -> parent category -> setting.
CREATE VIEW v_expiry_alert AS
SELECT *,
       CASE WHEN days_left < 0  THEN 'EXPIRED'
            WHEN days_left <= 7 THEN 'CRITICAL'
            ELSE 'SOON' END AS bucket
FROM (
  SELECT l.id AS lot_id, l.lot_code, l.batch_no, l.vendor_id,
         p.id AS product_id, p.sku, p.name,
         l.expiry_date, l.qty_on_hand,
         CAST(julianday(l.expiry_date) - julianday('now','localtime','start of day') AS INTEGER) AS days_left,
         COALESCE(p.expiry_alert_days, c.expiry_alert_days, pc.expiry_alert_days,
                  CAST((SELECT value FROM setting WHERE key = 'expiry.default_alert_days') AS INTEGER),
                  30) AS alert_days,
         CAST(ROUND(l.qty_on_hand * l.unit_cost / 1000.0) AS INTEGER) AS value_at_cost   -- fx3
  FROM product_lot l
  JOIN product  p       ON p.id  = l.product_id
  JOIN category c       ON c.id  = p.category_id
  LEFT JOIN category pc ON pc.id = c.parent_id
  WHERE l.qty_on_hand > 0 AND l.expiry_date IS NOT NULL
)
WHERE days_left <= alert_days;

-- Every P&L-relevant event from every module, as signed rows.
-- P&L for ANY date range:
--   SELECT section, head, SUM(amount) FROM v_pnl_entry
--   WHERE entry_date BETWEEN :from AND :to GROUP BY section, head;
--   Net profit = INCOME - COGS - EXPENSE
-- A new module joins the P&L by adding one UNION ALL branch.
CREATE VIEW v_pnl_entry AS
SELECT s.sale_date AS entry_date, 'INCOME' AS section, 'Sales' AS head, s.taxable_total AS amount
  FROM sale s WHERE s.status = 'COMPLETED'
UNION ALL
SELECT s.sale_date, 'INCOME', 'Round off', s.round_off
  FROM sale s WHERE s.status = 'COMPLETED' AND s.round_off <> 0
UNION ALL
SELECT r.return_date, 'INCOME', 'Sales returns', -r.taxable_total
  FROM sale_return r
UNION ALL
SELECT s.sale_date, 'COGS', 'Cost of goods sold', s.cost_total
  FROM sale s WHERE s.status = 'COMPLETED'
UNION ALL
SELECT r.return_date, 'COGS', 'Cost of goods sold', -r.cost_total
  FROM sale_return r
UNION ALL
SELECT d.damage_date, 'EXPENSE', 'Damages & write-offs', d.cost_value - d.recovered_amount
  FROM damage d
UNION ALL
SELECT e.expense_date, 'EXPENSE', ec.name, e.amount
  FROM expense e JOIN expense_category ec ON ec.id = e.category_id
UNION ALL
SELECT date(sp.period_month || '-01', '+1 month', '-1 day'), 'EXPENSE', 'Salaries', sp.amount
  FROM salary_payment sp
UNION ALL
SELECT date(ad.period_month || '-01', '+1 month', '-1 day'), 'EXPENSE', 'Depreciation', ad.amount
  FROM asset_depreciation ad
UNION ALL
SELECT a.disposed_date, 'INCOME', 'Gain/loss on asset disposal',
       a.disposal_value - COALESCE(
         (SELECT ad.closing_value FROM asset_depreciation ad
           WHERE ad.asset_id = a.id ORDER BY ad.period_month DESC LIMIT 1), a.cost)
  FROM asset a WHERE a.disposed_date IS NOT NULL;

-- Dashboard: daily sales series.
CREATE VIEW v_daily_sales AS
SELECT sale_date,
       COUNT(*)                          AS bills,
       SUM(grand_total)                  AS sales,          -- fx3
       SUM(taxable_total - cost_total)   AS gross_profit    -- fx3
FROM sale WHERE status = 'COMPLETED'
GROUP BY sale_date;

-- What you owe each vendor.
CREATE VIEW v_vendor_balance AS
SELECT v.id AS vendor_id, v.name,
       v.opening_balance
       + COALESCE((SELECT SUM(p.grand_total) FROM purchase p
                    WHERE p.vendor_id = v.id AND p.status = 'POSTED'), 0)
       - COALESCE((SELECT SUM(vp.amount) FROM vendor_payment vp
                    WHERE vp.vendor_id = v.id), 0) AS balance_due   -- fx3
FROM vendor v;

-- Startup integrity check: must always return zero rows.
CREATE VIEW v_lot_cache_mismatch AS
SELECT l.id AS lot_id, l.qty_on_hand AS cached, COALESCE(m.total, 0) AS ledger
FROM product_lot l
LEFT JOIN (SELECT lot_id, SUM(qty) AS total FROM stock_movement GROUP BY lot_id) m
       ON m.lot_id = l.id
WHERE l.qty_on_hand <> COALESCE(m.total, 0);


-- =====================================================================
-- 12. SEED DATA
-- =====================================================================

INSERT INTO setting(key, value) VALUES
  ('store.name', ''), ('store.address', ''), ('store.phone', ''),
  ('store.gstin', ''), ('store.state', ''),
  ('tax.mode', 'REGULAR'),              -- REGULAR | COMPOSITION | NONE
  ('fy.start_month', '4'),              -- Indian FY: April-March
  ('expiry.default_alert_days', '30'),
  ('stock.allow_negative', '0'),
  ('bill.round_to', '1000'),            -- fx3: round payable to nearest Rs 1.000; '0' = off
  ('barcode.lot_prefix', '20'),         -- internal EAN-13 for lot labels
  ('barcode.product_prefix', '21'),     -- internal EAN-13 for products without a barcode
  ('backup.dir', ''),
  ('backup.keep', '30');

INSERT INTO uom(code, name, allow_fraction) VALUES
  ('PCS','Piece',0), ('KG','Kilogram',1), ('G','Gram',0),
  ('L','Litre',1), ('ML','Millilitre',0), ('DOZ','Dozen',1),
  ('PKT','Packet',0), ('BOX','Box',0);

-- Verify slabs with your CA before go-live; they are editable data.
INSERT INTO tax_rate(name, rate_pct) VALUES
  ('GST 0%', 0), ('GST 5%', 5000), ('GST 18%', 18000);

INSERT INTO stock_movement_type(code, name, direction) VALUES
  ('OPENING',         'Opening stock',          1),
  ('PURCHASE',        'Purchase receipt',       1),
  ('PURCHASE_CANCEL', 'Purchase cancelled',    -1),
  ('SALE',            'Sale',                  -1),
  ('SALE_VOID',       'Sale voided',            1),
  ('SALE_RETURN',     'Customer return',        1),
  ('DAMAGE',          'Damage / write-off',    -1),
  ('ADJUSTMENT',      'Stock-count adjustment', 0);

INSERT INTO expense_category(name) VALUES
  ('Rent'), ('Electricity'), ('Transport & Delivery'), ('Packaging'),
  ('Repairs & Maintenance'), ('Shop Supplies'), ('Phone & Internet'),
  ('Bank & UPI Charges'), ('Taxes & Fees'), ('Miscellaneous');

PRAGMA user_version = 1;   -- schema version; migrations bump this
