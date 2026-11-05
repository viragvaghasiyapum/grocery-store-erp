# Grocery ERP 1.2.0 release checklist

> [revised-implementation-plan.md](../revised-implementation-plan.md) §13.3 is authoritative for the release gates and their order. This checklist covers release 1.2.0 on database schema 41: everything after 1.1.0 (schema 37). That is the purchase entry update (schema 38), the operator feedback fixes, the detail panel, the trimmed Reports, the tax decisions of §15 (schema 39), the open-issue fixes in [revised-plan-issues.md](../revised-plan-issues.md), whose §15 fix added schema 40 (a deleted lot keeps its tax), and the review fixes in [revised-plan-bugs.md](../revised-plan-bugs.md), whose C-1 added schema 41 (five sales summaries nothing read are dropped). The plan's execution records hold the measurements behind each change. The 1.1.0 checklist and its evidence stay in [release-checklist-1.1.0.md](release-checklist-1.1.0.md); every gate it left open for hardware or people is open here too.

Record the operator, date, machine and evidence beside every item. Do not release with an unchecked
software item. Hardware, language and pilot items require a named human sign-off.

Software items marked done were verified on 5 and 6 November 2026 on the development PC (VARK, .NET 10.0.12,
SDK `10.0.401`). They are local evidence, not the target-PC sign-off.

## Release content

- [x] Release notes for 1.2.0 in English and Gujarati (`src/GroceryErp.App/CHANGELOG.en.md`, `.gu.md`) state schema 41 and are shown once after sign-in in the operator's language (`ReleaseNotesTests`), in a window that fits a 1366×768 screen at 100% and 150% (UI smoke test). `scripts/build-release.ps1` refuses to package a version the notes do not name. Screens of the running app: `artifacts/data/revision-issues-phase9/screens`
- [x] The packaged journey sees the notes on the 1.2.0 package's first sign-in (it now checks that they name the release; 1.1.0 never showed its notes). 5 November 2026: shown once, "Grocery ERP 1.2.0" … "Database schema: 41" (`artifacts/data/revised-plan-bugs/g4/screens/1-release-notes.png`)
- [x] Every item in `revised-plan-issues.md` is fixed, or deferred by the owner. The last two, §15 T-1 (the Products list's next-to-sell lot) and T-2 (a recreated customer-return lot's tax label), were fixed on 5 November 2026
- [ ] Owner review of the two expiry choices made in the Phase 8 fixes: blocked expired lots count in the Expired tile and badge, and the Expired tile opens Products at **Any status** so archived products' expired stock is listed
- [ ] Owner review of the Payables choice made in the B-1 fix (`revised-plan-bugs.md`, 5 November 2026): the tile counts inactive vendors' balances and opens Vendors at **Any status** with **With balance due**, so the balances listed add up to it. The alternative was to count active vendors only

## Automated release gate

