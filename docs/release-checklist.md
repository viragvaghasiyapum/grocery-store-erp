# Grocery ERP v1 release checklist

Record the operator, date, machine and evidence beside every item. Do not release with an unchecked
software item. Hardware, language and pilot items require a named human sign-off.

## Automated release gate

- [ ] `dotnet format GroceryErp.slnx --verify-no-changes --no-restore`
- [ ] `dotnet build GroceryErp.slnx -c Release --no-restore` has zero warnings
- [ ] `dotnet test GroceryErp.slnx -c Release --no-build` passes
- [ ] Full data set generated with `dotnet run -c Release --project tools/GroceryErp.DataGen -- --output=<path>`
- [ ] Performance results recorded in `docs/perf/<version>.md`; no path regressed by more than 20%
- [ ] `scripts/build-release.ps1 -Version <semver>` produces the installer and local update feed
- [ ] Clean-PC install, uninstall and USB update tested without internet access
- [ ] Backup created before update; post-update migrations, integrity checks and rollback verified

## Localization and UI

- [ ] UI smoke journey: setup, product, purchase, sale, dashboard, Gujarati, lock/unlock, close backup
- [ ] `--pseudo` pass on every page at 1366×768 and 125%/150% scaling; no clipping or raw `⟦key⟧`
- [ ] Native Gujarati reviewer signed off all UI, receipt, label and report terminology
- [ ] All visible and printed numbers remain English digits in Gujarati mode

## Store and hardware

- [ ] Windows and India time zone/date correct; plugged-in sleep/hibernate disabled; UPS tested
- [ ] Data folder is outside OneDrive; BitLocker enabled where available
- [ ] Scanner test sheet passes EAN-13, UPC-A, scale label and 2D scans
- [ ] Receipt printer passes English/Gujarati, cutter and cash-drawer tests
- [ ] Label printer is calibrated and its labels scan from 30 cm
- [ ] Power-pull during a draft bill recovers the draft and preserves ledger integrity
- [ ] Backup exported to USB and restored on a second PC

## Business data and pilot

- [ ] Store/GST/FY settings and tax/HSN data approved by the accountant
- [ ] Products and opening stock imported; ten random counts and top 200 barcodes checked
- [ ] Vendors, opening balances, employees and assets checked
- [ ] Owner walkthrough completed; password recovery copy stored safely
- [ ] One-to-two-week parallel run reconciled daily; owner approved cut-over
