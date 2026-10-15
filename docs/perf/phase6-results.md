# Phase 6 local scale work — 15 October 2026

This is an available-PC software pass, not release acceptance. The disposable
schema-24 store on VARK held 47,000 products, 222,500 lots, 868,744 sales,
5,189,881 sale lines and 5,413,062 stock movements. The main database was
2,291,167,232 bytes at the start of the final report pass. The runtime was
.NET 10.0.12 with SQLite 3.53.3. The OS file cache was not flushed, so
fresh-process measurements do not represent a physical cold disk.

## Schema 25 follow-up

The full-size schema-25 copy held 47,000 products and, after the final sustained run, 870,388 sales, 5,191,525 sale lines, and 5,414,706 stock movements in a 2,306,314,240-byte main database. Migration 25 added daily category/HSN/rate sale and return totals, the last completed sale date per product, and daily hour totals. The migration was transactional, took 52-105 seconds across local full-size copies, and grew the main database by about 12 MB (0.5%). The outward GST branch uses the projection; its inward purchase branch still reads source rows. This is the one-time upgrade cost, not startup time on an already upgraded store.

| Report path | Source query | Projected query | Evidence |
|---|---:|---:|---|
| Fiscal Category Sales | 3.0 s | 16-18 ms | Exact row parity for month, fiscal and partial windows. `artifacts/phase6-final3-parity.json`. |
| Fiscal GST Summary | 1.28 s | 24-29 ms | Exact row parity for the same windows. `artifacts/phase6-final3-parity.json`. |
| Fiscal Hourly Sales | 584 ms | 0.2-0.8 ms | Exact row parity for the same windows. `artifacts/phase6-final3-parity.json`. |
| Slow Movers, 100 rows | About 1.7 s | 9-44 ms | Full source last-sale reconciliation and void test. `artifacts/phase6-last-parity.json`, `artifacts/phase6-final3-reports.json`. |

The schema-25 report pass called all 18 report kinds with broad and selected filters without service errors. Its query plans for Category Sales, GST Summary and Hourly Sales read the new day tables. A later full-source check after 228 sustained sale writes found zero differences in `sale_report_day`, `sale_product_last`, and `sale_hour_day`; integrity, foreign keys, lot stock cache, expired sale lines and interrupted-write marker also passed. Evidence: `artifacts/phase6-final3-reports.json`, `artifacts/phase6-final3-verify.json`.

A 120-second four-reader/two-writer run with a verified background backup completed 228 sales without errors. Sale-commit p95 was 27.4 ms, 20-row Products FTS service p95 30.4 ms, 100-row FTS stress p95 135.3 ms, dashboard p95 493.5 ms, sampled WAL peak 38.7 MB and process working-set peak 167.5 MB. The 2.31 GB verified backup took 109.9 seconds. The OS file cache was uncontrolled. Evidence: `artifacts/phase6-final3-sustained-ui20-v2.json`.

The full-size WPF acceptance journey passed functionally. Nine 20-row Products search-to-visible UI Automation samples ranged from 333 to 503 ms; launch-to-login ranged from 2.8 to 9.1 seconds across local runs. The published ReadyToRun build had a 6.65-second launch-to-login observation. These UI Automation timings include cross-process control calls and polling. App-side timing now records startup stages and text-change-to-first-frame callback separately; a pre-change combined empty/populated search run reported first-frame p95 165 ms. The `<100 ms` visible-search and `<2 s` cold-start targets are not established here. The test-store results are under `%TEMP%/grocery-erp-packaged-ui-phase4-local/phase2-acceptance-ui.json` and `phase6-ui-startup.json`.

A 101-row direct Low Stock SQL trial returned the same rows in 134-161 ms warm; the exact report SQL and service repeated at 118-256 ms and 112-128 ms, respectively. A date-first grouped rewrite took 185-203 ms, so the current indexed product lookup was retained; separate full report passes had 4.65 s and 1.77 s fresh-process outliers. Stable cold and concurrent report latency remains to be established. Evidence: `artifacts/phase6-low-stock-trial-exact.json`, `artifacts/phase6-final3-reports-final.json`.

## Schema 26 Low Stock follow-up

The report now sums gross completed-sale quantity from `sale_product_day(product_id,day)` for the requested 30-day window. An intermediate covering `sale_line(product_id,sale_id,qty)` index reduced warm work but left a 2.05 s fresh-process report, so it was not included in migration 26. The daily table matched the source query's 101 selected product averages, and full-source reconciliation found zero differences across 4,482,975 product/day rows before and after 30 new completed sales. The migration took 59.9 s on the full disposable copy and increased its main database by 113 MB, or 4.9%.

