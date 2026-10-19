using Dapper;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Sales;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class SalesTests
{
    [Fact]
    public async Task HistoryCursor_ReachesSameAndOlderDatesWithoutDuplicates()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO sale(sale_date,status) VALUES
                    ('2026-09-26','DRAFT'),('2026-09-27','DRAFT'),
                    ('2026-09-27','DRAFT'),('2026-09-27','DRAFT')
                """);

        var seen = new List<long>();
        DateOnly? beforeDate = null;
        long? beforeId = null;
        while (true)
        {
            IReadOnlyList<SaleHistoryItem> page = await fixture.Sales.GetHistoryAsync(
                new SaleHistoryFilter(From: new DateOnly(2026, 9, 26), To: new DateOnly(2026, 9, 27),
                    BeforeDate: beforeDate, BeforeId: beforeId, Limit: 2), ct);
            seen.AddRange(page.Select(static item => item.Id));
            if (page.Count < 2) break;
            beforeDate = page[^1].SaleDate;
            beforeId = page[^1].Id;
        }
        seen.ShouldBe([4L, 3L, 2L, 1L]);

        IReadOnlyList<SaleHistoryItem> sameDate = await fixture.Sales.GetHistoryAsync(
            new SaleHistoryFilter(From: new DateOnly(2026, 9, 27), To: new DateOnly(2026, 9, 27),
                BeforeDate: new DateOnly(2026, 9, 27), BeforeId: 3, Limit: 2), ct);
        sameDate.Select(static item => item.Id).ShouldBe([2L]);
        IReadOnlyList<SaleHistoryItem> ended = await fixture.Sales.GetHistoryAsync(
            new SaleHistoryFilter(From: new DateOnly(2026, 9, 27), To: new DateOnly(2026, 9, 27),
                BeforeDate: new DateOnly(2026, 9, 26), BeforeId: 1, Limit: 2), ct);
        ended.ShouldBeEmpty();
    }

    [Fact]
    public async Task HistoryInvoicePrefix_UsesIndexAndKeepsCaseInsensitiveFilteredResults()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using (var connection = fixture.Factory.Open())
        {
            connection.Execute("""
                INSERT INTO sale(id,invoice_no,sale_date,status,taxable_total,grand_total) VALUES
                    (101,'AbC-001','2026-09-27','COMPLETED',100000,100000),
                    (102,'ABC-002','2026-09-26','COMPLETED',200000,200000),
                    (103,'Else-003','2026-09-27','COMPLETED',300000,300000);
                INSERT INTO sale(id,invoice_no,sale_date,status,void_reason)
                    VALUES (104,'abc-004','2026-09-27','VOID','Fixture void');
                INSERT INTO sale_payment(sale_id,mode,amount) VALUES
                    (101,'CASH',50000),(101,'UPI',50000),(102,'CARD',200000);
                """);
            string[] plan = connection.Query("""
                EXPLAIN QUERY PLAN
                SELECT 1 FROM sale WHERE invoice_no LIKE @invoicePattern LIMIT 1
                """, new { invoicePattern = "abc%" })
                .Select(static row => Convert.ToString(((IDictionary<string, object>)row)["detail"],
                    System.Globalization.CultureInfo.InvariantCulture) ?? "")
                .ToArray();
            plan.ShouldContain(detail => detail.Contains("ix_sale_invoice_nocase", StringComparison.Ordinal));
        }

        (await fixture.Sales.GetHistoryAsync(new SaleHistoryFilter(InvoiceNumber: "abc"), ct))
            .Select(static item => item.Id).ShouldBe([104L, 101L, 102L]);
        (await fixture.Sales.GetHistoryAsync(new SaleHistoryFilter(
            InvoiceNumber: "abc", Status: SaleStatus.Completed), ct))
            .Select(static item => item.Id).ShouldBe([101L, 102L]);
        (await fixture.Sales.GetHistoryAsync(new SaleHistoryFilter(
            InvoiceNumber: "abc", Status: SaleStatus.Void), ct))
            .Select(static item => item.Id).ShouldBe([104L]);
        IReadOnlyList<SaleHistoryItem> filtered = await fixture.Sales.GetHistoryAsync(new SaleHistoryFilter(
            InvoiceNumber: "abc", PaymentMode: PaymentMode.Cash,
            From: new DateOnly(2026, 9, 27), To: new DateOnly(2026, 9, 27)), ct);
        filtered.Select(static item => item.Id).ShouldBe([101L]);
        filtered[0].PaymentModes.ShouldContain("CASH");
        filtered[0].PaymentModes.ShouldContain("UPI");
        (await fixture.Sales.GetHistoryAsync(new SaleHistoryFilter(InvoiceNumber: "zzzz-no-match"), ct))
            .ShouldBeEmpty();

        IReadOnlyList<SaleHistoryItem> first = await fixture.Sales.GetHistoryAsync(
            new SaleHistoryFilter(InvoiceNumber: "abc", Limit: 1), ct);
        first.Select(static item => item.Id).ShouldBe([104L]);
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO sale(id,invoice_no,sale_date,status,taxable_total,grand_total)
                VALUES (105,'AbC-005','2026-09-28','COMPLETED',100000,100000)
                """);
        IReadOnlyList<SaleHistoryItem> next = await fixture.Sales.GetHistoryAsync(
            new SaleHistoryFilter(InvoiceNumber: "abc", BeforeDate: first[0].SaleDate,
                BeforeId: first[0].Id, Limit: 2), ct);
        next.Select(static item => item.Id).ShouldBe([101L, 102L]);
    }

    [Fact]
    public async Task HistoryPages_StayBoundedAndDoNotRepeatRowsAfterAnInsert()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                WITH RECURSIVE n(value) AS (
                    VALUES(1) UNION ALL SELECT value+1 FROM n WHERE value<501
                )
                INSERT INTO sale(sale_date,status) SELECT '2026-09-27','DRAFT' FROM n
                """);
        IReadOnlyList<SaleHistoryItem> first = await fixture.Sales.GetHistoryAsync(
            new SaleHistoryFilter(Limit: 1000), ct);
        first.Count.ShouldBe(500);
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                DELETE FROM sale WHERE id=@id;
                INSERT INTO sale(sale_date,status) VALUES ('2026-09-28','DRAFT');
                """, new { id = first[0].Id });
        IReadOnlyList<SaleHistoryItem> second = await fixture.Sales.GetHistoryAsync(
            new SaleHistoryFilter(BeforeDate: first[^1].SaleDate, BeforeId: first[^1].Id, Limit: 500), ct);
        second.Count.ShouldBe(1);
        first.Select(static item => item.Id).Concat(second.Select(static item => item.Id))
            .Distinct().Count().ShouldBe(501);
        second[0].Id.ShouldBeLessThan(first[^1].Id);
    }

    [Fact]
    public async Task Complete_IsIdempotent_AndPostsStockAtomically()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(2), cancellationToken: ct);
        string operation = OperationGuard.NewId();

        CompletedSale first = await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(236))], operation), ct);
        CompletedSale replay = await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(236))], operation), ct);

        first.InvoiceNumber.ShouldStartWith("S2627-");
        first.Totals.Taxable.ShouldBe(new Money(200));
        first.Totals.Tax.ShouldBe(new Money(36));
        replay.WasReplay.ShouldBeTrue();
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId })
            .ShouldBe(8000L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale_payment WHERE sale_id=@saleId", new { saleId })
            .ShouldBe(1L);
    }

    [Fact]
    public async Task FailedReceiptAfterCommittedSale_RemainsReviewableWithoutRepostingSale()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One,
            cancellationToken: ct);
        string operation = OperationGuard.NewId();
        CompletedSale completed = await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(118))], operation), ct);
        await fixture.Settings.SaveAsync(new Dictionary<string, string>
        {
            ["printer.receipt.name"] = $"missing-printer-{Guid.NewGuid():N}",
            ["printer.receipt.mode"] = "TEXT",
        }, ct);
        var journal = new PrintJobJournal(fixture.Factory);
        var printer = new ReceiptPrinter(fixture.Settings, journal);
        ReceiptData receipt = await fixture.Sales.GetReceiptAsync(saleId, cancellationToken: ct);
        await Should.ThrowAsync<Exception>(() => printer.PrintAsync(receipt));
        journal.ReviewCount().ShouldBe(1);
        CompletedSale replay = await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(118))], operation), ct);
        replay.WasReplay.ShouldBeTrue();
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale_payment WHERE sale_id=@saleId", new { saleId })
            .ShouldBe(1L);
        connection.ExecuteScalar<string>("SELECT status FROM sale WHERE id=@saleId", new { saleId })
            .ShouldBe("COMPLETED");
        completed.InvoiceNumber.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Return_ProrationReconciles_AndNonResellableStockBecomesDamage()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using (var db = fixture.Factory.Open())
            db.Execute("UPDATE product_lot SET batch_no='B-OLD',expiry_date='2026-12-31' WHERE id=@id",
                new { id = fixture.LotId });
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(2), cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(236))],
            OperationGuard.NewId()), ct);
        SaleDetails sale = await fixture.Sales.GetAsync(saleId, ct);
        sale.Lines[0].Sku.ShouldNotBeNullOrWhiteSpace();
        sale.Lines[0].Unit.ShouldNotBeNullOrWhiteSpace();
        sale.Lines[0].ProductClass.ShouldBe("SALEABLE");
        sale.Lines[0].BatchNumber.ShouldBe("B-OLD");
        sale.Lines[0].SnapshotProvenance.ShouldBe("AT_COMPLETION");
        using (var db = fixture.Factory.Open())
            db.Execute("UPDATE product_lot SET batch_no='B-NEW',expiry_date='2027-01-31' WHERE id=@id",
                new { id = fixture.LotId });
        (await fixture.Sales.GetAsync(saleId, ct)).Lines[0].BatchNumber.ShouldBe("B-OLD");

        CompletedReturn returned = await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate,
            "Opened package", RefundMode.Cash, [new(sale.Lines[0].Id, Quantity.One, false)],
            OperationGuard.NewId()), ct);

        returned.Refund.ShouldBe(new Money(118));
        returned.Taxable.ShouldBe(new Money(100));
        returned.Tax.ShouldBe(new Money(18));
        ReceiptData receipt = await fixture.Sales.GetReturnReceiptAsync(returned.ReturnId, ct);
        receipt.TaxMode.ShouldBe("RETURN");
        receipt.OriginalInvoiceNumber.ShouldBe(sale.InvoiceNumber);
        receipt.GrandTotal.ShouldBe(new Money(118));
        receipt.Lines.Single().Quantity.ShouldBe(Quantity.One);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId })
            .ShouldBe(8000L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM damage WHERE reason='CUSTOMER_RETURN'").ShouldBe(1L);
        connection.ExecuteScalar<string>("SELECT batch_no_snapshot FROM sale_return_line WHERE sale_return_id=@id",
            new { id = returned.ReturnId }).ShouldBe("B-OLD");
        connection.ExecuteScalar<string>("SELECT batch_no_snapshot FROM damage WHERE reason='CUSTOMER_RETURN'")
            .ShouldBe("B-OLD");
        DayEndSummary summary = await fixture.Sales.GetDayEndAsync(fixture.Clock.BusinessDate, ct);
        summary.CashExpected.ShouldBe(new Money(118));
    }

    [Fact]
    public async Task CompletedSale_SavesFieldsNeededForReviewedReturnRecreation()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using (var db = fixture.Factory.Open())
        {
            db.Execute("UPDATE product_lot SET expiry_date='2026-12-31' WHERE id=@id",
                new { id = fixture.LotId });
            db.Execute("UPDATE product SET hsn_code='1001' WHERE id=@id", new { id = fixture.ProductId });
        }
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One,
            cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(118))],
            OperationGuard.NewId()), ct);
        long saleLineId;
        using (var db = fixture.Factory.Open())
            saleLineId = db.ExecuteScalar<long>("SELECT id FROM sale_line WHERE sale_id=@saleId",
                new { saleId });
        ReturnRecreationDraft draft = await fixture.Sales.GetReturnRecreationDraftAsync(saleLineId, ct);
        draft.CategoryId.ShouldBe(fixture.CategoryId);
        draft.UnitId.ShouldBe(fixture.UnitId);
        draft.TaxRateId.ShouldBe(fixture.TaxId);
        draft.ExpiryDate.ShouldBe(new DateOnly(2026, 12, 31));
        draft.MissingFields.ShouldBeEmpty();
        draft.SuggestedSku.ShouldNotBe("SALE-001");
    }

    [Fact]
    public async Task Void_AfterReturn_IsRejected()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(118))],
            OperationGuard.NewId()), ct);
        SaleDetails sale = await fixture.Sales.GetAsync(saleId, ct);
        await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate, "Return", RefundMode.Cash,
            [new(sale.Lines[0].Id, Quantity.One, true)], OperationGuard.NewId()), ct);

        DomainException exception = await Should.ThrowAsync<DomainException>(() => fixture.Sales.VoidAsync(
            saleId, "Mistake", OperationGuard.NewId(), ct));
        exception.Code.ShouldBe(ErrorCode.VoidNotAllowed);
    }

    [Fact]
    public async Task QuickAddedProduct_WithOpeningStock_CanBeScannedIntoBill()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long productId = await fixture.Catalog.CreateAsync(new ProductInput(
            "QUICK-001", "Quick added soap", null, null, fixture.CategoryId, null, fixture.UnitId,
            "1001", fixture.TaxId, true, false, null, false, null, null,
            Quantity.Zero, new Money(118), new Money(120),
            [new("4006381333931", Quantity.One, "EAN13", true)]), ct);
        await fixture.Inventory.ImportOpeningStockAsync(
            [new OpeningStockLine(productId, new Quantity(2), new Money(70), new Money(118),
                new Money(120), null, null, fixture.Clock.BusinessDate)], OperationGuard.NewId(), ct);

        ScanResult scan = await new ScanResolver(fixture.Database).ResolveAsync("4006381333931", ct);
        scan.ProductId.ShouldBe(productId);
        long saleId = await fixture.Sales.AddProductAsync(null, productId, Quantity.One,
            scannedBarcode: "4006381333931", cancellationToken: ct);
        SaleDetails bill = await fixture.Sales.GetAsync(saleId, ct);
        bill.Lines.Single().ProductId.ShouldBe(productId);
        bill.Lines.Single().Quantity.ShouldBe(Quantity.One);
    }

    [Fact]
    public async Task DayEnd_SubtractsCashChange_AndReturnPickerShowsRemainingQuantity()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(2), cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(250))],
            OperationGuard.NewId()), ct);
        DayEndSummary summary = await fixture.Sales.GetDayEndAsync(fixture.Clock.BusinessDate, ct);
        summary.CashExpected.ShouldBe(new Money(236));

        SaleDetails sale = await fixture.Sales.GetAsync(saleId, ct);
        await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate, "One returned",
            RefundMode.Cash, [new(sale.Lines[0].Id, Quantity.One, true)], OperationGuard.NewId()), ct);
        IReadOnlyList<ReturnableSaleLine> returnable = await fixture.Sales.GetReturnableLinesAsync(saleId, ct);
        returnable.Single().Remaining.ShouldBe(Quantity.One);
        summary = await fixture.Sales.GetDayEndAsync(fixture.Clock.BusinessDate, ct);
        summary.CashExpected.ShouldBe(new Money(118));
    }

    [Fact]
    public async Task ChangeLineLot_UsesSelectedPrice_AndRejectsInsufficientStock()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(2), cancellationToken: ct);
        SaleDetails before = await fixture.Sales.GetAsync(saleId, ct);
        await fixture.Sales.ChangeLineLotAsync(saleId, before.Lines[0].Id, fixture.AlternateLotId, ct);
        SaleDetails changed = await fixture.Sales.GetAsync(saleId, ct);
        changed.Lines.Single().LotId.ShouldBe(fixture.AlternateLotId);
        changed.Lines.Single().UnitPrice.ShouldBe(new Money(125));

        await Should.ThrowAsync<DomainException>(() => fixture.Sales.SetLineQuantityAsync(
            saleId, changed.Lines[0].Id, new Quantity(6), ct));
        (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().Quantity.ShouldBe(new Quantity(2));
    }

    [Fact]
    public async Task LineDiscount_CanBeClearedWithoutChangingBillDiscount()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        long lineId = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().Id;
        await fixture.Sales.SetLineDiscountAsync(saleId, lineId, new Money(10), ct);
        await fixture.Sales.SetBillDiscountAsync(saleId, new Money(5), ct);
        (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().LineDiscount.ShouldBe(new Money(10));
        await fixture.Sales.SetLineDiscountAsync(saleId, lineId, Money.Zero, ct);
        SaleDetails final = await fixture.Sales.GetAsync(saleId, ct);
        final.Lines.Single().LineDiscount.ShouldBe(Money.Zero);
        final.BillDiscount.ShouldBe(new Money(5));
    }

    [Fact]
    public async Task SavedDraftAndHeldBill_CanBeSelectedAndResumed()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        (await fixture.Sales.GetDraftsAsync(ct)).Single().Id.ShouldBe(saleId);
        await fixture.Sales.HoldAsync(saleId, "Pickup", ct);
        (await fixture.Sales.GetDraftsAsync(ct)).ShouldBeEmpty();
        (await fixture.Sales.GetHeldAsync(ct)).Single().Label.ShouldBe("Pickup");
        await fixture.Sales.ResumeAsync(saleId, ct);
        (await fixture.Sales.GetAsync(saleId, ct)).Status.ShouldBe(SaleStatus.Draft);
    }

    [Fact]
    public async Task PriceOverride_IsAudited_AndCannotExceedMrp()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        long lineId = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().Id;
        await fixture.Sales.SetLinePriceAsync(saleId, lineId, new Money(115), ct);
        (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().UnitPrice.ShouldBe(new Money(115));
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM audit_log WHERE entity='sale_line' AND entity_id=@lineId AND action='PRICE_OVERRIDE'
            """, new { lineId }).ShouldBe(1L);
        DomainException error = await Should.ThrowAsync<DomainException>(() =>
            fixture.Sales.SetLinePriceAsync(saleId, lineId, new Money(121), ct));
        error.Code.ShouldBe(ErrorCode.PriceAboveMrp);
        (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().UnitPrice.ShouldBe(new Money(115));
    }

    [Fact]
    public async Task Customer_CanBeCreatedAttachedAndPrintedOnReceipt()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long customerId = await fixture.Sales.CreateCustomerAsync("Mrs. Shah", "9999999999", null, ct);
        (await fixture.Sales.SearchCustomersAsync("9999", ct)).Single().Id.ShouldBe(customerId);
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        await fixture.Sales.SetCustomerAsync(saleId, customerId, ct);
        (await fixture.Sales.GetAsync(saleId, ct)).CustomerName.ShouldBe("Mrs. Shah");
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(118))],
            OperationGuard.NewId()), ct);
        (await fixture.Sales.GetReceiptAsync(saleId, cancellationToken: ct)).CustomerName.ShouldBe("Mrs. Shah");
    }

    [Fact]
    public async Task ProductQuantityEdit_ReallocatesAcrossFefoLots_AndRollsBackOnShortage()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(2), cancellationToken: ct);
        SaleLine first = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single();
        await fixture.Sales.SetLineDiscountAsync(saleId, first.Id, new Money(10), ct);

        await fixture.Sales.SetProductQuantityAsync(saleId, fixture.ProductId, new Quantity(12), ct);
        SaleDetails split = await fixture.Sales.GetAsync(saleId, ct);
        split.Lines.Select(static line => line.LotId).ShouldBe([fixture.LotId, fixture.AlternateLotId]);
        split.Lines.Select(static line => line.Quantity).ShouldBe([new Quantity(10), new Quantity(2)]);
        split.Lines.Sum(static line => line.LineDiscount.Value).ShouldBe(60.593m);

        await Should.ThrowAsync<DomainException>(() => fixture.Sales.SetProductQuantityAsync(
            saleId, fixture.ProductId, new Quantity(16), ct));
        (await fixture.Sales.GetAsync(saleId, ct)).Lines.Select(static line => line.Quantity)
            .ShouldBe([new Quantity(10), new Quantity(2)]);

        await fixture.Sales.SetProductQuantityAsync(saleId, fixture.ProductId, Quantity.One, ct);
        (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().LotId.ShouldBe(fixture.LotId);
    }

    [Fact]
    public async Task HeldBill_ReviewDetectsChangedPrice_AndRejectsStaleAcceptance()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        await fixture.Sales.HoldAsync(saleId, "Waiting", ct);
        await fixture.Inventory.ChangeLotPriceAsync(fixture.LotId, new Money(115), new Money(120), ct);
        HeldBillReview review = await fixture.Sales.GetHeldBillReviewAsync(saleId, ct);
        review.Lines.Single().PriceChanged.ShouldBeTrue();
        await fixture.Inventory.ChangeLotPriceAsync(fixture.LotId, new Money(110), new Money(120), ct);
        await Should.ThrowAsync<DomainException>(() => fixture.Sales.ResumeReviewedAsync(review, ct));
        (await fixture.Sales.GetAsync(saleId, ct)).Status.ShouldBe(SaleStatus.Held);

        await fixture.Sales.ResumeReviewedAsync(await fixture.Sales.GetHeldBillReviewAsync(saleId, ct), ct);
        (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().UnitPrice.ShouldBe(new Money(110));
    }

    [Fact]
    public async Task CorrectedLotPrice_AppliesToFutureBillButPreservesCompletedBill()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long completedId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(completedId, [new(PaymentMode.Cash, new Money(118))],
            OperationGuard.NewId()), ct);
        Money billedPrice = (await fixture.Sales.GetAsync(completedId, ct)).Lines.Single().UnitPrice;
        LotItem original = (await fixture.Inventory.GetLotsByIdsAsync([fixture.LotId], ct)).Single();
        await fixture.Inventory.CorrectLotAsync(new(original, original.BatchNumber, original.ExpiryDate,
            original.UnitCost, new Money(110), original.Mrp, Quantity.Zero,
            fixture.Clock.BusinessDate, "Price correction"), OperationGuard.NewId(), ct);
        long futureId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        (await fixture.Sales.GetAsync(completedId, ct)).Lines.Single().UnitPrice.ShouldBe(billedPrice);
        (await fixture.Sales.GetAsync(futureId, ct)).Lines.Single().UnitPrice.ShouldBe(new Money(110));
    }

    [Fact]
    public async Task ReceiptSnapshots_KeepIssueTimeStoreAndPrintSettings()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Settings.SaveAsync(new Dictionary<string, string>
        {
            ["store.name"] = "Original Store",
            ["receipt.footer"] = "Original footer",
            ["printer.receipt.width_dots"] = "384",
            ["print.bilingual_names"] = "0",
        }, ct);
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(118))],
            OperationGuard.NewId()), ct);
        SaleLine line = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single();
        CompletedReturn returned = await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate,
            "Damaged", RefundMode.Cash, [new(line.Id, Quantity.One, false)], OperationGuard.NewId()), ct);
        await fixture.Settings.SaveAsync(new Dictionary<string, string>
        {
            ["store.name"] = "New Store",
            ["receipt.footer"] = "New footer",
            ["printer.receipt.width_dots"] = "576",
            ["print.bilingual_names"] = "1",
        }, ct);

        ReceiptData saleReceipt = await fixture.Sales.GetReceiptAsync(saleId, duplicate: true, cancellationToken: ct);
        ReceiptData returnReceipt = await fixture.Sales.GetReturnReceiptAsync(returned.ReturnId, ct);
        saleReceipt.StoreName.ShouldBe("Original Store");
        saleReceipt.Footer.ShouldBe("Original footer");
        saleReceipt.WidthDots.ShouldBe(384);
        saleReceipt.BilingualNames.ShouldBe(false);
        saleReceipt.Duplicate.ShouldBeTrue();
        returnReceipt.StoreName.ShouldBe("Original Store");
        returnReceipt.Footer.ShouldBe("Original footer");
    }

    [Fact]
    public async Task MultiLineReturn_RecordsOneDocument_AndRejectsDuplicateLineRequest()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(12), cancellationToken: ct);
        SaleDetails draft = await fixture.Sales.GetAsync(saleId, ct);
        await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, draft.Totals.GrandTotal)], OperationGuard.NewId()), ct);
        ReturnLineInput[] lines = draft.Lines.Select((line, index) =>
            new ReturnLineInput(line.Id, Quantity.One, index == 0)).ToArray();
        CompletedReturn result = await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate,
            "Two packs", RefundMode.Cash, lines, OperationGuard.NewId()), ct);
        (await fixture.Sales.GetReturnReceiptAsync(result.ReturnId, ct)).Lines.Count.ShouldBe(2);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale_return_line WHERE sale_return_id=@id",
            new { id = result.ReturnId }).ShouldBe(2L);
        await Should.ThrowAsync<DomainException>(() => fixture.Sales.ReturnAsync(new(saleId,
            fixture.Clock.BusinessDate, "Duplicate", RefundMode.Cash,
            [lines[0], lines[0]], OperationGuard.NewId()), ct));
    }

    [Fact]
    public async Task StockLedger_OpensSaleSourceWithAllDocumentLines()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(12), cancellationToken: ct);
        SaleDetails draft = await fixture.Sales.GetAsync(saleId, ct);
        CompletedSale completed = await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, draft.Totals.GrandTotal)], OperationGuard.NewId()), ct);
        LedgerItem movement = (await fixture.Inventory.GetLedgerAsync(fixture.ProductId,
            fixture.Clock.BusinessDate, fixture.Clock.BusinessDate, ct))
            .First(item => item.Type == "SALE");
        StockSourceDocument source = await fixture.Inventory.GetSourceDocumentAsync(movement, ct);
        source.Type.ShouldBe("Sale");
        source.Number.ShouldBe(completed.InvoiceNumber);
        source.Lines.Count.ShouldBe(2);
    }

    private sealed class SalesFixture : IDisposable
    {
        private readonly string _root;

        public SalesFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"grocery-erp-sales-{Guid.NewGuid():N}");
            var paths = new AppPaths(new SettingsBootstrap(_root));
            Factory = new SqliteConnectionFactory(paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            SettingsService settings = Settings;
            Clock = new FixedClock(new DateTime(2026, 9, 27, 10, 30, 0));
            var ledger = new StockLedger(settings);
            Catalog = new CatalogService(Database, new AuditLog(), settings);
            Inventory = new InventoryService(Database, ledger, new DocNumberService(settings), new PeriodGuard(settings), new AuditLog());
            Sales = new SalesService(Database, ledger, new DocNumberService(settings),
                new PeriodGuard(settings), settings, new AuditLog(), Clock);

            using var connection = Factory.Open();
            CategoryId = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Food') RETURNING id");
            UnitId = connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
            TaxId = connection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=18000");
            ProductId = connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,tax_rate_id,price_includes_tax,
                    default_selling_price,default_mrp)
                VALUES ('SALE-001','Sales Product',@category,@unit,@tax,1,118000,120000) RETURNING id
                """, new { category = CategoryId, unit = UnitId, tax = TaxId });
            LotId = connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(lot_code,product_id,received_date,unit_cost,selling_price,mrp)
                VALUES ('L-SALE',@productId,'2026-09-01',70000,118000,120000) RETURNING id
                """, new { productId = ProductId });
            AlternateLotId = connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(lot_code,product_id,received_date,unit_cost,selling_price,mrp)
                VALUES ('L-SALE-ALT',@productId,'2026-09-02',80000,125000,130000) RETURNING id
                """, new { productId = ProductId });
            using var transaction = connection.BeginTransaction();
            ledger.Post(new(connection, transaction), new(Clock.BusinessDate, ProductId, LotId, "OPENING",
                new Quantity(10), new Money(70), "test", 1));
            ledger.Post(new(connection, transaction), new(Clock.BusinessDate, ProductId, AlternateLotId, "OPENING",
                new Quantity(5), new Money(80), "test", 2));
            transaction.Commit();
        }

        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public CatalogService Catalog { get; }
        public InventoryService Inventory { get; }
        public SalesService Sales { get; }
        public SettingsService Settings { get; }
        public FixedClock Clock { get; }
        public long CategoryId { get; }
        public long UnitId { get; }
        public long TaxId { get; }
        public long ProductId { get; }
        public long LotId { get; }
        public long AlternateLotId { get; }

        public void Dispose()
        {
            Settings.Dispose();
            SqliteConnectionFactory.ClearPoolForDataRoot(_root);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime Now { get; } = now;
        public DateOnly BusinessDate { get; } = DateOnly.FromDateTime(now);
    }
}
