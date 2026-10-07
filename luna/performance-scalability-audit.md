# Performance and Scalability Audit

**Application:** Grocery ERP  
**Audit date:** 2026-10-07  
**Status:** Static/code-level audit complete; full-scale runtime validation pending the required .NET SDK 10.0.401.

## Executive conclusion

The application is suitable for small datasets and has several sound transactional safeguards, but it is not yet ready to claim the scalability targets in `implementation.md`.

Query optimization is required. The most serious issue is product search and stock calculation: correlated lot queries can become very expensive at the planned scale because there is no general `product_lot(product_id)` index and stock is calculated repeatedly per product.

The core sale and return flows are transactionally protected, but backup/restore coordination and several UI workflows can race. Under the planned five-year dataset, the application can experience long UI freezes, write starvation, `SQLITE_BUSY` failures, or restore/backup errors unless the high-priority items below are addressed.

## Scope and evidence

Reviewed:

- `implementation.md`, including performance targets, synthetic data volumes, release checklist, and promised pagination/caching behavior.
- SQLite schema and migrations.
- Database executor, maintenance, backup, restore, inventory, catalog, sales, purchasing, reports, assets, and UI workflows.
- Existing tests and the available prebuilt benchmark.

Planned scale from `implementation.md`:

| Entity | Target volume |
|---|---:|
| Products | 20,000 |
| Lots | ~150,000 |
| Bills | ~640,000 |
| Sale lines | ~3.8 million |
| Historical period | 5 years |

The prebuilt benchmark passed 20,000-product import/search testing. It measured approximately 4.9 seconds for import and below 0.5 ms p95 search. It did not include lots, stock movements, bills, sale lines, dashboard queries, reports, backup, restore, or concurrent workloads, so it does not validate the complete production target.

The normal test command could not run because `global.json` requires .NET SDK `10.0.401`, which is not installed in the environment.

## Findings by severity

### P0 — Fix before full-scale release

#### P0.1 Product search can degrade badly with lots

`src/Modules/GroceryErp.Catalog/CatalogService.cs` calculates stock through correlated subqueries for matching products and also performs a correlated FEFO lookup. The schema has a partial FEFO index, but no general index covering `product_lot(product_id)` for the stock aggregate.

At high match counts, the query can repeatedly scan lots. The current 20,000-product benchmark did not expose this because it did not populate the planned 150,000 lots.

Recommended fix:

1. Add a general index on `product_lot(product_id)`.
2. Rewrite search to aggregate stock once in a CTE or derived table and join it to products.
3. Select the FEFO lot through a separately optimized indexed query or precomputed availability projection.
4. Add `EXPLAIN QUERY PLAN` assertions and a benchmark containing realistic lots.

#### P0.2 Inventory and ledger screens are unbounded

Inventory overview, lots, expiry data, damage data, and stock ledgers are loaded without consistent pagination. The product-stock window requests the complete ledger using an unrestricted date range.

A fast-moving product can therefore return millions of movement rows, causing excessive memory use, UI freezes, or process termination.

Recommended fix:

- Apply keyset pagination to every large inventory result.
- Use a page size around 100–500 rows.
- Require a bounded date range for large ledger/report queries.
- Keep only the visible page in the UI model.

#### P0.3 Backup and restore are not fully coordinated with active database work

Backup and restore use direct connections/file operations outside the normal database executor. Restore can replace database files while UI readers, pooled connections, or asynchronous operations are still active. Multiple backup triggers can also overlap.

Possible outcomes include locked database errors, failed reads, incomplete restore, or backup filename collisions.

Recommended fix:

- Add an application-wide quiesce barrier that blocks new database operations.
- Wait for active reads/writes to finish before restore or file replacement.
- Serialize all backup operations with a dedicated semaphore.
- Rename a backup from partial to final only after verification succeeds.
- Remove or quarantine a failed final-name backup.

### P1 — Fix before claiming the documented performance targets

#### P1.1 Dashboard work is repeated and partly performed in memory

The dashboard starts several concurrent reads. Several of those reads repeatedly evaluate dynamic views for stock, expiry, vendor balance, and daily sales. The top-products query loads all groups and sorts them in memory before taking the top ten.

This is unlikely to remain below the documented one-second target at the full dataset size.

Recommended fix:

- Push `ORDER BY ... LIMIT 10` into SQL.
- Materialize or cache daily sales, stock, expiry, and payable summaries.
- Avoid starting multiple expensive historical scans at once.
- Measure each dashboard component separately.

#### P1.2 Long maintenance transactions starve POS writes

