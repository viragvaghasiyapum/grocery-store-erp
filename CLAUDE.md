# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

An offline, single-user, single-PC grocery ERP for one shop: WPF on .NET 10, SQLite through Dapper, English (default) and Gujarati UI. Every money, quantity and percent value is fixed-point with three decimals (`decimal` in code, `INTEGER × 1000` in SQLite).

## Which document wins

| Document | Role |
|---|---|
| `revised-implementation-plan.md` | **Single source of truth** for product behavior, phase scope, ordering and acceptance gates. Its dated execution records say what is actually done. |
| `PLANS.md` | Coding practices, workflow, Definition of Done. Read it before changing code. |
| `techstack.md` | Packages and versions. Any new package must follow PLANS.md §4 and be added to techstack.md §14. |
| `implementation.md` | Legacy spec, error codes (App. B), settings keys (App. C). Revised decisions override it. |
| Root `*.sql` migrations | The real schema. Take table and column names from these, never from docs. |

`optimization*.md`, `audit.md`, `revised plan.md` and `luna/` are history. Don't treat them as current requirements.

**Status (22 Oct 2026):** the old plan is fully implemented. The revised plan is implemented through **Phase 5** on **schema 33**, including the single live direct-stock cutover. Phase 6 is next (P/L heads, independent calendar/fiscal annual locks and snapshots, stock reports), then Phase 7 (UI simplification), Phase 8 (remove `stock_movement`, the ledger screens and dead code, then compact the database) and Phase 9 (scale and release). Hardware, target-PC, native-Gujarati and operator sign-offs are human gates. Never claim them as passed.

## Commands

The SDK is pinned to 10.0.401 by `global.json`. A repo-local SDK in `.tools/dotnet` is used if present, and NuGet restores into `.packages/`.

```powershell
dotnet restore GroceryErp.slnx
dotnet format GroceryErp.slnx --verify-no-changes --no-restore   # CI gate
dotnet build GroceryErp.slnx -c Release --no-restore            # warnings are errors
dotnet test GroceryErp.slnx -c Release --no-build               # ~200 tests; 2 opt-in UI journeys skip
```

The test runner is **Microsoft.Testing.Platform** with xUnit v3 (set in `global.json`). Use `--project` and the xUnit v3 filters. Older docs use VSTest-style `--filter "FullyQualifiedName~..."`; use the forms below instead:

```powershell
dotnet test --project tests/GroceryErp.Integration.Tests/GroceryErp.Integration.Tests.csproj -c Release --filter-class "*SalesTests"
dotnet test --project tests/GroceryErp.Integration.Tests/GroceryErp.Integration.Tests.csproj -c Release --filter-method "*Return*"
```

Run the app against generated demo data. Do not use the real `%LocalAppData%\GroceryErpData` store:

```powershell
./Start-Demo.ps1        # makes a temp store with DataGen, sets the login, builds and launches; login owner / scale-audit-2026
dotnet run -c Release --project tools/GroceryErp.DataGen -- --scale=0.001 --output=artifacts/data/x.db
dotnet run -c Release --project tools/GroceryErp.ScaleAudit -- <data-root> verify   # also: migrate, ui-setup, many profiling modes
./scripts/build-release.ps1 -Version 1.2.3   # self-contained R2R publish + Velopack pack into artifacts/
```

`tools/GroceryErp.ScaleAudit` (and `PasswordHashTool`) are **not in the solution**, so the solution build won't catch breaks there. After changing a service signature, build ScaleAudit too.

Opt-in UI tests: `PackagedOperatorJourneyTests` needs `GROCERY_PACKAGED_UI_TEST=1` after packaging. `ScaleUiTests` needs `GROCERY_SCALE_UI_ROOT` and `GROCERY_SCALE_UI_EXE`, with a root under `%TEMP%\grocery-erp-packaged-ui-*` that has been prepared by ScaleAudit `ui-setup`. The app's `--pseudo` argument turns on pseudo-localization.

## Architecture

### Project graph

`Core` (plain `net10.0`, BCL only; an architecture test checks this) ← `Localization` ← `Printing` / `UI.Common` ← `Data` ← `Modules/*` ← `App`. `Catalog`, `Expenses`, `Purchasing` and `Sales` reference `Inventory` directly.

**The code doesn't match the module anatomy in PLANS.md §5** (`Contracts/Domain/Application/...`):

- Modules are flat: `XxxModule.cs` (`IModule.Register` for DI, plus nav items), `XxxService*.cs` (services and their SQL together) and `XxxModels.cs` (DTOs). Partial classes split large services, e.g. `SalesService.ReturnRecreation.cs`.
- Every WPF page and window lives in `src/GroceryErp.App/Presentation/` with code-behind. Modules hold no views.
- All UI text is in one shared pair of files: `src/GroceryErp.Localization/Resources/Strings.resx` and `Strings.gu.resx`. Every module's `IModule.Strings` returns `SharedStrings.ResourceManager`. XAML uses `{l:Tr Key}`, and code uses `TranslationSource.Instance["Key"]`.

Follow the structure that exists. Don't create the PLANS.md folders for one feature.

The composition root is `App.xaml.cs` `OnStartup`. It takes the single-instance mutex, resolves the data root (`SettingsBootstrap`/`AppPaths`), registers the foundation services, calls `IModule.Register` for each module, runs `StartupPipeline` (migrations and checks), then shows first-run setup and login, then `ShellWindow`.

