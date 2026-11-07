# Development Plan and Coding Practices — Grocery ERP

> **Revision contract (18 October 2026).** For the planned schema-29+ implementation, [revised-implementation-plan.md](revised-implementation-plan.md) §1–2 supersedes the ledger, lot edit/delete, purchase merge/cancel, sale return, and period-lock instructions below. Existing schema-28 code still follows the legacy ledger rules until the single verified cutover. New stock writes must then change authoritative `product_lot.qty_on_hand` through conditional service transactions, update bounded projections and source documents atomically, and never create generic movements. Completed sale and posted purchase snapshots are immutable. Calendar and fiscal annual locks apply independently. [Phase 0 behavior matrix](docs/phase0/behavior-matrix.md) and [dependency inventory](docs/phase0/dependency-inventory.md) specify the migration scope. No shipped migration 002–028 may be edited; new scripts begin at 029.

These rules apply to every change in this repository, whether written by a person or by an AI
coding agent. They describe **how** to write code. **What** to build is defined by:

| Document | Owns |
|---|---|
| `techstack.md` | Technology decisions, packages, versions, rejected alternatives |
| `revised-implementation-plan.md` | Single source of truth for the revised product behavior, phase scope, ordering and acceptance gates |
| `implementation.md` | Legacy architecture, module specs, algorithms, error codes (App. B), settings keys (App. C); revised decisions take precedence |
| Shipped SQL migrations | The implemented database schema at each version; new scripts start at 029 |
| `PLANS.md` (this file) | Coding practices, workflow, definition of done |

For revised product behavior, phase scope, ordering or acceptance gates, follow `revised-implementation-plan.md` and correct conflicting older text. Raise unresolved disagreements about coding practices or currently implemented schema rather than silently guessing.
Section references like "impl §5.6" point into `implementation.md`.

---

## 1. Project Non-Negotiables

- **Always offline.** No feature may require internet access at install time, first launch or run time.
- **Single user, single Windows PC.** No server, no network, no multi-user concurrency. One app instance at a time.
- **Stack:** .NET 10 LTS, C# 14, WPF + WPF UI, CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting,
  SQLite via Microsoft.Data.Sqlite, Dapper. **No EF Core.**
- **Three-decimal precision for every money, quantity and percent value** — `decimal` in code,
  `INTEGER × 1000` ("fx3") in the database. No binary floating point for business values, anywhere.
- **Languages:** exactly two — **English (India) `en-IN`, the default**, and Gujarati (India) `gu-IN`.
  Both complete, shipped in the offline package, switchable at run time.
- **All numbers are always English digits `0–9`**, whichever language is chosen — on screen, in
  input fields, on receipts and labels, in reports and exports.
- **Modular:** one project per business module; modules talk only through each other's `Contracts`.
- **Stock (schema 36):** a lot's quantity is authoritative and changes only with the business document that causes it; there is no stock ledger. Source documents reconcile every lot, and finalized sale and purchase snapshots remain immutable.

---

## 2. Engineering Priority

When implementation decisions conflict, use this order:

**Data Integrity → Financial Accuracy → Reliability → Offline Availability → Security →
Maintainability → Performance → User Experience → Implementation Convenience**

POS-critical performance targets (impl §17.1: scan → line < 50 ms, search < 100 ms, bill commit
< 100 ms) are **functional requirements**, not optional polish. They are traded away only for the
first four priorities.

---

## 3. General Engineering Principles

- Before writing anything new, check in this order whether the need is already met by:
  1. Existing project code (search the solution first)
  2. The .NET base class library
  3. Windows / WPF platform APIs
  4. Packages already listed in `techstack.md`
- Only then consider a new package (§4).
- Prefer simple, reliable, maintainable solutions over clever ones.
- Introduce an abstraction only when it solves a real problem that exists today.
- Reuse existing project conventions; a second way of doing the same thing is a defect.

**Not wanted in this project** (do not introduce without an explicit decision recorded in
`techstack.md`): EF Core or any ORM, MediatR, AutoMapper, generic repository / unit-of-work layers
over Dapper, service locator, reflection-based module scanning, Rx, event-sourcing frameworks,
background job frameworks, online telemetry or crash reporting.

---

## 4. Dependency Policy

Before adding any NuGet package:

1. Confirm the functionality doesn't already exist in the project or the BCL.
2. Confirm a first-party .NET / Windows solution doesn't exist.
3. Decide whether a small amount of internal code (< ~200 lines, well tested) does the job —
   e.g. EAN check digits, GS1 parsing, ESC/POS commands are internal on purpose.
