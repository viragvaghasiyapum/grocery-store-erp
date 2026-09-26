# Luna implementation log

This file records the Phase 1 work completed by Luna. Each checkpoint commit uses the
`phase1:` prefix so a later debugging session can bisect the foundation in small, named slices.

## Phase 1 scope

Phase 1 is the Foundations phase from `implementation.md`: application paths, SQLite and Dapper,
migrations 001–003, fixed-point numerics, errors, clock and fiscal year helpers, document numbers,
settings, audit and operation guards, authentication and first-run setup, and verified backup and
restore support.

## Checkpoint 1 — core and persistence foundation

Implemented:

- `Money`, `Quantity`, `Percent`, fixed-point Fx3 rounding, tax helpers, allocation, and round-off.
- Domain error codes and domain exceptions.
- Clock, business-date, and fiscal-year helpers.
- Data-root path management with OneDrive rejection and required directories.
- SQLite connection factory with WAL, full synchronous mode, foreign keys, busy timeout, and cache settings.
- Thread-pool database executor with serialized `BEGIN IMMEDIATE` writes.
- Dapper handlers for fixed-point values and `DateOnly`.
- Embedded migration runner for the supplied schema migrations 001, 002, and 003.
- SQLite capability checks and stable constraint-to-domain-error mapping.
- Typed settings snapshot, period lock guard, document numbering, and audit log.
- Operation IDs with claim/complete/purge behavior for duplicate-submission protection.

## Checkpoint 2 — security and backup foundation

Implemented:

- Argon2id password hashing with encoded parameters and fixed-time verification.
- Login failure backoff, login timestamp update, and password-change service.
- First-run transaction that creates the owner account and persists store, tax, language, backup,
  and internal-barcode settings.
- Zip backup sets containing a `grocery.db` snapshot and JSON manifest.
- SHA-256, byte-length, quick-check, foreign-key, and schema-version verification.
- Restore inspection and staged apply flow with pre-restore backup and pending-restore marker.
- Pending-restore startup completion check.

## Checkpoint 3 — first-run application flow

Implemented:

- Startup dependency registration and initialization pipeline.
- SQLite capability check, migration execution, restore-marker handling, settings load, and language load.
- Eight-step first-run wizard with language, store, owner login, data/backup, printer, barcode,
  keyboard, and start pages.
- Login window with password failure feedback.
- Close backup creation and global UI/task exception logging.
- Shared section-title design token and English/Gujarati setup/login resource keys.

## Verification and completeness audit — 26 September 2026

The Release build passes with zero warnings and zero errors. The existing test suite passes all 6
tests, but those tests cover Phase 0 localization, architecture, and UI smoke behavior. There are
no Phase 1 property tests, migration tests, backup/restore round-trip tests, authentication tests,
or database-rule tests yet.

Phase 1 is **partially implemented**. The code present covers the foundation primitives and a basic
first-run/login path, but it does not meet the Phase 1 exit criteria in `implementation.md`.

Implemented and verified by source/build inspection:

- Paths, SQLite connection setup, WAL and pragmas, serialized database writes, Dapper handlers,
  migrations 001–003, numerics, fiscal-year helpers, document numbers, domain errors, settings
  snapshot, period guard, audit writer, operation guard, Argon2id authentication, first-run form,
  backup manifest/hash verification, staged restore, and startup/login wiring.
- The three supplied SQL files are embedded as migrations and the schema contains the required
  strict tables, triggers, foreign keys, FTS5 tables, views, seeds, and operation table.
- Tech stack packages for Microsoft.Data.Sqlite, Dapper, Argon2, Serilog, WPF-UI, analyzers, and
  test tooling are centrally pinned and the solution builds cleanly.

Missing or incomplete against the Phase 1 specification:

- No idle lock overlay, manual lock command, lock timer, or lock/unlock shell state.
- No clock-behind-last-bill startup check or audited override.
- No free-disk startup warning, lot-cache integrity job, daily-backup job, weekly integrity check,
  depreciation job, expiry refresh, operation purge job, stale-temp cleanup, or WAL checkpoint on close.
- No `StartupPipeline` or `ShutdownPipeline`; the startup work is directly in `App` and only covers
  capability check, migrations, restore marker quick-check, settings, wizard, login, and close backup.
- Restore does not implement confirmation, undo after failed restart, restore audit entry, migration
  of an older restored database, replaced-database retention/purge, or full startup integrity checks.
- Backup rotation does not enforce the documented newest-N/monthly/pre-* retention policy.
- Settings are only partially typed, do not publish `SettingsChangedMessage`, and settings changes
  are not audited. Several Appendix C settings have no typed code default.
- The first-run wizard displays printer, keyboard, and start pages but does not collect or persist
  the required printer configuration, USB reminder, scale rule, Excel import choice, or keyboard check.
  GSTIN validation and state prefill are also missing. Many wizard labels remain hard-coded instead
  of using English/Gujarati resources.
- No Phase 1 service handlers use `OperationGuard`; only the reusable guard itself exists.
- No Core property tests, migration-with-data tests, Dapper round-trip tests, backup tamper tests,
  duplicate-submission race tests, or restore round-trip tests.
- Public Core/Data APIs do not consistently have the XML documentation required by PLANS.md.
- The current implementation uses `DateTime.UtcNow` in backup rotation and bypasses the documented
  `IClock` boundary.

