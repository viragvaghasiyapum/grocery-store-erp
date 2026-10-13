# Phase 2 sustained-load optimization: implementation subphases

**Status:** plan, not implemented. This is the follow-up to the full-size Phase 2 workstation measurement in [optimization-phases.md](optimization-phases.md). Restore/recovery work remains the original Phase 2 scope; the query, summary, and read-scheduling changes below implement the measured latency follow-up that the main plan assigns to Phases 3 and 4. Keep measured outcomes and release status in `optimization-phases.md`; use this file for the implementation sequence and decisions.

## Goal and measured baseline

The 13 October 2026 Release audit used a disposable schema-15, 2,107,920,384-byte database on this PC (i7-13700HX, 15.7 GiB RAM, NVMe SSD). For 603.6 seconds, four continuously active readers and two paced sale writers completed 943 sales with no operation errors while a verified backup ran. Post-run integrity, foreign keys, lot cache, expiry, and interrupted-write checks passed. This is a deliberately heavy service workload, not an end-to-end WPF latency measurement or normal cashier traffic.

| Measurement | Baseline p95 | Applicable target or decision |
|---|---:|---|
| Dashboard service | 3,339 ms / 532 samples | Dashboard fully loaded under 1,000 ms, including UI work |
| Month P&L service | 1,687 ms / 533 samples | Month P&L under 200 ms, including UI work |
| Recent sales history service | 502 ms / 532 samples | History first page under 200 ms, including UI work |
| Broad catalog service | 1,293 ms / 531 samples | Search visible under 100 ms; query under 30 ms |
| Database read queue | 1,377 ms / 6,793 reads | Diagnose and reduce before adding indexes |
| Database write duration | 12.4 ms / 1,886 writes | Protect the separate under-100-ms bill-commit target |
| Composite sale add/get/complete | 1,390 ms / 943 sales | Includes reads; do not treat as commit-only latency |
| Verified backup under load | 158.9 seconds / ~2.11 GB DB | Measure alongside latency; the specified under-60-second target is for ~1 GB |
| Peak audit working set / sampled WAL | 162.8 MB / 129.8 MB | Keep bounded and compare candidate changes against this run |

Raw results are under `artifacts/data/phase2-sustained-final/`; the repeatable `sustained-profile` command and verification procedure are in `optimization-phases.md` Phase 2. Preserve the original generated fixture, test only disposable copies, and use the same schema, filters, workers, duration, sale pacing, and backup timing for before/after comparisons. Record commit, SDK/SQLite versions, PC, database size, row counts, available disk, p50/p95/p99, errors, and query plans with each run. OS file cache is not flushed by a new audit process.

**Order of work:** 2.1 attribution → 2.2 low-risk SQL → 2.3 dashboard projections if needed → 2.4 P&L summary if needed → 2.5 history if query time warrants it → 2.6 read scheduling → 2.7 acceptance. Re-run the short and full profiles after each material change so that added storage or write cost has a measured benefit.

## 2.1 Attribute service time before changing schema

**Code:** `ReportService.GetDashboardAsync`, `ReportService.GetPnlAsync`, `SalesService.GetHistoryAsync`, `DbExecutor.ReadAsync`, `DashboardPage`, `SalesHistoryPage`, and `GroceryErp.ScaleAudit`.

1. Capture separate queue wait, connection/open time, SQL execution, row mapping, service composition, WPF binding/layout, and first-frame callback for dashboard, month P&L, and the history first page. Tag metrics by operation without customer data. Report isolated warm/cold-process samples as well as the same mixed workload; process-cold does not mean disk-cache cold.
2. Time the four dashboard branches separately: core totals, payments, 30-day trend, and top products. Record returned rows and `EXPLAIN QUERY PLAN` for the **actual executed SQL** and representative dates, including empty and populated days. The current top-products query already ranks and caps results in SQL; measure its 30-day grouping cost rather than proposing another top-ten limit.
3. Break down `GetPnlAsync` into sale totals, returns, damage/adjustments, expense categories, salary/depreciation, disposal/vendor variance, and object construction. Compare month and fiscal-year periods, including backdated entries.
4. For history, compare SQL-only/row-mapping time with `db.read.queue` and UI timing. Test unfiltered first page, current/old ranges, status, payment, invoice prefix with and without a match, amount, and combined filters. The service **already** caps pages at 500, uses a date/ID cursor, and has `ix_sale_date_id`; do not reimplement pagination or add an index without plan evidence.

