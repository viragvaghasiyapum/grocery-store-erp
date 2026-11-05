# Revised plan: bugs and remaining work

Progress (6 November 2026):
- Fixed: B-1, C-1 (release 1.2.0 now on schema 41), D-1 to D-5, and B-2 to B-7, which were found while fixing.
  - B-3 was a High bug: the restore-product window could not open in the running app.
  - G-5 found B-5 to B-7. B-6 and B-7 were 1.2.0 regressions: page actions folded under **More** at any window width, and opening a bill or an asset's timeline kept that page in memory for the rest of the session. Over ten rounds the app reached 375 MiB against the 350 MiB gate; with the fix it stays at about 280 MiB.
- Release gates that run on the development PC: G-1 to G-6 are done. G-3 and G-4 must be repeated on packages rebuilt after the commit, since B-5 to B-7 changed the app after they ran.
- Drive D: was cleared before this work (199 GB free), so packaging was not blocked.
- The owner decisions remain open.

Verified on the current working tree, 6 November:
- `dotnet format --verify-no-changes` passed. The Release build of the solution had no warnings; ScaleAudit and DataGen have not changed since they built without warnings on 5 November.
- 327 tests ran, with 325 passed and the 2 opt-in journeys skipped. No test method was added for B-5 to B-7; their checks extend the UI smoke test, each shown to fail on the previous code. The large-store journey passed on its own (G-5).
- The earlier fixes were committed as 7ed8707 on `revised`.
- Not committed yet: the B-3 to B-7 fixes and their tests, the journey and tool changes (`ScaleUiTests`, `PackagedOperatorJourneyTests` screenshots, ScaleAudit `restore-setup`), the guides' text and four of their pictures, `docs/perf/1.2.0.md`, and the updates to this file, the checklist, the plan and CLAUDE.md.

Still to do, in order:
1. Commit the changes.
2. Rebuild the 1.2.0 packages with `scripts/build-release.ps1 -Version 1.2.0`, so the embedded commit matches (G-3), and rerun the packaged journey on the new portable build (G-4).
3. Retake the guide's Purchases page pictures (`purchases-page.en/.gu.png`) with the vendor and status lists loaded; they still show the folded **More** (B-6).
4. The human gates in `docs/release-checklist.md`, and the owner decisions below.

Status: review of `revised-implementation-plan.md` and `revised-plan-issues.md` against the code, 5 November 2026, release 1.2.0 on schema 40. Every item in `revised-plan-issues.md` is closed with a regression test; this file lists what the review found beyond them.

**Read** means the item was found by reading the code or documents and was not run. Nothing below was reproduced yet. Line numbers are as of commit f765a62.

When fixing an item, add a regression test, and where the behaviour is visible, update the plan's execution record and both user guides in the same change, as CLAUDE.md requires.

The gates that need the target PC, store hardware or people (WPF memory on the reference and target PCs, target-PC timings, second-PC restore, scanner, printers and power cut, native Gujarati review, code signing, the pilot) stay in `docs/release-checklist.md` and are not repeated here.

## Bugs

