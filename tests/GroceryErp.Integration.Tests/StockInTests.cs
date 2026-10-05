using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Purchasing;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class StockInTests
{
    [Fact]
    public void LandedCost_AllocatesChargesAndFreeQuantityExactly()
    {
        PurchaseCostLine[] lines =
        [
            new(new Quantity(10), new Quantity(1), new Money(250), Money.Zero, new Money(125)),
            new(new Quantity(5), Quantity.Zero, new Money(100), Money.Zero, new Money(25)),
        ];

        IReadOnlyList<Money> regular = LandedCost.Compute(lines, new Money(300), taxIsCost: false);
        regular[0].ShouldBe(new Money(250));
        regular[1].ShouldBe(new Money(110));
        regular.Sum(static value => value.Scaled).ShouldBe(360000L);

        IReadOnlyList<Money> composition = LandedCost.Compute(lines, new Money(300), taxIsCost: true);
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
            connection.ExecuteScalar<long>("SELECT balance_due FROM v_vendor_balance WHERE vendor_id=@id", new { id = fixture.VendorId })
                .ShouldBe(2825000L);
        }

        StockValuationItem valuation = (await fixture.Inventory.GetValuationAsync(TestContext.Current.CancellationToken)).Single();
        valuation.Quantity.ShouldBe(new Quantity(11));
        valuation.CostValue.ShouldBe(new Money(2600.004m));
        valuation.SellingValue.ShouldBe(new Money(3300));
        valuation.MrpValue.ShouldBe(new Money(3850));

        await fixture.Purchasing.RecordPaymentAsync(new(fixture.VendorId, purchaseId, fixture.Date, new Money(500), "UPI", "TX1", null),
            OperationGuard.NewId(), TestContext.Current.CancellationToken);
        (await fixture.Purchasing.GetVendorLedgerAsync(fixture.VendorId, TestContext.Current.CancellationToken))[^1].Balance
            .ShouldBe(new Money(2325));

        await fixture.Purchasing.CancelAsync(purchaseId, fixture.Date, OperationGuard.NewId(), TestContext.Current.CancellationToken);
        using (var connection = fixture.Factory.Open())
        {
            connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = posted.LotIds[0] }).ShouldBe(0L);
            connection.ExecuteScalar<string>("SELECT status FROM purchase WHERE id=@purchaseId", new { purchaseId }).ShouldBe("CANCELLED");
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_movement WHERE lot_id=@id", new { id = posted.LotIds[0] }).ShouldBe(2L);
        }
    }

    [Fact]
    public async Task Purchase_CannotCancelAfterLotWasAdjusted()
    {
        using var fixture = new StockInFixture();
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("INV-2"), TestContext.Current.CancellationToken);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), TestContext.Current.CancellationToken);
        await fixture.Inventory.AdjustAsync(posted.LotIds[0], fixture.Date, new Quantity(-1), "Damaged during handling",
            OperationGuard.NewId(), TestContext.Current.CancellationToken);

        DomainException exception = await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.CancelAsync(
            purchaseId, fixture.Date, OperationGuard.NewId(), TestContext.Current.CancellationToken));
        exception.Code.ShouldBe(ErrorCode.CancelNotAllowed);
    }

    [Fact]
    public async Task VendorReturn_PostsCreditStockAndVarianceOnce_AndCapsSourceLot()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("INV-RETURN"), ct);
        PostedPurchase purchase = await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), ct);
        await fixture.Inventory.SetLotActiveAsync(purchase.LotIds[0], false, ct);
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
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_movement WHERE type_code='VENDOR_RETURN'")
            .ShouldBe(1L);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='VENDOR_RETURN_VARIANCE'")
            .ShouldBe(-36364L);
        LedgerItem movement = (await fixture.Inventory.GetLedgerAsync(fixture.ProductId, fixture.Date, fixture.Date, ct))
            .Single(item => item.Type == "VENDOR_RETURN");
        (await fixture.Inventory.GetSourceDocumentAsync(movement, ct)).Number.ShouldBe(posted.DocumentNumber);
    }

    [Fact]
    public async Task StockCount_CompensatesForMovementsAfterSnapshot()
    {
        using var fixture = new StockInFixture();
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("INV-COUNT"), TestContext.Current.CancellationToken);
        PostedPurchase purchase = await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), TestContext.Current.CancellationToken);
        long countId = await fixture.Inventory.CreateStockCountAsync(fixture.Date, null, "Closing count", TestContext.Current.CancellationToken);

        await fixture.Inventory.AdjustAsync(purchase.LotIds[0], fixture.Date, new Quantity(-1), "Sale-like movement during count",
            OperationGuard.NewId(), TestContext.Current.CancellationToken);
        await fixture.Inventory.SetCountedQuantityAsync(countId, purchase.LotIds[0], new Quantity(8), TestContext.Current.CancellationToken);
        StockCountDetails review = await fixture.Inventory.GetStockCountAsync(countId, TestContext.Current.CancellationToken);
        review.Lines.Single().Expected.ShouldBe(new Quantity(11));
        review.Lines.Single().MovementSinceSnapshot.ShouldBe(new Quantity(-1));
        review.Lines.Single().Difference.ShouldBe(new Quantity(-2));

        DomainException changed = await Should.ThrowAsync<DomainException>(() => fixture.Inventory.PostStockCountAsync(
            countId, OperationGuard.NewId(), new Dictionary<long, long> { [purchase.LotIds[0]] = 0 },
            TestContext.Current.CancellationToken));
        changed.Code.ShouldBe(ErrorCode.StockCountReviewChanged);
        await fixture.Inventory.PostStockCountAsync(countId, OperationGuard.NewId(),
            new Dictionary<long, long> { [purchase.LotIds[0]] = -1000 }, TestContext.Current.CancellationToken);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = purchase.LotIds[0] }).ShouldBe(8000L);
        connection.ExecuteScalar<string>("SELECT status FROM stock_count WHERE id=@countId", new { countId }).ShouldBe("POSTED");
        LedgerItem countMove = (await fixture.Inventory.GetLedgerAsync(fixture.ProductId, fixture.Date,
            fixture.Date, TestContext.Current.CancellationToken)).Single(item => item.RefType == "stock_count");
        (await fixture.Inventory.GetSourceDocumentAsync(countMove, TestContext.Current.CancellationToken))
            .Lines.Single().Quantity.ShouldBe(new Quantity(-2));
    }

    [Fact]
    public async Task StockCount_RefreshesLotsCreatedAfterSnapshotBeforePosting()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long firstId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("COUNT-FIRST"), ct);
        PostedPurchase first = await fixture.Purchasing.PostAsync(firstId, OperationGuard.NewId(), ct);
        long categoryId;
        using (var connection = fixture.Factory.Open())
            categoryId = connection.ExecuteScalar<long>("SELECT category_id FROM product WHERE id=@id",
                new { id = fixture.ProductId });
        long countId = await fixture.Inventory.CreateStockCountAsync(fixture.Date, [categoryId], null, ct);
        await fixture.Inventory.SetCountedQuantityAsync(countId, first.LotIds[0], new Quantity(11), ct);

        long nextId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("COUNT-NEXT"), ct);
        PostedPurchase next = await fixture.Purchasing.PostAsync(nextId, OperationGuard.NewId(), ct);
        DomainException stale = await Should.ThrowAsync<DomainException>(() => fixture.Inventory.PostStockCountAsync(
            countId, OperationGuard.NewId(), cancellationToken: ct));
        stale.Code.ShouldBe(ErrorCode.StockCountReviewChanged);
        (await fixture.Inventory.RefreshStockCountLotsAsync(countId, ct)).ShouldBe(1);
        (await fixture.Inventory.RefreshStockCountLotsAsync(countId, ct)).ShouldBe(0);
        StockCountDetails refreshed = await fixture.Inventory.GetStockCountAsync(countId, ct);
        StockCountLine added = refreshed.Lines.Single(line => line.LotId == next.LotIds[0]);
        added.Expected.ShouldBe(Quantity.Zero);
        added.MovementSinceSnapshot.ShouldBe(new Quantity(11));
        await fixture.Inventory.SetCountedQuantityAsync(countId, added.LotId, new Quantity(11), ct);
        await fixture.Inventory.PostStockCountAsync(countId, OperationGuard.NewId(),
            refreshed.Lines.ToDictionary(static line => line.LotId,
                static line => line.MovementSinceSnapshot.Scaled), ct);
        (await fixture.Inventory.GetStockCountAsync(countId, ct)).Status.ShouldBe("POSTED");
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
    public async Task StockCount_RejectsEmptySheetAndLocksEnteredQuantitiesAfterPosting()
    {
        using var fixture = new StockInFixture();
        await Should.ThrowAsync<DomainException>(() => fixture.Inventory.CreateStockCountAsync(
            fixture.Date, null, null, TestContext.Current.CancellationToken));
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("COUNT-LOCK"),
            TestContext.Current.CancellationToken);
        PostedPurchase purchase = await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(),
            TestContext.Current.CancellationToken);
        long categoryId;
        using (var connection = fixture.Factory.Open())
            categoryId = connection.ExecuteScalar<long>("SELECT category_id FROM product WHERE id=@id", new { id = fixture.ProductId });
        long countId = await fixture.Inventory.CreateStockCountAsync(fixture.Date, [categoryId], null,
            TestContext.Current.CancellationToken);
        StockCountSummary draft = (await fixture.Inventory.GetDraftCountsAsync(TestContext.Current.CancellationToken)).Single();
        draft.Id.ShouldBe(countId);
        draft.LotCount.ShouldBe(1);
        draft.CountedCount.ShouldBe(0);
        DomainException fractional = await Should.ThrowAsync<DomainException>(() => fixture.Inventory.SetCountedQuantityAsync(
            countId, purchase.LotIds[0], new Quantity(10.5m), TestContext.Current.CancellationToken));
        fractional.Code.ShouldBe(ErrorCode.FractionalQtyNotAllowed);
        await Should.ThrowAsync<DomainException>(() => fixture.Inventory.SetCountedQuantitiesAsync(countId,
            new Dictionary<long, Quantity> { [purchase.LotIds[0]] = new Quantity(11), [999999] = Quantity.One },
            TestContext.Current.CancellationToken));
        (await fixture.Inventory.GetDraftCountsAsync(TestContext.Current.CancellationToken)).Single().CountedCount.ShouldBe(0);
        await fixture.Inventory.SetCountedQuantityAsync(countId, purchase.LotIds[0], new Quantity(11),
            TestContext.Current.CancellationToken);
        await fixture.Inventory.PostStockCountAsync(countId, OperationGuard.NewId(),
            cancellationToken: TestContext.Current.CancellationToken);
        DomainException error = await Should.ThrowAsync<DomainException>(() => fixture.Inventory.SetCountedQuantityAsync(
            countId, purchase.LotIds[0], new Quantity(10), TestContext.Current.CancellationToken));
        error.Code.ShouldBe(ErrorCode.Validation);
        (await fixture.Inventory.GetDraftCountsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
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
    public async Task StockCache_RebuildRestoresLedgerBalanceAndAuditsRepair()
    {
        using var fixture = new StockInFixture();
        long purchaseId = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("INV-CACHE"), TestContext.Current.CancellationToken);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(purchaseId, OperationGuard.NewId(), TestContext.Current.CancellationToken);
        using (var connection = fixture.Factory.Open())
            connection.Execute("UPDATE product_lot SET qty_on_hand=123000 WHERE id=@id", new { id = posted.LotIds[0] });

        (await fixture.Inventory.RebuildStockCacheAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        using var verify = fixture.Factory.Open();
        verify.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = posted.LotIds[0] }).ShouldBe(11000L);
        verify.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity='stock_cache' AND action='REBUILD'").ShouldBe(1L);
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
            var periods = new PeriodGuard(Settings);
            Ledger = new StockLedger(Settings);
            Inventory = new InventoryService(Database, Ledger, numbers, periods, new AuditLog());
            Purchasing = new PurchasingService(Database, Ledger, numbers, periods, Settings, new AuditLog());
            Date = new DateOnly(2026, 9, 26);

            using var connection = Factory.Open();
            long category = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Food') RETURNING id");
            long unit = connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
            long tax = connection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=5000");
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
        public StockLedger Ledger { get; }
        public InventoryService Inventory { get; }
        public PurchasingService Purchasing { get; }
        public DateOnly Date { get; }
        public long ProductId { get; }
        public long VendorId { get; }

        public PurchaseDraftInput Purchase(string invoice = "INV-1") => new(
            VendorId, invoice, Date, Date, new Money(2725), new Money(100), null,
            [new(ProductId, "BATCH", Date.AddMonths(-1), Date.AddMonths(6), new Quantity(10), Quantity.One,
                new Money(250), Money.Zero, new Percent(5), new Money(350), new Money(300))]);

        public void Dispose()
        {
            Settings.Dispose();
            Database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