**Gate:** identify which portion of each p95 is waiting versus executing, and name the specific expensive statement/branch. If isolated history SQL is already below its budget, prioritize queue relief over history schema changes. Keep a before-state JSON and query-plan artifact on a disposable copy.

## 2.2 Remove repeated work in existing queries

**Dashboard:** In `ReportService`, compute today's sales/bills/profit from `v_daily_sales` once rather than three scalar subqueries; combine the three expiry-bucket counts into one grouped or conditional aggregate. Read low-stock count and stock value from the existing `product_stock_total` projection (migration 015) where equivalent, respecting active products. Keep the current SQL-ranked top-products output. Use one coherent snapshot for related dashboard numbers where practical without holding a long-lived read transaction. Measure each branch and total before accepting the rewrite.

**P&L:** Keep the existing date-range predicates and `MATERIALIZED` sale/return totals, then examine the actual plan and rows scanned for every `UNION ALL` branch. Test selective or covering indexes only for branches with demonstrated table scans; compare added index bytes, migration time, sale/return write p95, and backup time. Preserve integer scaled-money arithmetic, rounding, period boundaries, and the rule that salary/depreciation include only complete months.

**History:** If query execution is expensive after queue wait is removed, fetch a bounded page of sale IDs first and then aggregate payment modes only for those IDs. Preserve sort order, cursor transitions across dates, all filters, and one result per sale. Compare this with the existing correlated `group_concat` and `ix_sale_payment_sale` plan before adopting it.

**Gate:** exact output parity on existing report/history tests plus new populated edge cases; measurable p95 reduction on the full-size copy; no material regression in write duration, query memory, database size, WAL, or backup. Keep the simpler original query when a rewrite does not help.

## 2.3 Add only proven dashboard projections

If 2.2 cannot meet the dashboard target, add small, durable projections for the measured expensive branch:

- For repeated 30-day top-product work, test a `day × product` sales/return summary with scaled revenue, cost, quantity, and stable product ID. Rank the requested 30 days from that summary; retain snapshot names from sale lines where current output semantics require them. Compare its size with the existing 5.18 million sale lines and verify tie ordering.
- Use the existing `product_stock_total` projection for stock value and low stock. Do not build a duplicate stock table. If expiry scans dominate, test a daily expiry-count cache keyed by business date with explicit invalidation on lot, product/category threshold, default setting, and day rollover. A cached count must never silently survive a date change or backdated correction.
- Investigate vendor payable aggregation separately if the dashboard core branch shows it is costly. Apply a payable projection only after measuring its benefit and verifying purchase, payment, return, and correction paths.

Additive migration and backfill must run on a disposable full store first, with pre-migration backup, progress, free-disk estimate, interruption recovery, and idempotent rebuild. Update projections in the same transaction as source writes, or use a durable dirty-key mechanism whose reads clearly handle stale keys. Reconcile each projection against authoritative source queries after normal writes, voids, returns, backdated activity, restart, and process kill. Measure trigger/service-write overhead and WAL growth. Reject a projection whose maintenance cost or stale-state risk outweighs its measured read gain.

**Gate:** dashboard service leaves enough time for WPF rendering to satisfy the under-1-second visible target; equality of every dashboard field on populated fixtures; bounded backfill, storage, and write overhead.

## 2.4 Add a daily P&L summary only if range scans remain costly

Start with the branches 2.1 identifies as dominant. A candidate summary key is `(effective_date, section, head_code, reference_id)` with integer scaled amounts and count fields as required. Summarize sales and returns first if they account for most time; leave cheap expense/asset branches as indexed direct reads. Avoid a full duplicate of `v_pnl_entry` merely to save one query.

Design the update rule for sale completion and void, returns, damages, stock adjustments, expenses, salaries, depreciation, asset disposal, vendor returns, date edits, and backdated postings **before** enabling a summary read. A void must remove the completed sale's effect exactly once; a return must affect its own effective date. Map salaries/depreciation to the same completed-month semantics as `GetPnlAsync`. Use a transactional delta or durable dirty-day rebuild, with operation-ID/idempotency coverage. A summary must be recoverable by comparing and rebuilding from source rows.

