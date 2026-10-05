# Phase 1 audit — 28 September 2026

Scope: Phase 1 foundations and the first-run, backup, restore, settings, and shell UI described in [implementation.md](implementation.md), checked against [PLANS.md](PLANS.md) and [techstack.md](techstack.md). This audit covers the local implementation and screenshot review; it is not a physical store acceptance test.

## Result

Phase 1 software paths are implemented and the local Release build and tests pass. The audit found incomplete first-run persistence, missing backup/restore controls, a data-move interruption window, and cramped setup/restore layouts. Those issues were corrected. The remaining sign-offs require a Gujarati speaker and the target hardware; complete public Core/Data XML documentation is also outstanding.

| Area | Finding and action | Verification |
|---|---|---|
| First-run wizard | Replaced the stacked full-width tabs with a left step rail, kept the form visible, blocked skipping ahead, and revalidated every step at completion. Added folder selection and receipt, label, and A4 printer test actions. | WPF smoke test and [setup screenshot](docs/audit-screenshots/setup.png). |
| First-run data | The setup transaction now fills `name_local` for all eight seeded units and ten expense categories from Gujarati resources. An optional two-digit scale prefix creates a usable 13-digit weight-label rule (5-digit PLU, 5-digit gram value, check digit). Prefix collisions are rejected. | Integration assertions on seeded rows and `barcode_rule`; resource lookup test. |
| Backups and USB | Added manual backup, removable-drive selection, verified export of the newest and monthly backup sets, post-copy ZIP verification, last-export tracking, and a shell health indicator. | Existing backup round-trip tests and Release build. Physical USB write/eject testing remains open. |
| Restore | Added ZIP picker, staged verification, a current-versus-backup summary, exact typed `RESTORE`, and safe staged-file cleanup on cancellation. Rehashed the staged database before integrity inspection. Enlarged the dialog so all comparison rows and actions fit. | Restore smoke test and [restore screenshot](docs/audit-screenshots/restore.png). |
| Data-root move | Folder choice is validated against cloud-synced, nested, or occupied paths. The move runs before database startup, verifies copied database/WAL and SQLite integrity, and switches bootstrap only after the target is committed. A ready marker completes a move interrupted between target commit and bootstrap update; the old root is retained. | Normal-move and interrupted-move integration tests. A real power-cut test remains open. |
| Access and settings | Added Settings navigation, password change with current-password verification and audit entry, a single-instance mutex before database access, and a scrollable settings page with status feedback. | Release build, existing authentication tests, and [settings screenshot](docs/audit-screenshots/settings.png). |
| Localization and numerics | Registered missing shared resource prefixes, added English/Gujarati strings, preserved English digits in the new windows, and added deterministic property checks across the signed scaled-long range. | Resource parity/lookup, Gujarati UI, and numeric tests. Gujarati wording is machine-drafted pending native review. |

## Screenshot review

The linked captures were taken from rendered WPF windows, not XAML mockups. The setup step rail and form remain side by side at 760 × 640; the settings cards, paths, and backup actions are readable at 1000 × 720 with scrolling for lower sections; the restore comparison and typed-confirmation action fit at 660 × 600. The screenshots show English UI. Automated WPF assertions also switch setup and restore text to Gujarati and check English-digit numeric formatting. The captures do not establish 125%/150% scaling, keyboard-only use, or printer/USB behavior on shop hardware.

## Validation

- `dotnet format GroceryErp.slnx --verify-no-changes --no-restore`: passed after formatting.
- `dotnet build GroceryErp.slnx -c Release --no-restore`: passed with zero warnings and errors.
- xUnit v3 project runners: 63 passed across Core (10), Architecture (1), Integration (50), and UI (2); none failed or skipped. The project runners were invoked with `dotnet run --project tests/GroceryErp.<suite>.Tests -c Release --no-build --no-restore` because `dotnet test` in this local SDK invocation reported zero discovered tests.
- `git diff --check`: no whitespace errors.

## Open acceptance items

1. Have a native Gujarati reviewer approve the Phase 0/1 and newly seeded strings; replace the machine-drafted translations where needed.
2. Test receipt, label, and A4 output on the configured printers; verify USB export and restore from a removable drive; exercise an interrupted data move on the target PC.
3. Inspect the UI at 1366 × 768 and 125%/150% display scaling, with keyboard-only navigation and Gujarati input. The screenshots above are narrower window checks, not these full acceptance conditions.
4. Complete the public Core/Data XML documentation called for by the Phase 1 checklist.

