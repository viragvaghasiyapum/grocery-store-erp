# Phase 3 performance work — 14 October 2026

This is implementation evidence for Phase 3 in `optimization-phases.md`. The
workstation is VARK (24 logical processors, .NET 10.0.12, SQLite 3.53.3).
Measurements used a disposable schema-21 database with 5,189,072 sale lines,
5,392,253 stock movements, 202,500 lots, 203 vendors, and a 2,133,999,616-byte
main database. The process was new for each audit command; the OS cache was not
flushed. Times below are service or audit timings, not physical UI paint times.

## Implemented

- A durable clean-shutdown marker makes normal startup skip movement-history
  reconciliation. Interrupted sessions get a bounded recent-lot check and queue
  full reconciliation for the next idle maintenance window. Interrupted stock
  cache rebuilds leave a marker; the offline repair runs in 1,000-lot chunks,
  reports progress, and resumes safely by rechecking all lots. A verified backup
  precedes a new repair.
- Weekly integrity and daily backup work start after ten minutes of shell idle.
  New activity cancels the integrity read or backup and retries at the next idle
  window. The FTS write check now uses the writer gate. Backup uses bounded copy
  buffers, checks available disk before making a snapshot, and removes partial
  archives after cancellation. A recent final daily or close archive suppresses
  a repeat close backup after an app restart.
- Vendor purchase, payment, and return totals are maintained transactionally in
  `vendor_balance_total`. The picker, dashboard payable, and payable report no
  longer aggregate all historical vendor documents on every read.
- The expiry badge counts expired lots from a covering date index, then joins
  product/category thresholds only for the next seven days. Dashboard expiry
  counts exclude blocked lots and its components read one SQLite snapshot.
- The common current-window Item Sales and Category Sales reports reuse the
  transactional 30-day top-product projection, adding or subtracting at most a
  seven-day date edge. Other ranges retain the indexed source query. The
  pre-existing daily P&L projection and bounded Salary Register/Asset Schedule
  paths remain in use.

## Verification

| Full-size audit | Source | Changed path | Evidence |
|---|---:|---:|---|
| Expiry badge, five warm paired calls | 178–209 ms | 18–23 ms | All returned 164,766; view comparison had zero drift. |
| Item Sales, three warm paired calls | 860–980 ms | 188–196 ms | 101 source rows; populated return/prior-month parity test passed. |
| Category Sales, three warm paired calls | 1,309–1,402 ms | 689–761 ms | 81 source rows; populated comparison parity test passed. |
| Dashboard, five warm calls | — | 103–139 ms | Day and top projections reconciled with zero drift. |
| Startup health, process-new | — | 7.4 ms clean; 1.1 ms interrupted | Bounded check includes the latest 512 movements. |
| Vendor balance projection | Historical aggregates | 1–3 ms warm picker | Full fixture projection/source drift: zero vendors. |

The schema-20 vendor backfill took 264 ms; replacing the expiry index in
schema 21 took 1,511 ms on this copy. The 50,000-row Excel export took 11.7 s
and reopened with correct numeric cell types (238 MiB peak audit working set).
The 5,000-row A4 build produced 456 pages in 7.5 s. A full-size backup
canceled after 2.0 s left no archive or partial file; database `quick_check`
returned `ok` afterward. A verified backup of the 2.13 GB fixture took
243.8 s and produced an 818 MB archive while a Release build also ran; this
is a functional check, not an isolated backup throughput comparison. An
isolated repeat took 71.1 s with a 72 MiB peak working set. Stage timings
were 44.4 s for the SQLite snapshot, 15.8 s for integrity inspection,
1.2 s for snapshot hashing, 5.8 s for archive creation, and 3.6 s for
archive verification. The run had no cashier activity, so it does not
establish the target-store backup gate. The
Release solution build had zero warnings/errors. The final solution suite passed
143 tests, with two opt-in journeys skipped in that run.

