using Dapper;
using GroceryErp.Catalog;
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
    public async Task LedgerPages_MatchMovementPrefixBalancesAcrossCursorsAndTypeFilters()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long draft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("LEDGER-BALANCE"), ct);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(draft, OperationGuard.NewId(), ct);
        await fixture.Inventory.AdjustAsync(posted.LotIds[0], fixture.Date,
            new Quantity(-1), "First", OperationGuard.NewId(), ct);
        await fixture.Inventory.AdjustAsync(posted.LotIds[0], fixture.Date,
            new Quantity(-2), "Second", OperationGuard.NewId(), ct);
        LedgerPage page = await fixture.Inventory.GetLedgerPageAsync(fixture.ProductId,
            DateOnly.MinValue, DateOnly.MaxValue, limit: 2, cancellationToken: ct);
        page.HasMore.ShouldBeTrue();
        var movements = page.Items.ToList();
        movements.AddRange((await fixture.Inventory.GetLedgerPageAsync(fixture.ProductId,
            DateOnly.MinValue, DateOnly.MaxValue, beforeId: page.NextId, limit: 2,
            cancellationToken: ct)).Items);
        movements.AddRange((await fixture.Inventory.GetLedgerPageAsync(fixture.ProductId,
            DateOnly.MinValue, DateOnly.MaxValue, type: "ADJUSTMENT", limit: 2,
            cancellationToken: ct)).Items);
        using var connection = fixture.Factory.Open();
        foreach (LedgerItem movement in movements)
            movement.LotBalance.Scaled.ShouldBe(connection.ExecuteScalar<long>("""
                SELECT COALESCE(SUM(qty),0) FROM stock_movement WHERE lot_id=@lotId AND id<=@id
                """, new { lotId = posted.LotIds[0], id = movement.Id }));
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
        await fixture.Inventory.AdjustAsync(purchase.LotIds[0], fixture.Date,
            new Quantity(-2), "Projection test", OperationGuard.NewId(), ct);
        using var verify = fixture.Factory.Open();
        verify.ExecuteScalar<long>("SELECT is_low FROM product_stock_total WHERE product_id=@id",
            new { id = fixture.ProductId }).ShouldBe(1);
        verify.ExecuteScalar<long>("SELECT is_low FROM v_product_stock WHERE product_id=@id",
            new { id = fixture.ProductId }).ShouldBe(1);
        var catalog = new CatalogService(fixture.Database, new AuditLog(), fixture.Settings);
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
    }

    [Fact]
    public async Task CountReview_UsesBoundedSummaryAndRejectsScopedMovementAfterReview()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long draft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("REVIEW-PAGE"), ct);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(draft, OperationGuard.NewId(), ct);
        long countId = await fixture.Inventory.CreateStockCountAsync(fixture.Date, null, null, ct);
        await fixture.Inventory.SetCountedQuantityAsync(countId, posted.LotIds[0], new Quantity(11), ct);
        StockCountReview review = await fixture.Inventory.GetStockCountReviewAsync(countId, ct);
        review.Total.ShouldBe(1);
        review.Counted.ShouldBe(1);
        review.Differences.ShouldBe(0);
        (await fixture.Inventory.GetDraftCountPageAsync(cancellationToken: ct)).Items.Single().CountedCount.ShouldBe(1);

        await fixture.Inventory.AdjustAsync(posted.LotIds[0], fixture.Date, new Quantity(-1),
            "After review", OperationGuard.NewId(), ct);
        DomainException stale = await Should.ThrowAsync<DomainException>(() =>
            fixture.Inventory.PostReviewedStockCountAsync(countId, OperationGuard.NewId(),
                review.MovementHighWaterMark, ct));
        stale.Code.ShouldBe(ErrorCode.StockCountReviewChanged);
        StockCountReview refreshed = await fixture.Inventory.GetStockCountReviewAsync(countId, ct);
        refreshed.Differences.ShouldBe(1);
        await fixture.Inventory.PostReviewedStockCountAsync(countId, OperationGuard.NewId(),
            refreshed.MovementHighWaterMark, ct);
    }
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
            connection.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@id", new { id = fixture.ProductId })
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
            connection.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@id", new { id = fixture.ProductId }).ShouldBe(0L);
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
    public async Task StockCount_PageAndSearchReachLaterLotsAndKeepGlobalProgress()
    {
        using var fixture = new StockInFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long firstDraft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("PAGE-FIRST"), ct);
        PostedPurchase first = await fixture.Purchasing.PostAsync(firstDraft, OperationGuard.NewId(), ct);
        long secondDraft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Purchase("PAGE-SECOND"), ct);
        PostedPurchase second = await fixture.Purchasing.PostAsync(secondDraft, OperationGuard.NewId(), ct);
        long countId = await fixture.Inventory.CreateStockCountAsync(fixture.Date, null, null, ct);

        StockCountPage firstPage = await fixture.Inventory.GetStockCountPageAsync(countId, pageSize: 1, cancellationToken: ct);
        firstPage.Lines.Count.ShouldBe(1);
        firstPage.HasMore.ShouldBeTrue();
        firstPage.Total.ShouldBe(2);
        StockCountPage nextPage = await fixture.Inventory.GetStockCountPageAsync(countId,
            firstPage.NextLineId, 1, ct);
        nextPage.Lines.Single().LotId.ShouldBe(second.LotIds[0]);
        nextPage.HasMore.ShouldBeFalse();
        (await fixture.Inventory.FindStockCountLineAsync(countId, nextPage.Lines[0].LotCode,
            cancellationToken: ct)).ShouldBe(nextPage.Lines[0].Id);
        string productName = firstPage.Lines[0].ProductName;
        (await fixture.Inventory.FindStockCountLineAsync(countId,
            productName.Substring(1, Math.Min(4, productName.Length - 1)), cancellationToken: ct))
            .ShouldBe(firstPage.Lines[0].Id);
        (await fixture.Inventory.FindStockCountLineAsync(countId,
            productName[..2], cancellationToken: ct)).ShouldBe(firstPage.Lines[0].Id);

        await fixture.Inventory.SetCountedQuantityAsync(countId, first.LotIds[0], new Quantity(11), ct);
        StockCountPage progress = await fixture.Inventory.GetStockCountPageAsync(countId, pageSize: 1, cancellationToken: ct);
        progress.Counted.ShouldBe(1);
        progress.Total.ShouldBe(2);
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
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_movement WHERE type_code='OPENING'")
            .ShouldBe(205);
        OpeningStockLine[] changed = lines.ToArray();
        changed[0] = changed[0] with { Quantity = new Quantity(2) };
        DomainException conflict = await Should.ThrowAsync<DomainException>(() =>
            fixture.Inventory.ImportOpeningStockAsync(changed, operationId, TestContext.Current.CancellationToken));
        conflict.Code.ShouldBe(ErrorCode.Validation);
    }

    [Fact]
    public async Task LotEdits_RejectStaleValuesWithoutOverwritingNewerEdits()
    {
        using var fixture = new StockInFixture();
        long lotId = await fixture.Inventory.ImportOpeningStockAsync(
            [new(fixture.ProductId, Quantity.One, new Money(20), new Money(30), new Money(35),
                fixture.Date.AddMonths(2), "CAS", fixture.Date)],
            OperationGuard.NewId(), TestContext.Current.CancellationToken);
        await fixture.Inventory.ChangeLotPriceAsync(lotId, new Money(31), new Money(35),
            new Money(30), new Money(35), TestContext.Current.CancellationToken);
        DomainException stalePrice = await Should.ThrowAsync<DomainException>(() =>
            fixture.Inventory.ChangeLotPriceAsync(lotId, new Money(32), new Money(35),
                new Money(30), new Money(35), TestContext.Current.CancellationToken));
        stalePrice.Code.ShouldBe(ErrorCode.Validation);
        await fixture.Inventory.SetLotActiveAsync(lotId, false, true, TestContext.Current.CancellationToken);
        DomainException staleActive = await Should.ThrowAsync<DomainException>(() =>
            fixture.Inventory.SetLotActiveAsync(lotId, true, true, TestContext.Current.CancellationToken));
        staleActive.Code.ShouldBe(ErrorCode.Validation);
        using var connection = fixture.Factory.Open();
        connection.QuerySingle<(long Price, long Active)>("""
            SELECT selling_price AS Price,is_active AS Active FROM product_lot WHERE id=@lotId
            """, new { lotId }).ShouldBe((31_000L, 0L));
        connection.QuerySingle<(long Quantity, long Cost, long Selling, long Mrp)>("""
            SELECT quantity,cost_value,selling_value,mrp_value
            FROM product_stock_total WHERE product_id=@productId
            """, new { productId = fixture.ProductId }).ShouldBe((1_000L, 20_000L, 31_000L, 35_000L));
    }

    [Fact]
    public async Task NegativeStockFefo_DoesNotFallBackToExpiredLot()
    {
        using var fixture = new StockInFixture();
        await fixture.Settings.SaveAsync(new Dictionary<string, string>
        {
            ["stock.allow_negative"] = "1",
            ["pos.block_expired"] = "1",
        }, TestContext.Current.CancellationToken);
        await fixture.Inventory.ImportOpeningStockAsync(
        [
            new(fixture.ProductId, Quantity.One, new Money(20), new Money(30), new Money(35),
                fixture.Date.AddMonths(2), "GOOD", fixture.Date.AddDays(-2)),
            new(fixture.ProductId, Quantity.One, new Money(20), new Money(30), new Money(35),
                fixture.Date.AddDays(-1), "EXPIRED", fixture.Date.AddDays(-1)),
        ], OperationGuard.NewId(), TestContext.Current.CancellationToken);

        IReadOnlyList<LotAllocation> allocation = await fixture.Database.WriteAsync(transaction =>
            fixture.Ledger.AllocateFefo(transaction, fixture.ProductId, new Quantity(3), fixture.Date,
                new Dictionary<long, Quantity>()), TestContext.Current.CancellationToken);

        allocation.Sum(static item => item.Quantity.Value).ShouldBe(3m);
        allocation.All(item => item.Lot.Expiry is null || item.Lot.Expiry >= fixture.Date).ShouldBeTrue();
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
        (await fixture.Inventory.IsStockCacheRebuildPendingAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        using var verify = fixture.Factory.Open();
        verify.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = posted.LotIds[0] }).ShouldBe(11000L);
        verify.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity='stock_cache' AND action='REBUILD'").ShouldBe(1L);
        verify.Execute("UPDATE setting SET value='1' WHERE key='maintenance.stock_rebuild_pending'");
        (await fixture.Inventory.RebuildStockCacheAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await fixture.Inventory.IsStockCacheRebuildPendingAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
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
            Factory.ClearPool();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
