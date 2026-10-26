using Dapper;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Reports;
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
    public async Task PartialAndFullRefundStates_FollowLinkedReturnsWithoutChangingSaleValues()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId,
            new Quantity(2), cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(236))], OperationGuard.NewId()), ct);
        SaleDetails completed = await fixture.Sales.GetAsync(saleId, ct);
        long lineId = completed.Lines.Single().Id;
        await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate, "First",
            RefundMode.Cash, [new ReturnLineInput(lineId, Quantity.One, true)],
            OperationGuard.NewId()), ct);

        (await fixture.Sales.GetAsync(saleId, ct)).Status.ShouldBe(SaleStatus.PartiallyRefunded);
        ReturnableSaleLine remaining = (await fixture.Sales.GetReturnableLinesAsync(saleId, ct)).Single();
        remaining.AlreadyReturned.ShouldBe(Quantity.One);
        remaining.RemainingRefund.ShouldBe(new Money(118));
        remaining.OriginalLotAvailable.ShouldBeTrue();
        (await fixture.Sales.GetHistoryAsync(new SaleHistoryFilter(
            Status: SaleStatus.PartiallyRefunded), ct)).Single().Id.ShouldBe(saleId);
        (await fixture.Sales.GetHistoryAsync(new SaleHistoryFilter(
            Product: "Sales Pro", Customer: "nobody"), ct)).ShouldBeEmpty();

        await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate, "Second",
            RefundMode.Cash, [new ReturnLineInput(lineId, Quantity.One, true)],
            OperationGuard.NewId()), ct);
        SaleDetails refunded = await fixture.Sales.GetAsync(saleId, ct);
        refunded.Status.ShouldBe(SaleStatus.Refunded);
        refunded.Totals.GrandTotal.ShouldBe(completed.Totals.GrandTotal);
        (await fixture.Sales.GetReturnableLinesAsync(saleId, ct)).ShouldBeEmpty();
        (await fixture.Sales.GetHistoryAsync(new SaleHistoryFilter(
            Status: SaleStatus.Refunded, Product: "Sales Pro"), ct)).Single().Id.ShouldBe(saleId);
        (await fixture.Sales.GetReceiptAsync(saleId, duplicate: true, cancellationToken: ct))
            .GrandTotal.ShouldBe(completed.Totals.GrandTotal);
        SaleHistoryActivity activity = await fixture.Sales.GetHistoryActivityAsync(saleId, ct);
        activity.Returns.Count.ShouldBe(2);
        activity.Events.Select(static item => item.Type)
            .ShouldBe(["COMPLETED", "PARTIAL_RETURN", "REFUNDED"]);
    }

    [Fact]
    public async Task ReturnAfterLotEdit_CreatesLinkedReturnLotFromSaleSnapshot()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId,
            Quantity.One, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);
        SaleLine line = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single();
        using (var db = fixture.Factory.Open())
            db.Execute("UPDATE product_lot SET selling_price=110000 WHERE id=@lotId",
                new { lotId = fixture.LotId });
        (await fixture.Sales.GetReturnableLinesAsync(saleId, ct)).Single()
            .OriginalLotAvailable.ShouldBeFalse();

        string operationId = OperationGuard.NewId();
        CompletedReturn posted = await fixture.Sales.ReturnAsync(new(saleId,
            fixture.Clock.BusinessDate, "Changed lot", RefundMode.Cash,
            [new ReturnLineInput(line.Id, Quantity.One, true)], operationId), ct);
        (await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate,
            "Changed lot", RefundMode.Cash,
            [new ReturnLineInput(line.Id, Quantity.One, true)], operationId), ct)).WasReplay.ShouldBeTrue();
        using var connection = fixture.Factory.Open();
        long returnLotId = connection.ExecuteScalar<long>("""
            SELECT restock_lot_id FROM sale_return_line WHERE sale_return_id=@id
            """, new { id = posted.ReturnId });
        returnLotId.ShouldNotBe(fixture.LotId);
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
            new { id = fixture.LotId }).ShouldBe(9000L);
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
            new { id = returnLotId }).ShouldBe(1000L);
        connection.ExecuteScalar<string>("SELECT origin_kind FROM product_lot WHERE id=@id",
            new { id = returnLotId }).ShouldBe("CUSTOMER_RETURN");
        connection.ExecuteScalar<long>("SELECT origin_ref_id FROM product_lot WHERE id=@id",
            new { id = returnLotId }).ShouldBe(connection.ExecuteScalar<long>(
                "SELECT id FROM sale_return_line WHERE sale_return_id=@id", new { id = posted.ReturnId }));
        connection.ExecuteScalar<long>("SELECT selling_price FROM product_lot WHERE id=@id",
            new { id = returnLotId }).ShouldBe(line.UnitPrice.Scaled);
        (await fixture.Sales.GetHistoryActivityAsync(saleId, ct)).Returns.Single()
            .DispositionCode.ShouldBe("RETURN_NEW_LOT");
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() => connection.Execute("""
            UPDATE sale_return_line SET qty=2000 WHERE sale_return_id=@id
            """, new { id = posted.ReturnId }));
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() => connection.Execute("""
            UPDATE sale_return SET refund_total=0 WHERE id=@id
            """, new { id = posted.ReturnId }));
    }

    [Fact]
    public async Task DamagedReturnAndVoid_NeverRestockDamage_AndWriteNoMovements()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId,
            Quantity.One, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);
        long lineId = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().Id;
        using (var edit = fixture.Factory.Open())
            edit.Execute("UPDATE product_lot SET selling_price=110000 WHERE id=@id",
                new { id = fixture.LotId });
        CompletedReturn damaged = await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate,
            "Opened", RefundMode.Cash, [new ReturnLineInput(lineId, Quantity.One, false)],
            OperationGuard.NewId()), ct);
        long secondSale = await fixture.Sales.AddProductAsync(null, fixture.ProductId,
            Quantity.One, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(secondSale,
            [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);
        await fixture.Sales.VoidAsync(secondSale, "Mistake", OperationGuard.NewId(), ct);
        using var after = fixture.Factory.Open();
        after.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
            new { id = fixture.LotId }).ShouldBe(9000L);
        after.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot WHERE origin_kind='CUSTOMER_RETURN'")
            .ShouldBe(0L);
        after.QuerySingle<(long LotId, string Reason, long Cost)>("""
            SELECT d.lot_id AS LotId,d.reason AS Reason,d.cost_value AS Cost
            FROM damage d JOIN sale_return_line rl ON rl.damage_id=d.id
            WHERE rl.sale_return_id=@id AND rl.restock_lot_id IS NULL
            """, new { id = damaged.ReturnId }).ShouldBe((fixture.LotId, "CUSTOMER_RETURN", 70000L));
        (await fixture.Sales.GetHistoryActivityAsync(saleId, ct)).Returns.Single()
            .DispositionCode.ShouldBe("RETURN_DAMAGED");
        await fixture.Database.WriteAsync(tx =>
            LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction).ToArray().ShouldBeEmpty(), ct);
    }

    [Theory]
    [InlineData(118500L, 119000L)]
    [InlineData(118200L, 118000L)]
    public async Task FullReturnRefundsExactRoundedBill(long unitPrice, long expectedRefund)
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using (var db = fixture.Factory.Open())
            db.Execute("UPDATE product_lot SET selling_price=@unitPrice WHERE id=@id",
                new { unitPrice, id = fixture.LotId });
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId,
            Quantity.One, cancellationToken: ct);
        SaleDetails draft = await fixture.Sales.GetAsync(saleId, ct);
        await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, draft.Totals.GrandTotal)], OperationGuard.NewId()), ct);
        SaleDetails sale = await fixture.Sales.GetAsync(saleId, ct);
        sale.Totals.GrandTotal.Scaled.ShouldBe(expectedRefund);
        ReturnableSaleLine returnable = (await fixture.Sales.GetReturnableLinesAsync(saleId, ct)).Single();
        returnable.RemainingRefund.Scaled.ShouldBe(expectedRefund);
        CompletedReturn posted = await fixture.Sales.ReturnAsync(new(saleId,
            fixture.Clock.BusinessDate, "Full refund", RefundMode.Cash,
            [new ReturnLineInput(sale.Lines.Single().Id, Quantity.One, true)],
            OperationGuard.NewId()), ct);
        posted.Refund.Scaled.ShouldBe(expectedRefund);
        (await fixture.Sales.GetAsync(saleId, ct)).Status.ShouldBe(SaleStatus.Refunded);
        // The refund includes the bill's round-off, so the day's net sales return to zero.
        PnlStatement day = await new ReportService(fixture.Database, fixture.Settings, fixture.Clock)
            .GetPnlAsync(fixture.Clock.BusinessDate, fixture.Clock.BusinessDate, ct);
        day.NetSales.Scaled.ShouldBe(0L);
    }

    [Fact]
    public async Task DirectReturn_RecreatesDeletedProductAndLotFromCompletedSale()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        SalesService directSales = fixture.Sales;
        long productId;
        long lotId;
        using (var db = fixture.Factory.Open())
        {
            productId = db.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,tax_rate_id,
                    hsn_code,price_includes_tax,track_expiry,default_selling_price,default_mrp)
                VALUES ('RETURN-DELETE','Deleted item',@category,@unit,@tax,'1006',1,0,118000,120000)
                RETURNING id
                """, new { category = fixture.CategoryId, unit = fixture.UnitId, tax = fixture.TaxId });
            lotId = db.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,
                    selling_price,mrp,qty_received)
                VALUES (@productId,'L-RETURN-DELETE','2026-09-01',70000,118000,120000,1000)
                RETURNING id
                """, new { productId });
        }
        await fixture.ReceiveDirectAsync(productId, lotId, Quantity.One, ct);
        long saleId = await directSales.AddProductAsync(null, productId,
            Quantity.One, cancellationToken: ct);
        await directSales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);
        SaleDetails original = await directSales.GetAsync(saleId, ct);
        long version;
        using (var db = fixture.Factory.Open())
            version = db.ExecuteScalar<long>(
                "SELECT detail_version FROM product_lot WHERE id=@lotId", new { lotId });
        await fixture.Inventory.DeleteZeroLotAsync(lotId, version, ct);
        (await fixture.Catalog.RemoveAsync(productId, ct)).ShouldBe(ProductRemoval.Deleted);

        CompletedReturn result = await directSales.ReturnAsync(new(saleId,
            fixture.Clock.BusinessDate, "Refund", RefundMode.Cash,
            [new ReturnLineInput(original.Lines.Single().Id, Quantity.One, true)],
            OperationGuard.NewId()), ct);
        using var after = fixture.Factory.Open();
        (long NewProductId, long NewLotId) recreated = after.QuerySingle<(long NewProductId, long NewLotId)>("""
            SELECT restock_product_id_snapshot AS NewProductId,restock_lot_id AS NewLotId
            FROM sale_return_line WHERE sale_return_id=@id
            """, new { id = result.ReturnId });
        recreated.NewProductId.ShouldNotBe(productId);
        recreated.NewLotId.ShouldNotBe(lotId);
        (await directSales.GetHistoryActivityAsync(saleId, ct)).Returns.Single()
            .DispositionCode.ShouldBe("RETURN_NEW_LOT");
        after.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
            new { id = recreated.NewLotId }).ShouldBe(1000L);
        (await directSales.GetReceiptAsync(saleId, duplicate: true, cancellationToken: ct))
            .GrandTotal.ShouldBe(original.Totals.GrandTotal);
        var pnl = after.QuerySingle<(long Taxable, long Returned, long Cost, long ReturnCost)>("""
            SELECT taxable AS Taxable,return_taxable AS Returned,
                   cost AS Cost,return_cost AS ReturnCost
            FROM pnl_day WHERE day=@date
            """, new
        {
            date = fixture.Clock.BusinessDate.ToString("yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture)
        });
        pnl.Returned.ShouldBe(pnl.Taxable);
        pnl.ReturnCost.ShouldBe(pnl.Cost);
        after.ExecuteScalar<long>("""
            SELECT COALESCE(SUM(quantity),0) FROM sale_report_day
            WHERE day=@day AND category_id=@category
            """, new
        {
            day = fixture.Clock.BusinessDate.ToString("yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture),
            category = fixture.CategoryId
        })
            .ShouldBe(0L);
        await fixture.Database.WriteAsync(tx =>
            LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction).ToArray().ShouldBeEmpty(), ct);
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
    public async Task LegacyReturn_RequiresReviewedFieldsBeforeRecreatingDeletedProduct()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        SalesService sales = fixture.Sales;
        long productId;
        long lotId;
        using (var db = fixture.Factory.Open())
        {
            productId = db.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,tax_rate_id,hsn_code,
                    price_includes_tax,track_expiry,default_selling_price)
                VALUES ('LEGACY-REVIEW','Legacy review',@category,@unit,@tax,'1006',1,0,118000)
                RETURNING id
                """, new { category = fixture.CategoryId, unit = fixture.UnitId, tax = fixture.TaxId });
            lotId = db.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,selling_price,qty_received)
                VALUES (@productId,'LEGACY-LOT','2026-09-01',70000,118000,1000) RETURNING id
                """, new { productId });
        }
        await fixture.ReceiveDirectAsync(productId, lotId, Quantity.One, ct);
        long saleId = await sales.AddProductAsync(null, productId, Quantity.One, cancellationToken: ct);
        await sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);
        long lineId;
        using (var db = fixture.Factory.Open())
        {
            lineId = db.ExecuteScalar<long>("SELECT id FROM sale_line WHERE sale_id=@saleId", new { saleId });
            db.Execute("DROP TRIGGER trg_sale_line_lock_upd");
            db.Execute("""
                UPDATE sale_line SET product_sku_snapshot=NULL,category_id_snapshot=NULL,
                    uom_id_snapshot=NULL,tax_rate_id_snapshot=NULL,tax_mode_snapshot=NULL,
                    track_expiry_snapshot=NULL,is_loose_snapshot=NULL,product_class_snapshot=NULL
                WHERE id=@lineId
                """, new { lineId });
            db.Execute("DELETE FROM product_lot WHERE id=@lotId", new { lotId });
            db.Execute("DELETE FROM product WHERE id=@productId", new { productId });
        }
        ReturnRecreationDraft draft = await sales.GetReturnRecreationDraftAsync(lineId, ct);
        draft.ProductMissing.ShouldBeTrue();
        draft.MissingFields.ShouldContain("category");
        await Should.ThrowAsync<DomainException>(() => sales.ReturnAsync(new(saleId,
            fixture.Clock.BusinessDate, "Legacy refund", RefundMode.Cash,
            [new(lineId, Quantity.One, true)], OperationGuard.NewId()), ct));
        var reviewed = new ReturnRecreationInput(draft.SuggestedSku, draft.ProductName,
            draft.ProductNameLocal, fixture.CategoryId, fixture.UnitId, fixture.TaxId,
            "REGULAR", "1006", false, false, "SALEABLE", null);
        CompletedReturn posted = await sales.ReturnAsync(new(saleId,
            fixture.Clock.BusinessDate, "Legacy refund", RefundMode.Cash,
            [new(lineId, Quantity.One, true, reviewed)], OperationGuard.NewId()), ct);
        using var after = fixture.Factory.Open();
        after.ExecuteScalar<string>("""
            SELECT snapshot_provenance FROM sale_return_line WHERE sale_return_id=@id
            """, new { id = posted.ReturnId }).ShouldBe("OPERATOR_REVIEW");
        after.ExecuteScalar<long>("SELECT COUNT(*) FROM product WHERE sku=@sku",
            new { sku = draft.SuggestedSku }).ShouldBe(1L);
    }

    [Fact]
    public async Task SaleReturnAndVoid_WriteOnlyTheirDocuments()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long first = await fixture.Sales.AddProductAsync(null, fixture.ProductId,
            Quantity.One, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(first,
            [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);
        SaleDetails completed = await fixture.Sales.GetAsync(first, ct);
        await fixture.Sales.ReturnAsync(new(first, fixture.Clock.BusinessDate,
            "Customer changed mind", RefundMode.Cash,
            [new ReturnLineInput(completed.Lines.Single().Id, Quantity.One, true)],
            OperationGuard.NewId()), ct);
        long second = await fixture.Sales.AddProductAsync(null, fixture.ProductId,
            Quantity.One, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(second,
            [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);
        await fixture.Sales.VoidAsync(second, "Entry mistake", OperationGuard.NewId(), ct);
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
                new { id = fixture.LotId }).ShouldBe(10000L);
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_correction").ShouldBe(0L);
            // A manual correction saved before manual adjustments were removed still counts in P&L.
            db.Execute("""
                INSERT INTO stock_correction(correction_date,product_id,lot_id,quantity_delta,unit_cost,
                    source_kind,reason)
                VALUES (@date,@product,@lot,1000,70000,'MANUAL','Reviewed correction');
                UPDATE product_lot SET qty_on_hand=qty_on_hand+1000 WHERE id=@lot;
                """, new
            {
                date = fixture.Clock.BusinessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                product = fixture.ProductId,
                lot = fixture.LotId,
            });
            db.ExecuteScalar<long>("""
                SELECT amount FROM v_pnl_entry WHERE head_code='STOCK_ADJUSTMENT'
                """).ShouldBe(-70000L);
        }
        await fixture.Database.WriteAsync(tx => LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction)
            .ToArray().ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task ReturnAfterLotExpiry_CanOnlyBeDamaged()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using (var db = fixture.Factory.Open())
            db.Execute("UPDATE product_lot SET expiry_date='2026-09-30' WHERE id=@id", new { id = fixture.LotId });
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One,
            preferredLotId: fixture.LotId, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);
        SalesService later = fixture.ServiceAt(new DateTime(2026, 10, 5, 11, 0, 0));
        ReturnableSaleLine line = (await later.GetReturnableLinesAsync(saleId, ct)).Single();
        line.OriginalLotAvailable.ShouldBeTrue();
        line.ResaleExpired.ShouldBeTrue();
        DateOnly returnDate = new(2026, 10, 5);
        (await Should.ThrowAsync<DomainException>(() => later.ReturnAsync(new(saleId, returnDate,
            "Expired", RefundMode.Cash, [new ReturnLineInput(line.Line.Id, Quantity.One, true)],
            OperationGuard.NewId()), ct))).Code.ShouldBe(ErrorCode.ExpiredLot);
        await later.ReturnAsync(new(saleId, returnDate, "Expired", RefundMode.Cash,
            [new ReturnLineInput(line.Line.Id, Quantity.One, false)], OperationGuard.NewId()), ct);
        using var after = fixture.Factory.Open();
        after.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
            new { id = fixture.LotId }).ShouldBe(9000L);
        after.ExecuteScalar<long>("SELECT COUNT(*) FROM damage WHERE reason='CUSTOMER_RETURN'").ShouldBe(1L);
        (await later.GetAsync(saleId, ct)).Status.ShouldBe(SaleStatus.Refunded);
    }

    [Fact]
    public async Task Void_AfterTheSoldLotWasDeleted_IsRejected()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        (long productId, long[] lots) = await fixture.AddProductWithLotsAsync("VOID-GONE", 1, ct);
        long saleId = await fixture.Sales.AddProductAsync(null, productId, Quantity.One, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);
        await fixture.Inventory.DeleteZeroLotAsync(lots[0], fixture.DetailVersion(lots[0]), ct);
        (await Should.ThrowAsync<DomainException>(() => fixture.Sales.VoidAsync(saleId, "Mistake",
            OperationGuard.NewId(), ct))).Code.ShouldBe(ErrorCode.VoidNotAllowed);
        (await fixture.Sales.GetAsync(saleId, ct)).Status.ShouldBe(SaleStatus.Completed);
    }

    [Fact]
    public async Task ReturnOfDeletedProductSplitAcrossLots_RecreatesItOnce_AndNeverRestocksDamage()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        (long productId, long[] lots) = await fixture.AddProductWithLotsAsync("SPLIT-GONE", 3, ct);
        long saleId = await fixture.Sales.AddProductAsync(null, productId, new Quantity(3), cancellationToken: ct);
        SaleDetails draft = await fixture.Sales.GetAsync(saleId, ct);
        draft.Lines.Count.ShouldBe(3);
        await fixture.Sales.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, draft.Totals.GrandTotal)], OperationGuard.NewId()), ct);
        foreach (long lot in lots)
            await fixture.Inventory.DeleteZeroLotAsync(lot, fixture.DetailVersion(lot), ct);
        (await fixture.Catalog.RemoveAsync(productId, ct)).ShouldBe(ProductRemoval.Deleted);
        SaleDetails sold = await fixture.Sales.GetAsync(saleId, ct);
        (await fixture.Sales.GetReturnableLinesAsync(saleId, ct)).All(static line => !line.OriginalLotAvailable)
            .ShouldBeTrue();
        CompletedReturn returned = await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate,
            "Customer refund", RefundMode.Cash,
            [
                new ReturnLineInput(sold.Lines[0].Id, Quantity.One, true),
                new ReturnLineInput(sold.Lines[1].Id, Quantity.One, true),
                new ReturnLineInput(sold.Lines[2].Id, Quantity.One, false),
            ], OperationGuard.NewId()), ct);
        returned.Refund.ShouldBe(draft.Totals.GrandTotal);
        using var after = fixture.Factory.Open();
        after.ExecuteScalar<long>("""
            SELECT COUNT(DISTINCT restock_product_id_snapshot) FROM sale_return_line
            WHERE sale_return_id=@id AND restock_lot_id IS NOT NULL
            """, new { id = returned.ReturnId }).ShouldBe(1L);
        after.ExecuteScalar<long>("SELECT COUNT(*) FROM product WHERE sku='SPLIT-GONE'").ShouldBe(1L);
        after.ExecuteScalar<long>("""
            SELECT SUM(qty_on_hand) FROM product_lot WHERE origin_kind='CUSTOMER_RETURN'
            """).ShouldBe(2000L);
        after.QuerySingle<(long? Restock, long DamagedLot)>("""
            SELECT rl.restock_lot_id AS Restock,d.lot_id AS DamagedLot
            FROM sale_return_line rl JOIN damage d ON d.id=rl.damage_id
            WHERE rl.sale_return_id=@id
            """, new { id = returned.ReturnId }).ShouldBe(((long?)null, sold.Lines[2].LotId));
        (await fixture.Sales.GetReceiptAsync(saleId, duplicate: true, cancellationToken: ct))
            .GrandTotal.ShouldBe(draft.Totals.GrandTotal);
        await fixture.Database.WriteAsync(tx =>
            LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction).ToArray().ShouldBeEmpty(), ct);
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
            "1001", fixture.TaxId, false, null, false, null, null,
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
    public async Task DayEnd_CountsEachCashExpenseAtItsCurrentValues()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var moneyOut = new MoneyOutService(fixture.Database, new LotQuantities(fixture.Settings),
            new DocNumberService(fixture.Settings), new PeriodGuard(fixture.Settings, fixture.Clock), new AuditLog());
        DateOnly today = fixture.Clock.BusinessDate;
        DateOnly yesterday = today.AddDays(-1);
        long category = (await moneyOut.GetExpenseCategoriesAsync(cancellationToken: ct))[0].Id;
        async Task<ExpenseItem> Record(DateOnly date, decimal amount, string mode)
        {
            long id = await moneyOut.RecordExpenseAsync(new(date, category, new Money(amount), mode, null, null, null),
                OperationGuard.NewId(), ct);
            return (await moneyOut.GetExpensesAsync(date, date, ct)).Single(item => item.Id == id);
        }
        Task Correct(ExpenseItem item, ExpenseInput replacement) =>
            moneyOut.CorrectExpenseAsync(item, replacement, "Entered wrongly", OperationGuard.NewId(), ct);
        ExpenseInput Same(ExpenseItem item) => new(item.Date, item.CategoryId, item.Amount, item.Mode, null, null, null);

        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(2), cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(250))], OperationGuard.NewId()), ct);
        await moneyOut.VoidExpenseAsync(await Record(today, 100, "CASH"), "Duplicate", OperationGuard.NewId(), ct);
        ExpenseItem lowered = await Record(today, 200, "CASH");
        await Correct(lowered, Same(lowered) with { Amount = new Money(150) });
        ExpenseItem paidByUpi = await Record(today, 50, "CASH");
        await Correct(paidByUpi, Same(paidByUpi) with { Mode = "UPI" });
        ExpenseItem paidInCash = await Record(today, 70, "UPI");
        await Correct(paidInCash, Same(paidInCash) with { Mode = "CASH" });
        ExpenseItem movedAway = await Record(today, 30, "CASH");
        await Correct(movedAway, Same(movedAway) with { Date = yesterday });
        ExpenseItem movedHere = await Record(yesterday, 25, "CASH");
        await Correct(movedHere, Same(movedHere) with { Date = today });
        ExpenseItem twice = await Record(today, 40, "CASH");
        await Correct(twice, Same(twice) with { Amount = new Money(45) });
        twice = (await moneyOut.GetExpensesAsync(today, today, ct)).Single(item => item.Id == twice.Id);
        await Correct(twice, Same(twice) with { Amount = new Money(35) });
        await Record(today, 10, "CASH");

        // 150 lowered + 70 paid in cash + 25 moved here + 35 after two edits + 10 untouched.
        DayEndSummary summary = await fixture.Sales.GetDayEndAsync(today, ct);
        summary.CashExpenses.ShouldBe(new Money(290));
        summary.CashExpected.ShouldBe(new Money(236 - 290));
        (await fixture.Sales.GetDayEndAsync(yesterday, ct)).CashExpenses.ShouldBe(new Money(30));
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
        await fixture.EditLotPriceAsync(fixture.LotId, new Money(115), ct);
        HeldBillReview review = await fixture.Sales.GetHeldBillReviewAsync(saleId, ct);
        review.Lines.Single().PriceChanged.ShouldBeTrue();
        await fixture.EditLotPriceAsync(fixture.LotId, new Money(110), ct);
        await Should.ThrowAsync<DomainException>(() => fixture.Sales.ResumeReviewedAsync(review, ct));
        (await fixture.Sales.GetAsync(saleId, ct)).Status.ShouldBe(SaleStatus.Held);

        await fixture.Sales.ResumeReviewedAsync(await fixture.Sales.GetHeldBillReviewAsync(saleId, ct), ct);
        (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().UnitPrice.ShouldBe(new Money(110));
    }

    [Fact]
    public async Task HeldBill_CompletedOnALaterDay_IsDatedNumberedAndCountedOnThatDay()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Held on the fiscal year's last day and completed on the next, so the number shows the series.
        SalesService started = fixture.ServiceAt(new DateTime(2027, 3, 31, 21, 0, 0));
        SalesService nextDay = fixture.ServiceAt(new DateTime(2027, 4, 1, 9, 0, 0));
        long saleId = await started.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        await started.HoldAsync(saleId, "Pickup", ct);

        await nextDay.ResumeAsync(saleId, ct);
        (await nextDay.GetAsync(saleId, ct)).SaleDate.ShouldBe(new DateOnly(2027, 4, 1));
        CompletedSale completed = await nextDay.CompleteAsync(new(saleId,
            [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);

        completed.InvoiceNumber.ShouldBe("S2728-000001");
        (await nextDay.GetAsync(saleId, ct)).SaleDate.ShouldBe(new DateOnly(2027, 4, 1));
        (await nextDay.GetDayEndAsync(new DateOnly(2027, 4, 1), ct)).BillCount.ShouldBe(1);
        (await nextDay.GetDayEndAsync(new DateOnly(2027, 3, 31), ct)).BillCount.ShouldBe(0);
        using var connection = fixture.Factory.Open();
        connection.Query<string>("SELECT day FROM pnl_day WHERE bills>0").ShouldBe(["2027-04-01"]);
        connection.Query<string>("SELECT day FROM sale_report_day").Distinct().ShouldBe(["2027-04-01"]);
        await nextDay.VoidAsync(saleId, "Wrong item", OperationGuard.NewId(), ct);
    }

    [Fact]
    public async Task HeldOrOpenBill_FromBeforeAYearLock_CompletesOnTheOpenBusinessDate()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        SalesService started = fixture.ServiceAt(new DateTime(2026, 12, 31, 20, 0, 0));
        SalesService locked = fixture.ServiceAt(new DateTime(2027, 1, 9, 10, 0, 0));
        long heldId = await started.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        await started.HoldAsync(heldId, "Pickup", ct);
        long openId = await started.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);

        await locked.ResumeAsync(heldId, ct);
        await locked.CompleteAsync(new(heldId, [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);
        await locked.CompleteAsync(new(openId, [new(PaymentMode.Cash, new Money(118))], OperationGuard.NewId()), ct);

        (await locked.GetAsync(heldId, ct)).SaleDate.ShouldBe(new DateOnly(2027, 1, 9));
        (await locked.GetAsync(openId, ct)).SaleDate.ShouldBe(new DateOnly(2027, 1, 9));
        (await locked.GetDayEndAsync(new DateOnly(2027, 1, 9), ct)).BillCount.ShouldBe(2);
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
        await fixture.EditLotPriceAsync(fixture.LotId, new Money(110), ct);
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

    private sealed class SalesFixture : IDisposable
    {
        private readonly string _root;
        private readonly LotQuantities _ledger;

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
            var ledger = new LotQuantities(settings);
            _ledger = ledger;
            Catalog = new CatalogService(Database, new AuditLog(), settings, Clock);
            Inventory = new InventoryService(Database, ledger, new DocNumberService(settings), new PeriodGuard(settings, Clock), new AuditLog());
            Sales = new SalesService(Database, ledger, new DocNumberService(settings),
                new PeriodGuard(settings, Clock), settings, new AuditLog(), Clock);

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
                INSERT INTO product_lot(lot_code,product_id,received_date,unit_cost,selling_price,mrp,qty_received)
                VALUES ('L-SALE',@productId,'2026-09-01',70000,118000,120000,10000) RETURNING id
                """, new { productId = ProductId });
            AlternateLotId = connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(lot_code,product_id,received_date,unit_cost,selling_price,mrp,qty_received)
                VALUES ('L-SALE-ALT',@productId,'2026-09-02',80000,125000,130000,5000) RETURNING id
                """, new { productId = ProductId });
            using var transaction = connection.BeginTransaction();
            ledger.Change(new(connection, transaction), new(Clock.BusinessDate, ProductId, LotId,
                LotChangeKind.Opening, new Quantity(10)));
            ledger.Change(new(connection, transaction), new(Clock.BusinessDate, ProductId, AlternateLotId,
                LotChangeKind.Opening, new Quantity(5)));
            transaction.Commit();
        }

        // A lot inserted with qty_received becomes saleable through its direct origin.
        public Task ReceiveDirectAsync(long productId, long lotId, Quantity quantity, CancellationToken ct) =>
            Database.WriteAsync(tx => _ledger.Change(tx, new(Clock.BusinessDate, productId, lotId,
                LotChangeKind.DirectEntry, quantity)), ct);

        // Edits a lot's selling price through the versioned lot workflow, keeping its MRP. The
        // fixture's lots have no expiry, which the edit requires for an expiry-tracked product.
        public async Task EditLotPriceAsync(long lotId, Money price, CancellationToken ct)
        {
            LotItem lot = (await Inventory.GetLotsByIdsAsync([lotId], ct)).Single();
            await Inventory.EditLotDetailsAsync(new(lotId, lot.DetailVersion, price, lot.Mrp,
                lot.ExpiryDate ?? Clock.BusinessDate.AddYears(1)), ct);
        }

        // A product with one-unit lots received on consecutive days, in FEFO order.
        public async Task<(long ProductId, long[] Lots)> AddProductWithLotsAsync(string sku, int lots,
            CancellationToken ct)
        {
            long productId;
            var ids = new long[lots];
            using (var db = Factory.Open())
            {
                productId = db.ExecuteScalar<long>("""
                    INSERT INTO product(sku,name,category_id,uom_id,tax_rate_id,hsn_code,
                        price_includes_tax,track_expiry,default_selling_price,default_mrp)
                    VALUES (@sku,@sku,@category,@unit,@tax,'1006',1,0,118000,120000) RETURNING id
                    """, new { sku, category = CategoryId, unit = UnitId, tax = TaxId });
                for (int index = 0; index < lots; index++)
                    ids[index] = db.ExecuteScalar<long>("""
                        INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,selling_price,mrp,qty_received)
                        VALUES (@productId,@code,@received,70000,118000,120000,1000) RETURNING id
                        """, new { productId, code = $"{sku}-{index}", received = $"2026-09-0{index + 1}" });
            }
            foreach (long lot in ids) await ReceiveDirectAsync(productId, lot, Quantity.One, ct);
            return (productId, ids);
        }

        public long DetailVersion(long lotId)
        {
            using var db = Factory.Open();
            return db.ExecuteScalar<long>("SELECT detail_version FROM product_lot WHERE id=@lotId", new { lotId });
        }

        public SalesService ServiceAt(DateTime now) => new(Database, _ledger, new DocNumberService(Settings),
            new PeriodGuard(Settings, new FixedClock(now)), Settings, new AuditLog(), new FixedClock(now));

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