4. Only then evaluate third-party packages, reviewing:
   - maintenance activity and release cadence
   - documentation quality
   - **license** (commercial-license changes have happened in popular .NET packages — recheck)
   - security history
   - package size and **transitive dependencies**
   - native binaries (avoid beyond SQLite's `e_sqlite3`)
   - Windows x64 / .NET 10 support
5. Verify it works **fully offline** after installation: no license servers, no telemetry, no
   runtime downloads.
6. Add it via Central Package Management (`Directory.Packages.props`) with an **exact pinned version**.
   No floating versions.
7. Update the package table in `techstack.md` §14 in the same commit.

Do not add a package merely to avoid writing a small amount of straightforward code.
Wrap any third-party library that touches untrusted input (Excel files, images) behind a small
internal interface so it can be replaced.

---

## 5. Architecture Practices

Keep the module anatomy from impl §2.3 in every module:

```
Contracts/       interfaces + DTOs that other modules may use
Domain/          pure logic, no IO
Application/     services: validation, transactions, events
Infrastructure/  repositories: Dapper SQL
Presentation/    ViewModels + Views
Resources/       Strings.resx (English), Strings.gu.resx (Gujarati)
```

Dependency rules (enforced by architecture tests, impl §18.9):

- `GroceryErp.Core` references only the .NET base class library: no project references, no runtime
  packages (not even `Microsoft.Extensions.*`), no WPF, no SQLite. Build-time analyzers are allowed.
  An architecture test checks its compiled references (impl §18.9).
- Dependency injection stays at the edges: only `UI.Common` (the `IModule` contract), the modules
  and `App` know about `IServiceCollection`. Core, Data, Localization and Printing classes declare
  constructor parameters and are registered by `App` (`AddFoundation`).
- Modules reference other modules **only** through `*.Contracts` namespaces.
- ViewModels never reference `Microsoft.Data.Sqlite` or repositories directly.
- Views contain no business logic.

**Where does this code go?**

| Concern | Single home |
|---|---|
| Money / quantity / percent arithmetic, rounding | `Core/Numerics` (`Fx3`, `Money`, `Quantity`, `Percent`, `MoneyMath`) |
| GST split, inclusive/exclusive tax | `Core` `Tax` |
| Bill pricing (discounts, allocation, round-off, savings) | `Core` `PricingEngine` |
| Landed cost | `Core` `LandedCost` |
| Depreciation | `Core` `Depreciation` |
| EAN / GS1 / weighing-scale parsing | `Core` barcode algorithms |
| Fiscal year, business date | `Core` `FiscalYear`, `IClock` |
| Lot quantity changes, FEFO | `Inventory` `ILotQuantities` — nowhere else |
| SQL | the owning module's `Infrastructure` repository |
| Transactions | Application services via `DbExecutor.WriteAsync` |
| Document numbers | `DocNumberService` inside the finalizing transaction |
| Duplicate-submission protection | `OperationGuard` (claim `OpId` first, inside the transaction) |
| Backup sets, verification, restore | `BackupService` (manifest + SHA-256, staged restore) |
| Display formatting | `DisplayFormatter` |
| User-visible text | `.resx` resources |
| Printer/scanner protocols | `GroceryErp.Printing`, `UI.Common` scan detector |
| Cross-module notifications | `WeakReferenceMessenger`, published **after** commit |
| Module contract (`IModule`, `NavItem`) | `UI.Common/Modularity` |
| DI registrations | each module's `IModule.Register`; foundation services in `App` (`AddFoundation`) |

Before creating a new function, utility, service, repository, control, converter or SQL query,
search for an existing one. Duplicated business logic is a defect even if both copies are correct today.

---

## 6. Implementation Workflow

For every task:

1. Read the requirement and the relevant `implementation.md` section.
2. Inspect the existing code in the affected module(s).
3. Identify affected modules and their contracts.
4. Identify database implications — verify table/column names in `db/*.sql`, never from memory.
5. Identify i18n implications (new English + Gujarati strings, number display, printed text).
6. Check for reusable functionality (§5 table).
7. Consider failure scenarios: power loss mid-operation, printer offline, invalid input, duplicate
   submission, insufficient stock, locked period.
8. Consider backward compatibility of data and settings.
9. Implement the smallest **complete** solution.
10. Add or update tests (§22).
11. Run locally — all must pass with zero warnings:

```powershell
dotnet format --verify-no-changes
dotnet build -c Release          # nullable + analyzers, warnings are errors
dotnet test -c Release
```

12. Verify manually where appropriate: at 1366×768, at 125% scaling, in **English and Gujarati**
    (checking every number stays in English digits), keyboard only, and with real hardware for
    scanning/printing changes.
13. Review your own diff for: duplication, unnecessary complexity, performance problems,
    data-integrity risks, architecture drift, hard-coded strings, `double`/`float`, `DateTime.Now`,
    numbers formatted with the UI culture.
14. Update documentation that changed (impl sections, Appendix A/B/C, `techstack.md`).

Do not make unrelated refactors while implementing a focused requirement unless needed for correctness.

---

## 7. C# and .NET Practices

- Nullable reference types on; treat nullable warnings as bugs, not noise. No `!` suppression
  without a comment explaining why the value cannot be null.
- File-scoped namespaces, one public type per file, `sealed` by default.
- Primary constructors for DI; `required`/`init` for DTOs.
- `async`/`await` end to end. **Never** `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` — they
  deadlock on the WPF UI thread.
- `async void` only for event handlers and `OnStartup`/`OnExit`, always wrapped in `try/catch`.
- Pass `CancellationToken` through every service method that queries data for a page.
- Business dates are `DateOnly`; timestamps are local `DateTime` from `IClock`.
- No static mutable state except the documented singletons (`TranslationSource`).
- Exceptions are not control flow; business-rule violations throw `DomainException(ErrorCode, args)`.
- Logging uses message templates, never string interpolation (CA2254).

**Banned APIs** (`BannedApiAnalyzers`, `BannedSymbols.txt`):

```text
# All projects (SystemClock carries the single documented suppression)
P:System.DateTime.Now;Use IClock
P:System.DateTime.Today;Use IClock.BusinessDate
P:System.DateTimeOffset.Now;Use IClock
M:System.Decimal.Round(System.Decimal);Default is banker's rounding - use Fx3.Round
M:System.Decimal.Round(System.Decimal,System.Int32);Default is banker's rounding - use Fx3.Round
M:System.Math.Round(System.Decimal);Default is banker's rounding - use Fx3.Round
M:System.Math.Round(System.Decimal,System.Int32);Default is banker's rounding - use Fx3.Round

# GroceryErp.Core only
T:System.Double;Business values use decimal/fx3 types
T:System.Single;Business values use decimal/fx3 types

# UI projects
T:System.Windows.MessageBox;Use IDialogs (localized ContentDialog)
```

Module `Domain`/`Application` namespaces can't be covered by a per-project ban (their `Views` need
`double` for layout), so the architecture tests enforce "no `double`/`float`" there (impl §18.9).

