using Dapper;
using GroceryErp.Assets;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Purchasing;
using GroceryErp.Reports;
using GroceryErp.Sales;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GroceryErp.Integration.Tests;

// One October of a shop, entered only through the services, with every figure worked out by hand
// from the inputs. Each report, the dashboard, day end, the vendor balance, stock value and the
// receipts are checked against those figures and against each other. Mismatches are collected so
// one run lists all of them.
public sealed class AuditScenarioTests
{
    private static readonly DateOnly Today = new(2026, 10, 26);

    [Fact]
    public async Task OctoberScenario_EveryReportMatchesHandWorkedFigures()
    {
        using var f = new AuditFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var misses = new List<string>();
        void Check(string what, decimal expected, decimal actual)
        {
            if (expected != actual) misses.Add($"{what}: expected {expected:0.000}, got {actual:0.000}");
        }

        // ---------- Purchase P1, 20 Oct, rates before tax, other charges 39 ----------
        // Bases: A 10x100-50=950, B 4x250=1000, freight 200, internal 5x20=100 (sum 2250).
        // Other charges by base: 16.467, 17.333, 3.467, 1.733 (largest remainder).
        // Tax: A 47.500 (5%), B 180.000, freight 36.000, internal 18.000 (18%) = 281.500.
        // Total 2300-50+281.5+39 = 2570.500, round off +0.500, grand total 2571.000.
        long p1 = await f.Purchasing.SaveDraftAsync(null, new PurchaseDraftInput(f.VendorId, "INV-1", new(2026, 10, 20),
            new(2026, 10, 20), null, new Money(39), null,
            [
                new(f.A, null, null, null, new Quantity(10), new Quantity(2), new Money(100), new Money(50), f.Rate5, new Money(120), new Money(115)),
                new(f.B, null, null, null, new Quantity(4), Quantity.Zero, new Money(250), Money.Zero, f.Rate18, new Money(350), new Money(330)),
                new(f.A, null, null, null, Quantity.One, Quantity.Zero, new Money(200), Money.Zero, f.Rate18, null, Money.Zero)
                    { LineKind = "EXPENSE", ExpenseCategoryId = f.Freight },
                new(f.I, null, null, null, new Quantity(5), Quantity.Zero, new Money(20), Money.Zero, f.Rate18, null, new Money(20))
                    { LineKind = "INTERNAL" },
            ]), ct);
        // The Purchases list and the draft panel show a saved draft with this total.
        PurchaseListItem draftRow = (await f.Purchasing.GetPurchasePageAsync(new PurchaseFilter { Status = "DRAFT" }, null, null,
            cancellationToken: ct)).Items.Single(x => x.Id == p1);
        Check("draft P1 total in the Purchases list", 2571m, draftRow.GrandTotal.Value);
        PostedPurchase posted1 = await f.Purchasing.PostAsync(p1, OperationGuard.NewId(), ct);
        Check("P1 grand total", 2571m, posted1.Totals.GrandTotal.Value);
        Check("P1 round off", 0.5m, posted1.Totals.RoundOff.Value);
        Check("P1 tax", 281.5m, posted1.Totals.Tax.Value);
        (long lotA, long lotB, long lotI) = (posted1.LotIds[0], posted1.LotIds[1], posted1.LotIds[2]);
        // Lot A: (950+16.467)/12 = 80.539 booked; incl. tax (966.467+47.5)/12 = 84.497; tax 3.958.
        Check("lot A unit cost", 80.539m, f.Money("SELECT unit_cost FROM product_lot WHERE id=@id", lotA));
        Check("lot A unit tax", 3.958m, f.Money("SELECT unit_tax FROM product_lot WHERE id=@id", lotA));
        Check("lot A incl tax", 84.497m, f.Money("SELECT unit_cost_incl_tax FROM product_lot WHERE id=@id", lotA));
        // Lot B: 1017.333/4 = 254.333; incl. 1197.333/4 = 299.333; tax 45.000.
        Check("lot B unit cost", 254.333m, f.Money("SELECT unit_cost FROM product_lot WHERE id=@id", lotB));
        Check("lot B unit tax", 45m, f.Money("SELECT unit_tax FROM product_lot WHERE id=@id", lotB));
        // Internal lot: 101.733/5 = 20.347.
        Check("lot I unit cost", 20.347m, f.Money("SELECT unit_cost FROM product_lot WHERE id=@id", lotI));
        // Freight expense line books its exact before-tax amount: 200 + 3.467.
        Check("P1 freight expense", 203.467m, f.Money("SELECT amount FROM expense WHERE purchase_line_id IS NOT NULL ORDER BY id LIMIT 1", 0));

        // ---------- Purchase P2, 25 Oct: posted, then voided on 26 Oct ----------
        long p2 = await f.Purchasing.SaveDraftAsync(null, new PurchaseDraftInput(f.VendorId, "INV-2", new(2026, 10, 25),
            new(2026, 10, 25), null, Money.Zero, null,
            [
                new(f.A, null, null, null, new Quantity(2), Quantity.Zero, new Money(100), Money.Zero, f.Rate5, new Money(120), new Money(115)),
                new(f.A, null, null, null, Quantity.One, Quantity.Zero, new Money(100), Money.Zero, f.Rate18, null, Money.Zero)
                    { LineKind = "EXPENSE", ExpenseCategoryId = f.Freight },
            ]), ct);
        PostedPurchase posted2 = await f.Purchasing.PostAsync(p2, OperationGuard.NewId(), ct);
        Check("P2 grand total", 328m, posted2.Totals.GrandTotal.Value);
        await f.Purchasing.CancelAsync(p2, Today, OperationGuard.NewId(), ct);

        await f.Purchasing.RecordPaymentAsync(new(f.VendorId, null, new(2026, 10, 22), new Money(1500), "CASH", null, null),
            OperationGuard.NewId(), ct);

        // ---------- Direct lot of A, 5 at 90 before tax ----------
        long direct = await f.Inventory.CreateDirectLotAsync(new OpeningStockLine(f.A, new Quantity(5), new Money(90),
            new Money(115), new Money(120), null, null, Today), "Found in store", OperationGuard.NewId(), ct);
        Check("direct lot unit cost", 90m, f.Money("SELECT unit_cost FROM product_lot WHERE id=@id", direct));

        // ---------- S1, 24 Oct: A x3 (line discount 10) + B x1, bill discount 15, UPI ----------
        // Net: A 335-7.556 = 327.444 (taxable 311.851, tax 15.593); B 330-7.444 = 322.556 (273.353, 49.203).
        SalesService sales24 = f.SalesOn(new(2026, 10, 24));
        long s1 = await AuditFixture.SaleAsync(sales24, (f.A, lotA, 3), (f.B, lotB, 1));
        SaleDetails s1Draft = await sales24.GetAsync(s1, ct);
        await sales24.SetLineDiscountAsync(s1, s1Draft.Lines.Single(l => l.ProductId == f.A).Id, new Money(10), ct);
        await sales24.SetBillDiscountAsync(s1, new Money(15), ct);
        CompletedSale c1 = await sales24.CompleteAsync(new(s1, [new(PaymentMode.Upi, new Money(650))], OperationGuard.NewId()), ct);
        Check("S1 grand total", 650m, c1.Totals.GrandTotal.Value);
        Check("S1 taxable", 585.204m, c1.Totals.Taxable.Value);
        Check("S1 tax", 64.796m, c1.Totals.Tax.Value);
        Check("S1 cost", 495.950m, c1.Totals.Cost.Value);
        Check("S1 discount", 25m, c1.Totals.Discount.Value);

        // ---------- S2, 26 Oct: A x2 (line discount 0.40) + B x1, round off +0.400, cash 1000 ----------
        long s2 = await AuditFixture.SaleAsync(f.Sales, (f.A, lotA, 2), (f.B, lotB, 1));
        SaleDetails s2Draft = await f.Sales.GetAsync(s2, ct);
        await f.Sales.SetLineDiscountAsync(s2, s2Draft.Lines.Single(l => l.ProductId == f.A).Id, new Money(0.4m), ct);
        CompletedSale c2 = await f.Sales.CompleteAsync(new(s2, [new(PaymentMode.Cash, new Money(1000))], OperationGuard.NewId()), ct);
        Check("S2 grand total", 560m, c2.Totals.GrandTotal.Value);
        Check("S2 round off", 0.4m, c2.Totals.RoundOff.Value);
        Check("S2 taxable", 498.328m, c2.Totals.Taxable.Value);
        Check("S2 change", 440m, c2.Change.Value);
        Check("S2 details carry the change for the bill panel", 440m, (await f.Sales.GetAsync(s2, ct)).Change.Value);

        // ---------- S3, 26 Oct: A x1 from the direct lot, cash, voided the same day ----------
        long s3 = await AuditFixture.SaleAsync(f.Sales, (f.A, direct, 1));
        await f.Sales.CompleteAsync(new(s3, [new(PaymentMode.Cash, new Money(115))], OperationGuard.NewId()), ct);
        await f.Sales.VoidAsync(s3, "Wrong bill", OperationGuard.NewId(), ct);

        // ---------- S4, 26 Oct: A x1 (direct lot) + B x1, bill discount 0.60, round off -0.400, card ----------
        long s4 = await AuditFixture.SaleAsync(f.Sales, (f.A, direct, 1), (f.B, lotB, 1));
        await f.Sales.SetBillDiscountAsync(s4, new Money(0.6m), ct);
        CompletedSale c4 = await f.Sales.CompleteAsync(new(s4, [new(PaymentMode.Card, new Money(444))], OperationGuard.NewId()), ct);
        Check("S4 grand total", 444m, c4.Totals.GrandTotal.Value);
        Check("S4 round off", -0.4m, c4.Totals.RoundOff.Value);
        Check("S4 taxable", 388.660m, c4.Totals.Taxable.Value);
        Check("S4 cost", 344.333m, c4.Totals.Cost.Value);

        // ---------- R1 on S1, 26 Oct: A x1 back to its lot, B x1 damaged ----------
        SaleDetails s1Done = await f.Sales.GetAsync(s1, ct);
        CompletedReturn r1 = await f.Sales.ReturnAsync(new(s1, Today, "Returned", RefundMode.Cash,
            [new(s1Done.Lines.Single(l => l.ProductId == f.A).Id, Quantity.One, true),
             new(s1Done.Lines.Single(l => l.ProductId == f.B).Id, Quantity.One, false)], OperationGuard.NewId()), ct);
        // A 1/3: taxable 103.950, tax 5.198, refund 109.148, cost 80.539. B whole: 273.353, 49.203, 322.556, 254.333.
        Check("R1 taxable", 377.303m, r1.Taxable.Value);
        Check("R1 tax", 54.401m, r1.Tax.Value);
        Check("R1 refund", 431.704m, r1.Refund.Value);
        Check("R1 cost", 334.872m, r1.Cost.Value);

        // ---------- R2 on S2, 26 Oct: A x1 of 2; S2's +0.400 round off is shared 0.164/0.236 ----------
        SaleDetails s2Done = await f.Sales.GetAsync(s2, ct);
        CompletedReturn r2 = await f.Sales.ReturnAsync(new(s2, Today, "Returned", RefundMode.Cash,
            [new(s2Done.Lines.Single(l => l.ProductId == f.A).Id, Quantity.One, true)], OperationGuard.NewId()), ct);
        // Refund (229.6+0.164)/2 = 114.882; taxable 218.667/2 = 109.334; tax 10.933/2 = 5.467; rounding 0.081.
        Check("R2 refund", 114.882m, r2.Refund.Value);
        Check("R2 taxable", 109.334m, r2.Taxable.Value);
        Check("R2 tax", 5.467m, r2.Tax.Value);

        // The return receipt must add up: gross - discount + round off = refund total.
        ReceiptData r2Receipt = await f.Sales.GetReturnReceiptAsync(r2.ReturnId, ct);
        Check("R2 receipt: gross - discount + round off = refund total", r2Receipt.GrandTotal.Value,
            (r2Receipt.Gross - r2Receipt.Discount + r2Receipt.RoundOff).Value);
        Check("R2 receipt: taxable + tax + round off = refund total", r2Receipt.GrandTotal.Value,
            (r2Receipt.Taxable + r2Receipt.Tax + r2Receipt.RoundOff).Value);
        ReceiptData s2Receipt = await f.Sales.GetReceiptAsync(s2, cancellationToken: ct);
        Check("S2 receipt: gross - discount + round off = total", s2Receipt.GrandTotal.Value,
            (s2Receipt.Gross - s2Receipt.Discount + s2Receipt.RoundOff).Value);

        // ---------- Damage D1 on lot A: 1 broken, 10 recovered; corrected to 20 recovered ----------
        await f.MoneyOut.RecordDamageAsync(new(lotA, Today, Quantity.One, "BROKEN", new Money(10), null), OperationGuard.NewId(), ct);
        DamageItem d1 = (await f.MoneyOut.GetDamagePageAsync(new(Today, Today), null, null, cancellationToken: ct)).Items
            .Single(item => item.Reason == "BROKEN");
        await f.MoneyOut.CorrectDamageAsync(d1, new(Quantity.One, new Money(20), "BROKEN", null), "More recovered",
            OperationGuard.NewId(), ct);

        // ---------- Vendor returns on P1 ----------
        // VR1: B x1 for 250 + 45 tax against cost 254.333: variance -4.333. VR2: A x1 for 80 + 4, then cancelled.
        await f.Purchasing.PostVendorReturnAsync(new(p1, Today, "CN-1", "Leaking", [new(lotB, Quantity.One, new Money(250), new Money(45))]),
            OperationGuard.NewId(), ct);
        PostedVendorReturn vr2 = await f.Purchasing.PostVendorReturnAsync(new(p1, Today, "CN-2", "Torn",
            [new(lotA, Quantity.One, new Money(80), new Money(4))]), OperationGuard.NewId(), ct);
        await f.Purchasing.CancelVendorReturnAsync(vr2.Id, Today, "Vendor refused", OperationGuard.NewId(), ct);

        // ---------- Internal use of 2 packing units ----------
        await f.Purchasing.ConsumeInternalAsync(lotI, new Quantity(2), Today, "Shop use", OperationGuard.NewId(), ct);

        // ---------- Counts: lot A short by 1 (8 -> 7), direct lot over by 1 (4 -> 5) ----------
        Check("lot A before count", 8m, f.Qty(lotA));
        Check("direct lot before count", 4m, f.Qty(direct));
        await f.CountAsync(lotA, 7000, ct);
        await f.CountAsync(direct, 5000, ct);

        // ---------- Expenses ----------
        await f.MoneyOut.RecordExpenseAsync(new(new(2026, 10, 5), f.Rent, new Money(5000), "CASH", null, null, null), OperationGuard.NewId(), ct);
        long e2 = await f.MoneyOut.RecordExpenseAsync(new(Today, f.Rent, new Money(1200), "UPI", null, null, null), OperationGuard.NewId(), ct);
        long e3 = await f.MoneyOut.RecordExpenseAsync(new(Today, f.Rent, new Money(300), "CASH", null, null, null), OperationGuard.NewId(), ct);
        ExpensePage todayExpenses = await f.MoneyOut.GetExpensePageAsync(new(Today, Today), null, null, cancellationToken: ct);
        await f.MoneyOut.CorrectExpenseAsync(todayExpenses.Items.Single(x => x.Id == e2),
            new(Today, f.Rent, new Money(1000), "UPI", null, null, null), "Typo", OperationGuard.NewId(), ct);
        await f.MoneyOut.VoidExpenseAsync(todayExpenses.Items.Single(x => x.Id == e3), "Duplicate", OperationGuard.NewId(), ct);

        // ---------- Salary: October's run, 10,000 - 500 leave + 300 overtime; 2,000 of it was handed over on
        // 10 Oct, which the run records as part of the month (the app has no separate advance, O-9) ----------
        long employee = await f.MoneyOut.SaveEmployeeAsync(null, new("Ravi", null, null, new(2026, 1, 1), null,
            new Money(10000), true, null), ct);
        await f.MoneyOut.PostSalaryRunAsync(new(new YearMonth(2026, 10), Today, "CASH",
            [new(employee, new Money(10000), new Money(500), new Money(300), Money.Zero, "2,000 paid on 10 Oct")]),
            OperationGuard.NewId(), ct);

        // ---------- Asset: 12,000 from 1 Apr, SLM 10%, posted to September, sold 15 Oct for 11,000 ----------
        long asset = await f.Assets.SaveAssetAsync(null, new("Fridge", null, new(2026, 4, 1), new(2026, 4, 1), new Money(12000),
            null, DepreciationMethod.Slm, new Percent(10), Money.Zero, null), ct);
        await f.Assets.PostThroughAsync(new YearMonth(2026, 9), ct);
        // Apr-Sep: 1200 x days/365 = 98.630, 101.918, 98.630, 101.918, 101.918, 98.630 = 601.644.
        await f.Assets.DisposeAsync(asset, new(2026, 10, 15), new Money(11000), OperationGuard.NewId(), ct);
        IReadOnlyList<DepreciationEntry> schedule = await f.Assets.GetScheduleAsync(asset, ct);
        Check("depreciation Apr-Sep", 601.644m, schedule.Where(x => x.Period < new YearMonth(2026, 10)).Sum(x => x.Amount.Value));
        // October to the 15th: 1200 x 198/365 = 650.959, less 601.644 posted = 49.315. Book value 11,349.041.
        Check("depreciation Oct (to disposal)", 49.315m, schedule.Single(x => x.Period == new YearMonth(2026, 10)).Amount.Value);
        Check("disposal book value", 11349.041m, f.Money("SELECT disposal_book_value FROM asset WHERE id=@id", asset));

        // ================= Reports =================
        var reports = new ReportService(f.Database, f.Settings, f.Clock);
        var october = new ReportFilter(new(2026, 10, 1), new(2026, 10, 31));

        // ----- P&L for October -----
        PnlReport pnl = await reports.GetPnlReportAsync(reports.MonthPeriod(new YearMonth(2026, 10)), ct);
        decimal Head(string code) => pnl.Lines.Where(l => l.HeadCode == code).Sum(l => l.Amount.Value);
        decimal Category(long id) => pnl.Lines.Where(l => l.HeadCode == "EXPENSE_CATEGORY" && l.CategoryId == id).Sum(l => l.Amount.Value);
        // Sales: S1 585.204 + S2 498.328 + S4 388.660 (S3 void).
        Check("P&L sales", 1472.192m, Head("SALES"));
        Check("P&L sales returns", -486.637m, Head("SALES_RETURNS"));
        // Bills' round offs cancel (+0.4, -0.4); R2 refunded 0.081 of round off.
        Check("P&L round off", -0.081m, Head("ROUND_OFF"));
        // COGS: 495.950 + 415.411 + 344.333 - 334.872 - 80.539.
        Check("P&L COGS", 840.283m, Head("COGS"));
        // Damages: D1 70.539 - 10 correction; R1's B 254.333; count shortage 80.539.
        Check("P&L damages", 395.411m, Head("DAMAGES"));
        Check("P&L stock adjustments", 0m, Head("STOCK_ADJUSTMENT"));
        // Rent 5000 + 1000 (edited) + 0 (void); freight 203.467 (P1) + 100 - 100 (P2 voided).
        Check("P&L rent", 6000m, Category(f.Rent));
        Check("P&L freight", 203.467m, Category(f.Freight));
        Check("P&L depreciation", 49.315m, Head("DEPRECIATION"));
        Check("P&L disposal gain/(loss)", -349.041m, Head("ASSET_DISPOSAL"));
        Check("P&L vendor return variance", -4.333m, Head("VENDOR_RETURN_VARIANCE"));
        // The month's pay, 9,500 + 300, counted once.
        Check("P&L salaries", 9800m, Head("SALARIES"));
        Check("P&L net sales", 985.474m, pnl.Statement.NetSales.Value);
        Check("P&L gross profit", 145.191m, pnl.Statement.GrossProfit.Value);

        // ----- Sales register -----
        TabularReport register = await reports.GetReportAsync(ReportKind.SalesRegister, october, ct);
        Check("register rows", 4, register.Rows.Count);
        Check("register total", 1654m, register.Totals["Total"]);
        Check("register taxable", 1472.192m, register.Totals["Taxable"]);
        Check("register tax", 181.808m, register.Totals["Tax"]);
        Check("register round off", 0m, register.Totals["RoundOff"]);
        Check("register taxable = P&L sales", Head("SALES"), register.Totals["Taxable"]);

        // ----- Item-wise sales -----
        TabularReport items = await reports.GetReportAsync(ReportKind.ItemSales, october, ct);
        Check("item sales revenue = P&L sales + returns", Head("SALES") + Head("SALES_RETURNS"), items.Totals["Revenue"]);
        Check("item sales cost = P&L COGS", Head("COGS"), items.Totals["Cost"]);
        Check("item sales quantity", 6m, items.Totals["Quantity"]);
        Check("item sales A revenue", 426.610m, AuditFixture.Scaled(items.Rows.Single(r => (string?)r.Values.GetValueOrDefault("Sku") == "A").Values["Revenue"]));
        Check("item sales B revenue", 558.945m, AuditFixture.Scaled(items.Rows.Single(r => (string?)r.Values.GetValueOrDefault("Sku") == "B").Values["Revenue"]));

        // ----- GST summary -----
        TabularReport gst = await reports.GetReportAsync(ReportKind.GstSummary, october, ct);
        decimal Gst(string direction, decimal rate, string column) => gst.Rows
            .Where(r => (string?)r.Values["Direction"] == direction && (string?)r.Values["Hsn"] != "Subtotal" &&
                AuditFixture.Scaled(r.Values.GetValueOrDefault("Rate")) == rate)
            .Sum(r => AuditFixture.Scaled(r.Values[column]));
        Check("GST outward 5% taxable", 426.610m, Gst("OUTWARD", 5, "Taxable"));
        Check("GST outward 5% tax", 21.330m, Gst("OUTWARD", 5, "Tax"));
        Check("GST outward 18% taxable", 558.945m, Gst("OUTWARD", 18, "Taxable"));
        Check("GST outward 18% tax", 100.610m, Gst("OUTWARD", 18, "Tax"));
        Check("GST inward 5% taxable", 950m, Gst("INWARD", 5, "Taxable"));
        Check("GST inward 5% tax", 47.5m, Gst("INWARD", 5, "Tax"));
        Check("GST inward 18% taxable", 1050m, Gst("INWARD", 18, "Taxable"));
        Check("GST inward 18% tax", 189m, Gst("INWARD", 18, "Tax"));
        // Each direction ends with its subtotal; the footer names sales' and purchases' figures and
        // never adds them into one total.
        decimal Subtotal(string direction, string column) => AuditFixture.Scaled(gst.Rows.Single(r =>
            (string?)r.Values["Direction"] == direction && (string?)r.Values["Hsn"] == "Subtotal").Values[column]);
        Check("GST outward subtotal tax", 121.940m, Subtotal("OUTWARD", "Tax"));
        Check("GST inward subtotal tax", 236.5m, Subtotal("INWARD", "Tax"));
        Check("GST has no combined column total", 0m, gst.Totals.Count);
        Check("GST footer taxable value of sales", 985.555m, gst.FooterTotals.Single(t => t.Label == "Taxable value of sales").Value);
        Check("GST footer tax on sales", 121.940m, gst.FooterTotals.Single(t => t.Label == "Tax on sales").Value);
        Check("GST footer taxable value of purchases", 2000m, gst.FooterTotals.Single(t => t.Label == "Taxable value of purchases").Value);
        Check("GST footer tax on purchases", 236.5m, gst.FooterTotals.Single(t => t.Label == "Tax on purchases").Value);

        // ----- Purchase register -----
        TabularReport purchases = await reports.GetReportAsync(ReportKind.PurchaseRegister, october, ct);
        Check("purchase register rows", 1, purchases.Rows.Count);
        Check("purchase register total", 2571m, purchases.Totals["Total"]);

        // ----- Stock -----
        // Lot A 7 x 80.539 = 563.773; direct 4 x 90 = 360; surplus 1 x 90; internal 3 x 20.347 = 61.041.
        TabularReport stock = await reports.GetReportAsync(ReportKind.StockValue, new ReportFilter(), ct);
        Check("stock cost value", 1074.814m, stock.Totals["CostValue"]);
        Check("stock quantity", 15m, stock.Totals["Quantity"]);
        TabularReport reconciliation = await reports.GetReportAsync(ReportKind.StockReconciliation, new ReportFilter(), ct);
        Check("reconciliation differences", 0m, reconciliation.Totals["Difference"]);
        Check("lots that differ from documents", 0m, await f.Database.ReadAsync(c => LotSourceReconciliation.CheckAll(c, null).Count(), ct));

        // ----- Vendor balance: 1000 opening + 2571 - 1500 paid - 295 credit (P2 and VR2 cancelled out) -----
        Check("vendor balance", 1776m, f.Money("SELECT balance_due FROM v_vendor_balance WHERE vendor_id=@id", f.VendorId));

        // ----- Dashboard on 26 Oct -----
        DashboardSummary dashboard = await reports.GetDashboardAsync(ct);
        Check("dashboard today's sales", 1004m, dashboard.TodaySales.Value);
        Check("dashboard today's bills", 2, dashboard.TodayBills);
        Check("dashboard month sales", 1654m, dashboard.MonthSales.Value);
        Check("dashboard today's gross profit", 127.244m, dashboard.TodayGrossProfit.Value);
        Check("dashboard stock value = stock report", stock.Totals["CostValue"], dashboard.StockValue.Value);
        Check("dashboard payables", 1776m, dashboard.Payables.Value);
        Check("dashboard payments today add up to today's sales", dashboard.TodaySales.Value,
            dashboard.Payments.Sum(p => p.Amount.Value));
        // Cash kept: 1,000 handed over less 440 change (the voided bill's cash is left out).
        Check("dashboard cash today", 560m, dashboard.Payments.Single(p => p.Mode == "CASH").Amount.Value);
        Check("dashboard card today", 444m, dashboard.Payments.Single(p => p.Mode == "CARD").Amount.Value);

        // ----- Day end on 26 Oct -----
        DayEndSummary dayEnd = await f.Sales.GetDayEndAsync(Today, ct);
        Check("day end payable", 1004m, dayEnd.Payable.Value);
        Check("day end gross", 1005m, dayEnd.Gross.Value);
        Check("day end discounts", 1m, dayEnd.Discounts.Value);
        Check("day end gross - discounts + round off = payable", dayEnd.Payable.Value,
            (dayEnd.Gross - dayEnd.Discounts + dayEnd.RoundOff).Value);
        Check("day end returns value", 546.586m, dayEnd.ReturnValue.Value);
        Check("day end void value", 115m, dayEnd.VoidValue.Value);
        // 1000 tendered - 440 change - 546.586 cash refunds - 0 cash expenses (the 300 was voided).
        Check("day end cash expected", 13.414m, dayEnd.CashExpected.Value);

        // ----- Every projection and document header equals what its source rows add up to -----
        (string Name, string Sql)[] sourceChecks =
        [
            ("pnl_day vs sales and returns", """
                WITH s AS (SELECT sale_date day,COUNT(*) bills,SUM(taxable_total) taxable,SUM(round_off) rounding,SUM(cost_total) cost,SUM(grand_total) sales
                FROM sale WHERE status='COMPLETED' GROUP BY sale_date),
                r AS (SELECT return_date day,COUNT(*) returns,SUM(taxable_total) rt,SUM(cost_total) rc,SUM(refund_total-taxable_total-tax_total) rr
                FROM sale_return GROUP BY return_date),
                days AS (SELECT day FROM s UNION SELECT day FROM r),
                e AS (SELECT d.day,COALESCE(bills,0) bills,COALESCE(taxable,0) taxable,COALESCE(rounding,0) rounding,COALESCE(cost,0) cost,
                COALESCE(sales,0) sales,COALESCE(returns,0) returns,COALESCE(rt,0) rt,COALESCE(rc,0) rc,COALESCE(rr,0) rr
                FROM days d LEFT JOIN s ON s.day=d.day LEFT JOIN r ON r.day=d.day),
                p AS (SELECT day,bills,taxable,rounding,cost,sales,returns,return_taxable rt,return_cost rc,return_rounding rr FROM pnl_day
                WHERE bills<>0 OR taxable<>0 OR rounding<>0 OR cost<>0 OR sales<>0 OR returns<>0 OR return_taxable<>0 OR return_cost<>0 OR return_rounding<>0)
                SELECT COUNT(*) n FROM (SELECT * FROM e EXCEPT SELECT * FROM p UNION ALL SELECT * FROM p EXCEPT SELECT * FROM e)
                """),
            ("product_stock_total vs lots", """
                WITH e AS (SELECT p.id product_id,COALESCE(SUM(l.qty_on_hand),0) q,COALESCE(SUM(CAST(ROUND(l.qty_on_hand*l.unit_cost/1000.0) AS INTEGER)),0) c,
                COALESCE(SUM(CAST(ROUND(l.qty_on_hand*l.selling_price/1000.0) AS INTEGER)),0) sv,
                COALESCE(SUM(CAST(ROUND(l.qty_on_hand*COALESCE(l.mrp,l.selling_price)/1000.0) AS INTEGER)),0) mv,
                CASE WHEN p.reorder_level>0 AND COALESCE(SUM(l.qty_on_hand),0)<=p.reorder_level THEN 1 ELSE 0 END low
                FROM product p LEFT JOIN product_lot l ON l.product_id=p.id GROUP BY p.id),
                t AS (SELECT product_id,quantity,cost_value,selling_value,mrp_value,is_low FROM product_stock_total)
                SELECT COUNT(*) n FROM (SELECT * FROM e EXCEPT SELECT * FROM t UNION ALL SELECT * FROM t EXCEPT SELECT * FROM e)
                """),
            ("vendor balance vs documents", """
                SELECT COUNT(*) n FROM vendor v JOIN v_vendor_balance b ON b.vendor_id=v.id
                WHERE b.balance_due <> v.opening_balance
                + COALESCE((SELECT SUM(grand_total) FROM purchase WHERE vendor_id=v.id AND status='POSTED'),0)
                - COALESCE((SELECT SUM(amount) FROM vendor_payment WHERE vendor_id=v.id),0)
                - COALESCE((SELECT SUM(credit_total) FROM vendor_return r WHERE vendor_id=v.id
                AND NOT EXISTS(SELECT 1 FROM vendor_return_cancellation c WHERE c.vendor_return_id=r.id)),0)
                """),
            ("completed sale header vs its lines", """
                SELECT COUNT(*) n FROM sale s JOIN (SELECT sale_id,COUNT(*) c,SUM(taxable_amount) t,SUM(tax_amount) x,SUM(line_total) lt,SUM(cost_amount) k,
                SUM(discount) d,SUM(CAST(ROUND(qty*unit_price/1000.0) AS INTEGER)) g
                FROM sale_line GROUP BY sale_id) l ON l.sale_id=s.id
                WHERE s.status IN ('COMPLETED','VOID') AND (s.line_count<>l.c OR s.taxable_total<>l.t OR s.tax_total<>l.x OR s.cost_total<>l.k
                OR s.grand_total<>l.lt+s.round_off OR s.discount_total<>l.d OR s.gross_total<>l.g OR l.t+l.x<>l.lt)
                """),
            ("sale paid covers total, change = paid - total", """
                SELECT COUNT(*) n FROM sale s WHERE s.status='COMPLETED' AND (s.paid_total < s.grand_total OR s.change_returned <> s.paid_total-s.grand_total
                OR s.paid_total <> (SELECT SUM(amount) FROM sale_payment p WHERE p.sale_id=s.id))
                """),
            ("return header vs its lines", """
                SELECT COUNT(*) n FROM sale_return r JOIN (SELECT sale_return_id id,SUM(taxable_amount) t,SUM(tax_amount) x,SUM(refund_amount) f,SUM(cost_amount) k
                FROM sale_return_line GROUP BY sale_return_id) l ON l.id=r.id
                WHERE r.taxable_total<>l.t OR r.tax_total<>l.x OR r.refund_total<>l.f OR r.cost_total<>l.k
                """),
            ("returned never exceeds sold (qty, taxable, refund, cost)", """
                SELECT COUNT(*) n FROM sale_line sl JOIN (SELECT sale_line_id,SUM(qty) q,SUM(taxable_amount) t,SUM(cost_amount) k FROM sale_return_line GROUP BY sale_line_id) r
                ON r.sale_line_id=sl.id WHERE r.q>sl.qty OR r.t>sl.taxable_amount OR r.k>sl.cost_amount
                """),
            ("posted purchase header vs its lines", """
                SELECT COUNT(*) n FROM purchase p JOIN (SELECT purchase_id,SUM(CAST(ROUND(qty*unit_cost/1000.0) AS INTEGER)) sub,SUM(discount) d,SUM(tax_amount) x,SUM(line_total) lt
                FROM purchase_line GROUP BY purchase_id) l ON l.purchase_id=p.id
                WHERE p.status IN ('POSTED','CANCELLED') AND (p.subtotal<>l.sub OR p.discount_total<>l.d OR p.tax_total<>l.x
                OR p.grand_total<>p.subtotal-p.discount_total+p.tax_total+p.other_charges+p.round_off OR l.lt<>l.sub-l.d+l.x)
                """),
            ("sale line cost = unit cost x qty", """
                SELECT COUNT(*) n FROM sale_line WHERE cost_amount<>CAST(ROUND(qty*unit_cost/1000.0) AS INTEGER)
                """),
            ("sale line unit cost = its lot cost", """
                SELECT COUNT(*) n FROM sale_line sl JOIN product_lot l ON l.id=sl.lot_id WHERE sl.unit_cost<>l.unit_cost
                """),
            ("lot tax columns add up (before tax + tax = incl tax)", """
                SELECT COUNT(*) n FROM product_lot WHERE unit_tax IS NOT NULL AND unit_cost_incl_tax IS NOT NULL
                AND unit_cost<>unit_cost_incl_tax AND unit_cost+unit_tax<>unit_cost_incl_tax
                """),
        ];
        using (SqliteConnection db = f.Factory.Open())
            foreach ((string name, string sql) in sourceChecks)
                Check(name + " (rows that differ)", 0m, db.ExecuteScalar<long>(sql));

        Assert.True(misses.Count == 0, "Mismatches:\n" + string.Join("\n", misses));
    }