- [x] **B-1 The dashboard Payables tile counts vendors that the list it opens leaves out** (Low–Medium, read; reproduced by the new test on the previous filter)
  - The tile sums `balance_due` over every vendor in `v_vendor_balance`, archived ones included (`src/Modules/GroceryErp.Reports/ReportService.cs:79`).
  - Clicking it calls `VendorsPage.RequestBalanceFilter`, which opens Vendors with the status at index 0, **Active only**, and **With balance due** ticked (`src/GroceryErp.App/Presentation/VendorsPage.xaml.cs:81-82`; filter SQL at `PurchasingService.Lists.cs:354-355`).
  - Archiving a vendor does not check its balance (`RunVendorBulkBatchAsync`, `PurchasingService.Lists.cs:254`; the vendor form's `UpdateVendorAsync` likewise).
  - Result: an archived vendor the shop still owes is in the tile's total but not in the list. This is the same kind of mismatch P8-1 fixed for the expiry tiles.
  - The Reports-trimmed execution record says the Low stock and Payables tiles' new destinations were never clicked through, and no UI test covers them (`PhaseZeroSmokeTests.VerifyExpiryTiles` covers only the expiry tiles).
  - Fix: open Vendors at **Any status** with **With balance due**, as the Expired tile now opens Products at **Any status**; or count only active vendors in the tile. Add a test that the tile equals the list's balances, with an archived vendor holding a balance.
  - Also confirm the Low stock tile against the list it opens, in the same test.
  - **Fixed 5 Nov 2026.** The tile still counts every vendor's balance, since the shop still owes an inactive vendor, and now opens Vendors at **Any status** with **With balance due**, as Expired opens Products at **Any status**. Both read `v_vendor_balance`, and the filter keeps every nonzero balance (`balance_due<>0`), so the balances listed add up to the tile. No query or schema change, so no cost change.
    - Code: `VendorsPage.RequestBalanceFilter` sets status index 2 (Any status). The page applies a saved state through a new `ApplyState`, as `ProductsPage` does, so the test can apply the state a tile requests.
    - The Low stock tile already matched its list: both take active products whose `product_stock_total.is_low` is set. No change.
    - Test: `PhaseZeroSmokeTests.VerifyStockAndPayableTiles`, on a fresh store, in English and Gujarati. Vendors: an active one owed 1,500, an inactive one owed 2,500, and settled active and inactive ones. Products: low, not low, never stocked with a reorder level, no reorder level, and archived while low. It checks the tile against the list each one opens. On the previous filter it fails at the Vendors list (one row instead of two).
    - Running app (Release build, a DataGen store at scale 0.001 on schema 40; an inactive vendor owed ₹2,500, a settled inactive vendor, two active low products and an archived low product were added in SQL): the dashboard showed Payables ₹15,77,500.000 and **2 low stock**. Clicking Payables opened Vendors at **Any status** with **With balance due** and two rows, 2,500 (inactive) and 15,75,000 (active). Clicking Low stock opened the two active products. Driven by UI Automation (`artifacts/data/revised-plan-bugs/tiles-journey.ps1`).
    - Screens checked by eye: 6 from the smoke test and 3 from the running app, in `artifacts/data/revised-plan-bugs/b1-screens`.
    - Documents: both user guides (dashboard paragraph) and the plan's "Review after 1.2.0" execution record.

## Cleanup

- [x] **C-1 Five sales summaries are still written but nothing reads them** (Low, read; measured on the full-size store)
  - `sale_product_last`, `sale_product_day`, `sale_hour_day`, `sale_hour_return_day` and `sale_product_return_day` lost their last reader when Reports was trimmed to seven. Outside `tools/GroceryErp.ScaleAudit/Program.cs` and `ReportsTests.cs`, nothing names them.
  - Their triggers still run on every sale, return and void: `025_sale_report_day.sql:294-400`, `026_sale_product_day.sql`, `032_sales_return_lookup.sql:140`.
  - The costly one is the void: `trg_sale_product_last_void` (`025_sale_report_day.sql:308`) recomputes the last sale date over every sale line of the bill's products. P5-1 measured the same query shape (`trg_sale_product_last_date`) at 140–364 ms on the full-size store, all of it holding the writer.
  - The plan records this under "Left in place" (Reports trimmed), but it is not in `revised-plan-issues.md` or the release checklist.
  - Fix: migration 041 drops the five tables and their triggers. Update ScaleAudit's modes and `ReportsTests` that name them, give the step a rate in the update window's estimate, and measure void and completion on the full-size store before and after. Rewrite the release notes' schema line (`ReleaseNotesTests` will require it).
  - **Fixed 5 Nov 2026 (schema 41, in release 1.2.0 by the decision taken that day).** `041_drop_unread_sale_summaries.sql` drops the 19 triggers and then the five tables (`IF EXISTS`, so a fixture built from drafts still upgrades). The triggers go first because they sit on `sale`, `sale_line` and `sale_return` and would fail at their next run once their tables were gone. The `EmbeddedResource` line is added and `MigrationRunner.LatestVersion` is 41.
    - Checked on a schema-40 store that nothing else names the tables: no view, index or other trigger, and no C# in `src`.
    - Full-size store (copies of the preserved schema-36 fixture taken to 40; median of 15, alternating base, new, new, base). Void of a 1-line bill of the busiest product: 132–134 ms → 0.59 ms. Void of a 5-line bill: 343–345 ms → 0.57–0.60 ms. Complete: 0.84–1.05 ms → 0.67–0.70 ms. Return: 0.29 → 0.25 ms.
    - Migration 41 took 75–85 ms on 1.8 GB (three copies, file cached), within the estimate's 0.1 s per GiB for light steps; `MigrationPlan` records the measurement. It frees about 100 MB (24,426 pages), which the next compaction returns to the disk.
    - On the schema-41 copy, all eight remaining store checks passed (ScaleAudit `verify`).
    - ScaleAudit: `verify` runs the last-sale and hourly checks only before schema 41. `phase6-low-stock-day-verify` and `phase6-last-parity` refuse a schema-41 store with a message, and `phase5-sales-read` skips its refund-day check there.
    - Tests: `ReportsTests.SaleWrites_RunWithoutTheDroppedSummaries` replaces the `sale_product_day` test. `UpgradeTests` adds schema 40 to the parity cases, and `Migration41_DropsTheUnreadSaleSummaries_AndASaleCanStillBeVoided` drops tables that hold rows, then voids a sale from before the upgrade.
    - Documents: the release notes (both languages), the guides' schema line, CLAUDE.md, the release checklist (schema 41; eight store checks; the owner's approval of schema 41 added as an open item) and the plan's "Review after 1.2.0" record.
    - Evidence: `artifacts/data/revised-plan-bugs/c1` (README, timing script and results, migration and verify logs).

## Found while fixing