`decimal.Round(x, n)` without a `MidpointRounding` argument uses **banker's rounding**
(2.0025 → 2.002). This project uses half-away-from-zero everywhere; the ban prevents silent drift.

---

## 8. WPF and MVVM Practices

- Code-behind only for view plumbing (focus, keyboard routing, scan detector wiring).
- ViewModels: `[ObservableProperty]`, `[RelayCommand]`; async commands disable themselves while
  running (`IsBusy` / `CanExecute`) — the first defense against double submission. The guarantee
  is the operation ID (§9, impl §3.15): forms and dialogs create an `OpId` when they open and pass it
  with the save command; a new one is created only after a successful save.
- **The UI thread never touches the database.** All DB work goes through services built on `DbExecutor`.
- Page data loads in `OnActivatedAsync`, never in constructors.
- No hard-coded colors, sizes or fonts in pages — use tokens from `Themes/Tokens.xaml` and `AppFont`.
- No user-visible string literals in XAML — use `{l:Tr Key}`.
- No business logic in converters. Converters format; they never calculate money or stock.
- `DataGrid` rules (impl §4.5): virtualization on, never inside a `ScrollViewer`/`StackPanel`,
  no `Auto` column widths on large lists, row height fits two text lines.
- Subscriptions from transient objects to long-lived ones (messenger, singletons, static events)
  must be weak or unregistered — otherwise closed pages leak.
- POS: after any dialog closes, focus returns to the scan box. Every frequent action has a shortcut,
  documented in impl Appendix A.
- Destructive actions (void, write-off, restore, cancel purchase) require explicit confirmation and,
  where the spec says so, a reason.

---

## 9. Database Practices (SQLite + Dapper)

**Queries**

- Every value is a bound parameter. Never interpolate values into SQL. (Sole exception:
  `PRAGMA user_version = {int}` in the migration runner.)
- SQL lives in repositories as raw string literals with uppercase keywords and explicit column
  aliases (`selling_price AS SellingPrice`).
- No `SELECT *` in application queries; list the columns you use.
- Avoid N+1 queries: use joins or Dapper list expansion (`WHERE id IN @ids`).
- Every user-facing list query has a `LIMIT`; histories use keyset pagination (impl §4.5).
- Aggregate in SQL, not in C# over loaded rows.
- Query result DTOs are `sealed class` with `init` properties — not positional records (impl §3.4).

**Transactions**

- One user action = one transaction, opened by `DbExecutor.WriteAsync` (`BEGIN IMMEDIATE`).
- Never keep a transaction open across user interaction, printing, dialogs or `await` of anything
  other than the DB work itself.
- Side effects outside the DB (printing, drawer kick, messenger events) happen **after** commit,
  and are skipped when the result is a replay (`IsReplay`).
- **Every finalizing command carries an `OpId` and claims it first** with `OperationGuard.Claim`
  inside its transaction (migration 003, impl §3.15). A second submission with the same `OpId`
  returns the original result instead of saving again. Covered: bill completion, void, return,
  purchase post/cancel, vendor payment, damage, expense, salary run, stock count, adjustment,
  price change, asset disposal, imports. New finalizing commands must be added to `OpType`.

**Schema rules** (for new migrations)