Stock-cache rebuilds, depreciation posting, large imports, and some maintenance jobs hold the single process-wide write gate for long periods. SQLite writes are correctly serialized, but this means POS writes wait behind maintenance.

Recommended fix:

- Process large jobs in bounded chunks.
- Add progress reporting and cancellation checkpoints.
- Record writer wait time and transaction duration.
- Schedule expensive work outside trading hours where possible.

#### P1.3 Missing or mismatched indexes remain on hot paths

Indexes to evaluate and add after checking query plans:

- `product_lot(product_id)`
- `sale(sale_date, id)` or a status-aware equivalent
- `sale_payment(sale_id, mode)`
- `sale_return_line(sale_return_id)`
- `audit_log(at)`
- stock-movement indexes matching ledger filters and ordering

Several report predicates apply functions such as `strftime`, `date`, or `substr` to indexed columns. These predicates can prevent efficient index range scans. Rewrite them as sargable ranges where possible.

#### P1.4 Reports can overwrite newer UI state

Reports and some history/refresh operations allow overlapping asynchronous calls. A slower, older request can complete after a newer request and overwrite the current view.

Recommended fix:

- Add cancellation/version tokens to every refreshable page.
- Serialize navigation actions such as history “Next”.
- Ignore results whose request version is no longer current.

#### P1.5 Scanner events can overlap

Scanner input and Enter-key handling can start multiple asynchronous add-to-sale operations. The database gate protects individual writes, but the ordering of scans and UI refreshes is not guaranteed.

Recommended fix:

- Queue scanner events in arrival order.
- Process one scan at a time.
- Make the UI state update only after the corresponding operation completes.

#### P1.6 Idempotency coverage is incomplete

Sale completion and returns use operation guards and are protected well. Lot price changes and lot activation do not consistently use the same idempotency mechanism, so repeated clicks may produce repeated audit actions or last-write-wins behavior.

Recommended fix:

- Add operation guards to all user-triggered mutations, or explicitly document which operations are intentionally last-write-wins.
- Add true concurrent duplicate-operation tests for each protected operation type.

### P2 — Improvements and hardening

- Bound concurrent read work; each physical SQLite connection has its own configured cache, so many simultaneous readers can consume substantial memory.
- Add transient `SQLITE_BUSY` retry with jitter for external/process-level contention, while retaining transaction timeouts.
- Run `ANALYZE`/`PRAGMA optimize` after bulk imports and schema changes.
- Add metrics for query duration, rows returned, database lock waits, writer queue length, and UI refresh age.
- Benchmark backup, restore, integrity checks, WAL checkpointing, and shutdown against a one-gigabyte database.
- Avoid startup checks that regroup all stock movements when a cheaper incremental consistency signal is available.

## Concurrency and crash assessment

### Strengths

- A process-wide write semaphore serializes writes.
- Sale completion and returns use database transactions.
- Stock is revalidated inside the write transaction.
- Operation guards provide useful duplicate-submission protection for core sales operations.
- The application uses a single-instance mutex for normal desktop launches.

### Remaining risks

- A second process or external database writer is not protected by the in-process semaphore.
- Long writes can exceed the five-second busy timeout.
- Restore can race with active readers/writers.
- Backup triggers can overlap.
- UI requests can complete out of order.
- Large unbounded result sets can exhaust memory or make the desktop process appear hung.
- Shutdown performs potentially long backup/checkpoint work and can be slow with active readers.

## Required validation after fixes

Install the SDK specified by `global.json`, then run:

1. Full unit, integration, and UI test suites.
2. A synthetic dataset at the documented 20k/150k/640k/3.8m scale.
3. Query-plan checks for search, inventory, dashboard, ledger, P&L, vendor balance, and expiry queries.
4. Concurrent sale, return, scan, report-refresh, backup, and restore tests.
5. Backup/restore and integrity testing with active readers and writers.
6. Cold-start, search, bill commit, history, P&L, dashboard, and backup measurements against the targets in `implementation.md`.

## Prioritized implementation plan

1. Fix product-lot indexing and rewrite catalog stock/search aggregation.
2. Add pagination and bounded date ranges to inventory, expiry, ledger, and report screens.
3. Optimize/materialize dashboard aggregates and push limits into SQL.
4. Add backup/restore quiescing and backup serialization.
5. Chunk maintenance/depreciation/cache rebuild transactions.
6. Add missing indexes and replace non-sargable date predicates.
7. Serialize scanner/UI refresh workflows and complete idempotency coverage.
8. Install the required SDK and run full-scale concurrent benchmarks.

## Final status

The code-level audit is complete and documented here. The application should be treated as **not yet performance/scalability sign-off ready** until the P0/P1 items are implemented and the full-scale runtime validation passes.
