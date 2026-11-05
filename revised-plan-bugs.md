# Revised plan: bugs and remaining work

Progress (5 November 2026): B-1, C-1 (release 1.2.0 now on schema 41), D-1 to D-5 and the found B-2 are fixed. Of G-6, the tiles and the weekly-check notice were tried in the running app; `RestoreProductWindow` from the three restore paths was not. G-1 to G-5 were deferred on 5 November 2026, and drive D: is nearly full, which packaging (G-3, G-4) needs cleared first. The owner decisions remain open.

Verified after these fixes: `dotnet format --verify-no-changes` passed; Release builds of the solution, ScaleAudit and DataGen had no warnings; 325 tests ran: 278 integration (276 before), 32 core, 2 architecture, 13 UI, with 323 passed and the 2 opt-in journeys skipped. Committed as 7ed8707 on `revised`.

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

## Release gates that need no shop hardware

`docs/release-checklist.md` says not to release with an unchecked software item. These can be run on the development PC. CLAUDE.md's "What remains are human gates" does not hold until they are done.

- [ ] **G-1 Full-size upgrade from 1.1.0**: a schema-37 copy of the full-size store upgraded to 41, then the store checks after profiling writes: eight from schema 41, which dropped what the other two checked (checklist line 25). Rehearsed so far: 36→39, and for C-1, 36→40 (10.3 s) and 40→41 with the eight checks passing, without profiling writes.
- [ ] **G-2 Same-machine profile of 1.1.0 against 1.2.0** for every §13.1 hot path, with mixed readers, writers and a backup (checklist line 25). Only schema 38 against 39 and the per-fix timings exist.
- [ ] **G-3 Build the 1.2.0 packages** with `scripts/build-release.ps1 -Version 1.2.0`: setup, portable zip, full package and the 1.1.0 → 1.2.0 delta (checklist line 26). `artifacts/Releases` holds only 1.1.0.
- [ ] **G-4 Packaged operator journey on the 1.2.0 portable build** (`GROCERY_PACKAGED_UI_TEST=1`), including the release notes window on first sign-in (checklist lines 14 and 27). It last ran on 1.1.0, before the schema-38 purchase form, the table footer, the detail panel, the tax columns and the notes window.
- [ ] **G-5 Large-store WPF journey on the published 1.2.0 build** (`ScaleUiTests`): the update window from schema 37, every page over three rounds, the detail panels and a full-history export, with the working set recorded (checklist lines 28 and 44). Memory was last measured on 1.1.0 (296 MiB peak), before the detail panel.
- [ ] **G-6 Behaviour never tried in the running app**, each noted in its own record:
  - [x] the weekly-check notice and **Show differences** (P6-1): tried in the running app on 5 Nov 2026.
    - Setup: a fresh DataGen store (schema 41, B-2 fixed) with one lot's quantity lowered by 5 in SQL, the only way to make a lot differ from its documents on fresh data. Release build, driven by UI Automation (`artifacts/data/revised-plan-bugs/weekly-journey.ps1`). Nothing was touched after sign-in.
    - Result: after exactly the 10-minute idle window the weekly check ran. The notice under the title bar read "The weekly stock check found 1 lots whose quantity differs from their documents." with **Show differences**. Clicking it opened Reports at **Purchased vs used reconciliation**, **Differences only**, listing that one lot (L0000001). Screen: `artifacts/data/revised-plan-bugs/g6-screens/app-weekly-notice-show-differences.png`.
    - The first attempt, on a DataGen store before the B-2 fix, showed the general "A background safety check failed" notice with **Settings** instead, because due depreciation failed in the same session (see B-2).
    - Wording seen on screen, not changed: with one lot the text says "1 lots" (`Error_LotsDifferFromDocuments`, English and Gujarati); a count-neutral wording would need the Gujarati review too.
  - [ ] `RestoreProductWindow` from a return, a damage correction and a vendor-return cancel (P3-F3): not yet tried in the running app.
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