# Phase 2 audit — 29 September 2026; updated 2 October 2026

Scope: the Catalog and barcode requirements in [implementation.md](implementation.md) §§7–8, the Phase 2 delivery row, [PLANS.md](PLANS.md), and [techstack.md](techstack.md). This is a local code, database-test, and rendered-WPF review. The Phase 1 audit above remains separate.

## Result

The catalog and barcode software workflows now run and the Release suite is green. The original audit fixed data-loss, validation, search, import, and label-output defects; the 2 October follow-up implemented the remaining bulk/lot label and POS unknown-barcode workflows. Physical printer/scanner, display-scaling, keyboard, and native Gujarati sign-offs remain open. The Phase 2 status in `implementation.md` reflects this boundary.

| Area | Finding and correction | Evidence |
|---|---|---|
| Product list | The page requested only 200 products, omitted category, brand, low-stock, and loose-item filters, had no restore action, and crowded eight actions into one footer. It now requests the documented 20k ceiling in a virtualized grid, exposes those filters, archive restore, and direct Lots and Stock ledger views, keeps actions responsive, and ignores stale search responses. A zero reorder level no longer creates a low-stock alert; blocked lots are excluded from the displayed live price. | Catalog and inventory integration checks; [English list](docs/audit-screenshots/products.en.png), [Gujarati list](docs/audit-screenshots/products.gu.png), [lots](docs/audit-screenshots/product-lots.en.png), and [ledger](docs/audit-screenshots/product-ledger.en.png). |
| Product editing and masters | Editing converted every barcode into a single-unit manufacturer code and lost its original source and primary setting. It also dropped the expiry-alert override. Those values now round-trip, the first remaining barcode becomes primary when needed, and category alert days populate on edit. Categories now display as a tree and support drag-to-reparent, with full descendant-depth validation in the catalog service. Loose items require a fractional unit; repeated internal-barcode generation returns the existing code. | New round-trip, hierarchy, unit, and internal-barcode integration tests; [editor screenshot](docs/audit-screenshots/product-editor.en.png) and [category tree](docs/audit-screenshots/category-tree.en.png). |
| Search and scanning | Gujarati numerals now normalize to English digits in catalog text, Excel cells and scans. Short local-name/alias prefixes work, and wildcard characters in fallback search are escaped. GS1 rejects unsupported AIM prefixes and leaves GTIN-only scans to FEFO rather than choosing an arbitrary lot; an explicitly matched lot must be active and in stock. Unresolved POS scans now open quick-add/attach choices. Attachment is idempotent and reports another barcode owner with a link to that product; a quick-created product with opening stock can be scanned into a bill. | Search, GS1, barcode-owner and sale-path integration tests; [unknown barcode](docs/audit-screenshots/unknown-barcode.en.png), [attach view](docs/audit-screenshots/unknown-barcode-attach.en.png), and [quick form](docs/audit-screenshots/quick-product.en.png). |
| Excel import | Imported updates previously bypassed the stocked-product unit lock and silently ignored a barcode claimed after dry run. Posting now checks both inside the transaction. Dry run catches additional length, rate, precision, and scaled-number overflow errors, allows valid rows to be deselected, and exports an error workbook. Invalid rows cannot be selected for import; the Import button follows checkbox changes and stays disabled during posting. | Real temporary-SQLite and workbook integration tests; WPF build and smoke test. |
| Labels | The preview used a solid placeholder bar; it now renders a Code 128 barcode. TSPL output rasterizes Gujarati names into `BITMAP` bytes instead of sending glyphs to the printer's built-in font. Multi-product selection, lot rows with received-quantity copy defaults, three templates, purchase-posting offer, and lot-view entry use one batch preview/print/export workflow. Lot codes persist and scan to the exact lot. Large copy counts split into smaller TSPL commands; print spooling stays off the UI thread. | TSPL template/batch and exact-lot scan integration tests; [batch label view](docs/audit-screenshots/label-batch.en.png) and [single label preview](docs/audit-screenshots/product-label.en.png). Physical print and scan verification remains open. |

## Screenshot review

The linked PNGs were captured from actual WPF controls with deterministic test data, then visually inspected. The product view is shown in a 1080 × 700 content window in both languages; its filters and selected-product actions fit. The 900 × 620 product stock window shows lots and ledger without clipped columns. The 720 × 560 category window shows an expanded hierarchy and drag guidance. The 760 × 720 editor and 650 × 560 quick-add form keep their actions visible. The 1080 × 720 batch-label view shows editable per-row copies and a real barcode preview; the 780 × 480 unknown-barcode dialog shows both choices without clipped columns, with a visible search label in the attach view. These are isolated view captures, so they do not prove a complete shell navigation journey, 125%/150% display scaling, drag behavior by mouse, or hardware output. Gujarati wording remains machine-drafted.

