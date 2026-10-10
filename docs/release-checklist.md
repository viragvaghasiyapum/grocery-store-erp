# Grocery ERP v1 release checklist

Record the operator, date, machine and evidence beside every item. Do not release with an unchecked
software item. Hardware, language and pilot items require a named human sign-off.

Interim code recheck on 8 October 2026 with SDK `10.0.401`: Release build had zero warnings,
`dotnet format --verify-no-changes` passed, and `dotnet test GroceryErp.slnx -c Release --no-build --no-restore`
reported 106 passed, zero failed, one packaged-journey skip. The boxes below require a versioned
release run and evidence before sign-off.

## Automated release gate

- [ ] `dotnet format GroceryErp.slnx --verify-no-changes --no-restore`
- [ ] `dotnet build GroceryErp.slnx -c Release --no-restore` has zero warnings
- [ ] Run `dotnet test GroceryErp.slnx -c Release --no-build --no-restore`; require zero failures.
      The packaged UI journey may skip before packaging and must pass in the post-package rerun below.
- [ ] Full data set generated with `dotnet run -c Release --project tools/GroceryErp.DataGen -- --output=<path>`
- [ ] Performance results recorded in `docs/perf/<version>.md`; no path regressed by more than 20%
- [ ] `scripts/build-release.ps1 -Version <semver>` produces the installer and local update feed
- [ ] After packaging, set `GROCERY_PACKAGED_UI_TEST=1` and run the UI project runner again.
      The FlaUI case extracts the portable release and uses an isolated temporary data root;
      verify setup, product and vendor creation, purchase posting, every main page, scanned
      cash sale, language switch, lock/unlock, manual backup and close backup.
- [ ] Clean-PC install, uninstall and USB update tested without internet access
- [ ] Backup created before update; post-update migrations, integrity checks and rollback verified

## Localization and UI

- [ ] Operator walkthrough on the target PC: create a product and vendor, post a purchase,
      scan and complete a sale, view the dashboard, use Gujarati, lock/unlock, and close with a backup
- [ ] `--pseudo` pass on every page at 1366×768 and 125%/150% scaling; no clipping or raw `⟦key⟧`
- [ ] Native Gujarati reviewer signed off all UI, receipt, label and report terminology
- [ ] All visible and printed numbers remain English digits in Gujarati mode

## Store and hardware

- [ ] Windows and India time zone/date correct; plugged-in sleep/hibernate disabled; UPS tested
- [ ] Data folder is outside OneDrive; BitLocker enabled where available
- [ ] Scanner test sheet passes EAN-13, UPC-A, scale label and 2D scans
- [ ] Receipt printer passes English/Gujarati, cutter and cash-drawer tests
- [ ] Interrupt printing before and after spooler acceptance; after restart, review each saved receipt/day-end/label job, confirm Ctrl+P retry and receipt DUPLICATE mark, and check that drawer pulses do not replay
- [ ] Label printer is calibrated and its labels scan from 30 cm
- [ ] Power-pull during a draft bill recovers the draft and preserves ledger integrity
- [ ] Backup exported to USB and restored on a second PC

## Business data and pilot

- [ ] Store/GST/FY settings and tax/HSN data approved by the accountant
- [ ] Products and opening stock imported; ten random counts and top 200 barcodes checked
- [ ] Vendors, opening balances, employees and assets checked
- [ ] Owner walkthrough completed; password recovery copy stored safely
- [ ] One-to-two-week parallel run reconciled daily; owner approved cut-over
