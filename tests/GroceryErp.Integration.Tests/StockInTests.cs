using Dapper;
using GroceryErp.Catalog;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Purchasing;
using GroceryErp.Reports;
using GroceryErp.Sales;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class StockInTests
{
    [Fact]
    public async Task CatalogPurchaseVendorReturnAndCancellation_ChangeOnlyTheirLots()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var catalog = new CatalogService(fixture.Database, new AuditLog(), fixture.Settings, new SystemClock(fixture.Settings));
        ProductInput source = (await catalog.GetAsync(fixture.ProductId, ct)).Product;
        long directProduct = await catalog.CreateWithDirectLotAsync(
            source with { Sku = "LIVE-DIRECT", Name = "Direct stock", HsnCode = "1234", Barcodes = [] },
            new NewProductLotInput(Quantity.One, new Money(20), new Money(30),
                new Money(35), "DIRECT", fixture.Date.AddMonths(2), fixture.Date, "Owner stock"),
            fixture.Ledger, new DocNumberService(fixture.Settings),
            fixture.Periods, OperationGuard.NewId(), ct);
        using (var db = fixture.Factory.Open())
            db.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@id",
                new { id = directProduct }).ShouldBe(1000L);
        long first = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("LIVE-CANCEL"), ct);
        PostedPurchase cancelled = await fixture.Purchasing.PostAsync(first, OperationGuard.NewId(), ct);
        await fixture.Purchasing.CancelAsync(first, fixture.Date, OperationGuard.NewId(), ct);
        long second = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("LIVE-RETURN"), ct);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(second, OperationGuard.NewId(), ct);
        VendorReturnLot lot = (await fixture.Purchasing.GetVendorReturnLotsAsync(second, ct)).Single();
        PostedVendorReturn returned = await fixture.Purchasing.PostVendorReturnAsync(new(
            second, fixture.Date, "LIVE-CREDIT", "Surplus",
            [new(lot.LotId, Quantity.One, new Money(200), new Money(10))]),
            OperationGuard.NewId(), ct);
        await fixture.Purchasing.CancelVendorReturnAsync(returned.Id, fixture.Date,
            "Credit cancelled", OperationGuard.NewId(), ct);
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
                new { id = cancelled.LotIds.Single() }).ShouldBe(0L);
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
                new { id = posted.LotIds.Single() }).ShouldBe(11000L);
        }
        await fixture.Database.WriteAsync(tx => LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction)
            .ToArray().ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task ProductAndFirstDirectLot_AreAtomicAndHaveDedicatedOrigin()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var catalog = new CatalogService(fixture.Database, new AuditLog(), fixture.Settings, new SystemClock(fixture.Settings));
        ProductInput source = (await catalog.GetAsync(fixture.ProductId, ct)).Product;
        var input = source with { Sku = "PH3-DIRECT", Name = "Direct product", HsnCode = "1234", Barcodes = [] };
        var firstLot = new NewProductLotInput(new Quantity(3), new Money(20), new Money(30),
            new Money(35), "DIRECT", fixture.Date.AddMonths(2), fixture.Date, "Owner added stock");
        string operation = OperationGuard.NewId();
        var numbers = new DocNumberService(fixture.Settings);
        PeriodGuard periods = fixture.Periods;
        long productId = await catalog.CreateWithDirectLotAsync(input, firstLot, fixture.Ledger,
            numbers, periods, operation, ct);
        (await catalog.CreateWithDirectLotAsync(input, firstLot, fixture.Ledger,
            numbers, periods, operation, ct)).ShouldBe(productId);
        using var db = fixture.Factory.Open();
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot WHERE product_id=@productId", new { productId }).ShouldBe(1);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM direct_stock_origin WHERE product_id_snapshot=@productId AND reason='Owner added stock'", new { productId }).ShouldBe(1);
        db.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@productId", new { productId }).ShouldBe(3000);
        await Should.ThrowAsync<DomainException>(() => catalog.CreateWithDirectLotAsync(
            input with { Sku = "PH3-FAIL" }, firstLot with { Quantity = Quantity.Zero },
            fixture.Ledger, numbers, periods, OperationGuard.NewId(), ct));
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM product WHERE sku='PH3-FAIL'").ShouldBe(0);
    }

    [Fact]
    public async Task PhaseThreePurchase_TypesCreateDistinctLotsAndLinkedExpense_ThenVoidReversesEffects()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long internalProduct;
        long expenseCategory;
        using (var db = fixture.Factory.Open())
        {
            internalProduct = db.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,product_class)
                SELECT 'RAW-1','Packing material',category_id,uom_id,'INTERNAL'
                FROM product WHERE id=@id RETURNING id
                """, new { id = fixture.ProductId });
            expenseCategory = db.ExecuteScalar<long>(
                "INSERT INTO expense_category(name) VALUES ('Freight') RETURNING id");
        }
        PurchaseLineInput first = fixture.Purchase().Lines.Single();
        PurchaseLineInput raw = first with { ProductId = internalProduct, BatchNumber = "RAW" };
        PurchaseLineInput expense = first with
        {
            Quantity = Quantity.One,
            FreeQuantity = Quantity.Zero,
            Rate = new Money(50),
            Discount = Money.Zero,
            TaxRateId = fixture.ZeroTaxRateId,
            LineKind = "EXPENSE",
            ExpenseCategoryId = expenseCategory,
        };
        var draftInput = fixture.Purchase("PH3-MIX") with
        {
            VendorBillTotal = null,
            Lines = [first, first with { BatchNumber = first.BatchNumber },
                raw with { LineKind = "INTERNAL" }, expense],
        };
        long id = await fixture.Purchasing.SaveDraftAsync(null, draftInput, ct);
        (await fixture.Purchasing.GetDraftAsync(id, ct)).Input.Lines[3].LineKind.ShouldBe("EXPENSE");
        PostedPurchase posted = await fixture.Purchasing.PostAsync(id, OperationGuard.NewId(), ct);
        posted.LotIds.Count.ShouldBe(3);
        posted.LotIds.Distinct().Count().ShouldBe(3);
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot WHERE purchase_line_id IN (SELECT id FROM purchase_line WHERE purchase_id=@id)", new { id }).ShouldBe(3);
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM expense e JOIN purchase_line pl ON pl.id=e.purchase_line_id WHERE pl.purchase_id=@id", new { id }).ShouldBe(1);
            db.Execute("UPDATE expense_category SET name='Freight renamed' WHERE id=@expenseCategory", new { expenseCategory });
            Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() => db.Execute(
                "DELETE FROM expense WHERE purchase_line_id IN (SELECT id FROM purchase_line WHERE purchase_id=@id)", new { id }));
        }
        (await fixture.Purchasing.GetInvoiceAsync(id, ct)).Lines[3].ExpenseCategoryName.ShouldBe("Freight");
        (await fixture.Purchasing.GetPurchasesPageAsync(status: "POSTED", lineKind: "EXPENSE",
            cancellationToken: ct)).Items.Select(static item => item.Id).ShouldContain(id);
        await fixture.Purchasing.CancelAsync(id, fixture.Date, OperationGuard.NewId(), ct);
        using var verify = fixture.Factory.Open();
        verify.ExecuteScalar<long>("SELECT COUNT(*) FROM purchase_expense_reversal WHERE purchase_id=@id", new { id }).ShouldBe(1);
        verify.ExecuteScalar<long>("SELECT COALESCE(SUM(amount),0) FROM v_pnl_entry WHERE head_code='EXPENSE_CATEGORY' AND category_id=@expenseCategory", new { expenseCategory }).ShouldBe(0);
        verify.ExecuteScalar<long>("SELECT balance_due FROM v_vendor_balance WHERE vendor_id=@id", new { id = fixture.VendorId }).ShouldBe(100000);
    }

    [Fact]
    public async Task PurchaseExpenseLine_BooksTheLineAmountNotTheRoundedPiecePrice()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long category;
        using (var db = fixture.Factory.Open())
            category = db.ExecuteScalar<long>("INSERT INTO expense_category(name) VALUES ('Packing') RETURNING id");
        // 1,000 at 1.000 less 0.001 is 999.999, and the other charges add 0.455. One piece rounds to 1.000.
        PurchaseLineInput line = fixture.Purchase().Lines.Single() with
        {
            Quantity = new Quantity(1000),
            FreeQuantity = Quantity.Zero,
            Rate = new Money(1),
            Discount = new Money(0.001m),
            TaxRateId = fixture.ZeroTaxRateId,
            LineKind = "EXPENSE",
            ExpenseCategoryId = category,
        };
        PurchaseDraftInput bill = fixture.Purchase() with { VendorBillTotal = null, OtherCharges = new Money(0.455m) };
        long regular = await fixture.Purchasing.SaveDraftAsync(null,
            bill with { VendorInvoiceNumber = "EXP-EXACT", Lines = [line] }, ct);
        await fixture.Purchasing.PostAsync(regular, OperationGuard.NewId(), ct);
        // Without input credit the tax is cost too: 5% of 999.999 is 50.000.
        await fixture.Settings.SaveAsync(new Dictionary<string, string> { ["tax.mode"] = "COMPOSITION" }, ct);
        long composition = await fixture.Purchasing.SaveDraftAsync(null,
            bill with { VendorInvoiceNumber = "EXP-EXACT-TAX", Lines = [line with { TaxRateId = fixture.TaxRateId }] }, ct);
        await fixture.Purchasing.PostAsync(composition, OperationGuard.NewId(), ct);

        using var verify = fixture.Factory.Open();
        verify.Query<long>("""
            SELECT e.amount FROM expense e JOIN purchase_line pl ON pl.id=e.purchase_line_id ORDER BY pl.purchase_id
            """).ShouldBe([1_000_454L, 1_050_454L]);
        await fixture.Purchasing.CancelAsync(regular, fixture.Date, OperationGuard.NewId(), ct);
        verify.ExecuteScalar<long>("SELECT amount FROM purchase_expense_reversal WHERE purchase_id=@regular",
            new { regular }).ShouldBe(1_000_454L);
    }

    [Fact]
    public async Task VendorReturnCancellation_RestoresLotAndBalanceOnce()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("PH3-RET"), ct);
        PostedPurchase purchase = await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), ct);
        VendorReturnLot lot = (await fixture.Purchasing.GetVendorReturnLotsAsync(purchaseId, ct)).Single();
        PostedVendorReturn returned = await fixture.Purchasing.PostVendorReturnAsync(
            new(purchaseId, fixture.Date, "PH3-CN", "Surplus",
                [new(lot.LotId, Quantity.One, new Money(200), new Money(10))]),
            OperationGuard.NewId(), ct);
        await fixture.Purchasing.CancelVendorReturnAsync(returned.Id, fixture.Date,
            "Credit cancelled", OperationGuard.NewId(), ct);
        await fixture.Purchasing.CancelVendorReturnAsync(returned.Id, fixture.Date,
            "Repeated request", OperationGuard.NewId(), ct);
        using var db = fixture.Factory.Open();
        db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = purchase.LotIds[0] }).ShouldBe(11000);
        db.ExecuteScalar<long>("SELECT balance_due FROM v_vendor_balance WHERE vendor_id=@id", new { id = fixture.VendorId }).ShouldBe(2825000);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM vendor_return_cancellation").ShouldBe(1);
        LotSourceReconciliation.CheckLot(db, null, purchase.LotIds[0]).ShouldBeNull();
        db.ExecuteScalar<long>("SELECT COALESCE(SUM(amount),0) FROM v_pnl_entry WHERE head_code='VENDOR_RETURN_VARIANCE'").ShouldBe(0);
        (await fixture.Purchasing.GetVendorReturnLotsAsync(purchaseId, ct)).Single().AlreadyReturned.ShouldBe(Quantity.Zero);
    }

    [Fact]
    public async Task GstSummary_NetsVendorReturnsAgainstCancellations_AndKeepsThemAfterTheLotIsDeleted()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var reports = new ReportService(fixture.Database, fixture.Settings, new FixedClock(fixture.Date));
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("GST-RET"), ct);
        long lotId = (await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), ct)).LotIds.Single();
        var october = new DateOnly(2026, 10, 2);

        // Cancelled in the month it was made, a return leaves only the purchase: 10 × 250 at 5%.
        PostedVendorReturn sameMonth = await ReturnAsync("GST-CN-1", Quantity.One, 200, 10);
        await fixture.Purchasing.CancelVendorReturnAsync(sameMonth.Id, fixture.Date, "Credit refused",
            OperationGuard.NewId(), ct);
        (await InwardAsync(fixture.Date)).ShouldBe((2_500_000L, 125_000L));

        // Cancelled the next month, it stays in September and is added back in October.
        PostedVendorReturn nextMonth = await ReturnAsync("GST-CN-2", Quantity.One, 200, 10);
        await fixture.Purchasing.CancelVendorReturnAsync(nextMonth.Id, october, "Credit refused",
            OperationGuard.NewId(), ct);
        (await InwardAsync(fixture.Date)).ShouldBe((2_300_000L, 115_000L));
        (await InwardAsync(october)).ShouldBe((200_000L, 10_000L));

        // A full return leaves the lot at zero; deleting the lot keeps the return in the summary.
        await ReturnAsync("GST-CN-3", new Quantity(11), 400, 20);
        await fixture.DeleteZeroLotAsync(lotId, ct);
        (await InwardAsync(fixture.Date)).ShouldBe((1_900_000L, 95_000L));

        Task<PostedVendorReturn> ReturnAsync(string credit, Quantity quantity, decimal beforeTax, decimal tax) =>
            fixture.Purchasing.PostVendorReturnAsync(new(purchaseId, fixture.Date, credit, "Surplus",
                [new(lotId, quantity, new Money(beforeTax), new Money(tax))]), OperationGuard.NewId(), ct);

        async Task<(long Taxable, long Tax)> InwardAsync(DateOnly day)
        {
            var month = new DateOnly(day.Year, day.Month, 1);
            TabularReport report = await reports.GetReportAsync(ReportKind.GstSummary,
                new(From: month, To: month.AddMonths(1).AddDays(-1)), ct);
            ReportRow inward = report.Rows.Single(row => (string)row.Values["Direction"]! == "INWARD");
            inward.Values["Hsn"].ShouldBe("24ABCDE1234F1Z5");
            return ((long)inward.Values["Taxable"]!, (long)inward.Values["Tax"]!);
        }
    }

    // Owner ruling, 26 Oct 2026: a void means the purchase was entered by mistake, so it leaves
    // every period's GST summary; goods sent back are a vendor return, dated on its own date.
    [Fact]
    public async Task GstSummary_LeavesOutAVoidedPurchase_EvenInTheMonthItWasReceived()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var reports = new ReportService(fixture.Database, fixture.Settings, new FixedClock(fixture.Date));
        long kept = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("GST-KEPT"), ct);
        await fixture.Purchasing.PostAsync(kept, OperationGuard.NewId(), ct);
        long voided = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("GST-VOID"), ct);
        await fixture.Purchasing.PostAsync(voided, OperationGuard.NewId(), ct);

        await fixture.Purchasing.CancelAsync(voided, new DateOnly(2026, 10, 2), OperationGuard.NewId(), ct);

        foreach (DateOnly month in new[] { new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1) })
        {
            TabularReport report = await reports.GetReportAsync(ReportKind.GstSummary,
                new(From: month, To: month.AddMonths(1).AddDays(-1)), ct);
            ReportRow[] inward = report.Rows.Where(row => (string)row.Values["Direction"]! == "INWARD").ToArray();
            if (month.Month == 9)
                inward.Single().Values["Tax"].ShouldBe(125_000L);
            else
                inward.ShouldBeEmpty();
        }
    }

    // Like a sale void, a purchase void erases the purchase from its own date, so that date must
    // still be open. A purchase from a locked year is corrected with a dated vendor return instead.
    [Fact]
    public async Task PurchaseVoid_IsRefusedWhenThePurchaseIsInALockedYear_AndAVendorReturnStillWorks()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("LOCKED-VOID"), ct);
        long lotId = (await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), ct)).LotIds.Single();
        // On 10 April 2027 both years holding 26 September 2026 are locked: CY 2026 and FY 2026-27.
        var today = new DateOnly(2027, 4, 10);
        var later = new PurchasingService(fixture.Database, fixture.Ledger, new DocNumberService(fixture.Settings),
            new PeriodGuard(fixture.Settings, new FixedClock(today)), fixture.Settings, new AuditLog());

        PurchasingBulkRow preview = (await later.PreviewVoidAsync([purchaseId], ct)).Single();
        preview.Eligible.ShouldBeFalse();
        preview.ReasonKey.ShouldBe("Bulk_Reason_PurchasePeriodLocked");
        (await Should.ThrowAsync<DomainException>(() => later.CancelAsync(purchaseId, today, OperationGuard.NewId(), ct)))
            .Code.ShouldBe(ErrorCode.PeriodLocked);
        (await later.RunVoidBatchAsync([purchaseId], today, BulkBatch.OperationId(OperationGuard.NewId(), 0), ct))
            .ShouldBe([new BulkItemResult(purchaseId, BulkItemOutcome.NotEligible, "Bulk_Reason_PurchasePeriodLocked")]);

        await later.PostVendorReturnAsync(new(purchaseId, today, "LOCKED-CN", "Entered twice",
            [new(lotId, new Quantity(11), new Money(2500), new Money(125))]), OperationGuard.NewId(), ct);
        using var db = fixture.Factory.Open();
        db.ExecuteScalar<string>("SELECT status FROM purchase WHERE id=@purchaseId", new { purchaseId }).ShouldBe("POSTED");
        db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@lotId", new { lotId }).ShouldBe(0L);
    }

    [Fact]
    public async Task VendorReturnCancellation_AfterItsLotWasDeleted_RestoresTheUnitsInANewLot()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("PH3-GONE"), ct);
        long lotId = (await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), ct)).LotIds.Single();
        PostedVendorReturn returned = await fixture.Purchasing.PostVendorReturnAsync(new(purchaseId, fixture.Date,
            "PH3-CN-ALL", "Recalled", [new(lotId, new Quantity(11), new Money(400), new Money(20))]),
            OperationGuard.NewId(), ct);
        await fixture.DeleteZeroLotAsync(lotId, ct);

        string operation = OperationGuard.NewId();
        await fixture.Purchasing.CancelVendorReturnAsync(returned.Id, fixture.Date, "Credit refused", operation, ct);
        await fixture.Purchasing.CancelVendorReturnAsync(returned.Id, fixture.Date, "Credit refused", operation, ct);
        await fixture.Purchasing.CancelVendorReturnAsync(returned.Id, fixture.Date, "Repeated request",
            OperationGuard.NewId(), ct);

        using var db = fixture.Factory.Open();
        // One new direct lot holds the units, with the deleted lot's cost, tax, batch, expiry and prices.
        db.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM product_lot l JOIN purchase_line pl ON pl.purchase_id=@purchaseId
            WHERE l.product_id=@productId AND l.purchase_line_id IS NULL AND l.origin_kind='DIRECT'
              AND l.lot_code='L000002' AND l.received_date=@date AND l.vendor_id=@vendorId
              AND l.qty_received=11000 AND l.qty_on_hand=11000
              AND l.unit_cost=pl.landed_unit_cost AND l.unit_tax=pl.landed_unit_tax
              AND l.unit_cost_incl_tax=pl.landed_unit_cost_incl_tax
              AND l.tax_rate_id_snapshot=pl.tax_rate_id AND l.tax_label_snapshot=pl.tax_label_snapshot
              AND l.tax_rate_pct_snapshot=pl.tax_rate_pct AND l.batch_no=pl.batch_no
              AND l.mfg_date=pl.mfg_date AND l.expiry_date=pl.expiry_date
              AND l.selling_price=pl.selling_price AND l.mrp=pl.mrp
            """, new
        {
            purchaseId,
            productId = fixture.ProductId,
            vendorId = fixture.VendorId,
            date = fixture.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        }).ShouldBe(1L);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot WHERE product_id=@id", new { id = fixture.ProductId })
            .ShouldBe(1L);
        db.ExecuteScalar<string>("""
            SELECT o.reason FROM direct_stock_origin o JOIN product_lot l ON l.id=o.lot_id WHERE l.lot_code='L000002'
            """).ShouldBe($"Vendor return {returned.DocumentNumber} cancelled: Credit refused");
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM vendor_return_cancellation").ShouldBe(1L);
        db.ExecuteScalar<long>("SELECT balance_due FROM v_vendor_balance WHERE vendor_id=@id",
            new { id = fixture.VendorId }).ShouldBe(2825000L);
        db.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@id",
            new { id = fixture.ProductId }).ShouldBe(11000L);
        db.ExecuteScalar<long>("SELECT COALESCE(SUM(amount),0) FROM v_pnl_entry WHERE head_code='VENDOR_RETURN_VARIANCE'")
            .ShouldBe(0L);
        LotSourceReconciliation.CheckAll(db, null, cancellationToken: ct).ShouldBeEmpty();
    }

    [Fact]
    public async Task VendorReturnCancellation_OfADeletedProduct_IsRefusedWithItsOwnReason()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("PH3-DELETED"), ct);
        long lotId = (await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), ct)).LotIds.Single();
        PostedVendorReturn returned = await fixture.Purchasing.PostVendorReturnAsync(new(purchaseId, fixture.Date,
            "PH3-CN-DEL", "Discontinued", [new(lotId, new Quantity(11), new Money(400), new Money(20))]),
            OperationGuard.NewId(), ct);
        var catalog = new CatalogService(fixture.Database, new AuditLog(), fixture.Settings, new SystemClock(fixture.Settings));
        (await catalog.RemoveAsync(fixture.ProductId, ct)).ShouldBe(ProductRemoval.Deleted);

        DomainException refused = await Should.ThrowAsync<DomainException>(() =>
            fixture.Purchasing.CancelVendorReturnAsync(returned.Id, fixture.Date, "Credit refused",
                OperationGuard.NewId(), ct));

        refused.Code.ShouldBe(ErrorCode.VendorReturnProductDeleted);
        using var db = fixture.Factory.Open();
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM vendor_return_cancellation").ShouldBe(0L);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot").ShouldBe(0L);
        db.ExecuteScalar<long>("SELECT balance_due FROM v_vendor_balance WHERE vendor_id=@id",
            new { id = fixture.VendorId }).ShouldBe(2405000L);
    }

    [Fact]
    public async Task VendorReturnCancellation_OfADeletedProduct_RestoresIntoANewProduct_WithThePurchaseTax()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("PH3-F3"), ct);
        long lotId = (await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), ct)).LotIds.Single();
        PostedVendorReturn returned = await fixture.Purchasing.PostVendorReturnAsync(new(purchaseId, fixture.Date,
            "PH3-CN-F3", "Discontinued", [new(lotId, new Quantity(11), new Money(400), new Money(20))]),
            OperationGuard.NewId(), ct);
        var catalog = new CatalogService(fixture.Database, new AuditLog(), fixture.Settings, new SystemClock(fixture.Settings));
        (await catalog.RemoveAsync(fixture.ProductId, ct)).ShouldBe(ProductRemoval.Deleted);

        RestoreDraft draft = (await fixture.Purchasing.GetVendorReturnRestoreDraftsAsync(returned.Id, ct)).Single();
        using var db = fixture.Factory.Open();
        draft.Key.ShouldBe(db.ExecuteScalar<long>("SELECT id FROM vendor_return_line"));
        draft.DocumentNumber.ShouldBe(returned.DocumentNumber);
        draft.ProductName.ShouldBe("Phase Three Product");
        draft.SuggestedSku.ShouldBe("PH3-001");
        draft.UnitCode.ShouldBe("PCS");
        draft.TaxRateId.ShouldBe(fixture.TaxRateId);
        draft.BatchNumber.ShouldBe("BATCH");
        draft.ExpiryDate.ShouldBe(fixture.Date.AddMonths(6));
        draft.Quantity.ShouldBe(new Quantity(11));
        (draft.SellingPrice, draft.Mrp).ShouldBe((new Money(300), new Money(350)));
        long category = db.ExecuteScalar<long>("SELECT id FROM category");
        var choice = new RestoreProductChoice(null, new RestoreNewProduct(draft.SuggestedSku, draft.ProductName, null,
            category, draft.UnitId!.Value, draft.TaxRateId, "1006", TrackExpiry: true, IsLoose: false),
            new Money(290), new Money(350));

        await fixture.Purchasing.CancelVendorReturnAsync(returned.Id, fixture.Date, "Credit refused", OperationGuard.NewId(),
            new Dictionary<long, RestoreProductChoice> { [draft.Key] = choice }, ct);

        long product = db.ExecuteScalar<long>("""
            SELECT id FROM product WHERE sku='PH3-001' AND id<>@old AND product_class='SALEABLE' AND hsn_code='1006'
              AND tax_rate_id=@tax AND track_expiry=1 AND default_selling_price=290000 AND default_mrp=350000
            """, new { old = fixture.ProductId, tax = fixture.TaxRateId });
        // The lot keeps the purchase's cost and tax; its prices are the operator's.
        db.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM product_lot l JOIN purchase_line pl ON pl.purchase_id=@purchaseId
            WHERE l.product_id=@product AND l.origin_kind='DIRECT' AND l.qty_on_hand=11000
              AND l.unit_cost=pl.landed_unit_cost AND l.unit_tax=pl.landed_unit_tax
              AND l.unit_cost_incl_tax=pl.landed_unit_cost_incl_tax AND l.tax_rate_id_snapshot=pl.tax_rate_id
              AND l.batch_no=pl.batch_no AND l.expiry_date=pl.expiry_date
              AND l.selling_price=290000 AND l.mrp=350000
            """, new { purchaseId, product }).ShouldBe(1L);
        db.ExecuteScalar<long>("SELECT balance_due FROM v_vendor_balance WHERE vendor_id=@id",
            new { id = fixture.VendorId }).ShouldBe(2825000L);
        db.ExecuteScalar<long>("SELECT COALESCE(SUM(amount),0) FROM v_pnl_entry WHERE head_code='VENDOR_RETURN_VARIANCE'")
            .ShouldBe(0L);
        db.ExecuteScalar<string>("SELECT changes FROM audit_log WHERE entity='vendor_return' AND action='CANCEL'")!
            .ShouldContain($"\"productId\":{product},\"createdProduct\":true");
        (await fixture.Purchasing.GetVendorReturnRestoreDraftsAsync(returned.Id, ct)).ShouldBeEmpty();
        LotSourceReconciliation.CheckAll(db, null, cancellationToken: ct).ShouldBeEmpty();
    }

    [Fact]
    public async Task InternalUse_IsAnImmutableNonPnlConsumption()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long internalProduct;
        using (var db = fixture.Factory.Open())
            internalProduct = db.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,product_class)
                SELECT 'RAW-2','Bags',category_id,uom_id,'INTERNAL'
                FROM product WHERE id=@id RETURNING id
                """, new { id = fixture.ProductId });
        var line = fixture.Purchase().Lines.Single() with { ProductId = internalProduct, LineKind = "INTERNAL" };
        long draft = await fixture.Purchasing.SaveDraftAsync(null,
            fixture.Purchase("PH3-USE") with { Lines = [line] }, ct);
        PostedPurchase purchase = await fixture.Purchasing.PostAsync(draft, OperationGuard.NewId(), ct);
        string operation = OperationGuard.NewId();
        long id = await fixture.Purchasing.ConsumeInternalAsync(purchase.LotIds[0], Quantity.One,
            fixture.Date, "Used for shop packing", operation, ct);
        (await fixture.Purchasing.ConsumeInternalAsync(purchase.LotIds[0], Quantity.One,
            fixture.Date, "Used for shop packing", operation, ct)).ShouldBe(id);
        using var verify = fixture.Factory.Open();
        verify.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = purchase.LotIds[0] }).ShouldBe(10000);
        verify.ExecuteScalar<long>("SELECT COUNT(*) FROM v_pnl_entry WHERE head_code='INTERNAL_USE'").ShouldBe(0);
        await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.CancelAsync(
            draft, fixture.Date, OperationGuard.NewId(), ct));
    }

    [Fact]
    public async Task PostedPurchaseInvoice_ShowsSavedLinesAndOriginalNamesAfterRename()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long draft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("INV-VIEW"), ct);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(draft, OperationGuard.NewId(), ct);
        using (var connection = fixture.Factory.Open())
        {
            connection.Execute("UPDATE vendor SET name='Renamed Supplier' WHERE id=@id", new { id = fixture.VendorId });
            connection.Execute("UPDATE product SET name='Renamed Product',sku='RENAMED' WHERE id=@id",
                new { id = fixture.ProductId });
        }

        PurchaseInvoice invoice = await fixture.Purchasing.GetInvoiceAsync(posted.Id, ct);
        invoice.DocumentNumber.ShouldBe(posted.DocumentNumber);
        invoice.VendorName.ShouldBe("Supplier");
        invoice.VendorInvoiceNumber.ShouldBe("INV-VIEW");
        invoice.TaxMode.ShouldBe(fixture.Settings.Current.TaxMode);
        invoice.Totals.GrandTotal.ShouldBe(posted.Totals.GrandTotal);
        PurchaseInvoiceLine line = invoice.Lines.Single();
        line.ProductName.ShouldBe("Phase Three Product");
        line.Sku.ShouldBe("PH3-001");
        line.BatchNumber.ShouldBe("BATCH");
        line.Quantity.ShouldBe(new Quantity(10));
        line.FreeQuantity.ShouldBe(Quantity.One);
        line.Rate.ShouldBe(new Money(250));
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<string>("SELECT snapshot_provenance FROM purchase_line WHERE purchase_id=@id",
                new { id = posted.Id }).ShouldBe("AT_POST");
            db.ExecuteScalar<string>("SELECT product_class_snapshot FROM purchase_line WHERE purchase_id=@id",
                new { id = posted.Id }).ShouldBe("SALEABLE");
            db.ExecuteScalar<string>("SELECT origin_kind FROM product_lot WHERE id=@id",
                new { id = posted.LotIds[0] }).ShouldBe("PURCHASE_LINE");
            db.ExecuteScalar<long>("SELECT origin_ref_id FROM product_lot WHERE id=@id",
                new { id = posted.LotIds[0] }).ShouldBe(db.ExecuteScalar<long>(
                "SELECT id FROM purchase_line WHERE purchase_id=@id", new { id = posted.Id }));
        }
    }

    [Fact]
    public async Task CountedDeficits_AreReasonedDamage_ThatKeepEachLotReconciled()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long draft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("CORRECTIONS"), ct);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(draft, OperationGuard.NewId(), ct);
        await fixture.CountAsync(posted.LotIds[0], new Quantity(10), "First", ct);
        await fixture.CountAsync(posted.LotIds[0], new Quantity(8), "Second", ct);
        using var connection = fixture.Factory.Open();
        connection.Query<long>("SELECT qty FROM damage WHERE lot_id=@lotId ORDER BY id",
            new { lotId = posted.LotIds[0] }).ShouldBe([1000L, 2000L]);
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
            new { id = posted.LotIds[0] }).ShouldBe(8000L);
        LotSourceReconciliation.CheckLot(connection, null, posted.LotIds[0]).ShouldBeNull();
        await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.CancelAsync(
            draft, fixture.Date, OperationGuard.NewId(), ct));
    }

    [Fact]
    public async Task LowStockProjection_TracksReorderAndLotMovements()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using (var connection = fixture.Factory.Open())
        {
            connection.Execute("UPDATE product SET reorder_level=12000 WHERE id=@id",
                new { id = fixture.ProductId });
            connection.ExecuteScalar<long>("SELECT is_low FROM product_stock_total WHERE product_id=@id",
                new { id = fixture.ProductId }).ShouldBe(1);
        }
        long draft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("LOW-PROJECTION"), ct);
        PostedPurchase purchase = await fixture.Purchasing.PostAsync(draft, OperationGuard.NewId(), ct);
        using (var connection = fixture.Factory.Open())
        {
            connection.ExecuteScalar<long>("SELECT is_low FROM product_stock_total WHERE product_id=@id",
                new { id = fixture.ProductId }).ShouldBe(1);
            connection.Execute("UPDATE product SET reorder_level=10000 WHERE id=@id",
                new { id = fixture.ProductId });
            connection.ExecuteScalar<long>("SELECT is_low FROM product_stock_total WHERE product_id=@id",
                new { id = fixture.ProductId }).ShouldBe(0);
        }
        await fixture.CountAsync(purchase.LotIds[0], new Quantity(9), "Projection test", ct);
        using var verify = fixture.Factory.Open();
        verify.ExecuteScalar<long>("SELECT is_low FROM product_stock_total WHERE product_id=@id",
            new { id = fixture.ProductId }).ShouldBe(1);
        var catalog = new CatalogService(fixture.Database, new AuditLog(), fixture.Settings, new SystemClock(fixture.Settings));
        (await catalog.SearchPageAsync(new ProductSearchFilter(LowStockOnly: true),
            cancellationToken: ct)).Items.Select(static item => item.Id).ShouldContain(fixture.ProductId);
    }

    [Fact]
    public async Task VendorHistory_PagesReachOldPurchasesAndKeepBalances()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var posted = new List<PostedPurchase>();
        for (int index = 0; index < 4; index++)
        {
            long draft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase($"INV-PAGE-{index}"), ct);
            posted.Add(await fixture.Purchasing.PostAsync(draft, OperationGuard.NewId(), ct));
        }
        await fixture.Purchasing.RecordPaymentAsync(new(fixture.VendorId, posted[0].Id,
            fixture.Date, new Money(500), "CASH", "PAGE-PAYMENT", null), OperationGuard.NewId(), ct);

        VendorLedgerPage first = await fixture.Purchasing.GetVendorLedgerPageAsync(fixture.VendorId, limit: 2,
            cancellationToken: ct);
        first.Items.Count.ShouldBe(2);
        first.HasMore.ShouldBeTrue();
        first.Items[0].Balance.ShouldBe(new Money(10500));
        var ledger = first.Items.ToList();
        VendorLedgerPage page = first;
        while (page.HasMore)
        {
            page = await fixture.Purchasing.GetVendorLedgerPageAsync(fixture.VendorId,
                page.NextDate, page.NextKind, page.NextId, limit: 2, cancellationToken: ct);
            ledger.AddRange(page.Items);
        }
        ledger.Count.ShouldBe(5);
        ledger[^1].Kind.ShouldBe("PAYMENT");
        ledger[^1].Balance.ShouldBe(new Money(-400));

        VendorLedgerPage purchasesOnly = await fixture.Purchasing.GetVendorLedgerPageAsync(
            fixture.VendorId, limit: 2, filterKind: "PURCHASE", cancellationToken: ct);
        purchasesOnly.Items.Count.ShouldBe(2);
        purchasesOnly.Items.All(static item => item.Kind == "PURCHASE").ShouldBeTrue();
        VendorLedgerPage olderPurchases = await fixture.Purchasing.GetVendorLedgerPageAsync(
            fixture.VendorId, purchasesOnly.NextDate, purchasesOnly.NextKind, purchasesOnly.NextId,
            limit: 2, filterKind: "PURCHASE", cancellationToken: ct);
        olderPurchases.Items.Count.ShouldBe(2);
        olderPurchases.HasMore.ShouldBeFalse();
        olderPurchases.Items[^1].Balance.ShouldBe(ledger.Single(item =>
            item.Kind == "PURCHASE" && item.Reference == olderPurchases.Items[^1].Reference).Balance);
        VendorLedgerPage paymentsOnly = await fixture.Purchasing.GetVendorLedgerPageAsync(
            fixture.VendorId, filterKind: "PAYMENT", cancellationToken: ct);
        paymentsOnly.Items.Single().Balance.ShouldBe(new Money(-400));

        VendorPurchasePage choices = await fixture.Purchasing.GetVendorPurchasePageAsync(
            fixture.VendorId, limit: 2, cancellationToken: ct);
        choices.Items.Count.ShouldBe(2);
        choices.HasMore.ShouldBeTrue();
        VendorPurchasePage older = await fixture.Purchasing.GetVendorPurchasePageAsync(
            fixture.VendorId, beforeDate: choices.NextDate, beforeId: choices.NextId, limit: 2,
            cancellationToken: ct);
        older.Items.Select(static item => item.Id).ShouldContain(posted[0].Id);
        VendorPurchasePage searched = await fixture.Purchasing.GetVendorPurchasePageAsync(
            fixture.VendorId, "INV-PAGE-0", cancellationToken: ct);
        searched.Items.Single().Id.ShouldBe(posted[0].Id);

        VendorLotPage lots = await fixture.Purchasing.GetVendorLotPageAsync(fixture.VendorId, limit: 2,
            cancellationToken: ct);
        lots.Items.Count.ShouldBe(2);
        lots.HasMore.ShouldBeTrue();
        (await fixture.Purchasing.GetVendorLotPageAsync(fixture.VendorId, lots.NextDate,
            lots.NextId, limit: 2, cancellationToken: ct)).Items.Count.ShouldBe(2);

        PurchasePage firstPurchases = await fixture.Purchasing.GetPurchasesPageAsync(limit: 2,
            cancellationToken: ct);
        long newerDraft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("INV-NEW"), ct);
        PostedPurchase newer = await fixture.Purchasing.PostAsync(newerDraft, OperationGuard.NewId(), ct);
        PurchasePage middle = await fixture.Purchasing.GetPurchasesPageAsync(
            firstPurchases.NextDate, firstPurchases.NextId, vendorId: fixture.VendorId,
            limit: 2, cancellationToken: ct);
        middle.Items.Select(static item => item.Id).ShouldContain(posted[0].Id);
        middle.Items.Select(static item => item.Id).ShouldNotContain(newer.Id);
        PurchasePage dated = await fixture.Purchasing.GetPurchasesPageAsync(
            from: fixture.Date, to: fixture.Date, vendorId: fixture.VendorId,
            limit: 2, cancellationToken: ct);
        dated.Items.Count.ShouldBe(2);
        PurchasePage productMatches = await fixture.Purchasing.GetPurchasesPageAsync(
            productQuery: "phase three", limit: 2, cancellationToken: ct);
        productMatches.Items.Count.ShouldBe(2);
        productMatches.HasMore.ShouldBeTrue();
        (await fixture.Purchasing.GetPurchasesPageAsync(productQuery: "PH3-001", limit: 2,
            cancellationToken: ct)).Items.Count.ShouldBe(2);
        (await fixture.Purchasing.GetPurchasesPageAsync(productQuery: "missing product",
            cancellationToken: ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public void LandedCost_AllocatesChargesAndFreeQuantityExactly()
    {
        PurchaseCostLine[] lines =
        [
            new(new Quantity(10), new Quantity(1), new Money(250), Money.Zero, new Money(125)),
            new(new Quantity(5), Quantity.Zero, new Money(100), Money.Zero, new Money(25)),
        ];

        List<Money> regular = LandedCost.Compute(lines, new Money(300), taxIsCost: false)
            .Select(static unit => unit.Booked).ToList();
        regular[0].ShouldBe(new Money(250));
        regular[1].ShouldBe(new Money(110));
        regular.Sum(static value => value.Scaled).ShouldBe(360000L);

        List<Money> composition = LandedCost.Compute(lines, new Money(300), taxIsCost: true)
            .Select(static unit => unit.Booked).ToList();
        composition[0].ShouldBe(new Money(261.364m));
        composition[1].ShouldBe(new Money(115));
    }

    [Fact]
    public async Task Purchase_PostCreatesLotsLedgerAndPayable_ThenCancellationReversesUntouchedStock()
    {
        using var fixture = new StockInFixture();
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase(), TestContext.Current.CancellationToken);

        string postOperation = OperationGuard.NewId();
        PostedPurchase posted = await fixture.Purchasing.PostAsync(purchaseId, postOperation, TestContext.Current.CancellationToken);
        PostedPurchase replay = await fixture.Purchasing.PostAsync(purchaseId, postOperation, TestContext.Current.CancellationToken);
        PurchaseLineDefaults defaults = await fixture.Purchasing.GetLineDefaultsAsync(fixture.VendorId, fixture.ProductId,
            fixture.Date, TestContext.Current.CancellationToken);
        defaults.LastRate.ShouldBe(new Money(250));
        defaults.TaxRate.ShouldBe(new Percent(5));
        defaults.Mrp.ShouldBe(new Money(350));
        defaults.SellingPrice.ShouldBe(new Money(300));
        VendorLotItem suppliedLot = (await fixture.Purchasing.GetVendorLotsAsync(fixture.VendorId,
            TestContext.Current.CancellationToken)).Single();
        suppliedLot.OnHand.ShouldBe(new Quantity(11));
        suppliedLot.BatchNumber.ShouldBe("BATCH");

        posted.DocumentNumber.ShouldStartWith("P2627-");
        posted.Totals.Subtotal.ShouldBe(new Money(2500));
        posted.Totals.Tax.ShouldBe(new Money(125));
        posted.Totals.GrandTotal.ShouldBe(new Money(2725));
        posted.LotIds.Count.ShouldBe(1);
        replay.WasReplay.ShouldBeTrue();

        using (var connection = fixture.Factory.Open())
        {
            connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = posted.LotIds[0] })
                .ShouldBe(11000L);
            connection.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@id", new { id = fixture.ProductId })
                .ShouldBe(11000L);
            connection.ExecuteScalar<long>("SELECT balance_due FROM v_vendor_balance WHERE vendor_id=@id", new { id = fixture.VendorId })
                .ShouldBe(2825000L);
        }

        using (var connection = fixture.Factory.Open())
            connection.QuerySingle<(long Quantity, long Cost, long Selling, long Mrp)>("""
                SELECT quantity,cost_value,selling_value,mrp_value FROM product_stock_total WHERE product_id=@id
                """, new { id = fixture.ProductId }).ShouldBe((11_000L, 2_600_004L, 3_300_000L, 3_850_000L));

        await fixture.Purchasing.RecordPaymentAsync(new(fixture.VendorId, purchaseId, fixture.Date, new Money(500), "UPI", "TX1", null),
            OperationGuard.NewId(), TestContext.Current.CancellationToken);
        (await fixture.Purchasing.GetVendorLedgerAsync(fixture.VendorId, TestContext.Current.CancellationToken))[^1].Balance
            .ShouldBe(new Money(2325));

        await fixture.Purchasing.CancelAsync(purchaseId, fixture.Date, OperationGuard.NewId(), TestContext.Current.CancellationToken);
        using (var connection = fixture.Factory.Open())
        {
            connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = posted.LotIds[0] }).ShouldBe(0L);
            connection.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@id", new { id = fixture.ProductId }).ShouldBe(0L);
            connection.ExecuteScalar<string>("SELECT status FROM purchase WHERE id=@purchaseId", new { purchaseId }).ShouldBe("CANCELLED");
            LotSourceReconciliation.CheckLot(connection, null, posted.LotIds[0]).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Purchase_CannotCancelAfterACountTouchedItsLot()
    {
        using var fixture = new StockInFixture();
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("INV-2"), TestContext.Current.CancellationToken);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), TestContext.Current.CancellationToken);
        await fixture.CountAsync(posted.LotIds[0], new Quantity(10), "Damaged during handling",
            TestContext.Current.CancellationToken);

        DomainException exception = await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.CancelAsync(
            purchaseId, fixture.Date, OperationGuard.NewId(), TestContext.Current.CancellationToken));
        exception.Code.ShouldBe(ErrorCode.CancelNotAllowed);
    }

    // "Untouched" means no document ever used a lot, not that its quantity is back to the receipt.
    [Fact]
    public async Task Purchase_SoldThenFullyReturned_StaysTouchedAndCannotBeVoided()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("INV-TOUCHED"), ct);
        long lot = (await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), ct)).LotIds[0];
        var sales = new SalesService(fixture.Database, fixture.Ledger, new DocNumberService(fixture.Settings), fixture.Periods,
            fixture.Settings, new AuditLog(), new FixedClock(fixture.Date));
        long sale = await sales.CreateDraftAsync(fixture.Date, ct);
        await sales.AddProductAsync(sale, fixture.ProductId, new Quantity(2), cancellationToken: ct);
        await sales.CompleteAsync(new(sale, [new(PaymentMode.Cash, (await sales.GetAsync(sale, ct)).Totals.GrandTotal)],
            OperationGuard.NewId()), ct);
        long line = (await sales.GetAsync(sale, ct)).Lines.Single().Id;
        await sales.ReturnAsync(new(sale, fixture.Date, "Customer changed mind", RefundMode.Cash,
            [new ReturnLineInput(line, new Quantity(2), true)], OperationGuard.NewId()), ct);
        using (var connection = fixture.Factory.Open())
            connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@lot", new { lot })
                .ShouldBe(new Quantity(11).Scaled);

        DomainException exception = await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.CancelAsync(
            purchaseId, fixture.Date, OperationGuard.NewId(), ct));

        exception.Code.ShouldBe(ErrorCode.CancelNotAllowed);
        using var after = fixture.Factory.Open();
        after.ExecuteScalar<string>("SELECT status FROM purchase WHERE id=@purchaseId", new { purchaseId }).ShouldBe("POSTED");
        after.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@lot", new { lot }).ShouldBe(new Quantity(11).Scaled);
        LotSourceReconciliation.CheckLot(after, null, lot).ShouldBeNull();
        // A partial vendor return remains the way to send that stock back.
        (await fixture.Purchasing.GetVendorReturnLotsAsync(purchaseId, ct)).Single().Returnable.ShouldBe(new Quantity(11));
    }

    [Fact]
    public async Task VendorReturn_PostsCreditStockAndVarianceOnce_AndCapsSourceLot()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("INV-RETURN"), ct);
        PostedPurchase purchase = await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), ct);
        using (var block = fixture.Factory.Open())
            block.Execute("UPDATE product_lot SET is_active=0 WHERE id=@id", new { id = purchase.LotIds[0] });
        await fixture.Settings.SaveAsync(new Dictionary<string, string> { ["tax.mode"] = "COMPOSITION" }, ct);
        VendorReturnLot lot = (await fixture.Purchasing.GetVendorReturnLotsAsync(purchaseId, ct)).Single();
        lot.Returnable.ShouldBe(new Quantity(11));
        var input = new VendorReturnInput(purchaseId, fixture.Date, "CN-001", "Expired at receipt",
            [new(lot.LotId, Quantity.One, new Money(200), new Money(10))]);
        string operation = OperationGuard.NewId();
        PostedVendorReturn posted = await fixture.Purchasing.PostVendorReturnAsync(input, operation, ct);
        PostedVendorReturn replay = await fixture.Purchasing.PostVendorReturnAsync(input, operation, ct);
        posted.DocumentNumber.ShouldStartWith("V2627-");
        posted.CreditTotal.ShouldBe(new Money(210));
        replay.WasReplay.ShouldBeTrue();
        (await fixture.Purchasing.GetVendorLedgerAsync(fixture.VendorId, ct))[^1].Kind.ShouldBe("VENDOR_RETURN");
        await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.CancelAsync(
            purchaseId, fixture.Date, OperationGuard.NewId(), ct));
        await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.PostVendorReturnAsync(
            input with { VendorCreditNumber = "CN-002", Lines = [new(lot.LotId, new Quantity(11), new Money(100), Money.Zero)] },
            OperationGuard.NewId(), ct));

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
            new { id = purchase.LotIds[0] }).ShouldBe(10000L);
        connection.ExecuteScalar<long>("SELECT balance_due FROM v_vendor_balance WHERE vendor_id=@id",
            new { id = fixture.VendorId }).ShouldBe(2615000L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM vendor_return_line WHERE lot_id=@id",
            new { id = purchase.LotIds[0] }).ShouldBe(1L);
        LotSourceReconciliation.CheckLot(connection, null, purchase.LotIds[0]).ShouldBeNull();
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='VENDOR_RETURN_VARIANCE'")
            .ShouldBe(-36364L);
    }

    [Fact]
    public async Task PurchaseDraft_CanBeReopenedWithAllEditableFields()
    {
        using var fixture = new StockInFixture();
        PurchaseDraftInput input = fixture.Purchase("SAVED-1");
        long id = await fixture.Purchasing.SaveDraftAsync(null, input, TestContext.Current.CancellationToken);

        PurchaseDraft reopened = await fixture.Purchasing.GetDraftAsync(id, TestContext.Current.CancellationToken);
        reopened.Input.VendorId.ShouldBe(input.VendorId);
        reopened.Input.VendorInvoiceNumber.ShouldBe("SAVED-1");
        reopened.Input.VendorBillTotal.ShouldBe(new Money(2725));
        reopened.Input.Lines.Single().ManufacturingDate.ShouldBe(fixture.Date.AddMonths(-1));
        reopened.Input.Lines.Single().FreeQuantity.ShouldBe(Quantity.One);

        await fixture.Purchasing.SaveDraftAsync(id, input with { VendorInvoiceNumber = "SAVED-2" },
            TestContext.Current.CancellationToken);
        (await fixture.Purchasing.GetDraftAsync(id, TestContext.Current.CancellationToken))
            .Input.VendorInvoiceNumber.ShouldBe("SAVED-2");
    }

    [Fact]
    public async Task PurchaseTypedIncludingGst_StoresBeforeGstFigures_ReopensAsTyped_AndIsRememberedForTheVendor()
    {
        using var fixture = new StockInFixture();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // The bill prints 262.50 a unit and a 21 discount, both including 5% GST: 250 and 20 before GST.
        PurchaseDraftInput input = fixture.Purchase("INCL-1") with
        {
            Lines = [fixture.Purchase().Lines[0] with { Rate = new Money(262.5m), Discount = new Money(21) }],
            RatesIncludeTax = true,
        };
        (await fixture.Purchasing.GetLastRatesIncludeTaxAsync(fixture.VendorId, cancellationToken)).ShouldBeFalse();
        long id = await fixture.Purchasing.SaveDraftAsync(null, input, cancellationToken);
        (await fixture.Purchasing.GetLastRatesIncludeTaxAsync(fixture.VendorId, cancellationToken)).ShouldBeTrue();

        PurchaseDraft reopened = await fixture.Purchasing.GetDraftAsync(id, cancellationToken);
        reopened.Input.RatesIncludeTax.ShouldBeTrue();
        reopened.Input.Lines.Single().Rate.ShouldBe(new Money(262.5m));
        reopened.Input.Lines.Single().Discount.ShouldBe(new Money(21));

        PostedPurchase posted = await fixture.Purchasing.PostAsync(id, OperationGuard.NewId(), cancellationToken);
        posted.Totals.Subtotal.ShouldBe(new Money(2500));
        posted.Totals.Discount.ShouldBe(new Money(20));
        posted.Totals.Tax.ShouldBe(new Money(124));
        // 10 × 262.50 − 21 as typed, plus 100 other charges.
        posted.Totals.GrandTotal.ShouldBe(new Money(2704));
        using (var connection = fixture.Factory.Open())
            connection.QuerySingle<(long Rate, long Discount, long EnteredRate, long EnteredDiscount, long LineTotal)>("""
                SELECT unit_cost,discount,entered_rate,entered_discount,line_total FROM purchase_line WHERE purchase_id=@id
                """, new { id }).ShouldBe((250_000L, 20_000L, 262_500L, 21_000L, 2_604_000L));

        // A later bill typed before GST keeps no typed copy and becomes the vendor's choice.
        long next = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("EXCL-1"), cancellationToken);
        (await fixture.Purchasing.GetDraftAsync(next, cancellationToken)).Input.RatesIncludeTax.ShouldBeFalse();
        (await fixture.Purchasing.GetLastRatesIncludeTaxAsync(fixture.VendorId, cancellationToken)).ShouldBeFalse();
        using (var connection = fixture.Factory.Open())
            connection.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM purchase_line WHERE purchase_id=@next AND entered_rate IS NULL AND entered_discount IS NULL
                """, new { next }).ShouldBe(1L);
    }

    [Fact]
    public void PurchaseLineExcludingTax_KeepsTheDiscountWithinTheConvertedLine()
    {
        // 1.001 including 5% is 0.953 before GST, so ten units are worth 9.53; the full discount
        // of 10.01 converts to 9.533 and is capped at the line value.
        PurchaseLineInput line = new PurchaseLineInput(1, null, null, null, new Quantity(10), Quantity.Zero,
            new Money(1.001m), new Money(10.01m), 2, null, Money.Zero).ExcludingTax(new Percent(5));
        line.Rate.ShouldBe(new Money(0.953m));
        line.Discount.ShouldBe(new Money(9.53m));

        PurchaseLineInput untaxed = new(1, null, null, null, Quantity.One, Quantity.Zero,
            new Money(99), new Money(9), 1, null, Money.Zero);
        untaxed.ExcludingTax(Percent.Zero).ShouldBe(untaxed);
    }

    [Fact]
    public async Task VendorDetails_CanBeLoadedEditedAndShownInLedger()
    {
        using var fixture = new StockInFixture();
        VendorInput vendor = await fixture.Purchasing.GetVendorAsync(fixture.VendorId, TestContext.Current.CancellationToken);
        vendor.Name.ShouldBe("Supplier");
        vendor.Pan.ShouldBe("ABCDE1234F");
        vendor.OpeningBalance.ShouldBe(new Money(100));

        await fixture.Purchasing.UpdateVendorAsync(fixture.VendorId,
            vendor with { Name = "Supplier Updated", PaymentTermsDays = 45 },
            cancellationToken: TestContext.Current.CancellationToken);
        VendorInput updated = await fixture.Purchasing.GetVendorAsync(fixture.VendorId, TestContext.Current.CancellationToken);
        updated.Name.ShouldBe("Supplier Updated");
        updated.PaymentTermsDays.ShouldBe(45);
        (await fixture.Purchasing.GetVendorLedgerAsync(fixture.VendorId, TestContext.Current.CancellationToken))[0]
            .Balance.ShouldBe(new Money(100));
    }

    [Fact]
    public async Task VendorBulkStatus_IsAtomicAndEditingPreservesStatus()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        VendorInput first = await fixture.Purchasing.GetVendorAsync(fixture.VendorId, ct);
        long secondId = await fixture.Purchasing.CreateVendorAsync(first with { Name = "Second Supplier", Gstin = null }, ct);

        IReadOnlyList<PurchasingBulkRow> preview = await fixture.Purchasing.PreviewVendorBulkAsync(
            [fixture.VendorId, long.MaxValue], false, ct);
        preview.Single(row => row.Id == long.MaxValue).ReasonKey.ShouldBe("Bulk_Missing");
        (await fixture.Purchasing.GetVendorsAsync(includeInactive: true, cancellationToken: ct))
            .Single(item => item.Id == fixture.VendorId).IsActive.ShouldBeTrue();

        (await fixture.Purchasing.RunVendorBulkBatchAsync([fixture.VendorId, secondId], false,
            BulkBatch.OperationId(OperationGuard.NewId(), 0), ct))
            .ShouldAllBe(result => result.Outcome == BulkItemOutcome.Changed);
        (await fixture.Purchasing.CountVendorsAsync(new VendorFilter { Status = "INACTIVE" }, ct)).ShouldBe(2);
        (await fixture.Purchasing.GetVendorsAsync(includeInactive: true, cancellationToken: ct))
            .Where(item => item.Id == fixture.VendorId || item.Id == secondId)
            .All(static item => !item.IsActive).ShouldBeTrue();
        await fixture.Purchasing.RecordPaymentAsync(new(fixture.VendorId, null, fixture.Date,
            new Money(10), "CASH", null, null), OperationGuard.NewId(), ct);
        (await fixture.Purchasing.GetVendorsAsync(includeInactive: true, cancellationToken: ct))
            .Single(item => item.Id == fixture.VendorId).BalanceDue.ShouldBe(new Money(90));

        await fixture.Purchasing.UpdateVendorAsync(fixture.VendorId, first with { Name = "Edited Supplier" },
            cancellationToken: ct);
        (await fixture.Purchasing.GetVendorsAsync(includeInactive: true, cancellationToken: ct))
            .Single(item => item.Id == fixture.VendorId).IsActive.ShouldBeFalse();
        (await fixture.Purchasing.RunVendorBulkBatchAsync([fixture.VendorId, secondId], true,
            BulkBatch.OperationId(OperationGuard.NewId(), 0), ct))
            .ShouldAllBe(result => result.Outcome == BulkItemOutcome.Changed);
        VendorPage page = await fixture.Purchasing.GetVendorPageAsync(new VendorFilter { Query = "Second" }, null, null, 10, ct);
        page.Items.Select(static item => item.Id).ShouldBe([secondId]);
    }

    [Fact]
    public async Task OpeningStockAndFefo_UseEarliestUnexpiredLot()
    {
        using var fixture = new StockInFixture();
        await fixture.Inventory.ImportOpeningStockAsync(
        [
            new(fixture.ProductId, new Quantity(2), new Money(20), new Money(30), new Money(35), new DateOnly(2027, 1, 1), "B2", fixture.Date),
            new(fixture.ProductId, new Quantity(3), new Money(21), new Money(31), new Money(36), new DateOnly(2026, 11, 1), "B1", fixture.Date),
        ], OperationGuard.NewId(), TestContext.Current.CancellationToken);

        IReadOnlyList<LotAllocation> allocation = await fixture.Database.WriteAsync(transaction => fixture.Ledger.AllocateFefo(
            transaction, fixture.ProductId, new Quantity(4), fixture.Date, new Dictionary<long, Quantity>()), TestContext.Current.CancellationToken);

        allocation.Count.ShouldBe(2);
        allocation[0].Quantity.ShouldBe(new Quantity(3));
        allocation[0].Lot.Expiry.ShouldBe(new DateOnly(2026, 11, 1));
        allocation[1].Quantity.ShouldBe(new Quantity(1));
    }

    [Fact]
    public async Task OpeningStockImport_BatchesAndReplayPreserveExactlyOneLotPerRow()
    {
        using var fixture = new StockInFixture();
        OpeningStockLine[] lines = Enumerable.Range(0, 205).Select(index => new OpeningStockLine(
            fixture.ProductId, Quantity.One, new Money(20), new Money(30), new Money(35),
            fixture.Date.AddMonths(2), $"IMPORT-{index}", fixture.Date)).ToArray();
        string operationId = OperationGuard.NewId();
        long first = await fixture.Inventory.ImportOpeningStockAsync(lines, operationId, TestContext.Current.CancellationToken);
        (await fixture.Inventory.ImportOpeningStockAsync(lines, operationId, TestContext.Current.CancellationToken))
            .ShouldBe(first);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM import_batch WHERE op_id=@operationId",
            new { operationId }).ShouldBe(3);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot WHERE batch_no LIKE 'IMPORT-%'")
            .ShouldBe(205);
        connection.ExecuteScalar<long>("SELECT SUM(qty_on_hand) FROM product_lot WHERE batch_no LIKE 'IMPORT-%'")
            .ShouldBe(205_000);
        OpeningStockLine[] changed = lines.ToArray();
        changed[0] = changed[0] with { Quantity = new Quantity(2) };
        DomainException conflict = await Should.ThrowAsync<DomainException>(() =>
            fixture.Inventory.ImportOpeningStockAsync(changed, operationId, TestContext.Current.CancellationToken));
        conflict.Code.ShouldBe(ErrorCode.Validation);
    }

    [Fact]
    public async Task LotDetailEdit_IsVersionedAndExpiryFiltersStayBounded()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long lotId = await fixture.Inventory.ImportOpeningStockAsync(
            [new(fixture.ProductId, new Quantity(4), new Money(20), new Money(30), new Money(35),
                fixture.Date.AddDays(10), "DETAIL", fixture.Date)], OperationGuard.NewId(), ct);
        LotItem original = (await fixture.Inventory.GetLotPageAsync(fixture.ProductId,
            limit: 1, cancellationToken: ct)).Items.Single();
        original.DetailVersion.ShouldBeGreaterThan(0);
        await fixture.Inventory.EditLotDetailsAsync(new(lotId, original.DetailVersion,
            new Money(32), new Money(35), fixture.Date.AddDays(20)), ct);
        DomainException stale = await Should.ThrowAsync<DomainException>(() =>
            fixture.Inventory.EditLotDetailsAsync(new(lotId, original.DetailVersion,
                new Money(33), new Money(35), fixture.Date.AddDays(21)), ct));
        stale.Code.ShouldBe(ErrorCode.Validation);
        (await fixture.Inventory.GetLotPageAsync(fixture.ProductId, limit: 1,
            expiryFilter: "EXPIRING", asOfDate: fixture.Date, cancellationToken: ct)).Items.Single().Id.ShouldBe(lotId);
        (await fixture.Inventory.GetLotPageAsync(fixture.ProductId, limit: 1,
            expiryFilter: "EXPIRED", asOfDate: fixture.Date, cancellationToken: ct)).Items.ShouldBeEmpty();
        using var connection = fixture.Factory.Open();
        connection.QuerySingle<(long Quantity, long Cost, long Price)>("""
            SELECT qty_on_hand AS Quantity,unit_cost AS Cost,selling_price AS Price
            FROM product_lot WHERE id=@lotId
            """, new { lotId }).ShouldBe((4_000L, 20_000L, 32_000L));
        connection.QuerySingle<(long Quantity, long Cost, long Selling, long Mrp)>("""
            SELECT quantity,cost_value,selling_value,mrp_value
            FROM product_stock_total WHERE product_id=@productId
            """, new { productId = fixture.ProductId }).ShouldBe((4_000L, 80_000L, 128_000L, 140_000L));
        connection.ExecuteScalar<string>("SELECT updated_at FROM product_lot WHERE id=@lotId",
            new { lotId }).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ZeroLotDelete_KeepsHistory_ButRejectsStockAndOpenBills()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long lotId = await fixture.Inventory.ImportOpeningStockAsync(
            [new(fixture.ProductId, Quantity.One, new Money(20), new Money(30), new Money(35),
                fixture.Date.AddMonths(2), "DELETE", fixture.Date)], OperationGuard.NewId(), ct);
        LotItem lot = (await fixture.Inventory.GetLotPageAsync(fixture.ProductId,
            cancellationToken: ct)).Items.Single();
        LotDeletionPreview withStock = await fixture.Inventory.PreviewDeleteZeroLotAsync(lotId, ct);
        withStock.CanDelete.ShouldBeFalse();
        withStock.BlockReason.ShouldBe("Stock_DeleteHasStock");
        await Should.ThrowAsync<DomainException>(() => fixture.Inventory.DeleteZeroLotAsync(
            lotId, lot.DetailVersion, ct));
        await fixture.CountAsync(lotId, Quantity.Zero, "Test zero", ct);
        long heldSale;
        using (var connection = fixture.Factory.Open())
        {
            heldSale = connection.ExecuteScalar<long>("""
                INSERT INTO sale(sale_date,status) VALUES (@date,'HELD') RETURNING id
                """, new { date = fixture.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
            connection.Execute("UPDATE sale SET status='DRAFT' WHERE id=@heldSale", new { heldSale });
            connection.Execute("""
                INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,
                    taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
                VALUES (@heldSale,1,@product,@lotId,'Held',1000,30000,30000,0,30000,20000,20000)
                """, new { heldSale, product = fixture.ProductId, lotId });
        }
        (await fixture.Inventory.PreviewDeleteZeroLotAsync(lotId, ct)).BlockReason.ShouldBe("Stock_DeleteOpenBill");
        await Should.ThrowAsync<DomainException>(() => fixture.Inventory.DeleteZeroLotAsync(
            lotId, lot.DetailVersion, ct));
        using (var connection = fixture.Factory.Open())
            connection.Execute("DELETE FROM sale WHERE id=@heldSale", new { heldSale });
        (await fixture.Inventory.PreviewDeleteZeroLotAsync(lotId, ct)).CanDelete.ShouldBeTrue();
        await fixture.Inventory.DeleteZeroLotAsync(lotId, lot.DetailVersion, ct);
        using var after = fixture.Factory.Open();
        after.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot WHERE id=@lotId", new { lotId }).ShouldBe(0);
        after.ExecuteScalar<long>("SELECT COUNT(*) FROM direct_stock_origin WHERE lot_id=@lotId", new { lotId }).ShouldBe(1);
        after.ExecuteScalar<long>("SELECT COUNT(*) FROM damage WHERE lot_id=@lotId", new { lotId }).ShouldBe(1);
    }

    [Fact]
    public async Task BulkZeroLotDelete_PreviewsAndRechecksStockInsideTheBatch()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var connection = fixture.Factory.Open();
        long firstId = InsertEmptyLot("EMPTY-A");
        long secondId = InsertEmptyLot("EMPTY-B");
        var filter = new LotFilter { ProductId = fixture.ProductId, Status = "ZERO", AsOf = fixture.Date };
        (await fixture.Inventory.CountLotsAsync(filter, ct)).ShouldBe(2);
        IReadOnlyList<long> matching = await fixture.Inventory.ResolveLotIdsAsync(filter, cancellationToken: ct);
        matching.ShouldBe([secondId, firstId]);
        IReadOnlyList<LotBulkRow> preview = await fixture.Inventory.PreviewLotBulkAsync(matching, LotBulkAction.DeleteZero, ct);
        preview.Count.ShouldBe(2);
        preview.All(static row => row.Eligible).ShouldBeTrue();
        // A lot that gained stock after the review is reported, not deleted.
        connection.Execute("UPDATE product_lot SET qty_on_hand=1000 WHERE id=@secondId", new { secondId });
        IReadOnlyList<BulkItemResult> results = await fixture.Inventory.RunLotBulkBatchAsync(LotBulkAction.DeleteZero,
            matching, BulkBatch.OperationId(OperationGuard.NewId(), 0), ct);
        results.Single(result => result.Id == firstId).Outcome.ShouldBe(BulkItemOutcome.Changed);
        results.Single(result => result.Id == secondId).ReasonKey.ShouldBe("Stock_DeleteHasStock");
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot WHERE id IN @ids",
            new { ids = new[] { firstId, secondId } }).ShouldBe(1);

        long InsertEmptyLot(string code) => connection.ExecuteScalar<long>("""
            INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,
                unit_cost,mrp,selling_price,qty_received,qty_on_hand,origin_kind)
            VALUES (@productId,@code,@date,@expiry,20000,35000,30000,0,0,'CUSTOMER_RETURN') RETURNING id
            """, new
        {
            productId = fixture.ProductId,
            code,
            date = fixture.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            expiry = fixture.Date.AddMonths(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    [Fact]
    public async Task LotBulkStatus_IsAtomicAndKeepsStockQuantities()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long firstId = await fixture.Inventory.ImportOpeningStockAsync(
            [new(fixture.ProductId, new Quantity(2), new Money(20), new Money(30), new Money(35),
                fixture.Date.AddMonths(2), "BULK-A", fixture.Date)], OperationGuard.NewId(), ct);
        long secondId = await fixture.Inventory.ImportOpeningStockAsync(
            [new(fixture.ProductId, new Quantity(3), new Money(20), new Money(30), new Money(35),
                fixture.Date.AddMonths(2), "BULK-B", fixture.Date)], OperationGuard.NewId(), ct);

        IReadOnlyList<BulkItemResult> withMissing = await fixture.Inventory.RunLotBulkBatchAsync(LotBulkAction.Block,
            [firstId, long.MaxValue], BulkBatch.OperationId(OperationGuard.NewId(), 0), ct);
        withMissing.Single(result => result.Id == long.MaxValue).Outcome.ShouldBe(BulkItemOutcome.Missing);
        (await fixture.Inventory.GetLotsByIdsAsync([firstId], ct)).Single().IsActive.ShouldBeFalse();
        (await fixture.Inventory.RunLotBulkBatchAsync(LotBulkAction.Block, [firstId, secondId],
                BulkBatch.OperationId(OperationGuard.NewId(), 0), ct))
            .Count(static result => result.Outcome == BulkItemOutcome.Changed).ShouldBe(1);
        (await fixture.Inventory.GetLotsByIdsAsync([firstId, secondId], ct))
            .All(static item => !item.IsActive).ShouldBeTrue();
        (await fixture.Inventory.GetLotPageAsync(new LotFilter { ProductId = fixture.ProductId, Status = "BLOCKED" }, null, 10, ct))
            .Items.Count.ShouldBe(2);
        (await fixture.Inventory.RunLotBulkBatchAsync(LotBulkAction.Unblock, [firstId, secondId],
                BulkBatch.OperationId(OperationGuard.NewId(), 0), ct))
            .ShouldAllBe(result => result.Outcome == BulkItemOutcome.Changed);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@id",
            new { id = fixture.ProductId }).ShouldBe(5_000L);
    }

    [Fact]
    public async Task Fefo_NeverAllocatesAnExpiredLotOrNegativeStock()
    {
        using var fixture = new StockInFixture();
        await fixture.Settings.SaveAsync(new Dictionary<string, string>
        {
            ["pos.block_expired"] = "1",
        }, TestContext.Current.CancellationToken);
        await fixture.Inventory.ImportOpeningStockAsync(
        [
            new(fixture.ProductId, new Quantity(2), new Money(20), new Money(30), new Money(35),
                fixture.Date.AddMonths(2), "GOOD", fixture.Date.AddDays(-2)),
            new(fixture.ProductId, Quantity.One, new Money(20), new Money(30), new Money(35),
                fixture.Date.AddDays(-1), "EXPIRED", fixture.Date.AddDays(-1)),
        ], OperationGuard.NewId(), TestContext.Current.CancellationToken);

        IReadOnlyList<LotAllocation> allocation = await fixture.Database.WriteAsync(transaction =>
            fixture.Ledger.AllocateFefo(transaction, fixture.ProductId, new Quantity(2), fixture.Date,
                new Dictionary<long, Quantity>()), TestContext.Current.CancellationToken);
        allocation.Single().Lot.Expiry.ShouldBe(fixture.Date.AddMonths(2));
        DomainException shortage = await Should.ThrowAsync<DomainException>(() => fixture.Database.WriteAsync(transaction =>
            fixture.Ledger.AllocateFefo(transaction, fixture.ProductId, new Quantity(3), fixture.Date,
                new Dictionary<long, Quantity>()), TestContext.Current.CancellationToken));
        shortage.Code.ShouldBe(ErrorCode.InsufficientStock);
    }

    private sealed class StockInFixture : IDisposable
    {
        public StockInFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-stock-in-{Guid.NewGuid():N}");
            Paths = new AppPaths(new SettingsBootstrap(Root));
            Factory = new SqliteConnectionFactory(Paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, Paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            var numbers = new DocNumberService(Settings);
            Periods = new PeriodGuard(Settings, new FixedClock(new DateOnly(2026, 9, 26)));
            PeriodGuard periods = Periods;
            Ledger = new LotQuantities(Settings);
            Inventory = new InventoryService(Database, Ledger, numbers, periods, new AuditLog());
            Purchasing = new PurchasingService(Database, Ledger, numbers, periods, Settings, new AuditLog());
            Date = new DateOnly(2026, 9, 26);

            using var connection = Factory.Open();
            long category = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Food') RETURNING id");
            long unit = connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
            long tax = connection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=5000");
            TaxRateId = tax;
            ZeroTaxRateId = connection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=0");
            ProductId = connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,tax_rate_id,track_expiry,default_selling_price,default_mrp)
                VALUES ('PH3-001','Phase Three Product',@category,@unit,@tax,1,300000,350000) RETURNING id
                """, new { category, unit, tax });
            VendorId = Purchasing.CreateVendorAsync(new("Supplier", null, "9999999999", null, null, null, "Ahmedabad", "Gujarat",
                "380001", "24ABCDE1234F1Z5", null, null, null, 30, new Money(100), null)).GetAwaiter().GetResult();
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public LotQuantities Ledger { get; }
        public InventoryService Inventory { get; }
        public PurchasingService Purchasing { get; }
        public DateOnly Date { get; }
        public PeriodGuard Periods { get; }
        public long ProductId { get; }
        public long VendorId { get; }

        /// <summary>Gets the GST 5% rate, the product's.</summary>
        public long TaxRateId { get; }

        /// <summary>Gets the GST 0% rate.</summary>
        public long ZeroTaxRateId { get; }

        // A reviewed physical count of one lot; a deficit becomes a categorized damage document.
        public Task<long> CountAsync(long lotId, Quantity counted, string reason, CancellationToken ct) =>
            Database.WriteAsync(tx =>
            {
                (long onHand, long version) = tx.Connection.QuerySingle<(long, long)>(
                    "SELECT qty_on_hand,quantity_version FROM product_lot WHERE id=@lotId", new { lotId }, tx.Transaction);
                return new DirectStockEngine(new DocNumberService(Settings)).ReviewCount(tx, lotId, version, onHand,
                    counted.Scaled, Date, reason, OperationGuard.NewId());
            }, ct);

        public async Task DeleteZeroLotAsync(long lotId, CancellationToken ct)
        {
            long version;
            using (var connection = Factory.Open())
                version = connection.ExecuteScalar<long>(
                    "SELECT detail_version FROM product_lot WHERE id=@lotId", new { lotId });
            await Inventory.DeleteZeroLotAsync(lotId, version, ct);
        }

        public PurchaseDraftInput Purchase(string invoice = "INV-1") => new(
            VendorId, invoice, Date, Date, new Money(2725), new Money(100), null,
            [new(ProductId, "BATCH", Date.AddMonths(-1), Date.AddMonths(6), new Quantity(10), Quantity.One,
                new Money(250), Money.Zero, TaxRateId, new Money(350), new Money(300))]);

        public void Dispose()
        {
            Settings.Dispose();
            Database.Dispose();
            Factory.ClearPool();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FixedClock(DateOnly date) : IClock
    {
        public DateTime Now => date.ToDateTime(new TimeOnly(12, 0));
        public DateOnly BusinessDate => date;
    }
}