- `STRICT` tables; `snake_case` names; fx3 columns are `INTEGER` with a `-- fx3` comment.
- Dates `TEXT 'YYYY-MM-DD'` with `CHECK (col = date(col))`; timestamps `TEXT 'YYYY-MM-DD HH:MM:SS'`;
  flags `INTEGER` with `CHECK (col IN (0,1))`.
- Foreign keys on every reference; composite FKs where two columns must agree (lot ↔ product).
- Store **codes**, never translated text (`'COMPLETED'`, `'UPI'`, `'SALES'`).
- History tables carry **snapshots** (names, prices, rates, costs) instead of relying on joins.
- Add an index only for a real query pattern, and add a test asserting the plan uses it
  (`EXPLAIN QUERY PLAN`). Don't index columns "just in case".
- Constraints and triggers are the last line of defense; services validate first with localized messages.
- Never disable `foreign_keys`, never switch off `synchronous = FULL`, never bypass triggers in
  application code.

---

## 10. Migration Practices

- Every schema change is a new file `db/NNN_short_name.sql`; the runner applies it in a transaction
  and sets `PRAGMA user_version`.
- **Shipped migrations are never edited.** Fix mistakes with a new migration.
- Migrations are forward-only. The rollback strategy is the automatic **pre-migration backup**.
- Never delete or reset user data to resolve a schema incompatibility. Use the SQLite 12-step table
  rebuild (copy data into the new shape) when `ALTER TABLE` is not enough.
- New `NOT NULL` columns need a `DEFAULT` that is correct for existing rows.
- New settings keys: seed in the migration **and** give a code default (impl §3.10).
- Each migration ships with tests: fresh DB → latest, and a previous-version DB **with data** →
  latest, asserting data is preserved (impl §18.7).
- Record the new schema version in the release notes.

---

## 11. Numeric and Financial Practices

- Business values use `Money`, `Quantity` and `Percent` — never raw `decimal` in service or domain
  signatures, never `double`/`float` anywhere.
- Conversion to/from fx3 integers happens **only** at the database boundary (Dapper type handlers).
- Rounding: half away from zero, to 3 decimals, **once per operation** (impl §3.6 table).
- When parts must add up to a total, compute one part and **derive the other by subtraction**
  (taxable/tax, CGST/SGST, final-month depreciation). Distribute amounts with `MoneyMath.Allocate`.
- Single authoritative implementation for each calculation (§5 table). UI screens, reports and
  exports call these — they never re-implement them.
- The UI's numbers are for display only; finalizing services **recompute** from stored inputs
  (e.g. bill completion re-runs `PricingEngine`).
- Parsing user numbers: `CultureInfo.InvariantCulture`, `.` as decimal separator, max 3 decimals.
- Never use string formatting to round a value.
- Financial tests assert **explicit expected values** (impl §3.6 and §13.2 worked examples),
  including rounding boundaries (`x.xxx5`), ties in allocation and zero/negative guards.

---

## 12. Inventory Practices

A lot's quantity is authoritative. Schema 36 removed the stock ledger (`stock_movement`) and the old count sheets; the business documents are the stock record.

- Lot quantity changes **only** through `ILotQuantities.Change` (or `CorrectDamage`), with the
  `LotChangeKind` of the document that causes it. Nothing else writes `product_lot.qty_on_hand`.
  Each change is one conditional update that never leaves a lot negative and, for a sale, needs an
  active lot that expiry blocking allows.
- Every change runs inside the same transaction as its document, which records the quantity and
  cost: sale, return, purchase, vendor return, damage, internal use or direct origin. Only a damage
  correction, which has no other document, also saves an immutable `stock_correction`.
- FEFO allocation lives only in `ILotQuantities.AllocateFefo`; callers pass what is already
  reserved on the current bill.
- Prevent duplicate changes: every finalizing command claims its `OpId` (impl §3.15), and
  finalizing services also check the document status **inside** the transaction
  (DRAFT → COMPLETED/POSTED), so a repeated attempt replays the original result or fails with
  `SaleFinalized` / `PurchaseFinalized` — it never posts twice.
- Corrections are new documents (void, return, damage, damage correction, reviewed count) — never
  edits or deletes of posted ones.
- Expired lots are excluded from sale unless the setting says otherwise.
- Source-document reconciliation (`LotSourceReconciliation`) checks lots: a reviewed count's
  affected lots inside its write, and every lot in weekly maintenance. Never rebuild lot
  quantities from anything else.

---

## 13. Accounting, P&L, Depreciation and Salary Practices

**Target rule:** calendar years close from 8 January and fiscal years from 8 April, with the seven dates after each year end editable. Apply both locks independently to dated writes and corrections. Open reports use source records or bounded summaries; a closed annual report uses saved head/category/month snapshots. Post later corrections in an open current period. Do not offer a recalculate-year choice. The legacy `books.locked_until` rule below describes schema 28 only.

**One accounting path**

- P&L reads **only** `v_pnl_entry`. Reports, dashboard tiles and exports use the same service —
  no parallel totals computed elsewhere.