Raw audit files are `artifacts/phase3-warm-paired.json`,
`artifacts/phase3-dashboard-reconcile.json`, `artifacts/phase3-export.json`,
`artifacts/phase3-a4.json`, `artifacts/phase3-backup-cancel.json`, and
`artifacts/phase3-backup.json`, and `artifacts/phase3-backup-profile.json`. The
artifacts directory is ignored by Git; rerun `GroceryErp.ScaleAudit` with
`phase3-read`, `dashboard-reconcile`, `export`, `a4-render`, or `backup-cancel`
against a disposable data root to reproduce them.

The populated schema-21 FlaUI journey also passed. Its automated ready
observations were 436 ms for Dashboard, 661 ms for month P&L, 610 ms for
fiscal P&L, and 1,186 ms for the first 50-row sales-history page; nine
matching product searches took 477–785 ms. UI Automation includes polling
and grid traversal. In-app reads in that run had p95 of 234 ms for the
dashboard snapshot, 12 ms for month P&L, 3 ms for fiscal P&L, 8 ms for
history, and 52 ms for catalog search. The app's refresh-to-first-frame
callback p95 was 403 ms for dashboard, 90 ms for month P&L, 61 ms for
fiscal P&L, 271 ms for history, and 476 ms for products. These callbacks
do not measure physical paint; the history and product UI paths remain
above their earlier visible-latency goals.

Full fixture verification passed dashboard day/top reconciliation, SQLite
integrity, foreign keys, lot-cache equality, sale-line expiry checks, and
interrupted-write consistency. The integrity scan took 123.8 s, foreign keys
4.3 s, lot reconciliation 18.0 s, and sale-line expiry 17.1 s. Results are in
`artifacts/phase3-verify.json`. A regression test covers close-backup
suppression after process restart as well as daily-backup suppression.

## Local resource and overlap checks

On the same fixture, a 120-second mixed service run completed 233 sales with
zero reader or writer errors while four readers continuously queried the
catalog, dashboard, expiry badge, history, ledger, and P&L, and a verified
backup ran. Dashboard p95 was 111 ms, month P&L p95 2.3 ms, sale commit p95
7.2 ms, and end-to-end sale p95 98.2 ms. The backup took 40.4 s with a warm OS
cache; peak sampled WAL was 15.6 MB and working set 173 MB. This is service
workload evidence, not physical UI paint or a target-PC result.

A separate held-reader trial kept an SQLite read transaction open during a
verified daily backup and 20 completed sales. The backup took 45.5 s; the
combined trial took 49.1 s. One-second samples observed a 2.03 GB temporary
database snapshot, an 818 MB partial archive, a 2.85 GB drop in free disk,
98 MiB peak working set, and 3.23 MB peak WAL. After the reader was released,
shutdown took 7.8 ms, including a 4.2 ms checkpoint; WAL shrank from 3.23 MB
to 4 KB. The clean marker was durable and the recent verified daily archive
prevented a duplicate close backup. Raw results are
`artifacts/phase3-mixed-load.json` and `artifacts/phase3-resource.json`.

The weekly integrity scan overlapped 20 further sales without an error. The
scan took 142.0 s; sale-commit p95 was 3.5 ms, with a 69.6 ms first cold
commit. This directly exercises database writes during maintenance; the app
normally waits for ten minutes of idle time and cancels on new activity.
The result is `artifacts/phase3-maintenance-overlap.json`. An injected
zero-free-space backup preflight returned `DiskFull` before creating a snapshot
or archive. A separate SQLite full-disk write test rejected the write without
committing a partial audit row. Both targeted tests passed; neither changes
the host volume's actual free space. After the 253 mixed and maintenance sales,
dashboard day and top-product source reconciliation still had zero drift
(`artifacts/phase3-postload-reconcile.json`).

## Remaining release measurements

The target-store PC, physical UI paint latency, and 1 GB daily backup target
have not been measured here. Exported/printed report formatting still needs a
visual review. Storage power-cut recovery remains a separate Phase 5 release
check. The long-range GST report still
groups indexed source rows; a new maintained GST summary was not justified by
the sampled 30-day 0.1–0.4 s warm service timings and its write-path cost would need
separate measurement. The reported timings do not sign off those release gates.
