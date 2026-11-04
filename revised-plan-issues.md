# Revised plan: open issues

Status: review of the implemented revised plan on schema 39, 26 October 2026. Every item below needs only code or documentation work. None needs the target PC, store hardware or a human sign-off. Those gates stay in `revised-implementation-plan.md` and `docs/release-checklist.md` and are not repeated here.

Baseline at review time: `dotnet format --verify-no-changes` passed, the Release build had no warnings, 292 tests ran (290 passed, 2 opt-in UI journeys skipped), and ScaleAudit and DataGen built.

**Reproduced** means a throwaway integration test confirmed the behaviour (the test was deleted afterwards). **Read** means the issue was found by reading the code and was not run separately.

Suggested order: P3-1 and P3-2 (GST summary), P5-1 (held bill date), P6-1 (maintenance), P3-3 (vendor-return cancel), P6-2 (day-end cash).

When fixing an item, add a regression test, and where the behaviour is visible, update the plan's execution record and both user guides in the same change, as CLAUDE.md requires.

## Progress

Fixes go phase by phase, stopping after each phase for review. A problem that appears only with data carried over from older releases is checked on a fresh store first, because that data may be flawed (owner's ruling, 26 Oct 2026). Each fix is recorded under its item below and in the plan's "Open-issue fixes" execution record.

| Phase | Status | Verified |
|---|---|---|
| 2 | Done 26 Oct 2026: P2-1 closed (not reproducible on fresh data), P2-2 fixed | `dotnet format --verify-no-changes` passed; Release build of every project, ScaleAudit and DataGen without warnings (the App's `bin` was locked by a running instance, so the UI tests were built to a scratch folder); 293 tests: 251 integration, 31 core, 2 architecture, 9 UI (2 opt-in skipped) |
| 3 | Done 26 Oct 2026: P3-1, P3-2, P3-3 and P3-4 fixed, each reproduced on a fresh store first; then the owner's two rulings (P3-F1, P3-F2) | `dotnet format --verify-no-changes` passed. Release builds of every project, ScaleAudit and DataGen had no warnings; a running app instance locked the App's `bin`, so the UI tests were built to a scratch folder. 300 tests: 257 integration, 32 core, 2 architecture, 9 UI (2 opt-in skipped). Full-size store: `ScaleAudit phase6-summary-parity` matched the GST summary to its source form for month, fiscal year and a partial range, before and after seeding returns (timings under P3-1) |
| 5 | Done 26 Oct 2026: P5-1 fixed, reproduced on a fresh store first | `dotnet format --verify-no-changes` passed. Release builds of every project, ScaleAudit and DataGen had no warnings; a running app instance locked the App's `bin`, so the App and UI tests were built to a scratch folder. 302 tests: 259 integration, 32 core, 2 architecture, 9 UI (2 opt-in skipped). Full-size store: the re-date timing under P5-1 (script and results in `artifacts/data/revision-issues-phase5`) |
| 6 | Done 26 Oct 2026: P6-1 and P6-2 fixed. P6-2 was reproduced on a fresh store first. For P6-1, the weekly check was run on a fresh store after every stock workflow and passed | `dotnet format --verify-no-changes` passed. Release builds of every project, ScaleAudit and DataGen had no warnings. 306 tests: 260 integration, 32 core, 2 architecture, 12 UI (2 opt-in skipped). Full-size store: the weekly-check and day-end timings under P6-1 and P6-2 (scripts and results in `artifacts/data/revision-issues-phase6`) |
| 7, 8, 9, §15 | Open | |
| P3-F3 (product choice for returned units) | Done 4 Nov 2026: built as decided; also fixed a later correction of a damage restored into another product | `dotnet format --verify-no-changes` passed. Release builds of every project, ScaleAudit and DataGen had no warnings. 309 tests: 263 integration, 32 core, 2 architecture, 12 UI (2 opt-in skipped). The new window was captured in both modes and languages from the UI smoke journey and checked by eye; it was not used in the running app |

## Phase 0 — Freeze rules, map dependencies, and baseline

No open issues found.

## Phase 1 — Historical snapshots and schema foundation

No open issues found.

## Phase 2 — Direct lot stock engine and reconciliation

- [x] **P2-1 A stock count cannot resolve a lot whose documents disagree** (Medium, reproduced)
  - `DirectStockEngine.cs:183-189`: after posting a count, `CheckAffected` reconciles the lot against its documents. A difference that existed before the count is still there, so every count on that lot fails with the generic "Check the entered values" message.
  - Impact: a lot flagged by the weekly check can never be counted, and there is no other in-app correction (see P6-1).
  - Fix: decide on a reviewed correction for a source difference (for example a dated, reasoned `stock_correction`), or check only that the count's own change applied.
  - **Closed 26 Oct 2026: does not occur on fresh data; no behaviour change.** The owner ruled that a difference carried over from older data is a flaw of that data and should not get a correction workflow. The check was therefore repeated on a fresh store. `FreshStoreReconciliationTests` runs every lot-changing workflow on one schema-39 store through the services: purchase post and void, direct lot, opening import, sale, sale void, and returns to the original lot, to a recreated lot (after a lot edit) and as damage. It also covers damage, damage correction, a correction that recreates a deleted zero lot, bulk damage, vendor return and cancellation, internal use, and count shortage and surplus. It then counts every lot. No lot differs from its documents, and every count posts. The count keeps its strict check, which still catches a count that breaks a lot. The generic message is P7-1's work.
- [x] **P2-2 Count results skip normal document numbering** (Low, read)
  - A count deficit creates a damage with no `doc_no` (`DirectStockEngine.cs:93`). Every other damage writer takes a number from `DocNumberService`.
  - A count surplus lot gets the code `RC-` plus the 32-character operation ID (`DirectStockEngine.cs:128`) instead of the lot numbering the other lot writers use.
  - **Fixed 26 Oct 2026.** `DirectStockEngine` now takes `DocNumberService`. The shortage damage gets `numbers.Next(Damage, business date)` and the surplus lot `numbers.Next(Lot, …)`, inside the count's transaction, after validation and after the replay check, so a replay takes no number. Callers updated: DI in `InventoryModule`, ScaleAudit (`phase5-count-read`, `phase9-profile`, the stock-count review measure) and the UI smoke test. Integration tests that only reconciled now call `LotSourceReconciliation.CheckAll` directly. Earlier count damages keep no number; they are history. Regression: `DirectStockEngineTests.ReviewedCount_IsIdempotent_AndAStaleBatchChangesNothing` asserts `D2627-000001` and `L000001`. Both user guides' Reconcile selected stock row is updated.

## Phase 3 — Purchases, vendor returns, and direct entry

- [x] **P3-1 GST summary counts cancelled vendor returns** (High, reproduced)
  - `ReportDefinitions.cs:47-51` (`GstSummaryFromDay`, the SQL the report uses) and the reference SQL at `:209-213` never look at `vendor_return_cancellation`.
  - Repro: a return of ₹400 + ₹20 tax, then cancelled in the same month, still shows inward −40,000 / −2,000.
  - Fix: subtract the return on its return date and add it back on its cancellation date, as `v_pnl_entry` does for the variance head.
  - **Fixed 26 Oct 2026.** Reproduced first on a fresh store through the services: a return cancelled in its own month left September inward at 2,300,000 / 115,000 instead of the purchase's 2,500,000 / 125,000. The inward half of the summary is now one `InwardGst` block shared by `GstSummaryFromDay` and the reference form, so the two cannot drift apart again. It adds a third branch: each cancelled return's lines, positive, on the cancellation date. That branch takes a `CROSS JOIN` order hint so it starts from `ix_vendor_return_cancel_date`. Without the hint, SQLite walked every return through each vendor: 170–210 ms for any period with 20,000 returns, against 0.04 ms for a month and 2 ms for a fiscal year with it.
  - Regression: `StockInTests.GstSummary_NetsVendorReturnsAgainstCancellations_AndKeepsThemAfterTheLotIsDeleted` covers a return cancelled in the same month (only the purchase is left) and one cancelled the next month (still subtracted in September, +200,000 / +10,000 in October).
  - Full-size store: the Phase 9 fixture (47,010 products, 5.2 million sale lines) has no vendor returns, so a scratch copy was seeded with 20,000 returns (56,918 lines) and 2,000 cancellations. The app's summary query, median of 15 alternating runs of `phase6-summary-parity`, before → after: month 3.0 → 2.6 ms, fiscal year 41.6 → 38.3 ms, partial range 1.7 → 1.8 ms. Without the seeded returns: 2.1 → 2.2, 28.2 → 28.7 and 1.3 → 1.4 ms, within run-to-run noise. The source and projected forms matched in every run. Both user guides' Reports section now says how returns and cancellations count.
- [x] **P3-2 GST summary drops a vendor return once its zero lot is deleted** (High, reproduced)
  - The same SQL reaches the purchase line through the live lot (`JOIN product_lot l ON l.id=vrl.lot_id JOIN purchase_line pl ON pl.id=l.purchase_line_id`). Deleting referenced zero lots has been allowed since Phase 5, and a full vendor return leaves the lot at zero.
  - Repro: after the lot is deleted, the October inward rows are empty instead of −40,000 / −2,000.
  - Fix: join on `vrl.purchase_line_id_snapshot`. Vendor GSTIN still comes from the live vendor, which is acceptable because vendors are archived, not deleted.
  - **Fixed 26 Oct 2026** in the same `InwardGst` block. Both return branches join `purchase_line` on `vrl.purchase_line_id_snapshot`. Every return line has it: the service saves it at posting, and migration 029 filled it for older returns. Dropping the lot lookup also made the return branch faster: 2.3 vs 3.7 ms for a month and 11.9 vs 18.4 ms for a fiscal year on the seeded store, measured interleaved. The same regression test returns the rest of the lot in full, deletes the emptied lot through `DeleteZeroLotAsync`, and still sees the return (1,900,000 / 95,000). `ReportsTests.GstSummary_IncludesVendorCreditsWhenPurchaseIsOutsidePeriod` inserts its return line in raw SQL; it now saves the purchase line snapshot, as the service does.
- [x] **P3-3 Cancelling a vendor return fails after its lot was deleted** (Medium, reproduced)
  - `PurchasingService.cs:873` puts the units back into the original lot. A fully returned lot is at zero and can be deleted, and then the cancel fails with "The selected lot belongs to a different product."
  - Fix: either refuse to delete a lot that has an uncancelled vendor return, or recreate the lot from the return snapshot as `MoneyOutService.RecreateDamagedLot` does for damage corrections. Show a specific message either way.
  - **Fixed 26 Oct 2026 by recreating the lot**, the plan's rule for damage corrections and customer returns. Refusing deletion would have needed guards in single, bulk and product deletion, and would have kept every fully returned lot forever. Reproduced first on a fresh store: `LotProductMismatch`.
  - `CancelVendorReturnAsync` checks each line's lot in the same query that reads the lines. A line whose lot still exists is restored to it as before. For any other line, `RecreateReturnedLot` inserts a new direct lot, inside the cancel's transaction and after validation and the replay check:
    - The lot code comes from the normal series (`L000002` in the test). The vendor is the return's, the received date is the cancellation date, and the reason is `Vendor return V… cancelled: <reason>`.
    - The unit cost, batch and expiry come from the return line's snapshot; the mfg date, selling price, MRP and tax rate from its purchase line.
    - The purchase line's saved tax per piece is reused when its cost equals the return's. Otherwise, or for a line saved before schema 39, the tax is derived from that cost through `LotTaxes.FromBooked`.
    - The units are added with `LotChangeKind.DirectEntry`, as the damage restore does.
  - The audit entry lists the new lots. When the product itself was deleted, the cancel throws the new `ErrorCode.VendorReturnProductDeleted`, with English and Gujarati text (Gujarati marked `MT – needs review`), and nothing changes. No schema change.
  - Regressions:
    - `StockInTests.VendorReturnCancellation_AfterItsLotWasDeleted_RestoresTheUnitsInANewLot` checks the new lot's every field against the purchase line. It also checks an idempotent replay and repeat, the vendor balance, the stock total, a zero variance and a clean `CheckAll`.
    - `StockInTests.VendorReturnCancellation_OfADeletedProduct_IsRefusedWithItsOwnReason` covers the deleted-product refusal.
  - Both user guides now describe the cancel.
- [x] **P3-4 Purchase expense lines lose paise to per-piece rounding** (Low, reproduced)
  - `PurchasingService.cs:383` books `Booked × (qty + free)`, where `Booked` is already rounded to three decimals per piece.
  - Repro: 1,000 units at ₹1.000 less ₹0.001, plus a ₹0.455 share of other charges, is booked as ₹1,000.000 instead of ₹1,000.454.
  - Fix: book the line's exact amount (goods after discount, plus its share of charges, plus tax when tax is cost).
  - **Fixed 26 Oct 2026.** Reproduced first on a fresh store: 1,000,000 and 1,050,000 were booked instead of 1,000,454 and 1,050,454.
  - `LandedCost.ComputeLines` returns each line's per-piece prices and its exact booked amount from one allocation of other charges. `Compute` is now a projection of it, so the purchase form's figures are unchanged. `PriceLines` keeps the amount on `PricedPurchaseLine.Booked`, and posting books an Expense line's expense with it. A void reverses the same amount. Product and internal-use lots still carry a per-piece cost, as a lot must. Expense amounts already posted keep their value.
  - Regressions:
    - `StockInTests.PurchaseExpenseLine_BooksTheLineAmountNotTheRoundedPiecePrice` covers Regular and Composition modes and the void reversal.
    - `LandedCostTests.LineBooked_IsTheLineAmountBeforeThePiecePriceIsRounded` covers the exact amount.
    - The property test now also checks that each line's amount spread over its pieces gives the piece cost.
  - The Money out expenses text in both guides says an Expense line books its exact amount.

Owner rulings after the Phase 3 review (26 Oct 2026):

- [x] **P3-F1 A restore that needs a deleted product only says it is not possible**
  - Ruling: say that the restore is not possible because the original product was not found or was deleted. Give no advice. "Add the product again" never worked: a re-added product gets a new ID, and the restore looks for the original one.
  - **Done.** The damage message (`Error_DamageProductDeleted`) now reads "These units cannot be restored: the original product was not found or has been deleted." The vendor-return one (`Error_VendorReturnProductDeleted`) reads "This return cannot be cancelled: the original product was not found or has been deleted."
  - The text says "deleted", not "removed", because **Remove** archives a product that has stock, and an archived product restores normally. Behavior is unchanged: the action is refused and nothing changes.
  - Both Gujarati texts are marked `MT – needs review`, and both guides' damage text says the units cannot be restored once the product is deleted.
- [x] **P3-F2 A void means the purchase was entered by mistake** (High for closed periods, reproduced)
  - Ruling: a void takes the purchase out of every period, including the GST summary of the month it was received. Goods sent back are a vendor return, dated on its own date, just as a sale void and a customer return differ. The GST summary and purchase register already behaved this way, so they are unchanged. `StockInTests.GstSummary_LeavesOutAVoidedPurchase_EvenInTheMonthItWasReceived` pins it with a purchase received in September and voided in October.
  - Gap found: a sale void requires the sale's own date to be open, but a purchase void checked only the void date. An untouched purchase from a locked year could therefore be voided, and its tax silently left that closed year's GST summary and purchase register, which may already have been filed. Reproduced on a fresh store: on 10 April 2027 a purchase of 26 September 2026 was offered as voidable.
  - **Fixed 26 Oct 2026:**
    - `CancelAsync` also requires the received date to be open and throws `PeriodLocked` otherwise.
    - `PreviewVoidAsync` gives the new reason `Bulk_Reason_PurchasePeriodLocked`: "Received in a locked year; use a vendor return instead". It reads each distinct received date's period once and skips the lot checks for a locked purchase.
    - `RunVoidBatchAsync` rechecks the period inside the batch.
    - The **Void purchase** tooltip and the bulk review show the reason with no UI change.
    - Regression: `StockInTests.PurchaseVoid_IsRefusedWhenThePurchaseIsInALockedYear_AndAVendorReturnStillWorks` covers the preview, the single void, the batch, and the vendor return that remains possible.
  - Cost: the 2,000-purchase review took 1.5–3.4 s in the Phase 7 runs, nearly all of it in each purchase's two lot queries. The change adds one small indexed lookup per distinct received date. It was not measured separately on the full-size store.
  - Also corrected: `Purchasing_CancelBlocked`, shown when a void is refused because stock was used. It still pointed to stock adjustments (removed in Phases 7–8) and said vendor returns were "not yet available". It now reads "Stock from this purchase was used, so it cannot be voided. Use a vendor return to send back what is left."
  - Both guides now say what a void means and when it is allowed.
- [x] **P3-F3 Choose a product for returned units when the original was deleted** (planned; owner decision 26 Oct 2026; build after Phase 6)
  - Already true, no change: customer returns, vendor returns, vendor-return cancels and damage corrections may refer to documents from a locked year. Each is dated on its own open date and counts in open reports; a locked year never changes. Only a void needs the original date open (P3-F2).
  - Decision: when the product was deleted, a customer return, a vendor-return cancel or a damage restore asks the operator to pick an existing product or create one. A new product is prefilled from the saved details; the operator chooses the category, which vendor-return and damage records do not save. The units go into a new direct lot of that product, dated today. A vendor return itself never meets a deleted product, because deleting needs zero stock and a return needs stock in the purchase's lot.
  - Stays from the original documents:
    - the quantity;
    - the booked unit cost;
    - the P&L effect;
    - for a vendor-return cancel, the GST credit added back, at the original rate and amount.
  - The new lot keeps the purchase tax actually paid; when that is unknown (older records), it takes the chosen product's rate.
  - Follows the chosen product: category, HSN, selling price and MRP (prefilled, editable, capped at MRP), and the tax charged when the units are sold.
  - Guards on picking an existing product:
    - the same unit, or a quantity that fits its fraction rule;
    - the same type (saleable or internal use);
    - an active product;
    - an expiry is asked for when the product tracks expiry and the saved details have none.
  - Customer returns gain the "pick an existing product" choice, so a product re-added by hand is not duplicated.
  - Traceability: the lot's reason names the source document and the audit log records the chosen product. A schema-40 link table for the lot panel and the reconciliation report is optional.
  - Until this is built, P3-F1's refusal messages stay.
  - **Built 4 Nov 2026, no schema change.**
    - **Shared rule.** `Inventory/RestoreProducts.cs` holds the saved details (`RestoreDraft`) and the operator's choice (`RestoreProductChoice`: an existing product or a `RestoreNewProduct`, plus the new lot's selling price, MRP and expiry). `Resolve` applies the guards inside the restoring transaction:
      - the product is active (`RestoreProductUnavailable`);
      - it is of the same type (`RestoreProductTypeMismatch`);
      - it has the same unit, or a unit that takes the quantity (`FractionalQtyNotAllowed`);
      - an expiry is given when the product tracks expiry and none was saved (`RestoreExpiryRequired`);
      - the price is not above MRP (`PriceAboveMrp`).
    - **New product.** It follows the product form's rules: a free SKU (`SkuExists`), an HSN of 4, 6 or 8 digits, and HSN with a GST rate in Regular GST mode. It takes the original's type and the lot's prices. The four new error codes have English and Gujarati text, Gujarati marked `MT – needs review`.
    - **What stays.** Quantity, booked unit cost and the P&L effect never depend on the choice: the documents keep their own amounts. A vendor-return cancel adds back the return's tax credit, at its rate and amount, because the GST summary and the variance head read the return and purchase lines.
    - **Customer returns.**
      - `GetReturnRestoreDraftAsync(saleLine, quantity)` gives the draft only when the sold product was deleted. `ReturnLineInput.Restore` carries the choice.
      - The units still go into a `CUSTOMER_RETURN` lot linked to its return line, as every recreated return lot does, rather than the "direct lot" the decision names. That link is what the reconciliation counts.
      - The lot keeps the cost-side tax the sale line saved. A line from before tax snapshots takes the chosen product's rate.
      - A later return of the same original reuses the product an earlier return went into, now only while that product is active.
      - Without a choice, the service still recreates a product from complete saved fields, as before; the app always asks first.
      - The old legacy review form (`ReturnRecreationWindow`, `ReturnRecreationInput`) is replaced by the shared window. The return line keeps its copy of the sale instead of being overwritten with reviewed values, so `OPERATOR_REVIEW` is no longer written.
      - An audit entry `RESTORE_PRODUCT` on the return line names the original and chosen products and whether the product was created.
    - **Damage corrections.**
      - `GetDamageRestoreDraftAsync(damage, restored)` gives the draft when the damage's lot and product are both gone. `DamageCorrection.Restore` carries the choice.
      - The lot keeps the damage's cost and takes the chosen product's tax rate, since a damage saves no purchase tax.
      - Its reason now names the document: `Damage D… correction restore: …`. The audit entry adds `restoredProduct` and `createdProduct`.
      - Fixed on the way: a damage's product is now read from its current lot. Before, a later correction of a damage restored into another product looked for the lot under the original product, missed it, and tried to recreate it.
    - **Vendor-return cancels.** `GetVendorReturnRestoreDraftsAsync(return)` lists the lines whose lot and product are both gone. A new `CancelVendorReturnAsync` overload takes a choice per return line; the old signature passes none. The lot keeps the purchase's cost and tax per piece, and the audit entry lists `restoredProducts`.
    - **Without a choice,** damage restores and vendor-return cancels still refuse with P3-F1's messages. Only service callers can reach that now, because the app asks first.
    - **Screen.** `RestoreProductWindow` opens from Sales history's **Return**, a damage's **Edit** or **Void**, and **Cancel return** in Return history.
      - It offers **Use an existing product** (a search of active products of the same type, prefilled with the saved name) or **Create a new product** (prefilled; each detail the record did not save is marked).
      - Then the new lot's selling price and MRP (from the chosen product, or the saved prices), and the expiry when none was saved.
      - It checks the guards it can before closing: a product picked, the unit, the expiry, price against MRP, and the SKU against every product, archived ones included.
      - To support it, catalog search rows now carry `TrackExpiry` (a column of the product row already read), and unit choices carry their code. The strings are `Stock_Restore*` in both languages; the Gujarati is marked `MT – needs review`.
    - **Not built:** the optional schema-40 link table. The lot reason, the audit entry and, for returns, the return-line link carry the trace.
  - Regressions:
    - `SalesTests.ReturnRestoreDraft_IsOfferedOnlyOnceTheSoldProductIsDeleted_WithTheSavedDetails` checks the draft's every field.
    - `SalesTests.LegacyReturn_RequiresReviewedFieldsBeforeRecreatingDeletedProduct` (rewritten) checks a new product from the operator's details, its lot and the audit entry.
    - `SalesTests.ReturnOfADeletedProduct_GoesIntoTheProductTheOperatorReAdded_WithinItsGuards` covers:
      - an archived product, an internal-use product, a product that tracks expiry with none saved, a price above MRP and a taken SKU, each refused with its code;
      - the return into the re-added product, and a second return that follows it without asking;
      - the booked cost, the P&L return cost and a clean reconciliation.
    - `MoneyOutTests.DamageRestore_OfADeletedProduct_GoesIntoTheChosenProduct_AndLaterCorrectionsFollowIt` covers the refusals, the restore, its reason, audit and P&L, and a later correction into the restored lot.
    - `StockInTests.VendorReturnCancellation_OfADeletedProduct_RestoresIntoANewProduct_WithThePurchaseTax` covers the draft, the new product, the lot's purchase cost and tax, the vendor balance, the variance head and the audit entry.
    - The UI smoke journey opens the window in both modes and languages. It checks that saving with no product picked keeps the window open with the reason, and that picking a product fills its price.
  - Both guides describe the choice (Sales history) and point to it from damage corrections and vendor-return cancels. The screenshots `restore-product.en.png` and `.gu.png` replace `return-recreation.*`.

## Phase 4 — Catalog, lots, deletion, damage, and expiry

No open issues found. The expiry filter window is covered under P8-1.

## Phase 5 — Immutable sales, statuses, and customer returns

- [x] **P5-1 A held or resumed bill keeps the date it was started** (High, reproduced)
  - `SalesService.CompleteAsync` uses the draft's `sale_date` (`SalesService.cs:565`), and `ResumeCoreAsync` (`:405-425`) never moves it to the business date.
  - Repro: held on 26 October and completed on 27 October, the bill is dated the 26th, and day-end for the 27th shows 0 bills.
  - Impact:
    - P&L, day-end and the dashboard book the sale on the old date.
    - The invoice number comes from the old date's series.
    - A bill held across a year lock fails with "period locked" when completed.
  - Fix: set `sale_date` to the business date when a bill is resumed or completed. Its status is still `DRAFT` or `HELD` at that point, so the completed-sale lock does not apply.
  - **Fixed 26 Oct 2026.** Reproduced first on a fresh store: a bill held on 31 March 2027 and resumed on 1 April stayed dated 31 March, and a bill held on 31 December 2026 failed with `PeriodLocked` when completed on 9 January 2027.
    - `ResumeCoreAsync` sets `sale_date` to the business date in the same update that makes the bill a draft again.
    - `CompleteAsync` takes the business date, not the draft's date, for the period check, the invoice number, the stock changes and the sale. This also covers a draft that was never held but stayed open past midnight.
    - The date is changed in its own update while the bill is still open, and only when it differs. Every trigger on `sale` acts only on completed bills, so that update writes one row. Putting the date into the completing update would fire `trg_sale_product_last_date`, which recalculates the last sale date over every sale line of the bill's products.
    - Measured on a scratch copy of the full-size fixture (871,876 sales, 5.2 million lines; no trigger on `sale` changed after schema 36), with every change rolled back. Median of 15 interleaved runs, with the bill holding lines of the busiest products (38,851, 15,890, 12,367, 10,434 and 9,085 sale lines):

      | Completing step | 1 line | 5 lines |
      |---|---|---|
      | Same-day bill (no re-date) | 0.94 ms | 1.09 ms |
      | Re-dated while open, then completed (the fix) | 1.17 ms | 1.32 ms |
      | Date and status in one update (rejected) | 140 ms | 364 ms |
  - Regressions:
    - `SalesTests.HeldBill_CompletedOnALaterDay_IsDatedNumberedAndCountedOnThatDay`: a bill held on 31 March 2027, resumed and paid on 1 April, is dated 1 April and numbered `S2728-000001`. Day end counts it on 1 April and not on 31 March, `pnl_day` and `sale_report_day` hold only 1 April, and a same-day void is allowed.
    - `SalesTests.HeldOrOpenBill_FromBeforeAYearLock_CompletesOnTheOpenBusinessDate`: a held bill and an open draft, both from 31 December 2026, complete on 9 January 2027 after the calendar year locked, dated 9 January.
  - Both guides' Hold row says a bill takes its date and number on the day it is paid.

## Phase 6 — P/L, financial corrections, annual close, and stock reports

- [x] **P6-1 One failed weekly check stops the rest of maintenance, every session** (High, read; the failing count is reproduced in P2-1)
  - `MaintenanceService.cs:135-137` throws when any lot differs. Integrity, P&L-day drift and snapshot failures throw too.
  - `App.xaml.cs:462-464` then skips `PostDueDepreciationAsync` and `EnsureFinalizedAsync` for that session.
  - Because `maintenance.integrity_last_run` is never updated, the full lot check (about 200 s on the full-size store) reruns on every launch.
  - The only repairs are P2-1 (blocked) and `ScaleAudit rebuild-pnl-days`, which does not ship.
  - Fix:
    - Run depreciation and annual close independently of the integrity result.
    - Record that the check ran even when it found differences, so it waits a week again; the warning and the `lot_source_issue` list stay.
    - Give the operator a path for P2-1.
  - **Fixed 26 Oct 2026.** Checked on a fresh store first. `FreshStoreReconciliationTests` now ends with weekly maintenance after every stock workflow. The integrity, every-lot, open-P&L-day and saved-year checks all pass, and the run is recorded as "ok". So on fresh data the check does not fail. The fix is about what happens when it does: with differences carried over from older data, or after a later fault such as a failed daily backup.
    - **Independent steps.** `App.RunMaintenanceWhenIdleAsync` takes a list of steps: due depreciation, the annual close, then the daily backup and weekly check (`RunAfterShellAsync`). It runs each step once per session, whatever another step found, and reports all failures together after the last one. A step interrupted by activity resumes at the next idle window, and finished steps are not repeated. Depreciation and the close now run first: they take milliseconds, and before, a failed backup or check, or a ~100 s check interrupted by a customer, held them back.
    - **Recorded run.** `MaintenanceService` runs every check, then records the run with what it found: `maintenance.integrity_last_run`, `recovery_pending` 0, and an `INTEGRITY_CHECK` audit entry with result `differences`, the lot count, any invalid saved years and the first drifting P&L day. Only then does it fail, so the finding is logged and shown. The next full check is a week later. A damaged file (`integrity_check`, foreign keys, FTS) still stops the check and leaves it due, because nothing after it can be trusted.
    - **Operator path.** Under the P2-1 ruling, differences carried over from older data get no correction workflow. Differing lots now fail with their own `ErrorCode.LotsDifferFromDocuments`. When that is all maintenance found, the notice under the title bar reads "The weekly stock check found N lots whose quantity differs from their documents." Its **Show differences** button opens Purchased vs used reconciliation with **Differences only** ticked; `ReportNavigationState` now carries that tick. Any other failure keeps the general notice and its Settings button. The new texts are in both languages (Gujarati marked `MT – needs review`), and the notice re-renders on a language switch.
  - Full-size store: a schema-39 copy of the Phase 9 fixture, with ScaleAudit `weekly-maintenance`. Script and results are in `artifacts/data/revision-issues-phase6`.

    | Run | Time | Peak working set |
    |---|---|---|
    | Full check, one lot made to differ: recorded, then `LotsDifferFromDocuments` | 101.3 s | 82 MiB |
    | Next launch: not due | 35 ms | 67 MiB |
    | Lot restored: full check passes | 98.8 s | 83 MiB |

    Before the fix, every launch after a finding repeated the ~100 s check. The check's own cost is unchanged.
  - Regressions:
    - `AnnualCloseTests.StockReports_StateExcludedExpiredStockAndShowSourceDifferences`: maintenance fails with `LotsDifferFromDocuments` (1 lot), records the run date and the audit entry, and the next run does not check again.
    - `AnnualCloseTests.FullReturn_ReversesSalesCostAndRoundOff_AndOpenDaysReconcile`: a drifting P&L day is reported and recorded the same way.
    - `IdleMaintenanceTests` (UI tests): every step runs when an earlier one fails; an interrupted step resumes without repeating finished steps; the lot notice is used only when nothing else failed.
  - Not checked on screen: the notice needs ten idle minutes and a differing lot, so it was not opened in the running app. Both guides now describe the background upkeep and the notice (shell section) and the **Show differences** link (Reports).
- [x] **P6-2 Day-end cash ignores expense voids and edits** (Medium, reproduced)
  - `SalesService.GetDayEndAsync` (`SalesService.cs:1058-1060`) sums the raw `expense` table. Phase 6 made voids and edits separate `expense_correction` rows, which it never reads.
  - Repro: after voiding the day's only ₹100 cash expense, day-end still shows ₹100 of cash expenses and subtracts it from cash in drawer.
  - Fix: read each expense's current values (the newest correction), as `CurrentExpenseSql` in `MoneyOutService.cs` does.
  - **Fixed 26 Oct 2026.** Reproduced first on a fresh store: day end showed ₹430 of cash expenses instead of ₹290.
    - Day end now sums each cash expense at its current values, by the same rule as `CurrentExpenseSql`: the newest correction or, if there is none, the original row. A void is a correction to zero.
    - Each of the two branches reads its own date index.
    - A purchase Expense line is always booked with mode `OTHER`, so it is never cash, and its reversal on a void needs no branch.
    - A past day's figure changes when one of its expenses is later voided or edited. The day end shows the day as it is now known.
  - Full-size store (4,890 expenses): the plan uses `ix_expense_date`, `ix_expense_correction_new_date` and the covering `ix_expense_correction_expense`, with no scan. On the busiest expense day the query took 0.0059 ms (old: 0.0053 ms). With 2,689 corrections seeded in a rolled-back transaction, about 175 of them moved onto that day, it took 0.095 ms.
  - Regression: `SalesTests.DayEnd_CountsEachCashExpenseAtItsCurrentValues` covers a void, a lowered amount, cash changed to UPI, UPI changed to cash, an expense moved to the day before, one moved onto the day, one edited twice and one untouched. It checks the cash expected in the drawer and the other day's total.
  - Both guides' day-end text says how voided and edited expenses count.

## Phase 7 — Clean screens, fast search, filters, and bulk work

- [ ] **P7-1 Specific error reasons never reach the operator** (Medium, read)
  - 169 service checks throw `DomainException(ErrorCode.Validation, "<English reason>")`. `UserError.cs:22-26` formats `Error_Validation`, which has no placeholder, so all of them show "Check the entered values and try again."
  - Rules an operator can hit include:
    - "A held or draft bill still uses this lot"
    - "Internal-use products cannot be sold" (scanning an internal product's barcode)
    - "A bill lot is blocked"
    - "Salary for this employee and month is already posted"
    - "Depreciation is already posted for the disposal month or later"
    - "Expired reason requires an expired lot"
    - "This lot changed since it was loaded"
    - "More than 5000 products match. Narrow the filters"
    - the count failure in P2-1
  - Phase 7 requires actionable failure messages, and CLAUDE.md requires a localized `ErrorCode` for each service check.
  - Fix: give each operator-reachable rule its own `ErrorCode` with English and Gujarati text. Guards that only invalid arguments can trip may stay generic.

## Phase 8 — Remove obsolete features, code, and live schema

- [ ] **P8-1 The dashboard "Soon" tile opens a list that does not match it** (Medium, reproduced)
  - The tile counts lots within each product's, category's or default alert days (`ReportService.cs:70-83`).
  - The Products and Lots Expiring filters it opens use a fixed 30 days (`CatalogService.Search.cs:159,174`, `InventoryService.cs:123,139`).
  - Repro: with alert days of 60 and expiry in 45 days, the tile shows Soon = 1 and the filtered list is empty.
  - Fix: use the same alert-day rule in both places.
- [ ] **P8-2 The dashboard stock value leaves out archived products** (Low, read)
  - `ReportService.cs:93-94` filters `p.is_active=1`. A product with stock is archived instead of deleted, so its stock drops out of the tile, while the Current unsold stock report still counts it.
  - Fix: drop the filter for the stock value and keep it for the low-stock count.

## Phase 9 — Full-scale optimization, acceptance, and release

- [ ] **P9-1 Release notes and checklist still describe 1.1.0 on schema 37** (Medium, read)
  - `src/GroceryErp.App/CHANGELOG.en.md` and `CHANGELOG.gu.md` say "Grocery ERP 1.1.0 … Database schema: 37."
  - `ReleaseNotesService` shows that text once per new version, so the next package would announce the wrong version and none of the schema-38/39 changes.
  - Those changes are: rates including tax, line removal, table footers, Billing name search, the detail panel, trimmed reports and the tax columns.
  - `docs/release-checklist.md` covers only 1.1.0.
  - Fix: write the next version's notes in both languages (Gujarati marked `MT – needs review`) and start a checklist for that release.
- [ ] **P9-2 CLAUDE.md refers to a tool that is not in the repository** (Low, read)
  - It says `PasswordHashTool` is outside the solution, but `tools/PasswordHashTool` holds only `bin/` and `obj/`, and nothing for it is tracked in git. Remove the mention or commit the tool.

## §15 — Tax decisions (schema 39)

- [ ] **T-1 The Products list's "next to sell" lot can be an expired one** (Medium, reproduced)
  - `ListSql` in `CatalogService.Search.cs:314-316` picks the first lot by expiry with stock and active.
  - Billing's FEFO (`LotQuantities.cs:104-106`) also skips expired lots when `pos.block_expired` is on (the default), and breaks ties by received date.
  - Repro: with an expired lot at ₹100 and a fresh lot at ₹118, the list shows ₹100 and Billing adds the fresh lot at ₹118.
  - This goes against §15 decision 10, and Selling price, MRP and Purchase price incl. tax all come from the wrong lot.
  - Fix: apply the same expiry and ordering rules as `AllocateFefo`, as of the business date.
- [ ] **T-2 A recreated customer-return lot can show a tax label that contradicts its tax** (Low, read)
  - `SalesService.ReturnRecreation.cs:203` builds the lot's label and percent from the sale line, which is the sale side, but takes `unit_tax` and `unit_cost_incl_tax` from the sold lot, which is the purchase side.
  - In Composition or No-GST mode the sale line has no label and 0%, so the lot shows "—" next to a non-zero Tax/unit. When the purchase line used a different rate than the product, the label disagrees with the tax.
  - Fix: save the sold lot's label and percent on the sale line with the cost-side values, or derive them from the purchase line where it still exists.