- Heads are codes (`SALES`, `COGS`, `STOCK_ADJUSTMENT`…) translated in the UI.
- Revenue and COGS come from bill/return snapshots; purchases go to stock, not to P&L; buying an
  asset is not an expense (its depreciation is).
- Historical figures must be reproducible: nothing recalculates closed periods from current
  configuration (current prices, rates, names, tax settings).
- Corrections use explicit reversal documents (void, return, adjustment, reversing entry).
- `books.locked_until` is enforced by every service that writes dated records.
- Tests verify that monthly totals reconcile exactly to yearly totals.

**Depreciation**

- Calculated only by `Core` `Depreciation`; posted monthly by the posting job; posted
  `asset_depreciation` rows are **never recomputed or rewritten**.
- Changing an asset's rate or method affects **unposted** months only.
- Assets with posted depreciation are disposed, not deleted.
- The term is **depreciation** in code, UI and resources.

**Salary and employee expenses**

- `salary_payment` rows are the salary history; `employee.monthly_salary` is only a pre-fill default.
  Changing it never alters past payments.
- Salary is charged to `period_month` (month worked), not the pay date.
- Employees with payments are deactivated, never deleted; history, audit and report references remain.

---

## 14. Reliability Practices

Assume any operation can be interrupted by a crash, forced termination, power loss, full disk or
a database error.

- Durable writes: WAL + `synchronous = FULL` on every connection.
- POS drafts are saved on **every** change; after a restart, drafts are offered for resumption.
- Business operations are atomic — never leave a half-completed sale, purchase, return or count.
- A printer failure never rolls back a sale; reprint is always available.
- Never silently reset, recreate or delete the database or user data after an error. Show the
  problem, log it, and offer restore from backup.
- Take a backup before migrations, restores and updates (impl §16.1).
- A backup is a **backup set**: one `.zip` with the snapshot and a `manifest.json` (app and schema
  version, size, SHA-256, integrity result, summary). It is written as `.partial`, renamed only when
  complete, and re-verified after creation and after every copy to USB (impl §16.2–§16.4).
- Restore stages and fully verifies the backup in a temporary folder (manifest, checksum,
  `integrity_check`, foreign keys, schema version) before touching the live database, and keeps
  the replaced database until the restored one has started successfully (impl §16.5).
- Never extract zip entries by their stored path; extract known entries to fixed file names.
- Respect the startup checks: capability check, clock sanity, free disk space.
- Compaction (`BackupService.Compact`) runs only between sign-in and the shell, copies with
  `VACUUM INTO`, proves the copy holds every row and the same totals, saves it as a verified
  backup and swaps it in through the restore markers. Never `VACUUM` the live store.
- Single-instance guard stays in place.

---

## 15. Performance Practices

- Design for an ordinary shop PC (4 cores, 8 GB, SSD, 1366×768).
- Prefer indexed queries, keyset pagination, lazy page creation, virtualized lists, database-side
  filtering and aggregation, streaming for large imports/exports, background work for non-critical jobs.
- Measure before optimizing: BenchmarkDotNet against the 5-year synthetic dataset (impl §17.2).
- Expensive work never blocks the UI thread; anything > 300 ms shows progress, > 2 s is cancellable.
- A > 20% regression on a benchmarked path blocks the release (impl §17.4).
- Avoid premature optimization; no caches without a measured need and an invalidation rule.

---

## 16. Search Practices

- **Exact paths first:** barcode, SKU, invoice number, vendor bill number → single indexed lookups.
  Keep them separate from text search.
- Text search uses SQLite FTS5 (`trigram`); queries of 1–2 characters use an indexed prefix `LIKE`.
- No external or second search engine unless benchmarks prove FTS5 cannot meet the targets.
- The FTS table is **not** a source of truth. It is kept in sync by triggers and must be rebuildable
  from `product` at any time (`DELETE FROM product_fts` + re-insert from `product`, one transaction).
- Normalize identically on write and on query (NFC, whitespace, zero-width joiners, Gujarati
  digits → `0–9`). Search normalization never depends on the UI language.
- Debounce free-text input (150 ms), cancel stale queries, limit results (fetch 100, show 30),
  rank in the database, re-rank in memory only by the popularity map.
- Never load full tables into memory to filter them.
- Benchmarks cover: barcode, SKU, product name (English and Gujarati), partial text, vendor,
  customer, invoice search. Search regressions on POS paths are functional regressions.

---

## 17. Barcode, Scanner, Printing and Hardware Practices

- Barcodes are `TEXT`. Never convert them to numbers or strip leading zeros.
- Check-digit logic and internal barcode generation exist once (`Core` `Ean`); internal prefixes
  come from settings (`20` lot, `21` product).
- Scanner input is normalized at the input boundary (`InputNormalizer`); domain code receives
  ASCII strings.
- Scan and numeric fields force the English input layout (impl §5.6).
- Hardware access sits behind interfaces (`IReceiptPrinter`, `ILabelPrinter`, `ICashDrawer`) with:
  - production implementations (ESC/POS, TSPL via winspool),
  - a **file implementation** that writes the raw bytes and a PNG preview to disk — for development
    without hardware and for snapshot tests.