- [x] `dotnet format GroceryErp.slnx --verify-no-changes --no-restore`
- [x] `dotnet build GroceryErp.slnx -c Release --no-restore` has zero warnings; `tools/GroceryErp.ScaleAudit` and `tools/GroceryErp.DataGen` build
- [x] `dotnet test GroceryErp.slnx -c Release --no-build`: zero failures; the packaged and large-store journeys are opt-in and listed below
- [x] Migration parity: a store left at each of schemas 1, 4, 15, 19, 25, 28, 29, 32, 33, 35, 36, 37 (release 1.1.0), 38, 39 and 40 upgrades to the schema of a new store with clean integrity and foreign keys (`UpgradeTests`)
- [x] Full-size rehearsal from 1.1.0: a schema-37 copy of the full-size store upgraded to 41, then the store checks after profiling writes: eight from schema 41, which dropped the last-sale and hourly summaries the other two checked. The fixture was taken to 37 by the 1.1.0 build; three copies went from 37 to 41 in 10.0–10.1 s (file cached). On the last, after the 1.2.0 profile's writes, the eight checks passed; then a bill killed before commit left nothing behind, a verified backup took 15.9 s, the restore into an empty data root took 18.6 s and passed the eight checks, and the original passed them again (`artifacts/data/revised-plan-bugs/g1-g2`). Migration 39 took 37 s from a cold disk in the earlier rehearsal (`artifacts/data/revision-tax-rehearsal`)
- [x] Same-machine before/after profile of 1.1.0 against 1.2.0 for every §13.1 hot path, with mixed readers, writers and a backup; no unexplained regression. Five `phase9-profile` runs per build, alternating (three right after copy and upgrade, two after a 120 s settle): under mixed load no errors or lot mismatches, bill commit p95 10.2–10.3 ms (1.1.0: 9.4–11.2), scan p95 9.8–12.3 ms, WAL ≤ 11.6 MB, process ≤ 145 MiB; full-history exports equal (Sales register 9.5–9.7 s, Item sales 10.0–10.1 s). Isolated writes depend on the store file's recent writes rather than the build (identical statements and triggers); reads are within 0.3 ms, the two differences explained in [docs/perf/1.2.0.md](perf/1.2.0.md)
- [x] `scripts/build-release.ps1 -Version 1.2.0` produced the setup, portable zip, full package and the 1.1.0 → 1.2.0 delta in `artifacts/Releases` (5 November 2026: 90.0, 82.5, 82.5 and 2.26 MB; the delta patches 19 files and removes 2). Built from the working tree with the uncommitted B-3 and B-4 fixes; rebuild after the commit, so the embedded commit matches and the packages include B-5 to B-7
- [x] With `GROCERY_PACKAGED_UI_TEST=1`, the packaged operator journey passes on the 1.2.0 portable build (5 November 2026, 30 s; screens and log in `artifacts/data/revised-plan-bugs/g4`). Run it again on the rebuilt packages
- [x] Large-store WPF journey on the published 1.2.0 build: the update window from schema 37, every page over three rounds, the detail panels, and a full-history Sales History export, with the working set recorded. 6 November 2026, on the release script's publish of the working tree: the update took 29.5 s with its verified backup; ten rounds with every panel and a bill's panel after the export held 267–285 MiB at each round's end, process peak 325 MiB; the export of 871,876 bills took 28.0 s. 1.1.0 on the same PC without panels: peak 303 MiB. The runs found B-5 to B-7 in `revised-plan-bugs.md`, including a panel visit that kept its page in memory (375 MiB after ten rounds before the fix). Evidence: `artifacts/data/revised-plan-bugs/g5`; tables in [docs/perf/1.2.0.md](perf/1.2.0.md)
- [ ] Release binaries code-signed (needs the owner's signing certificate)
- [ ] Clean-PC install and uninstall, and a USB update from 1.1.0, tested without internet access

## Upgrade package

- [ ] A 1.1.0 store shows the update window before sign-in, from schema 37 to 41, with migration 39 in the time estimate (22 s per GiB; migration 40 only adds a table, and 41 drops five unread summaries in under 0.1 s per GiB); nothing changes until **Update now**
- [ ] After the upgrade, the owner checks selling prices: in Regular mode a product saved excluding tax gained the tax a bill used to add, capped at MRP (migration 39). Composition and No-GST stores keep their prices
- [ ] Upgrade time and peak disk measured on the target shop PC against the window's estimate
- [ ] Pilot period: daily lot reconciliation, report snapshots, backup success and error logs reviewed before broad rollout

## Localization and UI

- [x] English/Gujarati resource keys, placeholders and English digits (`LocalizationTests`)
- [x] Table pages at 1366×768 with the footer at the bottom edge, and every page's accessible names (UI smoke test). The same journey drives the purchase form, the detail panels, the tax columns, the restore-product window, the operator messages and the expiry tiles; its records in the plan and in `revised-plan-issues.md` say which were captured in both languages
- [ ] 125% and 150% display scaling checked on the target PC; no clipping or raw `⟦key⟧` with `--pseudo`
- [ ] WPF memory on the reference and target PCs against the 350 MiB candidate gate (on the development PC, 6 November 2026: 1.2.0 peaked at 325 MiB over ten rounds with every detail panel and the full export, 1.1.0 at 303 MiB without panels)
- [ ] Keyboard-only sale, purchase (including **Bill rates: Include tax** and removing a line) and damage with the physical scanner
- [ ] Native Gujarati reviewer signed off every string marked `MT – needs review` (253 in `Strings.gu.resx`, against 150 at 1.1.0; the last, `Error_PrinterNotConfigured`, from B-4 in `revised-plan-bugs.md`), the 1.2.0 release notes (`CHANGELOG.gu.md` carries the marker) and the Gujarati guide
- [ ] Owner's check on the shop PC of the purchase form, the table footer, Billing's name list, the detail panels, the tax columns and the restore-product window
- [ ] Operator walkthrough on the target PC: create a product and vendor, post a purchase, scan and complete a sale, hold and complete a bill, return an item, view the dashboard, use Gujarati, lock/unlock, and close with a backup

## Store and hardware

None of these was signed off for 1.1.0; each stays open.

- [ ] Windows and India time zone/date correct; plugged-in sleep/hibernate disabled; UPS tested
- [ ] Data folder is outside OneDrive; BitLocker enabled where available
- [ ] Scanner test sheet passes EAN-13, UPC-A, scale label and 2D scans
- [ ] Receipt printer passes English/Gujarati, cutter and cash-drawer tests
- [ ] Interrupt printing before and after spooler acceptance; after restart, review each saved receipt/day-end/label job, confirm Ctrl+P retry and receipt DUPLICATE mark, and check that drawer pulses do not replay
- [ ] Label printer is calibrated and its labels scan from 30 cm
- [ ] Power-pull during a draft bill recovers the draft and every lot still reconciles to its source documents
- [ ] Verified backup and compaction timed on the target PC
- [ ] Backup exported to USB and restored on a second PC

## Business data and pilot

- [ ] Store/GST/FY settings, tax rates and their names (each purchase line, lot and bill line now saves the name, such as "GST 5%") and HSN data approved by the accountant
- [x] Owner approval of release 1.2.0 on schema 40 (migration 40, `deleted_lot_tax`, from the §15 T-2 fix), given 5 November 2026 over saving the name on every bill line or making no schema change
- [ ] Owner approval of release 1.2.0 on schema 41 (migration 41 from `revised-plan-bugs.md` C-1: five sales summaries that nothing has read since Reports was trimmed are dropped with their triggers, so a void no longer recalculates the last sale date of the bill's products). Including it in 1.2.0 rather than a later release was chosen during development on 5 November 2026; the owner's sign-off of the schema-40 approval above does not cover it
- [ ] Owner review of the recorded decisions (Phases 6, 8 and 9 execution records, §15 and the rulings in `revised-plan-issues.md`)
- [ ] Owner decision on day end after a later expense correction (P6-2; `revised-plan-bugs.md` O-4): day end shows a day as it is now known, so voiding or editing an expense later changes the cash expected for a day whose drawer was already counted. Keep that, or count corrections on the day they were made
- [ ] Products and opening stock imported; ten random counts and top 200 barcodes checked
- [ ] Vendors, opening balances, employees and assets checked
- [ ] Owner walkthrough completed; password recovery copy stored safely
- [ ] One-to-two-week parallel run reconciled daily; owner approved cut-over
