# Owner review of the recorded decisions, release 1.2.0

This page is for the release checklist's item "Owner review of the recorded decisions". Each line is a rule already built into 1.2.0, taken from the Phase 6, 8 and 9 execution records, §15 of [the plan](../revised-implementation-plan.md) and the rulings in [revised-plan-issues.md](../revised-plan-issues.md). Tick a line to keep it, or write beside it what should change. The source of each is in brackets.

## Money and accounts (read these first)

- [ ] **Cost in P&L is the booked cost.** In Regular GST mode a piece costs its price before tax, because purchase tax is claimed back as input credit. In Composition and No-GST modes it costs its price including tax. COGS, stock value and the GST summary use the same cost. (§15 decision 2)
- [ ] **Every selling price and MRP includes tax**, and no price can be above MRP. When a tax rate changes, the shelf price stays and only the tax inside it changes; repricing is the owner's choice. (§15 decision 7)
- [ ] **Cost and tax are fixed when a record is made.** Renaming a tax rate or changing its percent affects products and new records only; old purchase lines, batches and bills keep what they were saved with. (§15 decisions 5 and 9)
- [ ] **Margin % in purchase entry is measured as P&L books it:** on the selling price before tax in Regular mode, and against the purchase price including tax in Composition and No-GST modes. (tax decisions, 26 Oct)
- [ ] **Years lock on their own:** the calendar year on 8 January and the fiscal year on 8 April. A locked year's P&L is saved once with a checksum and never changes, even if the clock is moved back. (Phase 6)
- [ ] **Corrections to expenses, damages and salary payments.** Nothing posted is rewritten or deleted; Edit and Void save a dated correction. While both years containing the original date are open, the change counts on the original date. Otherwise the original stays and the difference is dated today. (Phase 6)
- [ ] **Salary changes** take effect from a month, never earlier than the first month whose two years are both open. Payments already made stay as paid. (Phase 6)
- [ ] **Asset changes** (method, rate, salvage) take effect from a month and recalculate the open months, with a preview first. Cost and in-use date can change only while every posted month is open. A month never posted in a locked year is still posted, with its charge dated in the open period. (Phase 6)
- [ ] **Internal-use goods have no automatic P&L head.** A saleable product made from them takes the cost and price you enter. (Phase 6)
- [ ] **Voiding a purchase means it was entered by mistake.** It leaves every period, including that month's GST summary, and is allowed only while its received date is in an open year. Goods sent back are a vendor return, dated when they are sent. (P3-F2)
- [x] **Day end shows a past day as it is now known:** voiding or editing an expense later changes that day's expected cash. Kept 6 November 2026. (P6-2; `revised-plan-bugs.md` O-4)

## Stock

- [ ] **A count** turns a shortage into a numbered damage, and a surplus into a new batch at the counted batch's cost and tax. (Phase 2 fix; tax decisions)
- [ ] **Units that come back after their product was deleted** (a customer return, a cancelled vendor return, a damage correction) go to a product you pick or create. They keep their quantity and booked cost; a vendor-return cancel and a customer return also keep their purchase tax. (P3-F3)
- [ ] **A batch recreated by cancelling a vendor return** is linked to it only by its reason text and the audit entry; no link table was built. (Plan §2 invariant 4)
- [ ] **The opening-stock import** reads a before-tax purchase price and applies each product's tax rate. It has no tax column. (tax decisions, 26 Oct)
- [ ] **Accepted risk: no in-app repair for a batch whose quantity differs from its documents.** This can happen only with data from older versions. The weekly check reports it and a count on it is refused. (P2-1 ruling; `revised-plan-bugs.md` R-1)
- [ ] **Accepted risk: no shipped repair for an open P&L day that no longer matches its sales and returns.** The weekly check reports it; the repair is a developer tool. (Phase 6; R-2)
- [ ] **Problems seen only with data from older versions are checked on a fresh store first,** since that data may be flawed. (ruling of 26 Oct)

## Screens and housekeeping

- [ ] **Reports are cut to seven:** P&L, sales register, item-wise sales, purchase register, GST summary, current unsold stock, and purchased vs used reconciliation. Low stock and expiry are Products filters, balances due are in Vendors, and the audit log is written but has no screen. (Reports trimmed, 25 Oct)
- [x] **The Expired tile** counts blocked expired batches and opens Products at **Any status**. Kept 6 November 2026. (Phase 8 fixes; O-1)
- [x] **The Payables tile** includes inactive vendors' balances. Kept 6 November 2026. (B-1; O-5)
- [ ] **"Tax" replaces "GST"** in general labels; GSTIN, the receipt's CGST/SGST, the GST mode setting and the GST summary keep "GST". Every money column of the tax tables shows two decimals. (§15 decision 12; tax decisions)
- [ ] **Compaction** is offered once after the upgrade and is always in Settings. The daily expiry popup is gone. The last backup that holds the old stock ledger is kept for ever. (Phase 8)
- [ ] **An upgrade** shows its time and disk estimate and waits for **Update now**. The app draws in software by default, with a Settings option to use the graphics card. (Phase 9)
- [ ] **The shop's real store starts empty on 1.2.0,** so old documents are not filled in with the new tax columns. Selling prices converted from 1.1.0 matter only to a store upgraded from it (accepted 6 November 2026, O-2). (§15)
