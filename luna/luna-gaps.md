# Luna implementation gap audit

Audit date: 2026-10-06  
Scope: `implementation.md`, `PLANS.md`, and the current working tree.

## Executive conclusion

The main business workflows are present, but the project is not yet fully compliant with its own implementation contract. The highest-risk gaps are:

1. The prescribed module/plugin composition is scaffolded only as an unused interface; modules are not actually composed through `IModule`.
2. The window-level scanner fallback does not undo scanner keystrokes from the focused field.
3. Negative-stock FEFO fallback can select an expired lot even when expired lots are blocked.
4. Receipt/label printing does not implement the required queued ESC/POS pipeline, receipt `TEXT` mode, banded raster output, or label-driver fallback.
5. Gujarati output is incomplete in exported reports, dashboard payment labels, printed artifacts, and generic error paths.

The resource files currently have matching English/Gujarati key counts (908 each), but key parity is not the same as complete localized usage. Native-speaker, hardware, target-PC, packaged-app, and pilot sign-offs remain outstanding as explicitly recorded in `implementation.md`.

## Evidence and status legend

- **High** — functional, safety, architectural, or release-blocking gap.
- **Medium** — requirement non-compliance or user-facing quality gap that should be fixed before release.
- **Open acceptance** — implementation may exist, but the required target-environment sign-off is still missing.
- **Verification blocker** — cannot be independently verified in the current environment.

The audit was primarily static. `dotnet build GroceryErp.slnx -c Release --no-restore` and `dotnet format GroceryErp.slnx --verify-no-changes --no-restore` could not start because the machine has no compatible .NET SDK while `global.json` pins SDK `10.0.401`. Therefore the passing-test claims in `implementation.md` were not re-run during this audit.

## Phase 0 — Skeleton and architecture

### P0-01 — Module anatomy and composition are not implemented

**Severity:** High  
**Status:** Confirmed missing

`implementation.md` §§2.3–2.5 require every module to contain contracts, domain, application, infrastructure, presentation, resources, and an `<Module>Module.cs` implementing `IModule`; the App is supposed to compose an explicit module list and register navigation/resources through it.

Evidence:

- `src/GroceryErp.UI.Common/Modularity/IModule.cs:7` defines the contract, but no `CatalogModule`, `SalesModule`, `ReportsModule`, `SettingsModule`, or other concrete module implementation exists.
- `src/Modules/GroceryErp.Settings` contains only its `.csproj`; the other module projects contain services/models rather than the required layer/resource/presentation structure.
- `src/GroceryErp.App/App.xaml.cs:87-105` directly registers business services.
- `src/GroceryErp.App/Presentation/ShellWindow.xaml:50-99` hardcodes all navigation items and points them at App-owned pages.
- The planned `Resources`, module-local views, and module registration boundaries are therefore not being used. The architecture test suite also has no test that enforces `IModule` composition or the prescribed module boundaries.

**Recommended fix:** create concrete module classes and module-local presentation/resources, move registrations to each module, let the shell consume `IReadOnlyList<IModule>`, and add architecture tests for module shape, registration, navigation, and allowed project references.

## Phase 1 — Foundations, localization, and shared conventions

### P1-01 — User-facing error handling still leaks raw technical/English messages

**Severity:** Medium  
**Status:** Confirmed incomplete

The requirements call for localized, actionable errors. Many pages write `exception.Message` directly to status text; `BillingPage` only maps `DomainException` and returns the raw message for every other exception.

Evidence includes `src/GroceryErp.App/Presentation/BillingPage.xaml.cs:509`, `ReportsPage.xaml.cs:159,217,231`, `StockPage.xaml.cs:41,49,69,113,120,147,169,186`, `MoneyOutPage.xaml.cs:60,152,203,263,293`, and `PurchaseEntryWindow.xaml.cs:116`.

**Recommended fix:** centralize exception-to-resource mapping, log technical details, show a localized safe fallback with a retry/action where appropriate, and add failure-path UI tests in both languages.

### P1-02 — Presentation numeric formatting bypasses the required formatter convention

**Severity:** Medium  
**Status:** Confirmed incomplete

