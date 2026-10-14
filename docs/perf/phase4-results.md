# Phase 4 performance work — 14 October 2026

This records local implementation and verification for Phase 4 in
`optimization-phases.md`. The scale machine was VARK (24 logical processors,
.NET 10.0.12, SQLite 3.53.3). The main dummy database held 5,189,405 sale
lines and 5,392,586 stock movements before the final sustained run, with
202,500 lots and 27,000 products. It reached schema 24. The later combined
run grew the disposable copy to 222,500 lots and 47,000 products. These are
workstation service timings and keyboard-driven WPF automation results;
physical scanner/printer behavior was not measured.

## Changes

- Kept the already measured Phase 1 access paths for sale date and cursor,
  product movement, return lines, vendor lots, and audit time. Added
  `stock_movement(move_date,id)` after a direct before/after trial showed a
  broad Stock Ledger report using a product/date skip scan and temporary sort.
  Migration 23 creates the index. The index adds 124,850,176 bytes to the
  2.13 GB fixture (about 5.9%).
- Migration 24 maintains cost, selling and MRP values in
  `product_stock_total` for every lot insert, delete, quantity, cost, price,
  MRP, and product reassignment. Category valuation and Stock Value report
  read that projection. The full fixture had zero per-product drift against
  source lots after migration and mixed sale writes.
- Opening-stock preview parses the workbook without holding a SQLite reader,
  then resolves distinct SKUs in 400-key batches. Both workbook previews cap
  at 50,000 rows and check cancellation. Product and opening-stock imports
  commit in 100-row batches with durable batch markers and a payload digest.
  A retry skips committed batches; a changed payload for the same operation
  ID is rejected. The UI derives a stable operation ID from the selected
  preview so an interrupted workbook can be reopened and resumed. Imports
  with at least ten batches run `PRAGMA optimize` at completion.
- Stock-count edits already queried rules only for supplied lot IDs and wrote
  200 at a time; stock-cache repair already used 1,000-lot transactions. A
  new count now defaults to category scope in the UI. Whole-store scope is an
  explicit operator choice with a maintenance-hour warning. Exact lot-code search now uses the unique lot
  index followed by the count-line key. Paged count search and direct
  barcode/product lookup were already present. Name searches with at least
  three characters now use the existing trigram product FTS index, then
  confirm the product name and count scope; shorter terms and uncommon FTS
  misses retain the original substring path.
- The existing `DbExecutor` bounds reads to four concurrent connections,
  interrupts cancelled reads, sets a 30-second command timeout, and records
  reader, writer wait, transaction, WAL and row-count metrics. The POS scan
  queue, bill-switch queue and bill refresh version checks were already
  present. Sales-history navigation now commits its cursor stack only after
  its requested page succeeds and coalesces Refresh clicks during a load. Lot price and active-state UI edits use
  compare-and-swap values and reject stale screens.

## Results

