# Revision acceptance cases

This matrix expands the acceptance cases in the [revised implementation plan](../../revised-implementation-plan.md), which remains authoritative. Phase 0 keeps schema-28 behavioral tests executable and repairs invalid baseline fixtures; target cases become automated only when their schema/service arrives. Amounts and quantities below are fixed-point integers scaled by 1000.

| Area | Required positive assertion | Required rejection / parity assertion |
|---|---|---|
| Migration | Upgrade supported old schemas on a disposable copy; preserve counts, old invoice totals, receipt bytes and stable snapshot IDs. | Interrupted upgrade restarts or restores without duplicate documents; old binary rejects `user_version > 28`; foreign keys and source/lot arithmetic pass. |
| Sale snapshot | Product/lot rename, price/tax/expiry edit and deletion leave completed sale, return and reprint unchanged. | Completed sale line or total edit rejected; refund status without return changes neither P/L nor stock. |
| Purchase snapshot | Two identical batch/expiry lines make distinct lots with distinct source line IDs. Expense line makes no lot; internal-use lot cannot be sold. | Posted line edit rejected; void rejected after any dependent stock use, even sold then returned. |
| Direct lot | Direct origin quantity and cost saved; subsequent sale captures cost as COGS exactly once. | No unexplained quantity appears; zero lot delete allowed only with safe history links. |
| Returns | Unchanged original lot receives resellable quantity; edited/deleted lot creates linked new lot; unusable return creates damage. | Duplicate operation ID has one effect; missing legacy field requests operator input; printed receipt hides lot detail. |
| Vendor return | Partial return uses own purchase lot's current eligible quantity, saved cost and credit; cancellation restores stock and vendor balance atomically. | Excess quantity, duplicate return or second cancellation changes nothing; correction remains readable. |
| Bulk count | Reviewed deficit creates categorized damage; surplus creates direct origin with reason; affected totals match lot sums. | Stale lot version and partial failure expose exact affected IDs; no generic movement rows. |
| Annual locks | Calendar 31 Dec closes after 7 Jan; fiscal 31 Mar closes after 7 Apr. On 7 Jan/Apr edits remain allowed when the other period is open. | From 8 Jan/Apr either closed annual period prevents original-date mutation; current open-period correction leaves saved annual snapshot byte-stable. |
| Reports | Monthly/head/category sums match source sale/return, damage, expense, salary, asset and vendor-return records; closed annual reports use stored snapshots. | Current master edits do not rewrite historical P/L; open and frozen totals never count a purchase cost twice. |
| UI/performance | English/Gujarati forms, tabs, search/filter and bulk preview fit 1366×768 at 100/125/150%; grid remains virtualized and bounded. | Obsolete search response cannot replace latest; navigation memory reaches a stable plateau; full CSV cancellation does not retain history. |

The fixture must include hot products with many lots, same batch/expiry on separate receipts, partial returns, refunded sales, deleted products followed by returns, expired/blocked lots, direct and internal-use origins, and both annual boundaries. `docs/perf/phase0-runbook.md` provides the existing-schema generator/audit commands; `baseline-and-rollback.md` identifies the preserved revision fixture and measurements.