- Printing runs through the background print queue, never inside a DB transaction, never on the UI
  thread except the brief WPF render step.
- Do not assume printer fonts support Gujarati or `₹`; use the raster path (impl §15.3).
- Printed numbers are English digits in both print languages; print visuals carry the
  `NumberSubstitution` setters (impl §5.5).
- Every change to printing or scanning is verified on real hardware before release (impl §18.11).

---

## 18. Internationalization Practices (English default + Gujarati)

Localization is part of every phase and every user-facing change, not a final cosmetic pass.

### Languages

- Exactly two UI languages: **English (India) `en-IN` — the default** and **Gujarati (India) `gu-IN`**.
  Do not add resources, settings or code paths for any other language.
- First launch (including the first-run wizard) is always in English. The user can switch to
  Gujarati at any time; the choice is saved in `ui.language`.
- Printed bills/labels have their own language setting, `print.language`, also defaulting to English.

### The numbers rule

**Every number stays in English digits `0–9`, whatever language is chosen.** This covers money,
quantities, weights, rates, MRP, GST %, discounts, dates, times, invoice/lot/document numbers,
barcodes, SKUs, HSN codes, phone numbers, GSTIN, pincodes, page numbers, counts inside Gujarati
sentences, chart axes, receipts, labels, A4 documents, PDFs and Excel exports.

- Format every number with `TranslationSource.FormatCulture` (fixed `en-IN`) or `InvariantCulture`,
  through `DisplayFormatter`. **Never** format a number with the UI culture, and never rely on a
  parameterless `ToString()` in presentation code.
- The format culture is a constant in code, not a setting. Switching to Gujarati changes only
  `CurrentUICulture`; `CurrentCulture`, `FormatCulture` and the WPF `Language` stay `en-IN`.
- Pin digit rendering with `NumberSubstitution` (European) on every window, popup root and print
  visual (impl §5.5), so Windows regional settings can never draw `૧૨૩`.
- Numbers are never typed into translations; they arrive through `{0}` placeholders already
  formatted. A Gujarati digit (`૦–૯`, U+0AE6–U+0AEF) in any resource value fails the tests.
- Input: `InputNormalizer` converts Gujarati digits to `0–9` in **every** text field as the user
  types (numeric, scan, names, phone, address, notes), and services normalize again before saving,
  so imports and pasted text follow the rule too.
- Numeric entry: invariant parsing, `.` decimal, no grouping, max 3 decimals; the input hint is
  localized. Barcodes, SKUs and leading zeros are never altered.
- Month and weekday **names** follow the UI language; the digits next to them do not
  ("સપ્ટેમ્બર 2026").
- Machine-readable files (backups, import templates, logs) stay culture-invariant.

### Resources

- All application-owned text lives in `.resx`: labels, buttons, menus, tooltips, accessibility names
  (`AutomationProperties.Name`), confirmations, validation, status, errors, printed labels, report
  headings, help text.
- The one exception is screen help (revised plan §16): each screen's help is one Markdown file per
  language in `docs/user-guide/help/{en,gu}/`, embedded in the app and shared with the user guide.
  Tests hold it to the same rules: both languages, the same shape, English digits, and bold field
  names that match the labels on screen.
- `Strings.resx` holds English (neutral, the fallback); `Strings.gu.resx` holds Gujarati.
- Keys follow `{Prefix}_{Area}_{Item}` (impl §5.2), are stable and descriptive, and are never reused
  for a different meaning.
- Translate whole sentences with numbered placeholders; never build sentences from fragments.
  Use `_One` / `_Other` keys for counts.
- English and Gujarati key sets and placeholder sets must be identical (automated tests). English
  fallback is a safety net, **not** permission to leave Gujarati missing.
- Machine-drafted Gujarati is marked in the `.resx` comment (`MT – needs review`) and listed in the
  phase status until a Gujarati-speaking reviewer has approved it.
- Follow the glossary (impl §5.11) — e.g. lot/batch is **બેચ**, never લોટ (flour).

### Culture setup

- UI culture, format culture and WPF `Language` are set before any window is created (impl §5.4).
- UI language never changes currency, precision, tax, rounding, financial year, business-day or
  valuation rules.
- Background tasks inherit `en-IN` formatting via `CultureInfo.DefaultThreadCurrentCulture`, set once
  at startup.

### Data

- Codes stay invariant (statuses, payment modes, movement types, P&L heads, setting keys, audit
  actions, file formats); translation happens in presentation mappings (`Enum_*`, `Pnl_Head_*`).
- `name` is English, `name_local` is Gujarati. User text is stored as entered apart from the
  documented hygiene: Gujarati digits → `0–9` and NFC for all text; whitespace and zero-width
  joiner cleanup for names (impl §5.7). Never auto-translate business data.
- Historical snapshots (bill lines, Gujarati names on bills) are never re-localized.

### Layout, input and printing