## Validation

- Local `.tools/dotnet/dotnet.exe build GroceryErp.slnx -c Release --no-restore`: passed, zero warnings and errors.
- xUnit v3 project runners: 73 passed, none failed or skipped in the final quiet run (Core 10, Architecture 1, Integration 60 with `-maxThreads 1`, UI 2). The UI runner generated eleven Phase 2 screenshots. The existing 500 ms P&L performance assertion exceeded its limit under concurrent/build-worker load (527 ms and 2514 ms on separate busy runs); it passed alone and with the full integration suite after shutting down MSBuild workers.
- Local `.tools/dotnet/dotnet.exe format GroceryErp.slnx --verify-no-changes --no-restore` passed before the final quick-add window file; a scoped formatter check for that file and `git diff --check` passed after the final edits.
- The 20k-product local benchmark passed after these changes: 20,000 imported in 4.890 s; search averaged 0.371 ms with 0.491 ms p95 over 25 samples. These are local synthetic-data timings, not store-hardware results.

## Open Phase 2 work

1. Calibrate and scan printed labels on the target TSPL printer/scanner at the required distance, and review Gujarati print raster on that model.
2. Verify the UI at 1366 × 768, 125%/150% scaling and with keyboard-only operation.
3. Obtain native Gujarati review; the local 20k-product benchmark has passed, but store-hardware performance remains unverified.

# Phase 3 audit — 3 October 2026

Scope: the stock-in requirements in [implementation.md](implementation.md) §§9–10 and the Phase 3 delivery row, checked against [PLANS.md](PLANS.md) and [techstack.md](techstack.md). This review covered the purchasing and inventory services, their WPF workflows, SQLite integration tests, and rendered UI screenshots. It did not exercise physical shop equipment.

## Result

The purchase, vendor, opening-stock, stock-ledger and physical-count paths work in the local Release suite. The audit found several gaps between the working services and the operator screens: saved purchase drafts could not be reopened, vendor history and payment allocation were hard to reach, purchase cost checks were invisible, count sheets lacked a scope/review/resume workflow, and opening-stock import lacked a visible row review. These were corrected. The current limitations below remain part of Phase 3 acceptance.

| Area | Finding and correction | Evidence |
|---|---|---|
| Purchase entry | Added draft reopening with every editable header and line field, scan/name/SKU lookup, vendor-specific last rate and product defaults, manufacturing date, live landed cost and margin, short-expiry/below-cost/rate-change warnings, and a subtotal/discount/GST/round-off/bill-difference review. Numeric fields normalize Gujarati digits before parsing. Posting still uses the service's transactional validation and landed-cost calculation. | Draft/default integration tests; [purchase entry](docs/audit-screenshots/phase3/purchase-entry.en.png) and [warning state](docs/audit-screenshots/phase3/purchase-entry-warnings.en.png). |
| Vendors and payments | Added vendor edit loading, optional purchase selection on payment, and a vendor history view with purchases, payments, running ledger and lots supplied. The Purchases page enables actions only for suitable selections, refreshes saved drafts, and confirms cancellation. A blocked cancellation now explains the stock-adjustment path and the unavailable vendor-return feature. | Vendor edit/ledger integration test; [purchase list](docs/audit-screenshots/phase3/purchases-page.en.png), [Gujarati list](docs/audit-screenshots/phase3/purchases-page.gu.png), and [vendor history](docs/audit-screenshots/phase3/vendor-history.en.png). |
| Stock count | A count can be scoped to one or more categories, saved and resumed, located by lot/product search or scan, printed, reviewed by difference value, and posted only after every lot is entered. Empty sheets, fractional counts on whole-unit products, updates after finalization, and partial batch saves are rejected. The posting transaction compares movement totals against the reviewed sheet, so a concurrent stock movement requires a fresh review. | Count and concurrent-movement integration tests; [count sheet](docs/audit-screenshots/phase3/stock-count.en.png), [Gujarati count](docs/audit-screenshots/phase3/stock-count.gu.png), and [scope picker](docs/audit-screenshots/phase3/stock-count-scope.en.png). |
| Opening stock and ledger | The import now shows each dry-run row and its errors before enabling import. Stock actions are grouped, lot-only actions require a selected lot, and the stock ledger exposes date and movement filters with running lot balance and source references. | Existing opening-stock/ledger tests; [import review](docs/audit-screenshots/phase3/opening-stock-preview.en.png), [stock page](docs/audit-screenshots/phase3/stock-page.en.png), and [ledger](docs/audit-screenshots/phase3/stock-ledger.en.png). |