Five separate fresh-process 100-row service calls took 455.5, 230.3, 206.3, 231.5, and 223.3 ms. With four report readers and ten sale writers, eight broad Low Stock calls had p50 16.0 ms and p95 38.1 ms, with no errors. The first trigger version scanned the entire daily table on sale completion; product-scoped cleanup fixed this. After that correction, 19 warm commits ranged from 10.6 to 52.6 ms, generally 11-17 ms; the previous-schema comparison was generally 10-14 ms. The first call in both runs included cold startup work. Query plans now seek at most the requested day range using the `(product_id,day)` primary key. Evidence: `artifacts/phase6-lowstock-day-fixed-migrate.json`, `artifacts/phase6-lowstock-fixed-cold-*.json`, `artifacts/phase6-lowstock-day-fixed-report-profile.json`, `artifacts/phase6-lowstock-write-baseline.json`, `artifacts/phase6-lowstock-day-fixed-write.json`, and `artifacts/phase6-lowstock-day-fixed-verify-after-writes.json`. OS file cache was not flushed, so a physical-cold guarantee remains unmeasured.

A second schema-25 fixture with 20,000 products and 640,020 starting sales was 1.65 GB after migration. During the same 120-second four-reader/two-writer shape, its verified background backup took 44.9 seconds, 232 sales committed with zero errors, and sale-commit p95 was 12.3 ms. The 20-row Products FTS service p95 was 11.6 ms. Sampled WAL peaked at 40.4 MB and process working set at 175.9 MB. This passes the local `<60 s` backup gate at a database larger than the specified approximately 1 GB; target-PC confirmation remains open. Evidence: `artifacts/phase6-backup-1.46-migration.json`, `artifacts/phase6-backup-1.46-sustained.json`.

The final Products UI requests eight search results per page. A fresh 120-second full-store mixed run measured that service call at p95 6.7 ms, with zero reader/writer errors and a verified 2.31 GB backup. The WPF acceptance journey measured populated text-change-to-first-render-callback p95 80.8 ms across nine searches; UI Automation's cross-process observation was 157-186 ms. The same run recorded app-side login loading in 892 ms and shell loading 509 ms after sign-in. The callback is an app-side rendering proxy, not a measurement of pixels reaching a physical monitor. Evidence: `artifacts/phase6-final3-sustained-ui8.json`, the isolated test-store `performance-20261015.jsonl`, `phase2-acceptance-ui.json` and `phase6-ui-startup.json`.

The first hot-product ledger page had another optimizer issue: SQLite picked `ix_move_product_date` plus a sort despite the page's movement-ID ordering. A paired full-store trial showed about 550-650 ms warm with that plan versus about 1-6 ms using the existing `ix_move_product_id`; broad, old-date and type-filtered IDs and lot balances matched. The public service now specifies that index. Its fresh-process first page took 80.9 ms and warm p95 4.3 ms, with OS cache uncontrolled. Evidence: `artifacts/phase6-ledger-trial-v3.json`, `artifacts/phase6-final3-cold-warm-ui8.json`.

## Earlier schema-24 changes and measurements

| Path | Before | After on the same local fixture | Correctness evidence |
|---|---:|---:|---|
| Five-year P&L matrix | 29,019.6 ms against `v_pnl_entry` | 85.7 ms using `pnl_day` for sale/return entries | 742 populated source cells matched; partial-month 12-cell check matched. `artifacts/phase6-matrix.json`. |
| Selected fiscal Item Sales | 2,052.0 ms | 15.9 ms with direct product-index access | Populated selected report and CSV match the source path; query plan uses `ix_sale_line_product`. `artifacts/phase6-reports-v2.json`, `artifacts/phase6-reports-final.json`. |
| Low Stock, 100 rows | 4,144.9 ms | 341.7–362.9 ms after page selection before history sums | Focused low-stock row and truncation test. `artifacts/phase6-reports-final.json`. |
| Slow Movers, 100 rows | Prior Phase 0 broad p95 77.1 s; a Phase 6 legacy one-pass run was still executing after one minute | 1,657–1,696 ms using a recent-sale product set and exact last-sale lookups only on the result page | Recent, old, and never-sold fixture rows checked. `artifacts/phase6-reports-final.json`. |
| First product-ledger page | 2,272.9 ms fresh process; warm p95 645.5 ms | 596.3 ms fresh process; warm p95 517.2 ms | Page, cursor and type-filter balances match movement-prefix sums. `artifacts/phase6-cold-warm*.json`. |
| Selected Stock Ledger report, zero matching rows | 1,143.4 ms | 8.5 ms with direct lot-index access | Product/lot combinations and CSV match the broad result in a populated fixture. `artifacts/phase6-reports-complete.json`. |

A later P&L matrix repeat after the sustained sale run matched the same 742
source cells; its source-view query took 21.74 s and the projected service
47.4 ms. This is an OS-cache-uncontrolled repeat, not a paired physical-cold
comparison. Evidence: `artifacts/phase6-matrix-final.json`.

The first two report runs used the same 2.29 GB disposable store. The report
catalog pass called all 18 kinds with broad and selected filters, 36 shapes in
total, and recorded zero service and query-plan errors across 50 plans. Seven shapes returned zero rows, so
those calls do not prove populated output correctness. Existing integration
tests cover populated totals, returns, voids, CSV/Excel types and truncation;
the new tests cover the changed paths above. The report pass peaked at about
136 MiB process working set. The one-pass timings are not p95 latency.