    [Fact]
    public async Task CompositionMode_TaxIsCost_AndInclusiveRatesConvertExactly()
    {
        using var f = new AuditFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var misses = new List<string>();
        void Check(string what, decimal expected, decimal actual)
        {
            if (expected != actual) misses.Add($"{what}: expected {expected:0.000}, got {actual:0.000}");
        }

        // Regular mode, a bill typed with rates including tax: 7 soaps at 99 incl. 18%.
        // 99/1.18 = 83.898 before tax; 7 x 83.898 = 587.286; tax 105.711; 692.997 rounds to 693.
        long inclusive = await f.Purchasing.SaveDraftAsync(null, new PurchaseDraftInput(f.VendorId, "INC-1", new(2026, 10, 20),
            new(2026, 10, 20), new Money(693), Money.Zero, null,
            [new(f.B, null, null, null, new Quantity(7), Quantity.Zero, new Money(99), Money.Zero, f.Rate18, new Money(120), new Money(110))])
        { RatesIncludeTax = true }, ct);
        PostedPurchase inc = await f.Purchasing.PostAsync(inclusive, OperationGuard.NewId(), ct);
        Check("inclusive purchase tax", 105.711m, inc.Totals.Tax.Value);
        Check("inclusive purchase grand total", 693m, inc.Totals.GrandTotal.Value);
        Check("inclusive purchase bill difference", 0m, inc.Totals.Difference?.Value ?? -1m);
        Check("inclusive lot cost", 83.898m, f.Money("SELECT unit_cost FROM product_lot WHERE id=@id", inc.LotIds[0]));
        Check("inclusive lot incl. tax", 99m, f.Money("SELECT unit_cost_incl_tax FROM product_lot WHERE id=@id", inc.LotIds[0]));

        // Composition: purchase tax is cost and bills charge no tax.
        using (SqliteConnection db = f.Factory.Open())
            db.Execute("UPDATE setting SET value='COMPOSITION' WHERE key='tax.mode'");
        await f.Settings.LoadAsync(ct);
        // 10 x 100 + other charges 10 + tax 50 = 1060; booked cost per piece 106.000 (before tax 101, tax 5).
        long p = await f.Purchasing.SaveDraftAsync(null, new PurchaseDraftInput(f.VendorId, "CMP-1", new(2026, 10, 21),
            new(2026, 10, 21), null, new Money(10), null,
            [new(f.A, null, null, null, new Quantity(10), Quantity.Zero, new Money(100), Money.Zero, f.Rate5, new Money(120), new Money(115))]), ct);
        PostedPurchase posted = await f.Purchasing.PostAsync(p, OperationGuard.NewId(), ct);
        long lot = posted.LotIds[0];
        Check("composition purchase total", 1060m, posted.Totals.GrandTotal.Value);
        Check("composition lot cost (tax is cost)", 106m, f.Money("SELECT unit_cost FROM product_lot WHERE id=@id", lot));
        Check("composition lot unit tax", 5m, f.Money("SELECT unit_tax FROM product_lot WHERE id=@id", lot));

        long sale = await AuditFixture.SaleAsync(f.Sales, (f.A, lot, 2));
        CompletedSale done = await f.Sales.CompleteAsync(new(sale, [new(PaymentMode.Cash, new Money(230))], OperationGuard.NewId()), ct);
        Check("composition bill tax", 0m, done.Totals.Tax.Value);
        Check("composition bill taxable", 230m, done.Totals.Taxable.Value);
        Check("composition bill cost", 212m, done.Totals.Cost.Value);
        long line = (await f.Sales.GetAsync(sale, ct)).Lines.Single().Id;
        CompletedReturn back = await f.Sales.ReturnAsync(new(sale, Today, "Returned", RefundMode.Cash,
            [new(line, Quantity.One, true)], OperationGuard.NewId()), ct);
        Check("composition return refund", 115m, back.Refund.Value);
        Check("composition return cost", 106m, back.Cost.Value);
        // Returned to the vendor for 100 + 5 against a cost of 106 including tax: variance -1.
        await f.Purchasing.PostVendorReturnAsync(new(p, Today, "CN-C", "Torn", [new(lot, Quantity.One, new Money(100), new Money(5))]),
            OperationGuard.NewId(), ct);

        var reports = new ReportService(f.Database, f.Settings, f.Clock);
        PnlReport pnl = await reports.GetPnlReportAsync(reports.MonthPeriod(new YearMonth(2026, 10)), ct);
        decimal Head(string code) => pnl.Lines.Where(l => l.HeadCode == code).Sum(l => l.Amount.Value);
        Check("composition P&L sales", 230m, Head("SALES"));
        Check("composition P&L returns", -115m, Head("SALES_RETURNS"));
        Check("composition P&L COGS", 106m, Head("COGS"));
        Check("composition P&L vendor return variance", -1m, Head("VENDOR_RETURN_VARIANCE"));
        TabularReport stock = await reports.GetReportAsync(ReportKind.StockValue, new ReportFilter(), ct);
        // Composition lot 8 x 106 + the regular soap lot 7 x 83.898.
        Check("composition stock value", 848m + 587.286m, stock.Totals["CostValue"]);
        TabularReport gst = await reports.GetReportAsync(ReportKind.GstSummary, new ReportFilter(new(2026, 10, 1), new(2026, 10, 31)), ct);
        foreach (ReportRow row in gst.Rows.Where(r => (string?)r.Values["Direction"] == "OUTWARD"))
            Check($"composition outward row {row.Values["Hsn"]} has no tax and reads 0%",
                0m, AuditFixture.Scaled(row.Values.GetValueOrDefault("Rate")) + AuditFixture.Scaled(row.Values["Tax"]));
        ReceiptData receipt = await f.Sales.GetReceiptAsync(sale, cancellationToken: ct);
        Check("composition receipt tax", 0m, receipt.Tax.Value);

        Assert.True(misses.Count == 0, "Mismatches:\n" + string.Join("\n", misses));
    }