- Font stack `Segoe UI, Nirmala UI`; no fixed heights that clip Gujarati vowel signs or conjuncts;
  no fixed label widths; verify at 100/125/150% scaling with the conjunct test strings (impl §5.5).
- Gujarati-name fields switch to the Gujarati Phonetic keyboard; scan and numeric fields force English.
- Gujarati receipts and labels use the raster path and are checked on the real printer, including
  that every number prints in English digits.
- Windows-owned UI (file pickers, system dialogs) follows the Windows display language; prefer
  in-app localized dialogs and test this boundary on the deployment PC.

### Engineering hygiene

- All text files are UTF-8 (`.editorconfig`: `charset = utf-8`). `.resx` files declare UTF-8.
- Scripts that read or write resources must set the encoding explicitly. **Windows PowerShell 5.1
  defaults to the ANSI code page for BOM-less files** and will corrupt Gujarati text — use
  PowerShell 7 or `-Encoding utf8`. Re-run the i18n tests after any scripted resource change.
- Tests that depend on culture use a scope helper and never set process-wide defaults
  (`CultureInfo.DefaultThreadCurrent*`), because xUnit runs test classes in parallel:

```csharp
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    public CultureScope(string culture, string? uiCulture = null)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(uiCulture ?? culture);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }
}
```

- Tests touching the `TranslationSource` singleton use `Get(key, culture)` or run in a
  non-parallel test collection.
- Culture-switch tests prove that arithmetic, stored values, search results, document numbers and
  every displayed or printed number are identical in English and Gujarati.

---

## 19. Error Handling and Logging

- Never swallow exceptions. Every `catch` either handles the error meaningfully or rethrows.
- Expected failures are `DomainException(ErrorCode, args)`; each code has an `Error_*` resource and
  an entry in impl Appendix B.
- SQLite constraint errors are translated by `SqliteErrorMapper`; an unmapped constraint error is a
  bug — add the mapping and a test.
- User messages are localized, actionable and free of stack traces or SQL.
- Logs (Serilog, English) hold the technical detail: operation, IDs, error, stack trace.
- Never log passwords, password hashes, full bank account numbers, or bulk customer contact data.
- After an error the application state must remain valid: the transaction rolled back, the page
  usable, the draft bill intact.

---

## 20. Security Practices (single user, offline)