## Screenshot review

The 11 linked PNGs are captures of rendered WPF controls with deterministic fixture data. I visually checked the 1240 × 750 purchase form, including a negative red margin and all three warnings; the 980 × 660 count form in English and Gujarati; the scope and opening-import dialogs; the purchase/vendor views; and the 1080 × 700 stock and ledger pages. The revised columns, labels, totals and actions fit in these captures. The captures do not prove a full shell navigation journey, keyboard-only operation, 125%/150% display scaling, or real printer/scanner output. The `phase3` screenshot folder also retains older Phase 2 captures: automatic approval review rejected deletion of those extra generated files, so only the 11 linked files are Phase 3 evidence.

## Validation

- Local `.tools/dotnet/dotnet.exe build GroceryErp.slnx -c Release --no-restore`: passed with zero warnings and errors.
- xUnit v3 project runners: 76 passed, none failed or skipped (Core 10, Architecture 1, Integration 63 with `--max-threads 1`, UI 2). The UI runner regenerated the 11 linked screenshots and asserted the purchase warning state and Gujarati digit parsing.
- `.tools/dotnet/dotnet.exe format GroceryErp.slnx --verify-no-changes --no-restore` and `git diff --check`: passed. Git reported only its line-ending conversion notices.

## Open Phase 3 work

1. The stock ledger shows source type and ID but does not open the source document. The lot views do not yet show a colored expiry bucket or provide a direct write-off action; expiry write-off exists in the Money Out workflow. Vendor returns remain backlog, as indicated by the blocked-cancellation message.
2. A stock count snapshots lots that exist when the sheet is created. Lots created later are not added to that sheet; cancel it and create a new scoped sheet if those lots must be counted. Physical labels and paper count-sheet output need target-printer verification.
3. Verify at 1366 × 768 and 125%/150% scaling, run the complete workflows with keyboard/scanner hardware, and obtain native Gujarati review of the machine-drafted wording.

# Phase 4 audit — 4 October 2026

Scope: Sales/POS requirements in [implementation.md](implementation.md) §11 and the Phase 4 checklist, checked against [PLANS.md](PLANS.md) and [techstack.md](techstack.md). I reviewed the sales service, receipt renderer, WPF workflows, SQLite integration tests, and rendered screenshots. This is local software verification, not store-hardware acceptance.

## Result

The core sale, hold, payment, void and return transactions pass the local Release suite. The audit found operator-facing gaps in saved-bill access, lot and price controls, payment and return entry, customer selection, history filters, receipt detail, and day-end cash. Those paths were corrected. Phase 4 remains open because several §11 workflows and external acceptance checks below are incomplete.

| Area | Finding and correction | Evidence |
|---|---|---|
| Billing and saved bills | Added startup draft selection, a held-bill picker, optional hold labels, customer search/create/attach, selected-lot choice, audited unit-price override, editable line and bill discounts, and clearer totals, savings and item detail. Quantity-prefix scans now combine the typed prefix with the scanner burst and normalize Gujarati digits. Payment actions and the customer form were adjusted after screenshot review so their controls remain visible. | Saved-draft, exact-lot, price/MRP/audit, customer-receipt and discount-clearing integration tests; [English POS](docs/audit-screenshots/phase4/billing.en.png), [Gujarati POS](docs/audit-screenshots/phase4/billing.gu.png), [saved bills](docs/audit-screenshots/phase4/saved-bills.en.png), [lot picker](docs/audit-screenshots/phase4/lot-picker.en.png), [hold dialog](docs/audit-screenshots/phase4/hold-bill.en.png), [customer dialog](docs/audit-screenshots/phase4/customer.en.png). |
| Payment and day-end | The POS now gathers cash tender, UPI/card amounts and references in a split-capable dialog, shows remaining/change, validates totals, and completes once. A printer failure leaves the committed sale available for reprint; drawer work runs separately from receipt printing. Day-end expected cash now subtracts cash change as well as refunds/expenses. | Completion/replay and cash-change integration tests; [English payment](docs/audit-screenshots/phase4/payment.en.png) and [Gujarati payment](docs/audit-screenshots/phase4/payment.gu.png). |
| Sales history and returns | Added date/status/payment/invoice/amount filters, keyset navigation, selected-sale line/payment details, localized status/payment text, remaining returnable quantity, refund-mode choice, and a required reason for return/void. A committed return can print its own receipt without undoing the transaction when printing fails. | Return proration, remaining-quantity, damage and void-protection tests; [English history](docs/audit-screenshots/phase4/sales-history.en.png), [Gujarati history](docs/audit-screenshots/phase4/sales-history.gu.png), and [return dialog](docs/audit-screenshots/phase4/return.en.png). |
| Receipts | Receipt rastering and spool/drawer calls run off the UI thread. Added an invoice Code 128 barcode, line MRP, corrected CGST/SGST rounding allocation, and a localized return title, original-invoice reference, reason, taxable/GST, refund method and total. | Return-receipt reconstruction test and rendered [sale receipt](docs/audit-screenshots/phase4/sale-receipt.en.png) and [Gujarati return receipt](docs/audit-screenshots/phase4/return-receipt.gu.png). Physical output remains unverified. |

