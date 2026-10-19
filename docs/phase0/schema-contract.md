# Revision schema contract

`grocery_erp_schema.sql` is the embedded **001** migration and documents the original ledger schema. It and shipped migrations 002–028 are byte-preserved. The next numbered SQL script is **029**; no later schema is implemented by Phase 0. This document expands the migration proof obligations from the [revised implementation plan](../../revised-implementation-plan.md), which remains authoritative for target behavior and phase ordering.

## Target model

- `product_lot.qty_on_hand` is authoritative. Every lot has immutable origin kind/reference (purchase line, direct entry or return recreation), a monotonic ID, a version for optimistic edits, and current cost/MRP/price/expiry. Quantity changes only in a business document transaction through a conditional service update. Product stock totals remain a bounded projection.
- Sale, return, purchase, vendor return, damage, direct entry and internal-use documents retain the product/lot identity and three-decimal monetary, tax, quantity and cost snapshots needed after master deletion. Posted sale and purchase content cannot be rewritten. Live foreign keys may be cleared where history uses stable snapshot IDs; deleted IDs cannot be assigned to different items.
- Calendar and fiscal annual snapshots have independent year keys, lock dates and stored final totals with month, head and category breakdowns. Open reports use source rows or bounded daily summaries. A correction after either containing year closes has a new open-period date and leaves locked rows unchanged.
- The target has no generic `stock_movement` table, `movement_type` table, ledger views or ledger-cache mismatch check. Source reconciliation computes origin plus effective sales, voids, usable returns, damage, internal consumption, vendor returns/cancellations and reviewed corrections per lot. Interactive writes check only affected lots; full reconciliation is maintenance work.

## Migration ordering and proof

1. Migration 029+ adds forward-complete snapshots and optional historical links. Backfill from original documents or saved receipts where possible; record `legacy-unknown` plus provenance where not. Verify old receipt bytes, invoice sums, row counts and foreign keys.
2. Build direct-lot operations on an isolated migrated copy. All old ledger writers continue on the live schema until every business caller, report, export, startup/recovery path, ScaleAudit/DataGen path and test is migrated.
3. Perform one whole-app cutover to direct quantity writes. Compare every lot to source-document arithmetic and preserve a verified pre-upgrade backup. Old binaries must refuse `user_version > 28`.
4. Only after parity, drop movement objects in a new migration and reclaim pages via a verified compact-copy/swap workflow. Record `dbstat` table/index bytes, peak disk/WAL, elapsed time, backup/restore time and working set. Do not compact during cashier activity.

See [dependency inventory](dependency-inventory.md), [behavior matrix](behavior-matrix.md), [test matrix](test-matrix.md) and [baseline/rollback](baseline-and-rollback.md).