Backfill by bounded date chunks on a disposable 2.11 GB copy. Record migration and reconciliation time, peak extra disk, WAL, memory, and POS commit p95 during maintenance. Compare month, FY, arbitrary/custom, empty, partial-month, and cross-year results exactly against the existing query, including money totals and category labels. Keep the direct query available as a validation/rebuild reference.

**Gate:** month P&L service p95 leaves room for UI work under 200 ms, fiscal-year under 500 ms, exact business totals, and acceptable write/storage cost. If indexed direct SQL passes after 2.2/2.6, skip the summary table.

## 2.5 Tune history only after queue relief

Investigate the actual first-page SQL plan and payment-mode aggregation for each filter shape. Consider a page-ID-first query and an index matched to a demonstrably slow predicate, especially invoice-prefix no-match or payment/status combinations. Do not add a broad covering index by default: it costs disk space and every sale write must maintain it. Existing cursor paging and `ix_sale_date_id` must remain stable.

Test first, middle, oldest, and date-transition pages; inserts between page requests; same-date ties; 500-row maximum; deleted/voided rows; payment and invoice filters; and full-history CSV export, which must remain complete and streamed. Compare first-page SQL time, read queue time, UI-visible time, index bytes, and sale write p95.

**Gate:** history first page visibly under 200 ms at p95 on the intended PC under the agreed concurrent workload, with no missing/duplicated rows across cursors. If the query is already fast in isolation, close this subphase with no SQL change and show the queue improvement instead.

## 2.6 Control read contention and refresh traffic

`DbExecutor` currently has a four-reader gate **per executor instance**. Dashboard launches four reads; the audit adds other readers and a second sales executor. A 1,377 ms aggregate queue p95 does not establish whether four slots are too few, whether long queries hold them too long, or whether multiple instances exceed the intended global limit.

1. After 2.2–2.5, compare reader limits of 2, 4, 6, and 8 on the same full-size copy. Measure total throughput, dashboard/P&L/history p95, sale get/commit p95, CPU, peak working set, active connections, WAL, and backup overlap. More connections can increase contention and WAL retention; choose the measured optimum.
2. If needed, move the gate to a canonical-database-path shared coordinator so separate executors obey one limit. Keep restore exclusivity and active-connection accounting correct. Prevent long reports from starving cashier reads; use bounded, fair admission or a small reserved capacity only if measurements justify it.
3. Coalesce dashboard/report refreshes and cancel obsolete UI requests. Pass cancellation into SQLite commands where supported, dispose readers promptly, and keep command deadlines explicit. Do not cancel a committed write or retry an ambiguous sale outcome.

**Gate:** read queue and user-visible p95 fall together without a WAL/memory or backup regression, and restore still drains all readers. Add concurrency and cancellation tests for multiple factories, refresh storms, and restore overlap.

## 2.7 Full-size acceptance and rollback decision

Run focused unit/integration tests after each subphase, then the complete suite, Release solution build, and the ten-minute `sustained-profile` on a **fresh disposable copy**. Use the same four readers, two paced writers, 30-second delayed backup, and post-run `verify`; repeat the final candidate at least three times if results are near thresholds or variable. Run the opt-in full-size FlaUI journey to capture search-to-visible, dashboard fully loaded, P&L display, and history first-page time, separating DB, binding, layout, and first-frame callback. Preserve the distinction between first-frame callback and physical display paint.

| Acceptance check | Required result |
|---|---|
| Correctness | Exact dashboard/P&L totals and history rows versus source queries; no lost/duplicate sale, return, or payment; full `verify` clean after load and fault tests |
| User-visible latency | Dashboard <1 s; month/FY P&L <200/500 ms; history first page <200 ms; search visible <100 ms and query <30 ms, at p95 under the agreed workload |
| Cashier writes | Complete-bill commit <100 ms p95, with separate add/get/complete measurements and zero normal-operation `SQLITE_BUSY` failures |
| Resources | Report peak process/app working set, active readers, peak/end WAL, database/index/projection bytes, temporary disk, and backup time; explain any increase against the recorded baseline before acceptance |
| Recovery | Migration/backfill restart or rollback, projection reconciliation, process-kill recovery, restore-barrier tests, and preserved verified backup |

Record the before/after table, query plans, storage and write amplification, test commands/results, and any failed target in `optimization-phases.md`. If this PC is not the eventual store PC, repeat the final run there. Printer/scanner checks and physical storage power cuts remain separate hardware acceptance work; they cannot be inferred from this software workload.