## Screenshot review

The 13 linked PNGs were captured from rendered WPF controls and receipt rasters with deterministic fixture data, then visually inspected. The 1120 × 680 English/Gujarati POS views show the scan box, line detail, totals, all three payment actions, and a two-row action toolbar without clipping. The 760 × 570 customer dialog shows visible Name, Phone and GSTIN labels; saved-bill, lot, hold, payment and return dialogs fit their tested sizes. The 1120 × 700 history view shows both languages, filters, rows, detail and actions; the selected-line detail area was enlarged after review. The 384-pixel sale and return rasters show legible English-digit amounts and Code 128 bars. The screenshots use synthetic data and do not prove a full operator journey, physical printing/scanning, display scaling, or native Gujarati wording. The Gujarati return fixture deliberately contains an English operator-entered reason and store footer; those are data, not untranslated control labels.

## Validation

- `.tools/dotnet/dotnet.exe build GroceryErp.slnx -c Release --no-restore -m:1 -v:q`: passed with zero warnings and errors.
- xUnit v3 runners: 82 passed, none failed or skipped (Core 10, Architecture 1, Integration 69, UI 2). Integration ran with `-maxThreads 1`; the UI runner regenerated the Phase 4 screenshots.
- `.tools/dotnet/dotnet.exe format GroceryErp.slnx --verify-no-changes --no-restore --verbosity quiet` passed after scoped formatting. `git diff --check` passed.

## Open Phase 4 work

1. Reallocate all of a product's cart quantity across FEFO lots on F4 edits; show any stock/price changes before accepting a resumed held bill. The current F4 edit acts on the selected lot, and resume refreshes prices without a change review.
2. Add percent entry for line/bill discounts, multi-line entry in one return document, prior-return and movement detail in history, history export, and the full printable day-end report. The service supports multi-line returns and calculates a full day-end summary, but these operator views are incomplete.
3. Preserve every store/tax/footer value required for an identical historical reprint. Current reprints load current store settings. Add visible printer readiness and the documented POS status detail, including last scanned stock/MRP and held-bill chips.
4. Verify receipt printer, cutter, drawer and barcode scanning on target hardware; run a real power-pull recovery and scan-to-line timing test. Check 1366 × 768 at 125%/150% scaling, keyboard-only use, and obtain native Gujarati review. No physical or performance sign-off is claimed by these local screenshots.

# Phase 3 and 4 software completion — 5 October 2026

This follow-up closes the software items listed as open in the Phase 3 and 4 audits above. The earlier findings remain as a dated record. Migrations 006–009 are additive and run through the existing backup-before-upgrade path.

