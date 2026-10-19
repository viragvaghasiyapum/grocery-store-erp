# Revised product decisions — superseded where inconsistent

The single source of truth for revised decisions and acceptance gates is [revised-implementation-plan.md](revised-implementation-plan.md). The [Phase 0 behavior matrix](docs/phase0/behavior-matrix.md) is supporting analysis and still awaits operator/project-owner review. The notes below are preserved as design history. In particular, lots cannot be freely quantity-edited, purchases cannot merge receipts into an existing lot, sales cannot be edited after completion, and old-year reports cannot be recalculated after either annual lock. The target removes `stock_movement` only after a verified, whole-app stock-write cutover; schema-28 remains ledger-backed until then.

## Product:
- Products can be selling and for internal use (such as raw materials).
- add product (fields as we have currently)
- merge lots in product page as a tab (no stock ledger, no stock movement, no category valuation, no overview.). Also lots follow FEFO by default.
- lots are there just to know available quantity to sell and price difference among lots of a product. Therefore lots can be added/deleted/updated freely.
- add created at, updated at to lot and products.
- lots have option to mark damaged be it expiry or broken or else with qunatity selectable with confirmation dialog that it will remove it from lot or lot and add it to damages.

## Sales History:
- it have its own sales items noted independent of later changes or updates so no issues.
- returns add back quantity to matching lot or in case lot is updated after it or not found create a new lot for it also note expiry and lot details in bill but dont print in bill.
- update status to refunded in sales once refunded or voided if void.
- it saves all kind of this about product, selling price, purchase price, gst, MRP, final selling price including GST
- add created at and updated at.

## Purchases:
- it adds quantity or update prices of a existing lot for a product or create a new lot entirely.

## Asset:
- Asset can be freely adjustable, create/edit with confirm dialog to recalculate year or take effect from now on for updates. create is obviusly from now on but still confirm in dialog. Also they  can be disposed.
- add created at, updated at to assets

## Money out: (all have created at and updated at)
### 0. Expiry:
- remove expiry tab entirely.
### 1. Damages:
- edit with confirmation dialog recalculate year in reports. create is obviusly from now on but still confirm in dialog.
### 2. Expenses:
- Edit with confirmation dialog recalculate year in reports. create is obviusly from now on but still confirm in dialog. they can be voided also.
### 3. Employee payroll:
- Edit with confirmation dialog with option to either recalculate year or take effect from now on in reports. create is obviusly from now on but still confirm in dialog.
- employee also joining and leaving date in table, status working, relieved.
- per employee update history.

## Reports:
- P/L (Sales, Expenses, Asset Depreciation, Damages, Employee Payroll). For yearly calendar year and fiscal year P/L and  montlhy P/L is as usual.
- Current unsold stock with include/exclude expiry.
- purchased units vs sold/damaged/saleable units to match atomicity.