    // The owner's decisions O-10 to O-12 in revised-plan-bugs.md (7 November 2026), on a store created on
    // the current schema and written only through the services: a count surplus and internal use stay out
    // of P&L, and a voided purchase leaves its own date's P&L.
    [Fact]
    public async Task OwnerDecisions_OnAFreshStore()
    {
        using var f = new AuditFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var misses = new List<string>();
        void Check(string what, decimal expected, decimal actual)
        {
            if (expected != actual) misses.Add($"{what}: expected {expected:0.000}, got {actual:0.000}");
        }

        // O-10 (kept; correcting the damage is the operator's job): 10 bought at 100. A count finds 9 (a
        // damage of 100), a later count finds the 10th again (a new lot at 100, no P&L entry), and all 10
        // are sold, so the one unit costs 100 twice. Correcting the damage instead books it once.
        long p = await f.Purchasing.SaveDraftAsync(null, new PurchaseDraftInput(f.VendorId, "OD-1", new(2026, 10, 20),
            new(2026, 10, 20), null, Money.Zero, null,
            [new(f.A, null, null, null, new Quantity(10), Quantity.Zero, new Money(100), Money.Zero, f.Rate5, new Money(120), new Money(115))]), ct);
        long lot = (await f.Purchasing.PostAsync(p, OperationGuard.NewId(), ct)).LotIds[0];
        await f.CountAsync(lot, 9000, ct);
        long found = await f.CountAsync(lot, 10000, ct);
        Check("count surplus starts a new lot of 1", 1m, f.Qty(found));
        Check("the counted lot keeps 9", 9m, f.Qty(lot));
        long sale = await AuditFixture.SaleAsync(f.Sales, (f.A, lot, 9), (f.A, found, 1));
        await f.Sales.CompleteAsync(new(sale, [new(PaymentMode.Cash, new Money(1150))], OperationGuard.NewId()), ct);

        // O-11: packing material bought for internal use (5 at 20 + 18%) and used up.
        long internalDraft = await f.Purchasing.SaveDraftAsync(null, new PurchaseDraftInput(f.VendorId, "OD-2", new(2026, 10, 20),
            new(2026, 10, 20), null, Money.Zero, null,
            [new(f.I, null, null, null, new Quantity(5), Quantity.Zero, new Money(20), Money.Zero, f.Rate18, null, new Money(20))
                { LineKind = "INTERNAL" }]), ct);
        long internalLot = (await f.Purchasing.PostAsync(internalDraft, OperationGuard.NewId(), ct)).LotIds[0];
        await f.Purchasing.ConsumeInternalAsync(internalLot, new Quantity(5), Today, "Packing", OperationGuard.NewId(), ct);

        // O-12: a purchase of 25 October with a freight line of 100, voided on 26 October.
        long voided = await f.Purchasing.SaveDraftAsync(null, new PurchaseDraftInput(f.VendorId, "OD-3", new(2026, 10, 25),
            new(2026, 10, 25), null, Money.Zero, null,
            [
                new(f.A, null, null, null, Quantity.One, Quantity.Zero, new Money(100), Money.Zero, f.Rate5, new Money(120), new Money(115)),
                new(f.A, null, null, null, Quantity.One, Quantity.Zero, new Money(100), Money.Zero, f.Rate18, null, Money.Zero)
                    { LineKind = "EXPENSE", ExpenseCategoryId = f.Freight },
            ]), ct);
        await f.Purchasing.PostAsync(voided, OperationGuard.NewId(), ct);
        await f.Purchasing.CancelAsync(voided, Today, OperationGuard.NewId(), ct);

        var reports = new ReportService(f.Database, f.Settings, f.Clock);
        PnlReport month = await reports.GetPnlReportAsync(reports.MonthPeriod(new YearMonth(2026, 10)), ct);
        decimal Head(PnlReport report, string code) => report.Lines.Where(l => l.HeadCode == code).Sum(l => l.Amount.Value);
        // O-10: cost of goods 1,000 for the 10 sold, plus the damage of 100 for the unit later found.
        Check("O-10 cost of goods sold", 1000m, Head(month, "COGS"));
        Check("O-10 damages (the unit found again)", 100m, Head(month, "DAMAGES"));
        Check("O-10 stock adjustments (no entry for the surplus)", 0m, Head(month, "STOCK_ADJUSTMENT"));
        // O-11: the internal-use spending (100 before tax) is in no expense: only O-10's damage is.
        Check("O-11 total expenses hold no internal use", 100m, month.Statement.TotalExpenses.Value);
        // O-12: the voided purchase's freight and its reversal are both on 25 October, the purchase's
        // date, so that day nets to nothing and the void day has no entry.
        PnlReport the25th = await reports.GetPnlReportAsync(ReportService.CustomPeriod(new(2026, 10, 25), new(2026, 10, 25)), ct);
        PnlReport the26th = await reports.GetPnlReportAsync(ReportService.CustomPeriod(Today, Today), ct);
        Check("O-12 freight on 25 Oct, the purchase's date, nets to nothing", 0m, Head(the25th, "EXPENSE_CATEGORY"));
        Check("O-12 no freight entry on 26 Oct, the void date", 0m, the26th.Lines.Count(l => l.HeadCode == "EXPENSE_CATEGORY"));
        Check("O-12 the reversal is dated on the purchase's date", 1m,
            f.Money("SELECT COUNT(*)*1000 FROM purchase_expense_reversal WHERE reversal_date='2026-10-25' AND purchase_id=@id", voided));
        TabularReport register = await reports.GetReportAsync(ReportKind.PurchaseRegister,
            new ReportFilter(new(2026, 10, 25), new(2026, 10, 25)), ct);
        Check("O-12 the purchase register of 25 Oct leaves the voided purchase out", 0m, register.Rows.Count);

        Assert.True(misses.Count == 0, "Mismatches:\n" + string.Join("\n", misses));
    }