- [x] **B-2 DataGen and ScaleAudit's large-store fixture save assets without their initial term, so idle maintenance fails on every demo store** (Medium for verification, tools only; reproduced in the running app)
  - Found during G-6. On a DataGen store, idle maintenance failed in due depreciation: "Sequence contains no elements" in `AssetService.TermFor` (`AssetService.cs:350`), for every asset. Because that failure came with the weekly check's lot difference, the shell showed the general "A background safety check failed" notice with **Settings**, not the lot count with **Show differences**.
  - Cause: `tools/GroceryErp.DataGen/Program.cs` inserts assets straight into the latest schema without the `asset_term` row that `AssetService` always saves with a new asset ("INITIAL") and that migration 34 backfilled for older assets. `ScaleAudit`'s large-store UI fixture inserts 5,000 assets the same way.
  - Not an app bug: per the rule on suspect data, checked against what the app itself writes. No app path can save an asset without a term, and every store upgraded through 34 has them.
  - **Fixed 5 Nov 2026.** Both tools now add the INITIAL term with migration 34's backfill statement. A new DataGen store has none missing (it had 2 of 2 missing before). ScaleAudit and DataGen build without warnings.

- [x] **B-3 The restore-product window could not open from any path in the running app** (High; found by G-6, reproduced in the running app and by a new test)
  - Before it opens, `RestoreProductWindow.AskAsync` loads its choices through `InventoryService.GetRestoreOptionsAsync` (`RestoreProducts.Options`). Dapper builds a positional record only through a constructor whose parameters match the result columns exactly, and parameters with defaults do not count. `RestoreOption(long Id, string Label, long? RateScaled = null, bool AllowFraction = false, string? Code = null)` matched none of the three queries (two, five and three columns), so every call threw "A parameterless default constructor or one matching signature (System.Int64 Id, System.String Label) is required". The operator saw "Could not complete the action", and no customer return, damage correction or vendor-return cancel of a deleted product could be finished in the app.
  - Not caught before because nothing called `GetRestoreOptionsAsync`: the UI smoke journey builds the window with hand-made options, and the integration tests pass a choice straight to the services.
  - **Fixed 5 Nov 2026.** `RestoreProducts.Options` reads each row as a tuple and builds the option itself. Same three queries over small master tables, no schema change, no other caller.
  - Test: `SalesTests.RestoreOptions_ListActiveCategoriesAndTaxRates_AndEveryUnitWithItsCode` (active categories and tax rates only, every unit with its code and fraction rule, inactive rows left out). On the previous code it fails with the app's exception. All 28 restore tests pass.
  - Running app: see G-6. No document or guide described the failure, so neither changes.