The cold/warm pass found startup health first call 21.9 ms, recent sales history
warm p95 1.7 ms, month/FY P&L warm p95 2.2/3.5 ms and dashboard warm p95
290.4 ms. Broad catalog FTS search was 96.7 ms warm p95, above the `<30 ms`
query target; this is also close to the `<100 ms` search-to-visible target
before WPF layout is counted. These figures are service calls on VARK, not a
complete cold shell or cashier UI measurement.

The final solution test run passed 155 of 157 tests with two intentional opt-in UI
skips and no failures. The full-store `verify` run returned `integrity_check=ok`
and zero foreign-key failures, lot-cache mismatches, expired sale lines,
interrupted-write commits and dashboard day/top drift. Its integrity, foreign
key, lot-cache and expiry stages took 371.0, 26.0, 62.5 and 64.7 seconds in
this run. These stage timings vary markedly from the earlier local Phase 4
checks and are maintenance-window work, not startup-path targets. Evidence:
`artifacts/phase6-tests-final.log`, `artifacts/phase6-verify.json`.

A four-reader/two-writer mixed shape pass recorded 78 read/write shapes, zero
operation errors, zero query-plan errors and a 147 MiB process peak. Its
dashboard, FTS catalog query, Stock Ledger report and composite sale
add/read/complete p95s were 463, 164, 9 and 702 ms respectively. The composite
figure includes read queueing and is not bill-commit latency. Evidence:
`artifacts/phase6-shape-profile.json`.

A separate 120-second sustained run with four continuous readers, two sale
writers and a verified background backup completed 232 sales with no failures.
Sale add/commit/add-read-complete p95s were 10.2/12.6/135.4 ms; the sale read
alone reached 100.8 ms p95 under contention. Dashboard and catalog FTS p95s
were 260.6 and 89.0 ms. The sampled WAL peaked at 35.5 MiB and the process
working set at 166.7 MiB. The 2.29 GB backup took 126.8 seconds; the
approximately 1 GB `<60 s` target was not directly tested, and this run is
slower than the earlier 2.26 GB local backup. Sale counts reconciled exactly
from 868,764 to 868,996. Evidence: `artifacts/phase6-sustained.json`.

The opt-in WPF navigation journey passed on a reused full-size local store
after enabling its saved-bills picker handling. Products, Purchases, Stock,
Assets, Reports, Sales history, Billing, Dashboard and Money out all exposed
their target controls. Control discovery ranged from 232 ms (Dashboard) to
819 ms (Stock); the sampled application peak was 327 MiB. The test's settled
time includes a fixed 1.5-second wait and does not measure final monitor
paint. Evidence: `artifacts/phase6-ui-accepted.log` and the local test-store
`scale-audit-ui.json`.

A disposable-copy index trial added a sale-line covering index for fiscal
Category Sales. Three warm pre-index calls took 4.05–4.26 s; three after-index
calls took 2.71–2.72 s, and a separate report pass took 2.75 s. The plan used
the new covering index, but the index added 134,733,824 bytes to the main
database and the query still took nearly three seconds. It was not adopted in
production because its write cost was not isolated and the remaining latency
would require a different summary strategy. Evidence:
`artifacts/phase6-category-index-trial.json`,
`artifacts/phase6-category-index-reports.json`.
The approximately 2.43 GB disposable trial database remains at
`artifacts/data/phase6-category-index-trial/db/grocery.db`: automatic approval
review blocked the cleanup command.

## Open gates recorded at the schema-24 baseline

- Fiscal Category Sales still took 6.75 s, selected GST Summary about 2.6 s,
  and Slow Movers about 1.7 s in the final report pass. Broad fiscal
  aggregation still scans substantial history.
- Product FTS search, first ledger page, and some report paths do not meet a
  consistently fast interactive target on this PC. A one-pass run cannot
  establish p50/p95/p99 under sustained competing work.
- The intended store PC, physical scanner, receipt/label printer, A4 queue,
  cutter/drawer, full-disk and power-cut setup, second-PC restore, native
  Gujarati review and pilot are unavailable here. Scan-to-line `<50 ms`,
  receipt start `<1 s`, target-PC UI-ready time and backup on that PC remain
  unmeasured. Phase 5's software simulations do not substitute for them.

The schema-25 follow-up above supersedes the report latency findings in this
earlier baseline. Phase 6 remains **not signed off**. The current progress and
release decision are in `optimization-phases.md`; raw machine-readable runs
are under `artifacts/phase6-*`.

For a disposable copy of the same store, run `GroceryErp.ScaleAudit` with
`phase6-matrix`, `phase6-reports`, `cold-warm`, `shape-profile`,
`sustained-profile <output.json> 120`, and `verify`. The `phase6-reports` mode
captures one call per broad/selected report shape and fails if any service
call fails. The sustained mode writes sales and a backup, so it must run on a
disposable store copy.