| Trial | Result | Raw file |
|---|---|---|
| Broad Stock Ledger, 100 rows, 5.39m movements | Before: 429–487 ms warm and temporary sort. After: 0.6–1.4 ms with date/ID index. Index build: 13.3 s. | `artifacts/phase4-movement-date-index.json` |
| Category valuation, 202,500 lots | Earlier shape run: 358 ms p95. Projected: 6.1–8.6 ms warm for 81 categories; first read 23.7 ms. Projection drift: 0 products. | `artifacts/phase4-shape-profile.json`, `artifacts/phase4-valuation.json` |
| Stock Value report | Projected: 12.2–12.9 ms warm for 100 rows; first read 168 ms. | `artifacts/phase4-valuation.json` |
| 20,000-product preview and import with a competing sale | Two runs: preview 2.03–2.25 s; import 3.15–3.85 s in 200 batches; sale add 45.6–55.3 ms. The earlier monolithic import trial delayed sale add 2.17 s. The final run includes `PRAGMA optimize`. | `artifacts/phase4-product-import-overlap.json`, `artifacts/phase4-product-import-final.json` |
| 20,000 opening-stock rows with a competing sale | Two runs: preview 1.11–1.18 s; import 4.94–6.05 s in 200 batches; sale add 33.6–42.1 ms. Replay returned the original first lot; 200 batch markers existed. The final run includes `PRAGMA optimize`. | `artifacts/phase4-opening-import.json`, `artifacts/phase4-opening-import-final.json` |
| Whole-count scoped lookup, 150,001 lots | Warm exact lot code: 0.2–0.3 ms; direct lot ID: 0.2–0.3 ms; name contains: 45–47 ms. The exact query plan used both unique lot code and count-line indexes. | `artifacts/phase4-count-search.json` |
| 120-second mixed run, four readers, two sale writers and backup | 234 completed sales, zero errors; sale add p95 6.4 ms, commit p95 9.2 ms, end-to-end p95 93.3 ms. Peak sampled WAL 37.9 MB, working set 181 MB. A verified 2.26 GB backup took 91.7 s. | `artifacts/phase4-sustained.json` |
| 120-second combined run on this PC | Four continuous readers included dashboard, P&L and two reports; two sale writers completed 212 sales with zero errors. Two concurrent 20,000-row imports took 18.5/18.6 s; a 202,500-line count was created, paged and reviewed during checkout. Sale add/commit/composite p95: 81.5/79.2/155.6 ms. Dashboard/Stock Value report p95: 144/27 ms. Verified 2.26 GB backup: 96.5 s. Peak sampled WAL: 389 MB; peak process working set: 294 MB. Database grew 31.2 MB during imports, count creation and sales; that growth cannot isolate index cost. | `artifacts/phase4-combined-local.json` |
| 222,500-line count name lookup, paired on the same copy | Legacy substring SQL took 59–64 ms warm; the FTS-prefiltered service took 1.2–1.5 ms warm. Exact lot code took 0.1–0.3 ms warm. Plan uses the FTS virtual table and `ix_stock_count_line_product`. | `artifacts/phase4-combined-count-search-paired.json` |
| Local WPF keyboard journey on 222,500-lot copy | Eight Next/Previous/Refresh history cycles restored first-page navigation. Three keyboard-simulated scans plus double Enter produced exactly 3.000 units in one bill line; unknown barcode kept the bill, Hold cleared it, and New/Pay added exactly one completed sale. Whole-store scope was selected explicitly; search into an editable count row took 155 ms by external automation and kept only nine visible grid rows. The 118 ms burst result is three scans plus automation, not physical scan-to-line p95. | `artifacts/phase4-ui-local.json` |

The full-store check after the sustained run passed SQLite integrity,
foreign keys, lot-cache equality, dashboard daily/top reconciliation,
sale-line expiry checks, and interrupted-write consistency. It took 172.7 s
for integrity, 4.3 s for foreign keys, 63.0 s for lot reconciliation, and
76.2 s for sale-line expiry checks. See `artifacts/phase4-verify.json`.
The Release build had zero warnings and the default solution suite passed
146 tests, including import replay/payload rejection and stale lot-edit
checks.

After the combined run, the same verification passed again: dashboard day/top
drift, foreign keys, lot-cache drift, expired sale lines and interrupted-write
checks were all zero; `integrity_check` returned `ok`. Its integrity, foreign
key, lot-cache and expiry stages took 167.5, 4.3, 18.2 and 17.5 seconds.
See `artifacts/phase4-combined-verify.json`. The final local WPF journey passed
as one opt-in UI test, and the normal solution suite passed 146 tests with its
two hardware/package opt-ins skipped.

The combined harness's first last-lot lookup returned no row because it chose
the newest lot *after* the opening import had added lots beyond the count
snapshot. The harness now retains a pre-import lot code. The paired lookup
run and WPF search on the same count both found existing lines. This was a
test-data selection error, not a failed count lookup.

## Remaining acceptance work

- A physical barcode scanner and printer are unavailable. The keyboard WPF
  journey does not measure the specified scan-to-line `<50 ms` target from a
  real label or receipt-start timing under print contention. Those belong to
  the hardware acceptance in Phases 5–6 if such devices are later used.
- No separate store PC exists. The combined run measures this available PC.
  Its 79.2 ms sale-commit p95 met the `<100 ms` target, while the 155.6 ms
  add/get/commit p95 includes read queueing during four continuously active
  readers and both imports. The broad count was created and searched under
  load; posting a fully entered whole-store count remains a maintenance
  action outside checkout hours.
- The new movement index cost 125 MB (5.9%) on the full fixture. Combined
  database growth and 212 sale writes did not reveal a write failure, but
  they do not isolate that index's exact per-write cost from import, count and
  valuation work.
- An interrupted batch import leaves committed earlier batches visible and
  resumes only with the same preview. If a later batch encounters a permanent
  data conflict, an operator must resolve that conflict before replay. Do not
  start a changed preview against the same partially imported workbook.
- The 2.26 GB backup times are not a direct test of the plan's approximately
  1 GB under-60-second target. No appropriately sized fixture or separate
  store PC was available. The backup-size and physical-device gates remain
  open for the Phase 6 release decision.
