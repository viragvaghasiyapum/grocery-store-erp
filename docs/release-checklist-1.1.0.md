# Grocery ERP 1.1.0 release checklist

> This is the record of release 1.1.0. The next release's checklist is [release-checklist.md](release-checklist.md); the gates left open here are carried into it.
>
> [revised-implementation-plan.md](../revised-implementation-plan.md) §13.3 is authoritative for the release gates and their order; its Phase 9 execution record holds the measurements behind each checked item, and [docs/perf/1.1.0.md](perf/1.1.0.md) the performance results. This checklist covers release 1.1.0 on database schema 37.

Record the operator, date, machine and evidence beside every item. Do not release with an unchecked
software item. Hardware, language and pilot items require a named human sign-off.

Software items marked done were verified on 25 October 2026 on the development PC (VARK, 24 logical
processors, .NET 10.0.12, SQLite 3.53.3) with SDK `10.0.401`. They are local evidence, not the
target-PC sign-off. Full-size evidence is under `artifacts/data/revision-phase9-rehearsal`.

## Automated release gate

- [x] `dotnet format GroceryErp.slnx --verify-no-changes --no-restore`
- [x] `dotnet build GroceryErp.slnx -c Release --no-restore` has zero warnings; `tools/GroceryErp.ScaleAudit` builds
- [x] `dotnet test GroceryErp.slnx -c Release --no-build`: zero failures; the packaged and large-store journeys are opt-in and were run separately below
- [x] Migration parity: a store left at each of schemas 1, 4, 15, 19, 25, 28, 29, 32, 33, 35 and 36 upgrades to the schema of a new store with clean integrity and foreign keys (`UpgradeTests`)
- [x] Full-size rehearsal: the preserved 47,010-product / 223,272-lot / 871,876-sale / 5.19-million-line schema-36 store upgraded to 37; after profiling writes, a killed bill and a backup, all ten store checks passed (integrity, foreign keys, every lot against its source documents, every projection)
- [x] Same-machine before/after profile of every Phase 9 hot path with mixed readers, writers and a backup; no unexplained regression (`docs/perf/1.1.0.md`)
- [x] `scripts/build-release.ps1 -Version 1.1.0` produced the setup, portable zip, full package and the 1.0.10 → 1.1.0 delta in `artifacts/Releases`
- [x] With `GROCERY_PACKAGED_UI_TEST=1`, the packaged operator journey passed on the 1.1.0 portable build: setup, product and vendor, purchase, every page, cash sale, Gujarati, lock/unlock, manual and close backups
- [x] Large-store WPF journey on the published 1.1.0 build: the update window, every page over three rounds and a full-history Sales History export
- [ ] Release binaries code-signed (needs the owner's signing certificate; Velopack reports the bundle unsigned)
- [ ] Clean-PC install, uninstall and USB update tested without internet access

## Upgrade package

- [x] An existing store shows the update window before sign-in: data versions, time estimate, free space needed and free, the verified backup and, from before schema 36, its permanent ledger archive; nothing changes until **Update now**
- [x] Too little space blocks the update; a failed step reports that it changed nothing, where the backup and logs are, and how to retry or go back
- [x] Pre-upgrade verified backup is taken; a failing backup leaves the store unchanged; older binaries refuse the newer schema; restoring the pre-upgrade backup and upgrading again produces the same documents once (`UpgradeTests`)
- [ ] Upgrade time and peak disk measured on the target shop PC against the window's estimate
- [ ] Pilot period: daily lot reconciliation, report snapshots, backup success and error logs reviewed before broad rollout

## Localization and UI

- [x] Table pages at 1366×768: one title row with the count, page arrows and actions; one filter row; the table runs to the bottom edge (UI smoke test). Date boxes show dd-MM-yyyy on an en-US PC
- [x] Every page's visible buttons, text boxes, lists and date boxes have accessible names (UI smoke test)
- [x] English/Gujarati resource keys, placeholders and English digits (`LocalizationTests`)
- [ ] 125% and 150% display scaling checked on the target PC; no clipping or raw `⟦key⟧` with `--pseudo`
- [ ] WPF memory on the reference and target PCs: the local large-store journey is recorded in the Phase 9 record; the 350 MiB gate needs the reference PC
- [ ] Keyboard-only sale, purchase and damage with the physical scanner
- [ ] Native Gujarati reviewer signed off all UI, receipt, label, report, release-note and user-guide terminology (strings marked `MT – needs review`)
- [ ] Operator walkthrough on the target PC: create a product and vendor, post a purchase, scan and complete a sale, view the dashboard, use Gujarati, lock/unlock, and close with a backup

## Store and hardware

- [ ] Windows and India time zone/date correct; plugged-in sleep/hibernate disabled; UPS tested
- [ ] Data folder is outside OneDrive; BitLocker enabled where available
- [ ] Scanner test sheet passes EAN-13, UPC-A, scale label and 2D scans
- [ ] Receipt printer passes English/Gujarati, cutter and cash-drawer tests
- [ ] Interrupt printing before and after spooler acceptance; after restart, review each saved receipt/day-end/label job, confirm Ctrl+P retry and receipt DUPLICATE mark, and check that drawer pulses do not replay
- [ ] Label printer is calibrated and its labels scan from 30 cm
- [ ] Power-pull during a draft bill recovers the draft and every lot still reconciles to its source documents
- [ ] Verified backup and compaction timed on the target PC (local: 1.8 GB store backed up in 20.4 s)
- [ ] Backup exported to USB and restored on a second PC (local: restored into an empty data root in 18.8 s and passed all ten checks)

## Business data and pilot

- [ ] Store/GST/FY settings and tax/HSN data approved by the accountant
- [ ] Owner review of the recorded decisions (Phases 6, 8 and 9 execution records)
- [ ] Products and opening stock imported; ten random counts and top 200 barcodes checked
- [ ] Vendors, opening balances, employees and assets checked
- [ ] Owner walkthrough completed; password recovery copy stored safely
- [ ] One-to-two-week parallel run reconciled daily; owner approved cut-over