- [x] **B-4 Printing with no receipt printer chosen showed the general failure message, with a doubled full stop** (Low; found by G-6 in the running app)
  - `ReceiptPrinter` threw `InvalidOperationException("A receipt printer has not been configured.")`, which a screen shows as "Could not complete the action. Check the details and try again. The error was logged." `Sales_PrintFailed` and `Sales_ReturnPrintFailed` add a full stop after a message that already ends with one ("…was logged.."). A cash bill also repeated the drawer's identical message after "; ".
  - **Fixed 5 Nov 2026.** `ErrorCode.PrinterNotConfigured`: "No receipt printer is chosen. Choose one in Settings." (Gujarati marked `MT – needs review`; 253 marked strings now). Both templates no longer add a full stop after the message, in both languages, and Billing does not repeat the drawer's message when it is the receipt's.
  - Test: `SalesTests.PrintingWithoutAReceiptPrinter_IsRefusedWithItsOwnMessage` (bill receipt, return receipt and both drawer calls; neither language's status line has ".."). Running app: the return's status line reads "Return R2627-000001 saved. Printing failed: No receipt printer is chosen. Choose one in Settings." (`g6-restore/screens/1d-sales-history-after-return.png`).
- [x] **B-5 The purchase panel had two columns headed "Tax"** (Low; found by G-5 in the running app)
  - The §15 wording change ("Tax" instead of "GST") turned both of the purchase lines' tax headings into "Tax" (કર): `Purchasing_Gst`, the rate's name ("GST 5%"), had read "GST %", and `Purchasing_TaxTotal`, the line's tax amount, had read "GST". In `PurchaseInvoiceView` they sit side by side, so the panel showed "Tax | Tax" (`g5/v120d-captures/scale-audit-ui-purchases-detail.png`). No other table shows both.
  - **Fixed 5 Nov 2026.** The rate's column there now takes the existing `Catalog_TaxRate`, "Tax rate" (કર દર), as on the product form, so no new string needs the Gujarati review. `Purchasing_Gst` keeps "Tax" elsewhere, since Billing's totals use it for the bill's tax amount. XAML only.
  - Test: the UI smoke test's purchase-panel step checks that every column of the lines has its own heading and one is the tax rate. On the previous XAML it fails at the distinct count.
  - Documents: both user guides (the purchase panel's columns).
- [x] **B-6 Sales history, Purchases and Assets folded their last actions behind More at any window width** (Medium–Low, a 1.2.0 regression; found by G-5 in the running app)
  - `ActionStrip` keeps a title row's actions on one line and moves the last ones under **More ▾** when they do not fit. For a title row it reserved the title's width, read as `ActualWidth` (`ActionStrip.TitleRow`).
  - At 1.1.0 the title row ended with the pager's status text, so the title kept its text width. The table-footer change (4cd88b4, 25 Oct) moved the pager into the footer. The title became the row's last child and stretched over the free space, so the reserve took in all the room the actions did not use.
  - Result: the actions never fit. The last one or two were under **More** at any width: Sales history's **Export CSV** on a 1938-px window (`g5/v120d-captures/scale-audit-ui-sales-history.png`), Assets' **Post depreciation now** and **Dispose**, Purchases' last two. Each later width change, such as a panel opening beside the list, could fold another.
  - Found because the G-5 journey's export step looks for **Export CSV** in the title row, and both runs stopped there.
  - **Fixed 5 Nov 2026.** `TitleRow` reserves the title's measured text width with its margin, the same measure the strip uses for its buttons. One measure of the title on each width change; nothing else changes.
  - Test: the UI smoke test's new `VerifyTitleActionsFold`, for Purchases, Assets and Sales history.
    - At 1600 px, and after five width changes between 1520 and 1600, nothing is under **More**.
    - At 640 px some are, and every action is either shown or under **More**. Back at 1600 all return.
    - On the previous code it fails at Purchases, 1600 px, with 2 actions under **More**.
  - Screens: the guide's Sales history and Assets pictures (both languages) showed the folded **More** and were replaced from the smoke test's captures, checked by eye. `purchases-page.en/.gu.png` shows it too, but the smoke test's capture of that page has no vendor or status list loaded, so the old picture stays until it is retaken. The guide text already says the last actions move under **More** only when the row is narrow.
- [x] **B-7 Opening a bill in Sales history or an asset's timeline kept that page in memory for the rest of the session** (Medium, a 1.2.0 regression; found by G-5's ten-round run)
  - G-5's three rounds with the detail panels peaked at 329 MiB, under the 350 MiB gate, but the working set at each round's end rose about 10 MiB a round, against 3–4 on 1.1.0.
  - Ten rounds on the full-size store took it from 270 to 350 MiB (process peak 375 MiB, 383 after the export). The managed heap left after full collections grew from 36 to 106 MiB. Without the panels it stayed at 29–36 MiB, as on 1.1.0.
  - Cause: `PagerBar` subscribes to the app's language source (`TranslationSource.Instance`, which lives for the whole session) on `Loaded` and unsubscribes on `Unloaded`. The pagers inside the Sales history bill panel (`DetailPager`) and the asset timeline (`TimelinePager`) are loaded with their page while the panel is collapsed. They are loaded again when the panel first opens and its body joins the visual tree, but unloaded only once. The second subscription stayed, so the language source held the pager, and through it the page and its tables.
  - Found with an in-process check that showed each page in one frame and then left it: Sales history and Assets stayed alive only after their panels had opened. A heap snapshot of that process (ClrMD, a scratch tool, nothing added to the repo) gave the path `TranslationSource` → `PropertyChanged` → `PagerBar` → page.
  - **Fixed 6 Nov 2026.** `PagerBar` removes its handler before adding it, as `LanguageRefresh` already did, so it holds at most one. `ActionStrip` and `FilterRow`, which use the same pattern and can sit in a panel (the product panel's Lots tab), do the same. No other change.
  - Test: the UI smoke test shows Products (also with its panel and with its Lots tab), Purchases, Vendors, Sales history and Assets, each with and without its panel, one after another in one frame as the shell does. Each must leave the language source's subscriber count as it found it. On the previous `PagerBar` it fails, naming "Sales history with its panel, Assets with its panel".
    - The check first asserted that each page is collected. With the fix, Assets was still held, by a UI Automation client elsewhere on this desktop (a reference-counted `ElementProxy` for a table header), which the app does not control. So the check counts subscriptions instead.
  - Running app: ten rounds with every panel on the build with the fix (G-5). Working set 267–285 MiB at each round's end, flat from round 3; managed heap 31–41 MiB; process peak 325 MiB, including the export.

## Release gates that need no shop hardware

`docs/release-checklist.md` says not to release with an unchecked software item. These can be run on the development PC. CLAUDE.md's "What remains are human gates" does not hold until they are done.

- [x] **G-1 Full-size upgrade from 1.1.0**: a schema-37 copy of the full-size store upgraded to 41, then the store checks after profiling writes: eight from schema 41, which dropped what the other two checked (checklist line 25). Rehearsed so far: 36→39, and for C-1, 36→40 (10.3 s) and 40→41 with the eight checks passing, without profiling writes.
  - **Done 5 Nov 2026.** The preserved schema-36 fixture was taken to 37 by the 1.1.0 build (commit 05b8f26, 18 ms), as a shop on 1.1.0 has it. Three copies of that store went from 37 to 41 with the 1.2.0 build in 10.0–10.1 s each (file cached, process peak 69 MiB).
  - On the last copy, after the 1.2.0 `phase9-profile` writes (300 bills with voids and returns, purchase posts and voids, damage, counts, vendor returns and cancels, a verified backup during the bills), all eight store checks passed: report-day and dashboard projections, integrity (45 s), foreign keys, every lot against its source documents (40 s at 73 MiB), sale lines after lot expiry and interrupted writes.
  - Then, on the same copy: a bill killed after its lot update and before commit left no sale rows, no foreign-key violations and no lot mismatches, and `quick_check` was ok. A verified backup took 15.9 s (542 MB zip). Restored into an empty data root it took 18.6 s, and the restored store passed the eight checks. The eight checks passed again on the original afterwards.
  - Evidence: `artifacts/data/revised-plan-bugs/g1-g2` (`run-ab.sh`, `migrate-*`, `verify-after-profile`, `crash*`, `backup`, `restore*`, `verify-final`). The store copies were deleted after the run.
- [x] **G-2 Same-machine profile of 1.1.0 against 1.2.0** for every §13.1 hot path, with mixed readers, writers and a backup (checklist line 25). Only schema 38 against 39 and the per-fix timings exist.
  - **Done 5 Nov 2026; no regression.** `phase9-profile` alternated 1.1.0 (commit 05b8f26, built in a scratch worktree) on schema 37 and 1.2.0 on its own 37→41 upgrade, each on a fresh copy of the full-size store at 37. There were three rounds right after the copy and upgrade, then two more where every copy first waited 120 s. Tables are in `docs/perf/1.2.0.md`; evidence in `artifacts/data/revised-plan-bugs/g1-g2`.
  - Mixed load (300 bills with voids and returns, four readers, back-office writes and a verified backup at once), all ten runs: no errors and no lot mismatches. Bill commit p95 was 9.4–11.2 ms on 1.1.0 and 10.2–10.3 ms on 1.2.0 (gate 100 ms); scan to bill line p95 was 10.1–13.8 and 9.8–12.3 ms (gate 50 ms). WAL peaks were 9–11 MB and process peaks 135–145 MiB in both.
  - Isolated reads and searches are all within 0.3 ms of 1.1.0 and inside their gates. Two small differences have causes:
    - Products searches are 0.05–0.1 ms slower. They run T-1's query, which prices each row from the lot a bill takes next. It visits a quarter of the rows (160 against 650 VM steps) but compiles a longer statement.
    - Opening a finalized year's P&L is 0.2–0.3 ms slower on identical SQL and C#. SQLite timed alone shows no difference (`bench-reads.mjs`): schema 41 even opens faster, 2.2 against 2.4 ms, with 126 triggers against 138. The remainder is in the process and was accepted at under a millisecond on a report opened by a click.
  - Isolated writes depended on what the store file had just been through, not on the build:
    - Profiled right after its 10 s upgrade, 1.2.0's back-office writes were 1–4 ms slower than 1.1.0's.
    - With both copies settled for 120 s, 1.2.0's writes were 1.5–4 times faster. For example, completing a bill took 2.4–3.0 ms against 7.4–8.2 ms, and a purchase void 1.0 ms against 3.9–4.1 ms.
    - The SQL trace shows the same statements and triggers in both builds for damage, counts, purchases and vendor returns, and fewer for sales after C-1 (completing a bill 97 statements against 112).
  - Full-history exports, settled and alternating: Sales register (872,312 bills) took 9.5–9.7 s and Item sales 10.0–10.1 s in both builds, peaking at 87 and 97 MiB. In the first pass, 1.1.0's Item sales took 36.8 s because it ran first on a cold file cache.
  - Upgrade 37→41 took 10.0–10.1 s on three copies, at a 69 MiB peak (see G-1).
- [x] **G-3 Build the 1.2.0 packages** with `scripts/build-release.ps1 -Version 1.2.0`: setup, portable zip, full package and the 1.1.0 → 1.2.0 delta (checklist line 26). `artifacts/Releases` holds only 1.1.0.
  - **Done 5 Nov 2026**, after the format gate and the full suite passed on the same tree (327 tests: 325 passed, the 2 opt-in journeys skipped). `artifacts/publish` was cleared first, because the script publishes over it and 1.1.0's files were still there.
  - The script ran in 65 s and produced `GroceryErp-win-Setup.exe` (90.0 MB), `GroceryErp-win-Portable.zip` (82.5 MB), `GroceryErp-1.2.0-full.nupkg` (82.5 MB) and `GroceryErp-1.2.0-delta.nupkg` (2.26 MB: 19 files patched, 291 unchanged, 2 removed). The portable package's `sq.version` and the executable's file version are 1.2.0.
  - Velopack reports 307 files unsigned, as at 1.1.0; signing needs the owner's certificate (checklist).
  - The build includes the uncommitted changes of 5 November (B-3, B-4 and the test and tool changes). Its embedded commit reads e7d71d8, the last commit, so rebuild once they are committed.
  - 1.1.0's portable zip and setup, which this build overwrites, were kept in `artifacts/Releases-1.1.0-portable` for the same-machine WPF comparison (G-5).
- [x] **G-4 Packaged operator journey on the 1.2.0 portable build** (`GROCERY_PACKAGED_UI_TEST=1`), including the release notes window on first sign-in (checklist lines 14 and 27). It last ran on 1.1.0, before the schema-38 purchase form, the table footer, the detail panel, the tax columns and the notes window.
  - **Passed 5 Nov 2026** in 30 s on the 1.2.0 portable zip, extracted to a temporary folder with an empty store and no network use.
  - The journey covers first-run setup, a product and vendor, a purchase whose cells are found by column header, every page, a cash sale, Gujarati, lock and unlock, and manual and close backups.
  - The release notes opened once on the first sign-in, named "Grocery ERP 1.2.0" and ended "Database schema: 41".
  - The test can now save a picture of each step to `GROCERY_PACKAGED_UI_SCREENS`. Its seven screens were checked by eye: notes, purchase form, billing, payment, dashboard in English and Gujarati, and backup. They and the log are in `artifacts/data/revised-plan-bugs/g4`.
- [x] **G-5 Large-store WPF journey on the published 1.2.0 build** (`ScaleUiTests`): the update window from schema 37, every page over three rounds, the detail panels and a full-history export, with the working set recorded (checklist lines 28 and 44). Memory was last measured on 1.1.0 (296 MiB peak), before the detail panel.
  - **Done 6 Nov 2026**, after it found B-5, B-6 and B-7. The final run is on the 1.2.0 build with all three fixed: the release script's own self-contained ReadyToRun publish of the working tree, version 1.2.0. The packages are rebuilt after the commit (G-3).
  - The runner is `artifacts/data/revised-plan-bugs/g5/run-journey.sh <root> <exe> <label> [details] [rounds]`. It takes a fresh copy of the schema-37 full-size store into `%TEMP%\grocery-erp-packaged-ui-*` and sets the login with the 1.1.0 build's ScaleAudit `ui-setup`. It runs the rounds, the export of all 871,876 bills and, with `details`, the detail panels. It keeps the JSON, logs and screens, then deletes the copy. `summarize.mjs` and `roundends.mjs` print the figures below.
  - Journey changes (`ScaleUiTests`; test code only):
    - `GROCERY_SCALE_UI_DETAILS=1` opens the first record's panel on each page as an operator does, waits for the **Next record** arrow, then samples memory and captures the screen. A damage opens when picked, an asset's timeline from **View timeline** (a double-click there edits the asset), other records on a double-click.
    - With panels on, after the export it presses **Search**, so the list shows the export's dates (Sales history starts on today, which has no bills in the fixture), and opens the first bill's panel.
    - The app is brought to the front before every navigation, and the Save As dialog before the path is typed. The journey's presses are not real input, so a window that came in front stayed there: one run stalled when this session's terminal covered the app.
    - The export step repeats a dropped press, as the page rounds do, and saves a screen if Sales history never shows.
  - Results on the development PC, each build on a fresh copy of the store (MiB unless marked):

    | | 1.1.0, no panels | 1.2.0, no panels | 1.2.0, panels, ten rounds |
    |---|---|---|---|
    | Update 37 → 41, with the verified backup | — | 29.3 s | 29.5 s |
    | Sign-in window after launch, update included / shell after sign-in | 4.0 s / 0.97 s | 31.0 s / 0.58 s | 32.8 s / 0.64 s |
    | Working set at each round's end | 249, 251, 255, 260 | 258, 259, 269, 271 | 267, 269, 285, 281, then 279–283 |
    | Highest private bytes | 188 | 208 | 214 |
    | Process peak, export included | 303 | 318 | 325 |
    | Page control found, after the first | 148–305 ms | 186–284 ms | 177–294 ms |
    | Panel shown | — | — | 329–503 ms; a bill after the export 281 ms |
    | Export of 871,876 bills | 25.0 s | 32.3 s | 28.0 s |

  - The update window read "data version 37 to 41", about 2 minutes, 3.62 GB needed of 58.08 GB free, and a verified backup first (`v120x10-captures/scale-audit-ui-upgrade.png`).
  - Every capture of the final run was checked to be the app, and the panels by eye: product, purchase (**Tax rate** and **Tax** headings, B-5), vendor ledger, asset timeline, damage and bill. Sales history shows **Export CSV** in its title row (B-6).
  - Export time varied 25.9–32.3 s across the 1.2.0 runs and was 25.0 s on 1.1.0, all far under its 15-minute limit; it was not profiled further.
  - Seen, not changed: the bill panel's Expiry column reads "2027-10-18" (`ExpiryDisplay`), unlike the other tables' "18 Oct 2026"; it was the same at 1.1.0. With the timeline open, Assets' narrower title row puts **Post depreciation now** under **More**, because it does not fit there (the smoke test checks this).
  - Runs kept under `g5`: `v120` (first run, stopped at Assets, a journey fault); `v120d` and `v120p` (stopped at the export: B-6); `v120f`, `v120g` and `v110` (passed; panels before B-7); `v120n` (stopped when the terminal covered the app) and `v120n2`; `v120t10` (ten rounds before B-7); `v120x10` (the final run). Two screens from `v120p` that showed another program's window instead of the app were deleted.
- [x] **G-6 Behaviour never tried in the running app**, each noted in its own record (all three tried by 5 Nov 2026):
  - [x] the weekly-check notice and **Show differences** (P6-1): tried in the running app on 5 Nov 2026.
    - Setup: a fresh DataGen store (schema 41, B-2 fixed) with one lot's quantity lowered by 5 in SQL, the only way to make a lot differ from its documents on fresh data. Release build, driven by UI Automation (`artifacts/data/revised-plan-bugs/weekly-journey.ps1`). Nothing was touched after sign-in.
    - Result: after exactly the 10-minute idle window the weekly check ran. The notice under the title bar read "The weekly stock check found 1 lots whose quantity differs from their documents." with **Show differences**. Clicking it opened Reports at **Purchased vs used reconciliation**, **Differences only**, listing that one lot (L0000001). Screen: `artifacts/data/revised-plan-bugs/g6-screens/app-weekly-notice-show-differences.png`.
    - The first attempt, on a DataGen store before the B-2 fix, showed the general "A background safety check failed" notice with **Settings** instead, because due depreciation failed in the same session (see B-2).
    - Wording seen on screen, not changed: with one lot the text says "1 lots" (`Error_LotsDifferFromDocuments`, English and Gujarati); a count-neutral wording would need the Gujarati review too.
  - [x] `RestoreProductWindow` from a return, a damage correction and a vendor-return cancel (P3-F3): tried in the running app on 5 Nov 2026. The first try found B-3, which stopped all three paths, and B-4; both are fixed. The run below is on the fixed build.
    - Setup on a fresh DataGen store (schema 41), through the app's own services (new `ScaleAudit <root> restore-setup`):
      - Bill S2627-000001 sold all three of a soap; the product was then deleted and added again under a new SKU (G6-SOAP-2).
      - Damage D2627-000001 wrote off all ten of a biscuit as spoiled; the product was then deleted.
      - Vendor return V2627-000001 sent back all eleven of an oil; the product was then deleted.
      - Each deletion went through `CatalogService.RemoveAsync`, and the schema-40 trigger kept each lot's tax in `deleted_lot_tax`.
    - Driven by UI Automation on the Release build (`artifacts/data/revised-plan-bugs/restore-journey.ps1`, run by `g6-restore/run.ps1`); 14 screens are in `g6-restore/screens`.
    - **Return** (Sales history → Return → Return all remaining → Confirm). The window listed the re-added G6-SOAP-2 under **Use an existing product**, and picking it set the new lot's price to that product's 46. The bill became Refunded. Lot 155 (customer-return origin) holds 3 at the booked 30.000, with **GST 5%** and tax 1.500 from the deleted lot (T-2). A `RESTORE_PRODUCT` audit entry names both products.
    - **Damage correction** (Money out → the damage → Edit, quantity 10 → 6). **Create a new product** came prefilled with SKU, name, local name and unit. Category, tax rate, HSN, track expiry and fractional quantity each said "Not saved on the original record. Check it."; track expiry was preset to Yes because the lot's expiry was saved.
      - A damage saves no selling price and its lot is gone, so the price was empty, and **Restore units** refused with "Selling price incl. tax: Required" until it was typed. Nothing is guessed, as designed.
      - The damage shows Corrected, 6 units. Lot 156 of the new G6-BISCUIT holds 4 at 20.000, with the saved expiry and the reason "Damage D2627-000001 correction restore: …".
    - **Vendor-return cancel** (Purchases → Open → Return history → Cancel return). The existing-product search found nothing ("No active product of the same type matches"). **Create a new product** came prefilled with GST 5%, prices 190 and 199 from the purchase, and the saved expiry, locked.
      - The return shows Cancelled. Lot 157 of the new G6-OIL holds 11 at the purchase's 150.000 plus tax 7.500. The CANCEL audit entry lists `restoredProducts`.
    - Afterwards, all eight store checks passed on that store (`g6-restore/verify.json`). Every lot reconciles to its documents.
  - [x] the Low stock and Payables tiles' destinations: clicked through in the running app on 5 Nov 2026 (see B-1).

## Document drift

- [x] **D-1 Plan §1, Customer returns row** (`revised-implementation-plan.md:267`) still says "recreate a deleted product when possible or prompt for missing legacy fields". Since P3-F3 the app always asks the operator to pick an active product of the same type and unit, or create one, and `ReturnRecreationWindow` is gone.
  - **Fixed 5 Nov 2026.** The row now says the new lot takes the booked cost and the tax the unit was bought at (T-2), and that for a deleted product the operator picks an active product of the same type whose unit takes the quantity, or creates one prefilled from the saved details (P3-F3). Checked against `RestoreProductWindow` and `SalesService.ReturnRecreation.cs`; `ReturnRecreationWindow` no longer exists.
- [x] **D-2 Plan §15 does not reflect T-2 or P3-F3**, though its status line says the section was updated to match.
  - **Fixed 5 Nov 2026.** §15.5 step 5 keeps what was planned and adds what was built: the cost-side tax per piece still comes from the sale line; the label and percent come from the sold lot, then `deleted_lot_tax`, then the purchase line (the order in `SoldLotRate`); `ReturnRecreationWindow` was removed by P3-F3 for `RestoreProductWindow`. §15.4 gains a **Schema 40** table for `deleted_lot_tax` and its triggers, and the §15 status line points to both.
  - §15.5 step 5 (`revised-implementation-plan.md:587`) says return recreation "takes the label and cost-side tax from the sale line" and that `ReturnRecreationWindow` stops asking about the setting. Since T-2 the label and percent come from the sold lot, then `deleted_lot_tax`, then the purchase line; the window was replaced by `RestoreProductWindow`.
  - §15.4 has no row for schema 40 (`deleted_lot_tax` and its trigger). Add one, or a short §15.9, so the schema is described outside the execution record.
- [x] **D-3 Plan §2, invariant 4** (`revised-implementation-plan.md:282`) requires any new lot created for a return to have "an explicit link to its source return". Lots recreated when a vendor return is cancelled (P3-3, P3-F3) are direct lots linked only by their reason text and the audit entry; damage-restore lots likewise. The optional link table was not built. Either relax the invariant to name the reason and audit trace, or record the exception.
  - **Fixed 5 Nov 2026 by recording the exception.** Correction to the item: damage-restore lots do have a link. `damage_correction.restored_lot_id` names a lot recreated by a correction (`MoneyOutService.Corrections.cs`), and a restore into the existing lot saves a `stock_correction` with `source_id`. A customer return's lot is linked by `sale_return_line.restock_lot_id`. Only a lot recreated by a vendor-return cancel has no link column: its `direct_reason` names the return ("Vendor return … cancelled: …") and the `vendor_return` CANCEL audit entry lists `restoredLots`. Invariant 4 now names the two links and records the vendor-return-cancel exception. Whether to build the link table instead is for the owner; nothing reads such a link today.
- [x] **D-4 Stale status lines**
  - **Fixed 5 Nov 2026.** The plan's status line says 5 November. `revised-plan-issues.md` opens by saying every item is closed and points here, and its suggested order is marked as done. Its P3-F3 row now sits after Phase 6, in date order (Phase 7, also 4 November, follows it, as in the plan's records). The "Phase 3 owner rulings" bullet says the restores *were* refused and points to the P3-F3 bullet.
  - `revised-implementation-plan.md:3` says "updated 26 October 2026"; the records run to 5 November.
  - `revised-plan-issues.md:3` says "review of the implemented revised plan on schema 39, 26 October 2026", and line 9's "Suggested order" no longer applies: every item is closed. Say so at the top.
  - In the same file's Progress table, the P3-F3 row (4 Nov) sits after the §15 row (5 Nov).
  - The plan's "Phase 3 owner rulings" bullet (`revised-implementation-plan.md:207`) still ends "until then those restores are refused"; point it to the P3-F3 bullet below.
- [x] **D-5 CLAUDE.md**
  - "~290 tests" in Commands; the last full run had 323.
  - "Status (26 Oct 2026)" introduces content dated 5 November.
  - "What remains are human gates" is not yet true; see G-1 to G-6.
  - **Fixed 5 Nov 2026.** "~325 tests", "Status (5 Nov 2026)", and the closing sentence now names this file and says the development-PC gates (G-1 to G-6) remain before the human gates.

## Owner decisions still open

- [ ] **O-1** The two expiry choices from the Phase 8 fixes: blocked expired lots count in the Expired tile and badge, and the Expired tile opens Products at **Any status** (checklist line 16).
- [ ] **O-2** After an upgrade from 1.1.0, the selling prices migration 39 converted for products saved excluding tax (checklist line 35).
- [ ] **O-3** Review of the recorded decisions in the Phase 6, 8 and 9 records, §15 and the rulings in `revised-plan-issues.md` (checklist line 68).
- [ ] **O-4 Day end of a past day changes after a later expense correction** (P6-2). Day end now shows a day "as it is now known", so voiding or editing an expense later changes the cash expected for a day whose drawer was already counted. The P6-2 record states this, but it is not on the owner's review list. Ask whether that is wanted, or whether day end should count corrections on the day they were made.
  - 5 Nov 2026: now on the owner's list in `docs/release-checklist.md` (Business data and pilot). The decision stays open.
- [ ] **O-5 The Payables choice in the B-1 fix**: the tile counts inactive vendors' balances and opens Vendors at **Any status**; the alternative was to count active vendors only. Added to `docs/release-checklist.md` beside the Phase 8 expiry choices.

## Accepted risks (owner rulings; no fix planned)

Recorded so they stay visible. Neither occurs on a fresh store, and the production store starts empty.

- [ ] **R-1 No in-app repair for a lot that differs from its documents** (P2-1 ruling). A count on such a lot is refused with `CountLotDiffersFromDocuments`, and the weekly notice returns every week. The only path is outside the app.
- [ ] **R-2 No shipped repair for a drifting open P&L day.** The weekly check records and reports it (P6-1); `ScaleAudit rebuild-pnl-days` is the only repair and does not ship.