`PLANS.md:473-474` and `implementation.md:1365` prohibit parameterless `ToString()` in presentation code. Several edit/review screens still use it, including:

- `src/GroceryErp.App/Presentation/ProductEditorWindow.xaml.cs:63-65`
- `src/GroceryErp.App/Presentation/OpeningStockPreviewWindow.xaml.cs:25-27`
- `src/GroceryErp.App/Presentation/MoneyOutPage.xaml.cs:173,188,245,369`
- `src/GroceryErp.App/Presentation/AssetsPage.xaml.cs:100,102-103`
- `src/GroceryErp.App/Presentation/HeldBillReviewWindow.xaml.cs:30-34`

The app currently pins a culture at startup, but this still violates the explicit invariant-formatting contract and is fragile in tests or alternate construction paths.

**Recommended fix:** use `DisplayFormatter` or explicit `TranslationSource.FormatCulture` formats for every user-visible number and add a static check for parameterless numeric `ToString()` in presentation projects.

### P1-03 — Foundations still have explicit acceptance/documentation gates

**Severity:** Open acceptance  
**Status:** Already acknowledged in `implementation.md`

Phase 1 still requires native Gujarati review, physical printer/USB checks, data-root/restore validation on target hardware, and complete public Core/Data XML documentation (`implementation.md:3470,3516`). These are release gates, not merely optional polish.

## Phase 2 — Catalog, barcode, and labels

### P2-01 — Non-TSPL label-printer fallback is absent

**Severity:** Medium  
**Status:** Confirmed missing

`implementation.md` §15.4 requires a WPF `PrintVisual` fallback for non-TSPL printers. `src/GroceryErp.Printing/TsplLabel.cs` only builds raw TSPL, while `ProductLabelWindow.xaml.cs:61` and `LabelBatchWindow.xaml.cs:184` send directly through `RawPrinter`. No Windows-driver label visual/fallback implementation exists.

The TSPL builder does render Gujarati local names as raster text, so that specific path is present; the missing fallback remains a compatibility gap.

**Recommended fix:** share a label layout model between TSPL and a WPF visual, detect/configure printer mode, and route non-TSPL jobs through `PrintDialog.PrintVisual` with a testable preview.

### P2-02 — Catalog external sign-off remains open

**Severity:** Open acceptance  
**Status:** Already acknowledged

Physical label-printer calibration, 30 cm scan verification, 100/125/150% scaling, keyboard checks, and native Gujarati review remain unchecked for Phase 2 (`implementation.md:3471`).

## Phase 3 — Stock-in, vendors, and stock counts

### P3-01 — Requirement document contradicts the implemented vendor-return status

**Severity:** Medium  
**Status:** Documentation inconsistency

The Phase 3 status says vendor returns are implemented and migration `007_vendor_returns.sql` exists (`implementation.md:3472`), but the post-v1 backlog still lists “Vendor returns” (`implementation.md:3604`). This can cause later work to remove or duplicate a delivered feature.

**Recommended fix:** remove the stale backlog entry or mark it as completed/enhanced, and keep the phase checklist, status table, and backlog synchronized.

### P3-02 — Stock-in target-environment sign-off remains open

**Severity:** Open acceptance  
**Status:** Already acknowledged

Printer/scanner output, scaling/keyboard checks on the target PC, and native Gujarati review remain open (`implementation.md:3472`). No additional stock-in business-rule omission was confirmed by this static audit.

## Phase 4 — POS, scanning, FEFO, and receipt printing

### P4-01 — Scanner fallback does not undo typed scanner characters

**Severity:** High  
**Status:** Confirmed functional gap

`implementation.md` §8.2 requires the window-level detector to snapshot the focused `TextBox`, recognize the burst, restore the original text/caret, and then route the scan. `src/GroceryErp.UI.Common/ScanBurstDetector.cs:7-49` only buffers text and raises `ScanReceived`; it never records the focused field or restores its contents.

`BillingPage.xaml.cs:72-99` attaches this detector at shell-window level. If a scan occurs while the quantity or another text box is focused, the scanner characters can remain in that field. There is also no focused-field/burst regression test in the test tree.

