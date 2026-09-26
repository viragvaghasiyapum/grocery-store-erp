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

## Verification

`dotnet build GroceryErp.slnx -c Release --no-restore` passes with zero warnings and zero errors.

## Known follow-up

- The repository did not contain a `.git` directory when this work began. Checkpoint commits are
  created after repository initialization in the workspace, with the exact commit IDs recorded below.
- Native Gujarati review remains a Phase 0/Phase 1 content task; Gujarati strings added here are
  machine-drafted and marked for review.

## Commit history

- `phase1: core and persistence foundation` — pending commit ID
- `phase1: security and backup foundation` — pending commit ID
- `phase1: first-run application flow` — pending commit ID
