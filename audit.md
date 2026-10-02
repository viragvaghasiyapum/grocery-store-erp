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
