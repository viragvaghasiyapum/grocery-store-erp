# Grocery ERP 1.2.0 release checklist

> [revised-implementation-plan.md](../revised-implementation-plan.md) §13.3 is authoritative for the release gates and their order. This checklist covers release 1.2.0 on database schema 40: everything after 1.1.0 (schema 37). That is the purchase entry update (schema 38), the operator feedback fixes, the detail panel, the trimmed Reports, the tax decisions of §15 (schema 39) and the open-issue fixes in [revised-plan-issues.md](../revised-plan-issues.md), whose §15 fix added schema 40 (a deleted lot keeps its tax). The plan's execution records hold the measurements behind each change. The 1.1.0 checklist and its evidence stay in [release-checklist-1.1.0.md](release-checklist-1.1.0.md); every gate it left open for hardware or people is open here too.

Record the operator, date, machine and evidence beside every item. Do not release with an unchecked
software item. Hardware, language and pilot items require a named human sign-off.

Software items marked done were verified on 5 November 2026 on the development PC (VARK, .NET 10.0.12,
SDK `10.0.401`). They are local evidence, not the target-PC sign-off.

## Release content

- [x] Release notes for 1.2.0 in English and Gujarati (`src/GroceryErp.App/CHANGELOG.en.md`, `.gu.md`) state schema 40 and are shown once after sign-in in the operator's language (`ReleaseNotesTests`), in a window that fits a 1366×768 screen at 100% and 150% (UI smoke test). `scripts/build-release.ps1` refuses to package a version the notes do not name. Screens of the running app: `artifacts/data/revision-issues-phase9/screens`
- [ ] The packaged journey sees the notes on the 1.2.0 package's first sign-in (it now checks that they name the release; 1.1.0 never showed its notes)
- [x] Every item in `revised-plan-issues.md` is fixed, or deferred by the owner. The last two, §15 T-1 (the Products list's next-to-sell lot) and T-2 (a recreated customer-return lot's tax label), were fixed on 5 November 2026
- [ ] Owner review of the two expiry choices made in the Phase 8 fixes: blocked expired lots count in the Expired tile and badge, and the Expired tile opens Products at **Any status** so archived products' expired stock is listed

## Automated release gate

- [x] `dotnet format GroceryErp.slnx --verify-no-changes --no-restore`
- [x] `dotnet build GroceryErp.slnx -c Release --no-restore` has zero warnings; `tools/GroceryErp.ScaleAudit` and `tools/GroceryErp.DataGen` build
- [x] `dotnet test GroceryErp.slnx -c Release --no-build`: zero failures; the packaged and large-store journeys are opt-in and listed below
- [x] Migration parity: a store left at each of schemas 1, 4, 15, 19, 25, 28, 29, 32, 33, 35, 36, 37 (release 1.1.0), 38 and 39 upgrades to the schema of a new store with clean integrity and foreign keys (`UpgradeTests`)
- [ ] Full-size rehearsal from 1.1.0: a schema-37 copy of the full-size store upgraded to 40, then all ten store checks after profiling writes. Partial local evidence: the schema-36 fixture upgraded to 39 passed the ten checks after `phase9-profile`, and migration 39 took 11.1–13.1 s with the file cached and 37 s from a cold disk (`artifacts/data/revision-tax-rehearsal`)
- [ ] Same-machine before/after profile of 1.1.0 against 1.2.0 for every §13.1 hot path, with mixed readers, writers and a backup; no unexplained regression. Partial local evidence: schema 38 against 39 (`phase9-profile`) and the timings recorded with each open-issue fix
- [ ] `scripts/build-release.ps1 -Version 1.2.0` produced the setup, portable zip, full package and the 1.1.0 → 1.2.0 delta in `artifacts/Releases`
- [ ] With `GROCERY_PACKAGED_UI_TEST=1`, the packaged operator journey passes on the 1.2.0 portable build. It last ran on 1.1.0; its purchase step finds cells by column header since schema 38 and has not been run since
- [ ] Large-store WPF journey on the published 1.2.0 build: the update window from schema 37, every page over three rounds, the detail panels, and a full-history Sales History export, with the working set recorded
- [ ] Release binaries code-signed (needs the owner's signing certificate)
- [ ] Clean-PC install and uninstall, and a USB update from 1.1.0, tested without internet access

## Upgrade package

- [ ] A 1.1.0 store shows the update window before sign-in, from schema 37 to 40, with migration 39 in the time estimate (22 s per GiB; migration 40 only adds a table); nothing changes until **Update now**
- [ ] After the upgrade, the owner checks selling prices: in Regular mode a product saved excluding tax gained the tax a bill used to add, capped at MRP (migration 39). Composition and No-GST stores keep their prices
- [ ] Upgrade time and peak disk measured on the target shop PC against the window's estimate
- [ ] Pilot period: daily lot reconciliation, report snapshots, backup success and error logs reviewed before broad rollout

## Localization and UI

- [x] English/Gujarati resource keys, placeholders and English digits (`LocalizationTests`)
- [x] Table pages at 1366×768 with the footer at the bottom edge, and every page's accessible names (UI smoke test). The same journey drives the purchase form, the detail panels, the tax columns, the restore-product window, the operator messages and the expiry tiles; its records in the plan and in `revised-plan-issues.md` say which were captured in both languages
- [ ] 125% and 150% display scaling checked on the target PC; no clipping or raw `⟦key⟧` with `--pseudo`
- [ ] WPF memory on the reference and target PCs against the 350 MiB candidate gate (1.1.0 peaked at 296 MiB locally; not re-measured since the detail panel)
- [ ] Keyboard-only sale, purchase (including **Bill rates: Include tax** and removing a line) and damage with the physical scanner
- [ ] Native Gujarati reviewer signed off every string marked `MT – needs review` (252 in `Strings.gu.resx`, against 150 at 1.1.0), the 1.2.0 release notes (`CHANGELOG.gu.md` carries the marker) and the Gujarati guide
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
- [ ] Owner review of the recorded decisions (Phases 6, 8 and 9 execution records, §15 and the rulings in `revised-plan-issues.md`)
- [ ] Products and opening stock imported; ten random counts and top 200 barcodes checked
- [ ] Vendors, opening balances, employees and assets checked
- [ ] Owner walkthrough completed; password recovery copy stored safely
- [ ] One-to-two-week parallel run reconciled daily; owner approved cut-over
