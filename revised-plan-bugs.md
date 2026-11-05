# Revised plan: bugs and remaining work

Status: review of `revised-implementation-plan.md` and `revised-plan-issues.md` against the code, 5 November 2026, release 1.2.0 on schema 40. Every item in `revised-plan-issues.md` is closed with a regression test; this file lists what the review found beyond them.

**Read** means the item was found by reading the code or documents and was not run. Nothing below was reproduced yet. Line numbers are as of commit f765a62.

When fixing an item, add a regression test, and where the behaviour is visible, update the plan's execution record and both user guides in the same change, as CLAUDE.md requires.

The gates that need the target PC, store hardware or people (WPF memory on the reference and target PCs, target-PC timings, second-PC restore, scanner, printers and power cut, native Gujarati review, code signing, the pilot) stay in `docs/release-checklist.md` and are not repeated here.

## Bugs

- [ ] **B-1 The dashboard Payables tile counts vendors that the list it opens leaves out** (Low–Medium, read)
  - The tile sums `balance_due` over every vendor in `v_vendor_balance`, archived ones included (`src/Modules/GroceryErp.Reports/ReportService.cs:79`).
  - Clicking it calls `VendorsPage.RequestBalanceFilter`, which opens Vendors with the status at index 0, **Active only**, and **With balance due** ticked (`src/GroceryErp.App/Presentation/VendorsPage.xaml.cs:81-82`; filter SQL at `PurchasingService.Lists.cs:354-355`).
  - Archiving a vendor does not check its balance (`RunVendorBulkBatchAsync`, `PurchasingService.Lists.cs:254`; the vendor form's `UpdateVendorAsync` likewise).
  - Result: an archived vendor the shop still owes is in the tile's total but not in the list. This is the same kind of mismatch P8-1 fixed for the expiry tiles.
  - The Reports-trimmed execution record says the Low stock and Payables tiles' new destinations were never clicked through, and no UI test covers them (`PhaseZeroSmokeTests.VerifyExpiryTiles` covers only the expiry tiles).
  - Fix: open Vendors at **Any status** with **With balance due**, as the Expired tile now opens Products at **Any status**; or count only active vendors in the tile. Add a test that the tile equals the list's balances, with an archived vendor holding a balance.
  - Also confirm the Low stock tile against the list it opens, in the same test.

## Cleanup

- [ ] **C-1 Five sales summaries are still written but nothing reads them** (Low, read)
  - `sale_product_last`, `sale_product_day`, `sale_hour_day`, `sale_hour_return_day` and `sale_product_return_day` lost their last reader when Reports was trimmed to seven. Outside `tools/GroceryErp.ScaleAudit/Program.cs` and `ReportsTests.cs`, nothing names them.
  - Their triggers still run on every sale, return and void: `025_sale_report_day.sql:294-400`, `026_sale_product_day.sql`, `032_sales_return_lookup.sql:140`.
  - The costly one is the void: `trg_sale_product_last_void` (`025_sale_report_day.sql:308`) recomputes the last sale date over every sale line of the bill's products. P5-1 measured the same query shape (`trg_sale_product_last_date`) at 140–364 ms on the full-size store, all of it holding the writer.
  - The plan records this under "Left in place" (Reports trimmed), but it is not in `revised-plan-issues.md` or the release checklist.
  - Fix: migration 041 drops the five tables and their triggers. Update ScaleAudit's modes and `ReportsTests` that name them, give the step a rate in the update window's estimate, and measure void and completion on the full-size store before and after. Rewrite the release notes' schema line (`ReleaseNotesTests` will require it).

## Release gates that need no shop hardware

`docs/release-checklist.md` says not to release with an unchecked software item. These can be run on the development PC. CLAUDE.md's "What remains are human gates" does not hold until they are done.

- [ ] **G-1 Full-size upgrade from 1.1.0**: a schema-37 copy of the full-size store upgraded to 40, then the ten store checks after profiling writes (checklist line 24). Only schema 36→39 has been rehearsed.
- [ ] **G-2 Same-machine profile of 1.1.0 against 1.2.0** for every §13.1 hot path, with mixed readers, writers and a backup (checklist line 25). Only schema 38 against 39 and the per-fix timings exist.
- [ ] **G-3 Build the 1.2.0 packages** with `scripts/build-release.ps1 -Version 1.2.0`: setup, portable zip, full package and the 1.1.0 → 1.2.0 delta (checklist line 26). `artifacts/Releases` holds only 1.1.0.
- [ ] **G-4 Packaged operator journey on the 1.2.0 portable build** (`GROCERY_PACKAGED_UI_TEST=1`), including the release notes window on first sign-in (checklist lines 14 and 27). It last ran on 1.1.0, before the schema-38 purchase form, the table footer, the detail panel, the tax columns and the notes window.
- [ ] **G-5 Large-store WPF journey on the published 1.2.0 build** (`ScaleUiTests`): the update window from schema 37, every page over three rounds, the detail panels and a full-history export, with the working set recorded (checklist lines 28 and 44). Memory was last measured on 1.1.0 (296 MiB peak), before the detail panel.
- [ ] **G-6 Behaviour never tried in the running app**, each noted in its own record:
  - the weekly-check notice and **Show differences** (P6-1);
  - `RestoreProductWindow` from a return, a damage correction and a vendor-return cancel (P3-F3);
  - the Low stock and Payables tiles' destinations (see B-1).

## Document drift

- [ ] **D-1 Plan §1, Customer returns row** (`revised-implementation-plan.md:267`) still says "recreate a deleted product when possible or prompt for missing legacy fields". Since P3-F3 the app always asks the operator to pick an active product of the same type and unit, or create one, and `ReturnRecreationWindow` is gone.
- [ ] **D-2 Plan §15 does not reflect T-2 or P3-F3**, though its status line says the section was updated to match.
  - §15.5 step 5 (`revised-implementation-plan.md:587`) says return recreation "takes the label and cost-side tax from the sale line" and that `ReturnRecreationWindow` stops asking about the setting. Since T-2 the label and percent come from the sold lot, then `deleted_lot_tax`, then the purchase line; the window was replaced by `RestoreProductWindow`.
  - §15.4 has no row for schema 40 (`deleted_lot_tax` and its trigger). Add one, or a short §15.9, so the schema is described outside the execution record.
- [ ] **D-3 Plan §2, invariant 4** (`revised-implementation-plan.md:282`) requires any new lot created for a return to have "an explicit link to its source return". Lots recreated when a vendor return is cancelled (P3-3, P3-F3) are direct lots linked only by their reason text and the audit entry; damage-restore lots likewise. The optional link table was not built. Either relax the invariant to name the reason and audit trace, or record the exception.
- [ ] **D-4 Stale status lines**
  - `revised-implementation-plan.md:3` says "updated 26 October 2026"; the records run to 5 November.
  - `revised-plan-issues.md:3` says "review of the implemented revised plan on schema 39, 26 October 2026", and line 9's "Suggested order" no longer applies: every item is closed. Say so at the top.
  - In the same file's Progress table, the P3-F3 row (4 Nov) sits after the §15 row (5 Nov).
  - The plan's "Phase 3 owner rulings" bullet (`revised-implementation-plan.md:207`) still ends "until then those restores are refused"; point it to the P3-F3 bullet below.
- [ ] **D-5 CLAUDE.md**
  - "~290 tests" in Commands; the last full run had 323.
  - "Status (26 Oct 2026)" introduces content dated 5 November.
  - "What remains are human gates" is not yet true; see G-1 to G-6.

## Owner decisions still open

- [ ] **O-1** The two expiry choices from the Phase 8 fixes: blocked expired lots count in the Expired tile and badge, and the Expired tile opens Products at **Any status** (checklist line 16).
- [ ] **O-2** After an upgrade from 1.1.0, the selling prices migration 39 converted for products saved excluding tax (checklist line 35).
- [ ] **O-3** Review of the recorded decisions in the Phase 6, 8 and 9 records, §15 and the rulings in `revised-plan-issues.md` (checklist line 68).
- [ ] **O-4 Day end of a past day changes after a later expense correction** (P6-2). Day end now shows a day "as it is now known", so voiding or editing an expense later changes the cash expected for a day whose drawer was already counted. The P6-2 record states this, but it is not on the owner's review list. Ask whether that is wanted, or whether day end should count corrections on the day they were made.

## Accepted risks (owner rulings; no fix planned)

Recorded so they stay visible. Neither occurs on a fresh store, and the production store starts empty.

- [ ] **R-1 No in-app repair for a lot that differs from its documents** (P2-1 ruling). A count on such a lot is refused with `CountLotDiffersFromDocuments`, and the weekly notice returns every week. The only path is outside the app.
- [ ] **R-2 No shipped repair for a drifting open P&L day.** The weekly check records and reports it (P6-1); `ScaleAudit rebuild-pnl-days` is the only repair and does not ship.