**Recommended fix:** implement the snapshot/target/caret logic from §8.2 using `Stopwatch`, preserve normal human typing, and add tests for scan-box focus, quantity-box focus, suffix Enter, quantity prefix, GS1 separator, and slow/human input.

### P4-02 — Negative-stock fallback can allocate an expired lot

**Severity:** High  
**Status:** Confirmed data/stock-rule gap

The normal FEFO query in `src/Modules/GroceryErp.Inventory/StockLedger.cs:51-60` filters positive stock and blocked expiry. The negative-stock fallback at `:80-87` selects the newest active lot without `qty_on_hand > 0` or the `blockExpired/asOf` predicate. With `stock.allow_negative=1` and `pos.block_expired=1`, a shortfall can therefore be attached to an expired lot, contradicting `implementation.md` §§10.3 and 11.3.

**Recommended fix:** apply the same expiry predicate to the fallback or explicitly choose a non-expired active lot, then add a regression test for negative stock plus expired-lot blocking.

### P4-03 — Required print queue and receipt modes are not implemented

**Severity:** High  
**Status:** Confirmed incomplete

`implementation.md` §§15.1–15.3 require a single-consumer `Channel<PrintJob>` queue, POS completion that does not wait on the spooler, receipt `RASTER` and `TEXT` modes, `EscPos` output with banded raster data, and retryable failures.

Evidence:

- `src/Modules/GroceryErp.Sales/ReceiptPrinter.cs:16-26,51-82` creates a new STA thread and calls `RawPrinter.Send` directly for each job; there is no queue, `Channel`, retry queue, or `EscPos` builder.
- `src/GroceryErp.App/Presentation/BillingPage.xaml.cs:247-267` awaits `_printer.PrintAsync` and drawer output as part of bill completion, so the POS path waits for those operations.
- `src/GroceryErp.Data/Services/SettingsService.cs:46` exposes `ReceiptPrinterMode`, but no code reads it.
- `ReceiptPrinter.cs:234` rasterizes one large image rather than sending bounded raster bands.
- `rg` found no `EscPos`, `PrintJob`, or `Channel<` implementation in `src` or `tests`.
- `BillingPage` exposes a failure message but no retry action.

**Recommended fix:** introduce a singleton print worker with bounded jobs and retry state, implement `EscPos`/banded raster and explicit `TEXT` mode, enqueue receipt/drawer/label jobs, and surface localized retry actions without blocking sale commit.

### P4-04 — Receipt output has unreviewed hardcoded presentation labels

**Severity:** Medium  
**Status:** Confirmed incomplete

`src/Modules/GroceryErp.Sales/ReceiptPrinter.cs:106,130,178-179` hardcodes `GSTIN`, `MRP`, `GST`, `CGST`, and `SGST` while other receipt strings use resources. The glossary permits some technical acronyms to remain invariant, so this is not proof that those acronyms must be translated; it is a compliance/review gap because the implementation does not explicitly resource-map or whitelist them.

**Recommended fix:** add print-resource keys/mappings or an explicit invariant-acronym policy, then add English/Gujarati receipt-byte or rendered-image assertions.

### P4-05 — POS hardware and resilience acceptance remains open

**Severity:** Open acceptance  
**Status:** Already acknowledged

Physical printer/cutter/drawer behavior, power-pull draft recovery, hardware timing, scaling/keyboard/scanner checks, and native Gujarati receipt review remain unchecked (`implementation.md:3473,3586`).

## Phase 5 — Money out, salary, assets, and depreciation

### P5-01 — Shared presentation defects affect money-out and asset screens

**Severity:** Medium  
**Status:** Confirmed incomplete

The parameterless numeric formatting and raw exception paths listed under P1-01/P1-02 are present in Phase 5 screens, especially `MoneyOutPage.xaml.cs` and `AssetsPage.xaml.cs`. This can produce inconsistent number precision/formatting and untranslated technical errors in expense, salary, and asset workflows even though the underlying accounting services are implemented.

**Recommended fix:** apply the shared formatter/error presenter before calling Phase 5 complete in a Gujarati UI run.

## Phase 6 — Reports and dashboard

### P6-01 — P&L Excel export is English-only for its section labels