The correct status is therefore **Phase 1 foundation slice implemented; Phase 1 exit criteria not met**.
Phase 2 should not start until the missing lock/startup jobs, restore lifecycle, settings/audit wiring,
and Phase 1 test layer are completed.

## Commit history

- `00560ed phase1: foundations, security, backup, and app flow`
- `phase1: audit implementation status` — current documentation checkpoint

## Checkpoint 4 — Phase 1 reliability and verification slice

Implemented:

- Shell idle lock and manual `Ctrl+Shift+L` lock with authenticated owner-password unlock.
- Password-only unlock path in `AuthenticationService` with failed-password handling.
- Startup clock-behind-last-bill check and lot-cache check.
- Free-disk warning calculation, daily backup scheduling, operation purge, stale temporary cleanup,
  close backup, WAL checkpoint, and database optimization maintenance.
- Settings changes now write audit entries and publish `SettingsChangedMessage` through the weak
  reference messenger.
- Restore startup validation now checks quick-check, foreign keys, schema version, writes a RESTORE
  audit entry, purges old replaced databases, and exposes an undo path.
- Numeric unit tests, migration/seed tests, operation replay tests, backup manifest and restore
  inspection tests, and password hashing tests.

Verification after this checkpoint:

- `dotnet build GroceryErp.slnx -c Release --no-restore`: passed, 0 warnings, 0 errors.
- `dotnet test GroceryErp.slnx -c Release --no-build --no-restore`: passed, 14 tests, 0 failures.

Still outside the completed Phase 1 exit criteria:

- Visible low-disk/integrity warning UI and audited clock-warning override.
- Weekly `PRAGMA integrity_check`, elapsed-month depreciation posting, expiry/low-stock refresh,
  and full restore confirmation/undo UI.
- Complete backup rotation policy, USB export, and backup status messaging.
- Full typed Appendix C settings surface and settings UI.
- Complete wizard validation and persistence for printers, USB reminder, weighing-scale rules,
  keyboard detection, and Excel/start choice.
- Service-level use of operation IDs for business commands; only the reusable guard exists because
  Catalog, Purchasing, Inventory, Sales, Expenses, and Assets handlers are Phase 2–5 work.
- Native Gujarati review and XML documentation for public Core/Data APIs.

The implementation remains **Phase 1 partially complete**. The new reliability and test slice is
committed as `phase1: reliability checks, locking, and foundation tests`.

## Checkpoint 5 — startup pipeline, typed settings, and validation

Implemented:

- Explicit `StartupPipeline` and `ShutdownPipeline` services used by the application host.
- Complete typed accessors for the Appendix C settings surface, including tax, stock, expiry, POS,
  barcode, scanner, UI, printing, receipt, and backup values.
- Backup retention enforcement for newest sets, monthly retention, pre-operation retention, and
  stale partial-file cleanup.
- Background `PRAGMA integrity_check` and optimization after shell startup.
- Visible low-disk and lot-cache startup warnings.
- Clock-behind-last-bill confirmation with an audited override.
- Indian GSTIN, PAN, and pincode validation helpers; first-run GSTIN validation now uses them.
- Restore round-trip integration coverage now applies the staged database, finishes the pending
  restore, and asserts the RESTORE audit entry.

Verification:

- `dotnet build GroceryErp.slnx -c Release --no-restore`: passed, 0 warnings, 0 errors.
- `dotnet test GroceryErp.slnx -c Release --no-build --no-restore`: passed, 14 tests, 0 failures.

Remaining Phase 1 work is limited to the complete wizard printer/USB/keyboard/import controls,
full restore confirmation UI and failure recovery presentation, and final XML documentation/native
Gujarati review. Business-module operation handlers remain in their later phases.

## Checkpoint 6 — first-run wizard completion and search integrity

Implemented:

- The first-run wizard now collects and persists USB reminder frequency, receipt printer and width,
  label printer and size, A4 printer, weighing-scale rule, keyboard availability, and the empty-vs-
  import start choice.
- Wizard navigation validates store name/GSTIN, owner credentials, backup path, and barcode prefixes
  before advancing; GSTIN state codes prefill the state field when it is still empty.
- Gujarati Phonetic keyboard availability is detected through the installed Windows input languages
  and stored in the setup settings snapshot.
- First-run settings are upserted (rather than assumed to exist in the seed set), with typed accessors
  for the new Appendix C values and an integration test covering the persisted configuration.
- Background startup maintenance now runs SQLite's supported FTS5 integrity command in addition to
  `PRAGMA integrity_check`, so product search corruption is detected before normal use.
- Shared localization registration now includes setup, login, lock, and startup resource prefixes,
  so the existing wizard and startup warning keys resolve in both supported UI languages.
- A staged restore is now presented after login with explicit keep, undo, or cancel choices; undo
  restores the replaced database and closes the application without deleting the recovery marker.

Verification:

- `git diff --check`: passed.
- Build/test rerun is currently blocked in this shell because the repository requests .NET SDK
  `10.0.401` and this machine has no installed SDK. The previous committed baseline was verified with
  `dotnet build GroceryErp.slnx -c Release --no-restore` (0 warnings/0 errors) and 14 passing tests.

Remaining Phase 1 presentation work is the actual printer test-page action and a visible restore
confirmation/undo dialog; the printer choices are now stored and explicitly deferred to Settings,
while catalog import itself remains a later module feature. Final XML documentation and native
Gujarati review are still release-hardening work.
