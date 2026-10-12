# Phase 0 performance baseline

Run from the repository root with .NET SDK 10.0.401. Keep the original `audit-full-valid` and `audit-strong-1.35` fixtures unchanged. All write profiles and realism seeding must use a disposable copy.

## Generate and check a fresh fixture

```powershell
dotnet run --project tools/GroceryErp.DataGen/GroceryErp.DataGen.csproj -c Release -- --scale=1.35 --output=artifacts/data/phase0-strong-v2/db/grocery.db
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase0-strong-v2 data-shape artifacts/data/phase0-strong-v2-shape.json
```

The generator uses fixed seed 27092026. Generation verifies integrity, foreign keys, stock-cache totals, and zero sales after lot expiry. `data-shape` additionally checks the 2–40 purchase-line spread and scanner-compatible loose-product PLUs. The generated sales and purchases are synthetic SQL data; FEFO and cashier behavior must be exercised separately through services and the UI.

## Add realistic operations and collect timing

```powershell
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase0-strong-v2 realism-seed artifacts/data/phase0-strong-v2-realism.json
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase0-strong-v2 profile artifacts/data/phase0-strong-v2-profile.json
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase0-strong-v2 verify artifacts/data/phase0-strong-v2-verify.json
```

`realism-seed` posts 100 voids and 100 returns through application services and adds 30 dated expenses. `profile` runs four read workers with two independent write executors to exercise SQLite contention. Each read operation has 40 samples; p99 is therefore the slowest sample, not a stable production estimate. Both modes write to the database. `verify` scans the entire database and can take many minutes.

For broader Phase 0 coverage, use a disposable copy and run `shape-profile`, `report-profile`, and `cold-warm` in separate processes. `shape-profile` times 77 search/list/history/foundation/report shapes with four readers and two writers (78 operation shapes including sale writes). `report-profile` exercises broad and selected variants of all report kinds under concurrent sale writes. Both modes trace the actual expanded `SELECT`/`WITH` statements executed by their service calls and capture `EXPLAIN QUERY PLAN` for each distinct statement, as well as the manually registered hot plans. `feature-write`, `gap-read`, `weekly-maintenance-overlap`, and `print-journal-profile` also capture plans. Repeated write statements with different synthetic values are deduplicated by SQL shape, with one executable representative retained for planning. Expanded SQL contains synthetic fixture literals; keep these ignored benchmark artifacts off production stores and out of app logs. `cold-warm` records each operation's first call separately from twenty repeats. A new process does not clear the Windows file cache; never label these as physical-disk cold measurements.

```powershell
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase0-strong-v2 shape-profile artifacts/data/phase0-strong-v2-shape-profile.json
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase0-strong-v2 report-profile artifacts/data/phase0-strong-v2-report-profile.json
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase0-strong-v2 cold-warm artifacts/data/phase0-strong-v2-cold-warm.json
```

The generated full-size store has no customers. Before assessing customer search, run `customer-seed` once on a disposable copy to add 50,000 deterministic customer rows, then rerun `shape-profile`. The seed refuses an already populated Scale Customer fixture. Keep the original baseline files intact.

```powershell
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase0-strong-v2 customer-seed artifacts/data/phase0-strong-v2-customer-seed.json
dotnet run --project tools/GroceryErp.ScaleAudit/GroceryErp.ScaleAudit.csproj -c Release -- artifacts/data/phase0-strong-v2 shape-profile artifacts/data/phase0-strong-v2-customers-shapes.json
```

To test interruption, copy only `db/grocery.db` to a new disposable data root while the source has no active WAL writer. Launch audit mode `fault-transaction` in a separate process, wait for `phase0-fault-ready.txt`, then force-kill that process. Run `verify` and `optimized-read` on the same copy; require zero committed `phase0-interrupted-write` expenses, zero foreign-key/cache/projection/post-expiry mismatches, and `integrity_check=ok`. The two `terminal-*.json` outputs in `artifacts/data/phase0-fault-copy` are independent audit processes that each posted twenty sales before the forced kill.

JSON records process timing, p50/p95/p99, row counts, database/WAL bytes, SQLite/schema versions and query plans. Each invocation starts a new process; the OS file cache is uncontrolled. Record the hardware and commit alongside the result. The app writes rolling data-free metric summaries to `logs/performance-YYYYMMDD.jsonl` while it runs.

For paths absent from the generated full store, copy the preserved `audit-gap-seeded/db/grocery.db` into a disposable data root, run `migrate`, then run `shape-profile` and `report-profile` on that copy. It has 5,000 held bills, 5,001 drafts, 5,000 assets, payroll rows and a large draft stock count. The Phase 0 specialist copy also ran `feature-write`, `gap-read`, `weekly-maintenance-overlap` and `print-journal-profile`, followed by `verify`. Never run mutating audit modes on preserved baseline roots. The final artifacts are `phase0-specialist-coverage-final.json`, `phase0-specialist-reports.json`, `phase0-specialist-writes-traced.json`, `phase0-specialist-gap-read-traced.json`, `phase0-specialist-maintenance-traced.json`, `phase0-print-journal-profile.json`, and `phase0-specialist-final-verify.json` under `artifacts/data`.

The full fixture's final shape run is `phase0-strong-v2-coverage-final.json`; its post-write checks are `phase0-strong-v2-final-verify.json` and `phase0-strong-v2-final-projection.json`. Inspect `profiles.Errors`, `queryPlans[].Error`, and `results[].Error` explicitly: audit modes record individual call failures in JSON even when the process completes. A shape with `MaximumRows=0` measures a valid empty-result path, not populated-result performance. Exact output and UI acceptance for every filter remain later implementation gates.

## Functional and UI gates

```powershell
dotnet build GroceryErp.slnx -c Release --no-restore
dotnet test GroceryErp.slnx -c Release --no-restore --verbosity quiet
```

For the opt-in FlaUI scale journey, copy a disposable 10% database to a directory under `%TEMP%` whose name starts with `grocery-erp-packaged-ui-`. Run the audit tool's `migrate` and `ui-setup` modes on that directory, set `GROCERY_SCALE_UI_ROOT` to the directory and `GROCERY_SCALE_UI_EXE` to the Release app executable, then run the UI test project's `ScaleUiTests` filter. The test writes navigation timing, memory, screenshots and a timeout capture into that temporary directory. Its control-discovery time and fixed settling wait are separate fields.

The UI log distinguishes database calls, synchronous binding assignments, layout completion and the first WPF frame callback after refresh. The frame callback occurs before physical display presentation; it is not a measured monitor-paint duration. Phase 0 evidence does not replace target-PC p95, printer, restore or release acceptance in later phases.