**Severity:** High for Gujarati compliance  
**Status:** Confirmed output gap

`src/Modules/GroceryErp.Reports/ReportOutputService.cs:66-78` passes literal English labels (`Income`, `Net sales`, `Cost of goods sold`, `Operating expenses`, `Net profit`, etc.) to the Excel report. The on-screen P&L uses localized resource keys and the resource files contain Gujarati translations, so the export is inconsistent with the UI and with the requirement for localized report headings.

**Recommended fix:** inject/use `TranslationSource` for every P&L section/title/column label and test both cultures by reading the generated workbook.

### P6-02 — Generic reports and dashboard expose raw codes instead of localized labels

**Severity:** Medium  
**Status:** Confirmed incomplete

`ReportDefinitions.cs` selects raw database codes such as `Status`, `Type`, `Reason`, `Method`, `Direction`, and payment modes; `ReportService.GetReportAsync` localizes headings but does not translate row values before export/print. `DashboardPage.xaml.cs:69-71` interpolates `payment.Mode` directly. In Gujarati, users can therefore see values such as `OUTWARD`, `COMPLETED`, `CASH`, or movement/action codes untranslated.

**Recommended fix:** add explicit code-to-resource mappings for statuses, payment modes, movement types, report directions, depreciation methods, and reasons, and apply them consistently to grid, Excel, and A4 output.

### P6-03 — A4 pagination uses fixed rows and truncates content

**Severity:** Medium  
**Status:** Confirmed implementation gap

`implementation.md` §15.5 requires measuring rows and breaking when the page is full. `src/Modules/GroceryErp.Reports/ReportOutputService.cs:101-126` uses fixed `rowsPerPage` values (`22`/`27`) and `AddCell` uses a fixed 25-DIP height with `TextTrimming.CharacterEllipsis`. Long Gujarati names, notes, or report values can therefore be clipped instead of being measured and paginated.

**Recommended fix:** measure the actual WPF row visuals, wrap or size rows as needed, repeat headers after measured page breaks, and add a long-Gujarati A4 render test.

### P6-04 — “Complete” status is ahead of the code and verification state

**Severity:** Medium  
**Status:** Confirmed status mismatch

`implementation.md:3475` marks Phase 6 complete and claims full report/export coverage, but P6-01/P6-02/P6-03 remain, the current report-related files are uncommitted working-tree changes, and the Release suite could not be run in this environment.

**Recommended fix:** downgrade the phase status until localization/export fixes and a clean Release verification run are recorded.

## Phase 7 — Hardening, packaging, and release

### P7-01 — Automated release gate is not currently verifiable

**Severity:** High  
**Status:** Verification blocker

`global.json` pins SDK `10.0.401`, but no compatible SDK is installed locally. Both build and format commands failed before compilation. `.github/workflows/ci.yml` has the correct setup step, but `implementation.md:3469` says the first hosted run is still pending and `docs/release-checklist.md` has all automated release items unchecked.

**Recommended fix:** install/use the pinned SDK or run the hosted workflow, then record clean format, Release build, test, data-generator, full-scale performance, packaging, clean-PC install/update, and backup/restore evidence.

### P7-02 — Required full-scale and store acceptance work remains open

**Severity:** Open acceptance  
**Status:** Already acknowledged

The Phase 7 checklist leaves the five-year target-PC performance run, packaged FlaUI journey, native Gujarati review of all UI/printed artifacts, scanner/printer/cutter/drawer/power-pull/scaling/restore sign-off, and one-to-two-week pilot unchecked (`implementation.md:3573-3586`). These must remain release blockers.

## Recommended fix order

1. Fix P4-01 and P4-02 first because they affect scan correctness and stock integrity.
2. Replace the printing path with the required queued, mode-aware pipeline and add receipt/label regression tests.
3. Complete the module composition/architecture work so future features have the intended boundaries.
4. Centralize localized error handling, numeric formatting, and invariant-code translation across phases 1–6.
5. Resolve the Phase 3 backlog contradiction and downgrade/adjust phase status claims.
6. Restore the .NET SDK/CI gate, then perform the Phase 7 target-PC, hardware, Gujarati, packaged UI, restore, and pilot sign-offs.