    private sealed class AuditFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"grocery-erp-audit-{Guid.NewGuid():N}");

        public AuditFixture()
        {
            var paths = new AppPaths(new SettingsBootstrap(_root));
            Factory = new SqliteConnectionFactory(paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            Clock = new FixedClock(Today);
            var periods = new PeriodGuard(Settings, Clock);
            Inventory = new InventoryService(Database, new LotQuantities(Settings), new DocNumberService(Settings), periods, new AuditLog());
            Purchasing = new PurchasingService(Database, new LotQuantities(Settings), new DocNumberService(Settings), periods, Settings, new AuditLog());
            Sales = SalesOn(Today);
            MoneyOut = new MoneyOutService(Database, new LotQuantities(Settings), new DocNumberService(Settings), periods, new AuditLog());
            Assets = new AssetService(Database, Settings, periods, new AuditLog(), Clock);
            using SqliteConnection db = Factory.Open();
            long category = db.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Food') RETURNING id");
            long unit = db.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
            Rate5 = db.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=5000");
            Rate18 = db.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=18000");
            A = Product(db, "A", "Rice", category, unit, Rate5, "1006", "SALEABLE");
            B = Product(db, "B", "Soap", category, unit, Rate18, "3401", "SALEABLE");
            I = Product(db, "I", "Packing", category, unit, Rate18, null, "INTERNAL");
            Rent = Category(db, "Rent");
            Freight = Category(db, "Freight");
            VendorId = Purchasing.CreateVendorAsync(new("Supplier", null, "9999999999", null, null, null, "Ahmedabad",
                "Gujarat", "380001", "24ABCDE1234F1Z5", null, null, null, 30, new Money(1000), null)).GetAwaiter().GetResult();
        }

        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public FixedClock Clock { get; }
        public InventoryService Inventory { get; }
        public PurchasingService Purchasing { get; }
        public SalesService Sales { get; }
        public MoneyOutService MoneyOut { get; }
        public AssetService Assets { get; }
        public long Rate5 { get; }
        public long Rate18 { get; }
        public long A { get; }
        public long B { get; }
        public long I { get; }
        public long Rent { get; }
        public long Freight { get; }
        public long VendorId { get; }

        public SalesService SalesOn(DateOnly date)
        {
            var clock = new FixedClock(date);
            return new SalesService(Database, new LotQuantities(Settings), new DocNumberService(Settings),
                new PeriodGuard(Settings, clock), Settings, new AuditLog(), clock);
        }

        public static async Task<long> SaleAsync(SalesService sales, params (long Product, long Lot, decimal Quantity)[] lines)
        {
            long? id = null;
            foreach ((long product, long lot, decimal quantity) in lines)
                id = await sales.AddProductAsync(id, product, new Quantity(quantity), preferredLotId: lot);
            return id!.Value;
        }

        public Task<long> CountAsync(long lotId, long counted, CancellationToken ct) => Database.WriteAsync(tx =>
        {
            (long onHand, long version) = tx.Connection.QuerySingle<(long, long)>(
                "SELECT qty_on_hand,quantity_version FROM product_lot WHERE id=@lotId", new { lotId }, tx.Transaction);
            return new DirectStockEngine(new DocNumberService(Settings)).ReviewCount(tx, lotId, version, onHand,
                counted, Today, "Shelf count", OperationGuard.NewId());
        }, ct);

        public decimal Money(string sql, long id)
        {
            using SqliteConnection db = Factory.Open();
            return Fx3.FromScaled(db.ExecuteScalar<long>(sql, new { id }));
        }

        public decimal Qty(long lotId) => Money("SELECT qty_on_hand FROM product_lot WHERE id=@id", lotId);

        public static decimal Scaled(object? value) => value switch
        {
            long x => Fx3.FromScaled(x),
            int x => Fx3.FromScaled(x),
            decimal x => x,
            _ => 0m,
        };

        private static long Category(SqliteConnection db, string name) =>
            db.ExecuteScalar<long?>("SELECT id FROM expense_category WHERE name=@name", new { name })
            ?? db.ExecuteScalar<long>("INSERT INTO expense_category(name) VALUES (@name) RETURNING id", new { name });

        private static long Product(SqliteConnection db, string sku, string name, long category, long unit, long rate,
            string? hsn, string productClass) => db.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,hsn_code,tax_rate_id,track_expiry,product_class,
                    default_selling_price,default_mrp)
                VALUES (@sku,@name,@category,@unit,@hsn,@rate,0,@productClass,115000,120000) RETURNING id
                """, new { sku, name, category, unit, hsn, rate, productClass });

        public void Dispose()
        {
            Settings.Dispose();
            Database.Dispose();
            SqliteConnectionFactory.ClearPoolForDataRoot(_root);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedClock(DateOnly date) : IClock
    {
        public DateTime Now => date.ToDateTime(new TimeOnly(11, 0));
        public DateOnly BusinessDate => date;
    }
}
