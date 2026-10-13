# Phase 1 performance verification

Run from the repository root. Use disposable copies of the Phase 0 fixtures; the read audit, stock count creation, migration, and UI setup may change their databases. The original seed databases were left intact.

## Changes and checks

Migration 014 adds customer, draft count, and posted vendor purchase indexes. Migration 015 backfills and maintains a low-stock flag on `product_stock_total`, including products with no lots. Catalog and overview read the projection; a broad low-stock catalog page follows the product name index and probes the partial low-stock index. `phase1-read` checks every product's projection against its reorder threshold, traverses all low-stock pages without duplicate or missing IDs, and captures the relevant query plans.

Interactive screens use bounded keyset pages: catalog, overview, lots, movement ledger, counts, expiry, damage and expense history, purchase history, vendor ledger and lots, held sales, assets, labels and the other broad pickers. Selected records use direct ID reads. Vendor ledger tabs page each transaction type independently while showing the balance across all types. Customer text search is capped and phone prefix search uses a range; the UI debounces requests.

Stock count pages contain 100 lines, draft summaries 50 headers, and review uses SQL aggregates. Posting checks the movement high-water mark for scoped lots. The print button explicitly prints the current page. Reports display at most 500 rows; full CSV export uses an unbuffered reader, reports progress, honors cancellation, and renames a completed temporary file. Sales History has a separate streaming full CSV export. Asset schedule preview reads at most 501 posted rows and stops projecting after the visible cap; its full CSV export streams the entire schedule with a running fiscal charge. Sales History reads same-date cursor pages by indexed ID and older dates separately, keeping deep pages seekable.

## Reproduce

```powershell
dotnet test GroceryErp.slnx -c Release --no-restore --verbosity quiet
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase1-strong phase1-read artifacts/data/phase1-strong-low-read-verified.json
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase1-strong phase1-export artifacts/data/phase1-strong-export.json
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase1-whole-count phase1-count artifacts/data/phase1-whole-count-audit.json
```

The listed audit directories must be copied from the corresponding preserved Phase 0 fixtures and migrated to schema 15 before use. `phase1-count` creates a draft on its copy. For the opt-in FlaUI run, prepare a copy beneath `%TEMP%\grocery-erp-packaged-ui-...`, run `ui-setup`, set `GROCERY_SCALE_UI_ROOT` and `GROCERY_SCALE_UI_EXE`, and run `dotnet test tests/GroceryErp.UI.Tests/GroceryErp.UI.Tests.csproj -c Release --filter 'FullyQualifiedName~ScaleUiTests.LargeStore_ModuleNavigationRemainsResponsive'`. The UI test writes `scale-audit-ui.json` to that copy.

## Observed results, 12 October 2026

The final Release solution gate passed 116 tests with two opt-in UI skips; the full-size FlaUI journey was run separately. Integration coverage exercises all 18 report CSV kinds, numeric CSV cells, full output beyond the interactive cap, cursor reachability and ordering, vendor balances, stock count review changes, and low-stock trigger transitions. Later checks include a 532-row asset schedule CSV against a capped 500-row preview, matching the first and 500th numeric rows.

On the migrated 1.35× copy with 27,000 products, 202,500 lots, 864,180 sales, 5,185,317 sale lines and 5,388,498 movements, a warm 100-row catalog page took 2.5 ms, a populated 100-row low-stock page 7.3 ms, and a 100-row expiry page 15.0 ms. All 2,700 low-stock products were reached in 162 ms without duplicates or gaps; the projection check found zero mismatches. The broad low-stock query previously took 139 ms on an empty-result v14 copy; after migration 015 its empty-result page took about 3 ms. These are isolated single-process samples, not p95.

The Sales Register full CSV exported 864,180 rows in 14.8 seconds and produced a 69,936,179-byte file. Its row count and absence of partial files were checked. The audit process peaked at 86,614,016 bytes (82.6 MiB). This run used schema 14 before the low-stock migration, which does not change report SQL. The 150,001-line count copy returned its first 100 lines in 67 ms and its review aggregate in 125 ms, with a 73,773,056-byte (70.4 MiB) process peak.

The 10% and full-size FlaUI journeys visited Products, Purchases, Stock, Assets, Reports, Sales History, Billing, Dashboard and Money out. The initial full-size run saw Money out's control in 185 ms and a 557,531,136-byte (531.7 MiB) peak; an earlier full-size repetition peaked at 569,806,848 bytes (543.4 MiB). No navigation stall occurred. UI control discovery is earlier than render completion; the recorded settled time includes a fixed 1.5-second wait and does not measure monitor presentation. Process-cold audits did not clear the OS file cache. Sustained mixed-load p95 remains later-phase work.

## Five-million-sale export, 13 October 2026

A disposable 1.1 GB fixture was extended to exactly 5,000,000 sales. The 4,936,000 added sales share one date and have no lines or payments, so this validates history paging, CSV row count, UI responsiveness and memory rather than realistic sale-line joins. The packaged-app FlaUI journey used the Save As dialog and exported all dates to CSV. The 5,000,000 data rows were counted from the final 264,312,897-byte file, with no remaining partial file. The final run took 21.2 seconds from Export click through file rename and peaked at 705,163,264 bytes (672.5 MiB) of app working set. It also checked the first data row's invoice and numeric total. A same-date run exported 4,936,000 rows in 20.0 seconds, with a 645,525,504-byte (615.6 MiB) peak. The database can change during a multi-page export, so its row count is a snapshot only when writes are paused, as they were in this fixture.

The deep-page audit found that tuple cursors combined with optional date predicates made SQLite scan earlier rows. The revised same-date ID seek returned a middle page in 5 ms and an older page in 3 ms, versus 355 ms and 686 ms before the change. The general cursor path now fetches the remainder of the cursor date and then seeks older dates. The audit on the five-million-sale fixture returned the middle unbounded page in 2.9 ms and the date-transition page in 57.1 ms. The audit samples are single calls, not sustained p95.

The final full-size FlaUI page run recorded 100 grid row items each for Products, Purchases and Stock, four for Assets, and zero for the other pages with their default filters on this fixture. It opened and dismissed the draft-count picker in 224 ms and the vendor-payment purchase picker in 241 ms. Both dialog lists were empty with the fixture's current selections; integration tests cover populated picker paging and old-record reachability. The UI run peaked at 531,464,192 bytes (506.8 MiB). Set `GROCERY_SCALE_UI_DIALOGS=1` to repeat the dialog checks; their results are saved as `scale-audit-dialogs.json`.

To reproduce the full UI export, create a disposable `grocery-erp-packaged-ui-...` root under `%TEMP%`, migrate it to schema 15, run `phase1-history-seed` and `ui-setup`, then set `GROCERY_SCALE_UI_ROOT`, `GROCERY_SCALE_UI_EXE`, `GROCERY_SCALE_UI_EXPORT=1`, `GROCERY_SCALE_UI_EXPORT_FROM_DATE=1900-01-01`, `GROCERY_SCALE_UI_EXPORT_TO_DATE=2026-10-12`, and `GROCERY_SCALE_UI_EXPECT_EXPORT_ROWS=5000000`. Run the opt-in FlaUI test above. It writes `scale-audit-history-export.json` and `scale-audit-ui.json` into that root. The seed date is specific to this saved fixture; use its maximum sale date when reseeding on another day.