### Database access

`DbExecutor` in `Data/Infrastructure/SqliteInfrastructure.cs` is the only way services touch SQLite:

- `ReadAsync(Func<SqliteConnection,T>, ct)` runs on the thread pool, at most 4 concurrent reads. Cancellation interrupts SQLite.
- `WriteAsync(Func<DbTx,T>, ct)` uses one writer gate and `BEGIN IMMEDIATE`. The delegate runs once and is never retried.
- `SqliteException`s are converted by `SqliteErrorMapper` into `DomainException(ErrorCode, …)`.

A finalizing write follows this shape (see `SalesService.CompleteAsync`):

1. Inside `WriteAsync`, call `OperationGuard.Claim(tx, opId, OpType.X)`. A non-null result is a replay, so return the original result.
2. Validate, including `PeriodGuard` for dated writes.
3. Get the document number from `DocNumberService.Next(tx, …)`.
4. Write the rows, change stock, and call `IAuditLog.Write(tx, …)`.
5. Call `OperationGuard.Complete(tx, …)`.
6. After commit, and only if it wasn't a replay, send the `WeakReferenceMessenger` messages and do any printing.

### Stock after the Phase 5 cutover (easy to get wrong)

- A lot's `product_lot.qty_on_hand` is authoritative. Each source document (sale line, purchase line, return, damage, internal use, vendor return, direct origin) is its own stock record.
- `IStockLedger.Post` in `Inventory/StockLedger.cs` keeps its old name but is now the **single writer of lot quantity**. It does a conditional `UPDATE` (no negative stock, active and not-expired for sales) inside the caller's transaction. It writes **no** `stock_movement` rows; the trigger `trg_direct_no_movement` rejects them. Only `ADJUSTMENT` saves a row, an immutable `stock_correction`.
- PLANS.md §12 bullets about appending movements describe schema 28 and are obsolete.
- `stock_movement` stays read-only history until Phase 8 drops it.
- `DirectStockEngine` handles reviewed physical counts (a deficit becomes categorized damage, a surplus becomes a new direct-origin lot) and source-document reconciliation (`CheckLot`, `CheckAll`, `Data/Infrastructure/LotSourceReconciliation.cs`).
- FEFO allocation is `IStockLedger.AllocateFefo`.

### Projections and immutability live in SQL triggers

These summaries are maintained by triggers defined in the migrations: `product_stock_total`, low-stock, `pnl_day`, `sale_report_day`, `sale_product_day`, the hourly and dashboard tables, the vendor balance, and the per-sale return progress. Writing the source rows updates them. Don't also update them from C#.

Triggers also enforce immutability: completed sale financials, posted purchase and return lines, damage and corrections, no deleting a lot or product that has positive stock, and capping return quantity at the sold quantity. A constraint failure usually means the service is missing a validation. Add the service check (with a localized `ErrorCode`) and a `SqliteErrorMapper` mapping. Never work around the trigger.

### Migrations

- The scripts sit at the **repo root**, not in a `db/` folder, though PLANS.md says `db/*.sql`. `grocery_erp_schema.sql` is 001, followed by `002_…sql` through `033_direct_lot_stock.sql`.
- Each script is embedded through an explicit `<EmbeddedResource … Link="db/NNN_name.sql">` in `src/GroceryErp.Data/GroceryErp.Data.csproj`.
- To add a migration:
  1. Create root `034_name.sql`.
  2. Add its `EmbeddedResource` line.
  3. Bump `MigrationRunner.LatestVersion`. The runner requires a contiguous 001..Latest sequence.
- The runner strips `PRAGMA user_version` lines from scripts and sets the version itself. It takes a pre-migration backup automatically.
- Some versions have C# hooks around the SQL: 29 (`PhaseOneMigration`) and 33 (`DirectStockCutover`).
- Never edit a shipped migration.

## Tests

- Integration tests run against real temporary SQLite files migrated to `MigrationRunner.LatestVersion`. Assert against that constant, never a literal version.
- Each test class defines its own private fixture (`SalesFixture`, `StockInFixture`, …). There is no shared helper; copy the nearest fixture's pattern.
- `Localization/LocalizationTests` enforces identical English/Gujarati key and placeholder sets, and no Gujarati digits (૦–૯) in any resource.

## Rules that bite

These are the full rules from PLANS.md, condensed.

- **Banned APIs** (`BannedSymbols.txt`, enforced at build): `DateTime.Now`/`Today` (use `IClock`), and `decimal`/`Math.Round` without a `MidpointRounding` argument (use `Fx3.Round`). `double`/`float` are banned for business values. Use `Money`/`Quantity`/`Percent` from `Core/Numerics`.
- Numbers always display in English digits. Format through `DisplayFormatter`/`TranslationSource.FormatCulture`, never the UI culture.
- Every new string goes in **both** resx files. Mark machine-drafted Gujarati with the comment `MT – needs review`. Batch/lot is **બેચ**, never લોટ.
- Windows PowerShell 5.1 corrupts BOM-less UTF-8. Don't script edits to `.resx` or Gujarati text with it; use the Edit tool or pass `-Encoding utf8`.
- When behavior changes, update `revised-implementation-plan.md` (its phase execution record) and the user guides `docs/user-guide/english.md` and `gujarati.md` in the same change.
- `artifacts/` (fixtures, evidence, multi-GB databases), `*.db` files, `.tools/` and `.packages/` are gitignored. Never commit store data.