| Area | Completed change | Verification |
|---|---|---|
| Stock ledger and lots | A ledger movement now opens a read-only source document with its lines; the SQL projection was corrected so movement type and reference map properly. Purchase, sale, return, damage, count, adjustment, opening-stock and vendor-return sources are covered. Both lot views show a colored expiry bucket, and an expired lot can be written off directly through the bulk expiry service. | Source-navigation integration assertions; rendered [source document](docs/audit-screenshots/phase3/stock-source.en.png), [stock-page lot bucket](docs/audit-screenshots/phase3/stock-lots.en.png) and [product lot bucket](docs/audit-screenshots/phase3/product-lots.en.png). |
| Vendor returns | A posted purchase can create a supplier credit note with selected lots, quantities, before-tax credit, GST credit, date, reference and reason. Posting atomically reduces stock and vendor payable, records a P&L cost-versus-credit variance, and is replay-safe. Credits and quantities are capped against the source purchase and remaining lot stock. Blocked lots can also be returned. Vendor history includes returns. | Vendor-return integration test, including a tax-mode change after purchase; rendered [entry dialog](docs/audit-screenshots/phase3/vendor-return.en.png). Migrations 007 and 009 contain the immutable document, balance/P&L views, and purchase tax-mode snapshot. |
| Stock-count scope | Migration 008 preserves selected categories. Draft sheets can refresh lots created after the original snapshot; their expected quantity is reconstructed at the snapshot boundary. Review adds unseen lots, and posting rejects a stale sheet until every in-scope lot is counted. | New later-lot integration test and updated [count sheet](docs/audit-screenshots/phase3/stock-count.en.png). |
| POS quantity, hold and discounts | F4 replaces a product's full cart quantity and reallocates across saleable FEFO lots. Resuming a held bill shows changed prices, MRP, available stock and expiry before acceptance; acceptance rechecks the reviewed state. Line and bill discounts accept an amount or percentage. | FEFO, stale-review and discount integration tests; rendered [POS](docs/audit-screenshots/phase4/billing.en.png), [held review](docs/audit-screenshots/phase4/held-review.en.png) and [percent dialog](docs/audit-screenshots/phase4/discount-percent.en.png). |
| Returns and history | One return document accepts multiple sale lines with separate quantity and resellability choices. Sales history shows previous returns and stock movements, and exports the filtered result to Excel. | Multi-line return and history integration tests; rendered [return dialog](docs/audit-screenshots/phase4/return.en.png) and [history](docs/audit-screenshots/phase4/sales-history.en.png). |
| Day-end, receipts and POS status | Day-end now shows all payment, void, return, refund, change and expected-cash totals and prints a full raster report. New completions and returns persist receipt snapshots, so their reprints keep historical store, tax, language, width and footer settings. POS displays printer readiness, last scanned stock/MRP and held-bill chips. The POS card was tightened after screenshot review so all three payment actions fit at 1120 × 680. | Receipt snapshot, day-end and cash tests; rendered [day-end](docs/audit-screenshots/phase4/day-end.en.png), [receipt](docs/audit-screenshots/phase4/sale-receipt.en.png) and [POS](docs/audit-screenshots/phase4/billing.en.png). |

## Screenshot review

The WPF UI runner regenerated the Phase 3 and 4 PNGs from actual rendered controls and deterministic data. I inspected the new source, lot, vendor-return, count, held-review, discount, multi-line return, day-end and POS captures. It exposed a missing source-title lookup, narrow return/day-end columns, a truncated vendor date, horizontal stock-lot scrolling and a partly clipped POS split-payment button; those were corrected and the captures rerun. The final 1120 × 680 POS capture shows Cash, UPI and Split in full. The 570 × 720 day-end capture shows its final expected-cash row without scrolling. Screenshots demonstrate local rendering at the captured sizes; they do not establish physical print quality or behavior at other display scales.

## Validation

- `.tools/dotnet/dotnet.exe build GroceryErp.slnx -c Release --no-restore -m:1 -v:q`: passed with zero warnings and errors.
- xUnit v3 project runners: Core 10, Architecture 1, Integration 76, UI 2 passed; no failures or skips. The UI runner was executed separately for the Phase 3 and Phase 4 screenshot directories.
- `.tools/dotnet/dotnet.exe format GroceryErp.slnx --verify-no-changes --no-restore --verbosity quiet` and `git diff --check`: passed.
- `dotnet test` under this local SDK reported zero discovered xUnit v3 tests, so validation used `dotnet run --project tests/GroceryErp.<suite>.Tests -c Release` for each project.

## Remaining acceptance

Physical receipt/label/count-sheet printers, cutter, drawer, scanner and USB operation need testing on the store equipment. A real power-pull recovery and scan-to-line timing run remain. The UI needs target-PC checks at 1366 × 768 with 125%/150% scaling and keyboard-only use, and a native Gujarati reviewer must approve the machine-drafted wording. The earlier Phase 1 public XML documentation item remains open.

Legacy sales and returns created before migration 006 have no saved receipt snapshot and still reconstruct a reprint from current settings. Likewise, purchases posted before migration 009 have no tax-mode snapshot, so a later vendor return uses the current tax mode. These historical values cannot be reconstructed reliably from the old schema.