- Passwords are hashed with Argon2id (impl §6.2); plaintext is never stored or logged.
- There is no password recovery by design, therefore the database is **not** encrypted (a forgotten
  password must not destroy the store's records). Theft protection is BitLocker.
- There are no roles. Sensitive operations — void, return refund, price override, stock
  adjustment, purchase cancel, period-lock change, restore, settings changes — are protected by
  confirmation, a reason where specified, and an **audit entry**, all enforced in services, not
  only in the UI.
- Idle auto-lock stays on by default.
- Validate every imported file: `.xlsx` only, size limit, row limit, opened read-only, clear message
  when the file is locked by Excel, per-row validation before any write.
- Exports write user text as text, never as formulas.
- Validate paths for backup, restore and data-folder moves; reject OneDrive-synced locations.
- Never execute files, scripts or content supplied by the user.

---

## 21. Settings and Audit Practices

- Setting keys are lowercase, dot-separated (`printer.receipt.width_dots`), values are invariant strings.
- Every new key: code default + impl Appendix C entry (+ migration seed if needed).
- Settings are read through the typed `AppSettings` snapshot — no ad-hoc `SELECT` from `setting`.
- Audit (`IAuditLog`) every change listed in impl §3.12, inside the same transaction as the change,
  with an old/new JSON diff.

---

## 22. Testing Practices

**Priorities:** pricing, tax, discounts and allocation, rounding, bill completion, returns and
refunds, voids, FEFO and stock movements, purchase receiving and landed cost, stock counts,
depreciation, salary, P&L, migrations, backup and restore, i18n parity, barcode parsing, printing output.

**Edge cases to cover:**

- zero and negative quantities; fractional qty on a whole-unit product; very large quantities and amounts
- rounding boundaries (`x.xxx5`), allocation ties, 100% discounts
- duplicate submissions: same `OpId` twice, two parallel calls, retry after a failed attempt
- backup sets that are tampered, truncated, missing the manifest, or newer than the app
- insufficient stock, expired lots, a scan spanning two lots
- interrupted transactions (process killed mid-completion → nothing half-written)
- partial returns, repeated returns beyond the sold quantity
- financial-year boundary (31 March / 1 April), leap years, `business.day_start_hour`
- Gujarati names with vowel signs and conjuncts in search, sorting and printing
- Gujarati digits typed or pasted into numeric, scan and text fields
- locked periods

**Rules:**

- Integration tests use real temporary SQLite files with all migrations applied (impl §18.2).
- Time comes from a fake `IClock`; no `Thread.Sleep`, no dependence on the real date.
- Financial tests use explicit expected values; the golden "month in the life" scenario must stay green.
- Property-based tests guard invariants (impl §18.3).
- Snapshot tests (Verify) for receipt bytes/images and TSPL output; review snapshot diffs, never
  accept them blindly.
- Every bug fix includes a regression test.
- Never weaken, skip or delete a test to make incorrect code pass.

Naming: `Method_Scenario_ExpectedResult` (e.g. `Complete_WhenPaymentShort_ThrowsPaymentShort`).

---

## 23. Git Practices

- Small, focused commits; one coherent change per commit.
- Conventional messages: `feat(sales): hold and resume bills`, `fix(inventory): FEFO skips blocked lots`,
  `i18n(gu): purchase screen strings`, `perf(search): covering index for SKU lookup`,
  `db: 003 customer receipts`. Never "update", "fixes", "changes", "stuff".
- No unrelated formatting changes in feature commits.
- `.gitignore` must cover: `bin/`, `obj/`, `.vs/`, `*.user`, `publish/`, `Releases/`, `logs/`,
  `*.db`, `*.db-wal`, `*.db-shm`, generated test output.
- Never commit: store databases or backups, customer or employee data, credentials, signing
  certificates, build artifacts, debug output, machine-specific paths.
- Never force-push, hard-reset, rewrite history or delete branches unless explicitly requested.
- One branch per feature or phase; merge only with a green build and the Definition of Done met.

---

## 24. AI Coding Agent Behaviour

When an AI coding agent works on this project:

- Read this file and the relevant `implementation.md` section before changing code.
- Inspect existing code before modifying it. Never assume a file, class, method, table, column,
  setting key, resource key or package exists — verify it.
- Take table and column names from `db/*.sql`, not from memory or from documentation examples.
- Do not rewrite working code unnecessarily or introduce a new pattern where one already exists.
- Never edit a shipped migration; never change fx3 storage, rounding rules or the numeric types.
- Never add a package without following §4 and updating `techstack.md`.
- Never delete user data, reset the database or disable constraints/triggers to make something work.
- Never sacrifice data integrity or financial correctness for shorter code.
- Never bypass validation, tests, analyzers or nullable checks to make a feature look complete.
- Add every new string in English and Gujarati; mark drafted Gujarati as `MT – needs review`.
- Never format a number with the UI culture or put a Gujarati digit in a resource.
- Fix root causes; no temporary workarounds left in finished work.
- If the spec is ambiguous or conflicts with this file, stop and ask.

Progress messages stay short:

- what will be changed
- what was changed
- important architecture or database decisions
- tests run and results
- remaining issues or open questions

---

## 25. Documentation Practices

- `implementation.md` is updated in the same change when behaviour, algorithms, error codes,
  settings, shortcuts or the schema change.
- Technology changes are recorded in `techstack.md` with the reason and the rejected alternative.
- Public members of `*.Contracts` and `Core` carry XML doc comments.
- Comments explain **why**, not what. No commented-out code blocks, no stale TODOs
  (a TODO names a follow-up issue or phase).
- Release notes per version, per language (`CHANGELOG.<lang>.md`), stating the schema version.

---

## 26. Guardrails for Future Features

These are not in v1 scope, but if they are added:

- **UPI payment QR** (store VPA, optional amount) is generated locally with the existing ZXing.Net
  package, validated before encoding (`upi://pay?pa=…&pn=…&am=…&cu=INR`), and contains nothing
  beyond what the UPI format requires. A QR failure must not block billing.
- **Document attachments** (vendor bill scans, asset invoices) live in a configurable local folder
  with metadata in the DB (relative path, hash, type, size) — not as blobs in the operational
  database. Validate paths stay inside the folder, verify type and existence, open files with the
  Windows default viewer, never execute them; a missing file must not crash the app.
- **Customer credit (udhaar)** is a ledger with receipts and running balance — never a mutable
  "balance" column.
- Any feature needing the network requires an explicit decision recorded in `techstack.md`; the
  offline workflow must keep working without it.

---

## 27. Definition of Done

A change is complete only when:

- It satisfies the requirement and the relevant `implementation.md` section.
- Existing functionality still works; all tests pass; build has zero warnings.
- New critical logic has tests, including a failure path.
- Migrations are included and tested where the schema changed.
- Services validate input and map every failure to an `ErrorCode` with localized messages.
- Sensitive operations are confirmed and audited where required.
- It works fully offline.
- **All user-facing text exists in English and Gujarati** (Gujarati reviewed or marked
  `MT – needs review`), including validation and accessibility text; layout verified in both.
- **Every number on the new screen, print output or export shows English digits** with the UI
  and print language set to Gujarati.
- Arithmetic, stored values and search results are verified culture-independent where applicable.
- It works by keyboard, at 1366×768 and 125% scaling.
- No new dependency was added without §4.
- No debug code, commented-out blocks or temporary hacks remain.
- It follows the existing architecture and the rules in this file.
- Finalizing commands carry an `OpId` and are covered by duplicate-submission tests.
- It has been reviewed for data-integrity and financial-correctness risks.
- Documentation (impl sections, appendices, `techstack.md`) is updated.
