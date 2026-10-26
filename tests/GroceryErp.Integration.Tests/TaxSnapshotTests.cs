using Dapper;
using GroceryErp.Catalog;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Purchasing;
using GroceryErp.Reports;
using GroceryErp.Sales;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

// Section 15: every purchase line, lot and sale line saves its tax label and one piece's purchase
// price before tax, its tax and its price including tax, while P&L keeps the booked cost.
public sealed class TaxSnapshotTests
{
    [Fact]
    public async Task PostedPurchase_SavesTheLabelAndPerPieceTax_OnTheLineAndItsLot_AndKeepsThemWhenTheRateChanges()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        // §15.2: 10 at 100 less 50 at GST 5%, plus a second line of the same value; 60 freight
        // splits 30 to each, and 2 free pieces make 12.
        PurchaseDraftInput input = fixture.Draft("TAX-1", new Money(60),
            TaxFixture.Line(fixture.ProductId, new Quantity(10), new Money(100), fixture.Gst5) with
            {
                FreeQuantity = new Quantity(2),
                Discount = new Money(50),
            },
            TaxFixture.Line(fixture.ProductId, Quantity.One, new Money(950), fixture.Gst18));
        long id = await fixture.Purchasing.SaveDraftAsync(null, input, ct);
        PurchaseDraft draft = await fixture.Purchasing.GetDraftAsync(id, ct);
        draft.Input.Lines.Select(static line => line.TaxRateId).ShouldBe([fixture.Gst5, fixture.Gst18]);
        using (SqliteConnection db = fixture.Factory.Open())
            db.QuerySingle<(string Label, long Unit, long Tax, long Incl)>("""
                SELECT tax_label_snapshot,landed_unit_cost,landed_unit_tax,landed_unit_cost_incl_tax
                FROM purchase_line WHERE purchase_id=@id AND line_no=1
                """, new { id }).ShouldBe(("GST 5%", 81_667L, 3_958L, 85_625L));

        PostedPurchase posted = await fixture.Purchasing.PostAsync(id, OperationGuard.NewId(), ct);

        using (SqliteConnection db = fixture.Factory.Open())
        {
            db.QuerySingle<(string Label, long RateId, long Booked, long Tax, long Incl)>("""
                SELECT tax_label_snapshot,tax_rate_id,landed_unit_cost,landed_unit_tax,landed_unit_cost_incl_tax
                FROM purchase_line WHERE purchase_id=@id AND line_no=1
                """, new { id }).ShouldBe(("GST 5%", fixture.Gst5, 81_667L, 3_958L, 85_625L));
            db.QuerySingle<(long RateId, string Label, long Pct, long Cost, long Tax, long Incl)>("""
                SELECT tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,unit_cost,unit_tax,unit_cost_incl_tax
                FROM product_lot WHERE id=@lot
                """, new { lot = posted.LotIds[0] }).ShouldBe((fixture.Gst5, "GST 5%", 5_000L, 81_667L, 3_958L, 85_625L));
            // 950 + 30 = 980 before tax, 171 tax on the goods only.
            db.QuerySingle<(string Label, long Cost, long Tax, long Incl)>("""
                SELECT tax_label_snapshot,unit_cost,unit_tax,unit_cost_incl_tax FROM product_lot WHERE id=@lot
                """, new { lot = posted.LotIds[1] }).ShouldBe(("GST 18%", 980_000L, 171_000L, 1_151_000L));
        }
        PurchaseInvoiceLine line = (await fixture.Purchasing.GetInvoiceAsync(id, ct)).Lines[0];
        line.TaxLabel.ShouldBe("GST 5%");
        line.PurchasePrice.ShouldBe(new Money(81.667m));
        line.UnitTax.ShouldBe(new Money(3.958m));
        line.UnitCostInclTax.ShouldBe(new Money(85.625m));
        line.UnitTaxShown.ShouldBe(3.96m);

        // A renamed rate with a new percent changes products and new drafts only.
        await fixture.Catalog.UpdateTaxRateAsync(fixture.Gst5, "GST five", 6m, true, ct);
        LotItem lot = (await fixture.Inventory.GetLotsByIdsAsync([posted.LotIds[0]], ct)).Single();
        lot.TaxLabel.ShouldBe("GST 5%");
        lot.UnitTax.ShouldBe(new Money(3.958m));
        lot.PurchasePrice.ShouldBe(new Money(81.667m));
        (await fixture.Purchasing.GetInvoiceAsync(id, ct)).Lines[0].TaxLabel.ShouldBe("GST 5%");
        PurchaseLineDefaults defaults = await fixture.Purchasing.GetLineDefaultsAsync(fixture.VendorId,
            fixture.ProductId, fixture.Today, ct);
        defaults.TaxRateId.ShouldBe(fixture.Gst5);
        defaults.TaxRate.ShouldBe(new Percent(6));
        long next = await fixture.Purchasing.SaveDraftAsync(null, fixture.Draft("TAX-2", Money.Zero,
            TaxFixture.Line(fixture.ProductId, Quantity.One, new Money(100), fixture.Gst5)), ct);
        using (SqliteConnection db = fixture.Factory.Open())
            db.QuerySingle<(string Label, long Pct, long Tax)>("""
                SELECT tax_label_snapshot,tax_rate_pct,landed_unit_tax FROM purchase_line WHERE purchase_id=@next
                """, new { next }).ShouldBe(("GST five", 6_000L, 6_000L));
    }

    [Fact]
    public async Task Draft_RefusesAnInactiveRateAsANewChoice_ButKeepsOneItAlreadyUses()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        PurchaseDraftInput input = fixture.Draft("INACTIVE-1", Money.Zero,
            TaxFixture.Line(fixture.ProductId, new Quantity(2), new Money(100), fixture.Gst18));
        long id = await fixture.Purchasing.SaveDraftAsync(null, input, ct);
        await fixture.Catalog.UpdateTaxRateAsync(fixture.Gst18, "GST 18%", 18m, false, ct);

        // The draft keeps its choice and can still be saved and posted with it.
        await fixture.Purchasing.SaveDraftAsync(id, input with { Notes = "Re-saved" }, ct);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(id, OperationGuard.NewId(), ct);
        (await fixture.Inventory.GetLotsByIdsAsync(posted.LotIds, ct)).Single().TaxLabel.ShouldBe("GST 18%");

        (await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.SaveDraftAsync(null,
            fixture.Draft("INACTIVE-2", Money.Zero, TaxFixture.Line(fixture.ProductId, Quantity.One,
                new Money(100), fixture.Gst18)), ct))).Code.ShouldBe(ErrorCode.Validation);
        (await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.SaveDraftAsync(null,
            fixture.Draft("NO-RATE", Money.Zero, TaxFixture.Line(fixture.ProductId, Quantity.One,
                new Money(100), null)), ct))).Code.ShouldBe(ErrorCode.Validation);
    }

    [Fact]
    public async Task DraftSavedWithoutARate_TakesTheOnlyActiveRateWithItsPercent_OrMustBeChosen()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long id = await fixture.Purchasing.SaveDraftAsync(null, fixture.Draft("LEGACY-DRAFT", Money.Zero,
            TaxFixture.Line(fixture.ProductId, Quantity.One, new Money(100), fixture.Gst5)), ct);
        using (SqliteConnection db = fixture.Factory.Open())
            db.Execute("UPDATE purchase_line SET tax_rate_id=NULL,tax_label_snapshot=NULL WHERE purchase_id=@id", new { id });
        (await fixture.Purchasing.GetDraftAsync(id, ct)).Input.Lines.Single().TaxRateId.ShouldBe(fixture.Gst5);

        // A second active 5% rate makes the match ambiguous; the operator has to choose.
        await fixture.Catalog.CreateTaxRateAsync("IGST 5%", 5m, ct);
        (await fixture.Purchasing.GetDraftAsync(id, ct)).Input.Lines.Single().TaxRateId.ShouldBeNull();
        (await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.PostAsync(id, OperationGuard.NewId(), ct)))
            .Code.ShouldBe(ErrorCode.Validation);
    }

    [Fact]
    public async Task Posting_UsesTheRatesPercentAtPost_AndConvertsRatesTypedIncludingTaxAgain()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long id = await fixture.Purchasing.SaveDraftAsync(null, fixture.Draft("RATE-MOVED", Money.Zero,
            TaxFixture.Line(fixture.ProductId, new Quantity(10), new Money(262.5m), fixture.Gst5)) with
        { RatesIncludeTax = true }, ct);
        await fixture.Catalog.UpdateTaxRateAsync(fixture.Gst5, "GST 12% (was 5%)", 12m, true, ct);

        PostedPurchase posted = await fixture.Purchasing.PostAsync(id, OperationGuard.NewId(), ct);

        // 262.50 including 12% is 234.375 before tax; ten of them carry 281.25 tax.
        posted.Totals.Subtotal.ShouldBe(new Money(2343.75m));
        posted.Totals.Tax.ShouldBe(new Money(281.25m));
        using SqliteConnection db = fixture.Factory.Open();
        db.QuerySingle<(long Rate, long Pct, string Label, long Entered)>("""
            SELECT unit_cost,tax_rate_pct,tax_label_snapshot,entered_rate FROM purchase_line WHERE purchase_id=@id
            """, new { id }).ShouldBe((234_375L, 12_000L, "GST 12% (was 5%)", 262_500L));
    }

    [Fact]
    public async Task VendorReturn_CopiesThePurchaseLinesLabel()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long id = await fixture.Purchasing.SaveDraftAsync(null, fixture.Draft("RETURN-LABEL", Money.Zero,
            TaxFixture.Line(fixture.ProductId, new Quantity(4), new Money(100), fixture.Gst5)), ct);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(id, OperationGuard.NewId(), ct);
        PostedVendorReturn returned = await fixture.Purchasing.PostVendorReturnAsync(new(id, fixture.Today,
            "CN-1", "Damaged in transit", [new(posted.LotIds.Single(), Quantity.One, new Money(100), new Money(5))]),
            OperationGuard.NewId(), ct);
        using SqliteConnection db = fixture.Factory.Open();
        db.ExecuteScalar<string>("SELECT tax_label_snapshot FROM vendor_return_line WHERE vendor_return_id=@id",
            new { id = returned.Id }).ShouldBe("GST 5%");
    }

    [Fact]
    public async Task EveryLotWriter_SavesConsistentTaxValues()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        // A direct lot typed at 100 before tax with a chosen rate, and one at the product's rate.
        long chosen = await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(new Money(100)) with
        { TaxRateId = fixture.Gst18 }, "Found in store", OperationGuard.NewId(), ct);
        fixture.LotTax(chosen).ShouldBe(new LotTaxRow(fixture.Gst18, "GST 18%", 18_000, 100_000, 18_000, 118_000));
        long productRate = await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(new Money(80)),
            "Found in store", OperationGuard.NewId(), ct);
        fixture.LotTax(productRate).ShouldBe(new LotTaxRow(fixture.Gst5, "GST 5%", 5_000, 80_000, 4_000, 84_000));
        // An inactive rate is not a new choice.
        await fixture.Catalog.CreateTaxRateAsync("GST 40%", 40m, ct);
        long inactive = fixture.RateId("GST 40%");
        await fixture.Catalog.UpdateTaxRateAsync(inactive, "GST 40%", 40m, false, ct);
        await Should.ThrowAsync<DomainException>(() => fixture.Inventory.CreateDirectLotAsync(
            fixture.Opening(new Money(10)) with { TaxRateId = inactive }, "Found", OperationGuard.NewId(), ct));

        // The opening-stock import takes each product's rate.
        await fixture.Inventory.ImportOpeningStockAsync([fixture.Opening(new Money(50))], OperationGuard.NewId(), ct);
        fixture.LotTax(fixture.LatestLot()).ShouldBe(new LotTaxRow(fixture.Gst5, "GST 5%", 5_000, 50_000, 2_500, 52_500));

        // A new product's first lot starts at the product's rate.
        ProductInput source = (await fixture.Catalog.GetAsync(fixture.ProductId, ct)).Product;
        await fixture.Catalog.CreateWithDirectLotAsync(source with { Sku = "FIRST-LOT", Barcodes = [] },
            new NewProductLotInput(new Quantity(2), new Money(40), new Money(50), new Money(60), null, null,
                fixture.Today, "First stock"), fixture.Ledger, new DocNumberService(fixture.Settings),
            fixture.Periods, OperationGuard.NewId(), ct);
        fixture.LotTax(fixture.LatestLot()).ShouldBe(new LotTaxRow(fixture.Gst5, "GST 5%", 5_000, 40_000, 2_000, 42_000));

        // A count surplus is the counted lot's stock: its cost and saved tax.
        long surplus = await fixture.CountAsync(chosen, new Quantity(5), ct);
        fixture.LotTax(surplus).ShouldBe(fixture.LotTax(chosen));

        // A damage correction that recreates a deleted lot applies the product's rate as it is now.
        await fixture.MoneyOut.RecordDamageAsync(new(productRate, fixture.Today, new Quantity(10), "BROKEN",
            Money.Zero, null), OperationGuard.NewId(), ct);
        using (SqliteConnection db = fixture.Factory.Open())
            db.Execute("DELETE FROM product_lot WHERE id=@productRate", new { productRate });
        DamageItem damage = (await fixture.MoneyOut.GetDamagesAsync(fixture.Today, fixture.Today, ct)).Single();
        await fixture.Catalog.UpdateTaxRateAsync(fixture.Gst5, "GST 5% new", 5m, true, ct);
        await fixture.MoneyOut.CorrectDamageAsync(damage, new(new Quantity(9), Money.Zero, "BROKEN", null),
            "One was fine", OperationGuard.NewId(), ct);
        fixture.LotTax(fixture.LatestLot()).ShouldBe(new LotTaxRow(fixture.Gst5, "GST 5% new", 5_000, 80_000, 4_000, 84_000));

        // Composition books the price including tax as the cost.
        await fixture.Settings.SaveAsync(new Dictionary<string, string> { ["tax.mode"] = "COMPOSITION" }, ct);
        long composition = await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(new Money(100)),
            "Found in store", OperationGuard.NewId(), ct);
        fixture.LotTax(composition).ShouldBe(new LotTaxRow(fixture.Gst5, "GST 5% new", 5_000, 105_000, 5_000, 105_000));

        using SqliteConnection check = fixture.Factory.Open();
        check.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM product_lot WHERE unit_tax IS NULL OR unit_tax<0
               OR (unit_cost<>unit_cost_incl_tax-unit_tax AND unit_cost<>unit_cost_incl_tax)
            """).ShouldBe(0L);
        await fixture.Database.WriteAsync(tx => LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction).ToArray().ShouldBeEmpty(), ct);
    }

    [Fact]
    public void LotTriggers_RejectInconsistentTaxValues_AndAnyChangeToCostOrTax()
    {
        using var fixture = new TaxFixture();
        using SqliteConnection db = fixture.Factory.Open();
        void Insert(string values) => db.Execute($"""
            INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,selling_price,qty_received,
                unit_tax,unit_cost_incl_tax)
            VALUES (@product,@code,'2026-09-01',{values})
            """, new { product = fixture.ProductId, code = "T-" + Guid.NewGuid().ToString("N") });
        Should.Throw<SqliteException>(() => Insert("100000,150000,0,5000,NULL")).Message.ShouldContain("inconsistent");
        Should.Throw<SqliteException>(() => Insert("100000,150000,0,-1,99999")).Message.ShouldContain("inconsistent");
        Should.Throw<SqliteException>(() => Insert("100000,150000,0,5000,104000")).Message.ShouldContain("inconsistent");
        Insert("100000,150000,0,5000,105000");
        Insert("105000,150000,0,5000,105000");
        Insert("100000,150000,0,NULL,NULL");
        long lot = db.ExecuteScalar<long>("SELECT MAX(id) FROM product_lot WHERE unit_tax=5000");
        foreach (string change in new[] { "unit_cost=unit_cost+1", "unit_tax=0", "unit_cost_incl_tax=1",
                     "tax_label_snapshot='Other'", "tax_rate_pct_snapshot=1", "tax_rate_id_snapshot=99" })
            Should.Throw<SqliteException>(() => db.Execute($"UPDATE product_lot SET {change} WHERE id=@lot", new { lot }))
                .Message.ShouldContain("fixed when the lot is created");
        db.Execute("UPDATE product_lot SET selling_price=140000 WHERE id=@lot", new { lot });
        db.ExecuteScalar<long>("SELECT selling_price FROM product_lot WHERE id=@lot", new { lot }).ShouldBe(140_000L);
    }

    [Fact]
    public async Task SaleLine_CopiesItsLotsCostSideTax_AgainWhenTheLotChanges_AndKeepsTheLabelSavedAtCompletion()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long first = await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(new Money(100)) with
        { Quantity = new Quantity(5) }, "Stock", OperationGuard.NewId(), ct);
        long second = await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(new Money(100)) with
        { Quantity = new Quantity(5), TaxRateId = fixture.Gst18 }, "Stock", OperationGuard.NewId(), ct);
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One,
            preferredLotId: first, cancellationToken: ct);
        SaleLine line = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single();
        line.UnitCostTax.ShouldBe(new Money(5));
        line.UnitCostInclTax.ShouldBe(new Money(105));
        line.PurchasePrice.ShouldBe(new Money(100));

        await fixture.Sales.ChangeLineLotAsync(saleId, line.Id, second, ct);
        line = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single();
        line.UnitCostTax.ShouldBe(new Money(18));
        line.UnitCostInclTax.ShouldBe(new Money(118));

        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(150))], OperationGuard.NewId()), ct);
        line = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single();
        // The sale side is the product's rate; 150 includes 5%.
        line.TaxLabel.ShouldBe("GST 5%");
        line.Taxable.ShouldBe(new Money(142.857m));
        line.TaxChargedShown.ShouldBe(7.14m);
        await fixture.Catalog.UpdateTaxRateAsync(fixture.Gst5, "GST five", 5m, true, ct);
        (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().TaxLabel.ShouldBe("GST 5%");

        CompletedReturn returned = await fixture.Sales.ReturnAsync(new(saleId, fixture.Today, "Changed mind",
            RefundMode.Cash, [new(line.Id, Quantity.One, true)], OperationGuard.NewId()), ct);
        using SqliteConnection db = fixture.Factory.Open();
        db.ExecuteScalar<string>("SELECT tax_label_snapshot FROM sale_return_line WHERE sale_return_id=@id",
            new { id = returned.ReturnId }).ShouldBe("GST 5%");
    }

    [Theory]
    [InlineData("COMPOSITION")]
    [InlineData("NONE")]
    public async Task BillsWithoutTax_SaveNoLabelAndChargeNoTax(string mode)
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(new Money(100)), "Stock", OperationGuard.NewId(), ct);
        await fixture.Settings.SaveAsync(new Dictionary<string, string> { ["tax.mode"] = mode }, ct);
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(150))], OperationGuard.NewId()), ct);
        SaleLine line = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single();
        line.TaxLabel.ShouldBeNull();
        line.Tax.ShouldBe(Money.Zero);
        line.TaxChargedShown.ShouldBe(0m);
        // The cost side still shows what the lot was bought at.
        line.UnitCostInclTax.ShouldBe(new Money(105));
    }

    [Fact]
    public async Task Label_IsBuiltFromTheLinesPercent_WhenTheRatesPercentChangedSinceTheLineWasAdded()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(new Money(100)), "Stock", OperationGuard.NewId(), ct);
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        await fixture.Catalog.UpdateTaxRateAsync(fixture.Gst5, "GST 5%", 12m, true, ct);
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(150))], OperationGuard.NewId()), ct);
        SaleLine line = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single();
        line.TaxRate.ShouldBe(new Percent(5));
        line.TaxLabel.ShouldBe("Tax 5%");
    }

    [Fact]
    public async Task Prices_AlwaysIncludeTax_AndNoSellingPriceExceedsMrp()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using (SqliteConnection db = fixture.Factory.Open())
        {
            Should.Throw<SqliteException>(() => db.Execute("""
                INSERT INTO product(sku,name,category_id,uom_id,price_includes_tax) VALUES ('EXCL','Excl',@c,@u,0)
                """, new { c = fixture.CategoryId, u = fixture.UnitId })).Message.ShouldContain("must include tax");
            Should.Throw<SqliteException>(() => db.Execute(
                "UPDATE product SET price_includes_tax=0 WHERE id=@id", new { id = fixture.ProductId }))
                .Message.ShouldContain("must include tax");
        }
        // Lots, purchase lines, typed bill prices and the import all compare the price with MRP.
        (await Should.ThrowAsync<DomainException>(() => fixture.Inventory.CreateDirectLotAsync(
            fixture.Opening(new Money(100)) with { SellingPrice = new Money(105), Mrp = new Money(104) },
            "Over MRP", OperationGuard.NewId(), ct))).Code.ShouldBe(ErrorCode.PriceAboveMrp);
        (await Should.ThrowAsync<DomainException>(() => fixture.Inventory.ImportOpeningStockAsync(
            [fixture.Opening(new Money(100)) with { SellingPrice = new Money(105), Mrp = new Money(104) }],
            OperationGuard.NewId(), ct))).Code.ShouldBe(ErrorCode.PriceAboveMrp);
        (await Should.ThrowAsync<DomainException>(() => fixture.Purchasing.SaveDraftAsync(null,
            fixture.Draft("OVER-MRP", Money.Zero, TaxFixture.Line(fixture.ProductId, Quantity.One, new Money(90),
                fixture.Gst5) with
            { SellingPrice = new Money(105), Mrp = new Money(104) }), ct)))
            .Code.ShouldBe(ErrorCode.PriceAboveMrp);
        await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(new Money(100)), "Stock", OperationGuard.NewId(), ct);
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        SaleLine line = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single();
        (await Should.ThrowAsync<DomainException>(() => fixture.Sales.SetLinePriceAsync(saleId, line.Id,
            new Money(160.5m), ct))).Code.ShouldBe(ErrorCode.PriceAboveMrp);
        await fixture.Sales.SetLinePriceAsync(saleId, line.Id, new Money(160), ct);
        // 160 is the price the customer pays, tax included.
        (await fixture.Sales.GetAsync(saleId, ct)).Totals.GrandTotal.ShouldBe(new Money(160));
    }

    [Fact]
    public async Task HeldLineSavedExcludingTax_PricesAsItWasAdded_AndResumesAtTheLotsInclusivePrice()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(new Money(100)), "Stock", OperationGuard.NewId(), ct);
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, cancellationToken: ct);
        long lineId = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().Id;
        // A line added before every price included tax: 150 plus 5%.
        using (SqliteConnection db = fixture.Factory.Open())
            db.Execute("UPDATE sale_line SET price_includes_tax=0 WHERE id=@lineId", new { lineId });
        await fixture.Sales.SetLineQuantityAsync(saleId, lineId, Quantity.One, ct);
        (await fixture.Sales.GetAsync(saleId, ct)).Totals.GrandTotal.ShouldBe(new Money(158));
        await fixture.Sales.HoldAsync(saleId, "Across the upgrade", ct);

        await fixture.Sales.ResumeAsync(saleId, ct);

        (await fixture.Sales.GetAsync(saleId, ct)).Totals.GrandTotal.ShouldBe(new Money(150));
        using SqliteConnection check = fixture.Factory.Open();
        check.ExecuteScalar<long>("SELECT price_includes_tax FROM sale_line WHERE id=@lineId", new { lineId }).ShouldBe(1L);
    }

    [Fact]
    public async Task ReturnOfADeletedProduct_RecreatesItsLotWithTheSaleLinesTax_AndAnInclusivePrice()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long lot = await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(new Money(100)) with
        { Quantity = new Quantity(2), SellingPrice = new Money(100), Mrp = new Money(103) },
            "Stock", OperationGuard.NewId(), ct);
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(2), cancellationToken: ct);
        long lineId = (await fixture.Sales.GetAsync(saleId, ct)).Lines.Single().Id;
        // Sold as excluding tax: 100 + 5% = 105 each, which the returned stock's price is capped from.
        using (SqliteConnection db = fixture.Factory.Open())
            db.Execute("UPDATE sale_line SET price_includes_tax=0 WHERE id=@lineId", new { lineId });
        await fixture.Sales.SetLineQuantityAsync(saleId, lineId, new Quantity(2), ct);
        Money total = (await fixture.Sales.GetAsync(saleId, ct)).Totals.GrandTotal;
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, total)], OperationGuard.NewId()), ct);
        using (SqliteConnection db = fixture.Factory.Open())
        {
            long version = db.ExecuteScalar<long>("SELECT detail_version FROM product_lot WHERE id=@lot", new { lot });
            await fixture.Inventory.DeleteZeroLotAsync(lot, version, ct);
            // The product's other lots are gone too, so it can be deleted.
            db.Execute("DELETE FROM product_lot WHERE product_id=@id AND qty_on_hand=0", new { id = fixture.ProductId });
        }
        (await fixture.Catalog.RemoveAsync(fixture.ProductId, ct)).ShouldBe(ProductRemoval.Deleted);

        await fixture.Sales.ReturnAsync(new(saleId, fixture.Today, "Returned", RefundMode.Cash,
            [new(lineId, Quantity.One, true)], OperationGuard.NewId()), ct);

        using SqliteConnection check = fixture.Factory.Open();
        check.QuerySingle<(string Label, long Cost, long Tax, long Incl, long Price, long Mrp)>("""
            SELECT tax_label_snapshot,unit_cost,unit_tax,unit_cost_incl_tax,selling_price,mrp
            FROM product_lot WHERE origin_kind='CUSTOMER_RETURN'
            """).ShouldBe(("GST 5%", 100_000L, 5_000L, 105_000L, 103_000L, 103_000L));
        check.ExecuteScalar<long>("SELECT price_includes_tax FROM product WHERE sku='TAX-1'").ShouldBe(1L);
    }

    [Fact]
    public async Task BookedCost_Cogs_PnlAndGstSummary_AreThoseBeforeTaxSnapshots()
    {
        using var fixture = new TaxFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long id = await fixture.Purchasing.SaveDraftAsync(null, fixture.Draft("UNCHANGED", new Money(60),
            TaxFixture.Line(fixture.ProductId, new Quantity(10), new Money(100), fixture.Gst5) with
            { FreeQuantity = new Quantity(2), Discount = new Money(50) },
            TaxFixture.Line(fixture.ProductId, Quantity.One, new Money(950), fixture.Gst5)), ct);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(id, OperationGuard.NewId(), ct);
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(3),
            preferredLotId: posted.LotIds[0], cancellationToken: ct);
        CompletedSale sale = await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(450))],
            OperationGuard.NewId()), ct);

        // Regular books the purchase price before tax: (950 + 30) ÷ 12 = 81.667 a piece.
        using (SqliteConnection db = fixture.Factory.Open())
        {
            db.QuerySingle<(long Unit, long Cost)>("SELECT unit_cost,cost_amount FROM sale_line WHERE sale_id=@saleId",
                new { saleId }).ShouldBe((81_667L, 245_001L));
            db.ExecuteScalar<long>("SELECT cost FROM pnl_day WHERE day=@day", new { day = "2026-09-26" }).ShouldBe(245_001L);
            db.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='COGS'").ShouldBe(245_001L);
        }
        var reports = new ReportService(fixture.Database, fixture.Settings, fixture.Clock);
        TabularReport gst = await reports.GetReportAsync(ReportKind.GstSummary,
            new(From: fixture.Today, To: fixture.Today), ct);
        ReportRow inward = gst.Rows.Single(row => (string)row.Values["Direction"]! == "INWARD");
        inward.Values["Taxable"].ShouldBe(1_900_000L);
        inward.Values["Tax"].ShouldBe(95_000L);
        ReportRow outward = gst.Rows.Single(row => (string)row.Values["Direction"]! == "OUTWARD");
        outward.Values["Taxable"].ShouldBe(sale.Totals.Taxable.Scaled);
        outward.Values["Tax"].ShouldBe(sale.Totals.Tax.Scaled);
    }

    private sealed record LotTaxRow(long? RateId, string? Label, long Pct, long Cost, long Tax, long Incl);

    private sealed class TaxFixture : IDisposable
    {
        public TaxFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-tax-{Guid.NewGuid():N}");
            var paths = new AppPaths(new SettingsBootstrap(Root));
            Factory = new SqliteConnectionFactory(paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            Clock = new FixedClock(new DateTime(2026, 9, 26, 11, 0, 0));
            Periods = new PeriodGuard(Settings, Clock);
            Ledger = new LotQuantities(Settings);
            var numbers = new DocNumberService(Settings);
            Catalog = new CatalogService(Database, new AuditLog(), Settings, Clock);
            Inventory = new InventoryService(Database, Ledger, numbers, Periods, new AuditLog());
            Purchasing = new PurchasingService(Database, Ledger, numbers, Periods, Settings, new AuditLog());
            Sales = new SalesService(Database, Ledger, numbers, Periods, Settings, new AuditLog(), Clock);
            MoneyOut = new MoneyOutService(Database, Ledger, numbers, Periods, new AuditLog());

            using SqliteConnection connection = Factory.Open();
            CategoryId = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Food') RETURNING id");
            UnitId = connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
            Gst5 = connection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=5000");
            Gst18 = connection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=18000");
            ProductId = connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,hsn_code,tax_rate_id,track_expiry,
                    default_selling_price,default_mrp)
                VALUES ('TAX-1','Tax product',@CategoryId,@UnitId,'1006',@Gst5,0,150000,160000) RETURNING id
                """, new { CategoryId, UnitId, Gst5 });
            VendorId = Purchasing.CreateVendorAsync(new("Supplier", null, "9999999999", null, null, null, "Ahmedabad",
                "Gujarat", "380001", "24ABCDE1234F1Z5", null, null, null, 30, Money.Zero, null)).GetAwaiter().GetResult();
        }

        public string Root { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public FixedClock Clock { get; }
        public PeriodGuard Periods { get; }
        public LotQuantities Ledger { get; }
        public CatalogService Catalog { get; }
        public InventoryService Inventory { get; }
        public PurchasingService Purchasing { get; }
        public SalesService Sales { get; }
        public MoneyOutService MoneyOut { get; }
        public long CategoryId { get; }
        public long UnitId { get; }
        public long Gst5 { get; }
        public long Gst18 { get; }
        public long ProductId { get; }
        public long VendorId { get; }
        public DateOnly Today => Clock.BusinessDate;

        public static PurchaseLineInput Line(long productId, Quantity quantity, Money rate, long? taxRateId) =>
            new(productId, null, null, null, quantity, Quantity.Zero, rate, Money.Zero, taxRateId,
                new Money(160), new Money(150));

        public PurchaseDraftInput Draft(string invoice, Money otherCharges, params PurchaseLineInput[] lines) =>
            new(VendorId, invoice, Today, Today, null, otherCharges, null, lines);

        public OpeningStockLine Opening(Money purchasePrice) =>
            new(ProductId, new Quantity(10), purchasePrice, new Money(150), new Money(160), null, null, Today);

        public long RateId(string name)
        {
            using SqliteConnection db = Factory.Open();
            return db.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE name=@name", new { name });
        }

        public long LatestLot()
        {
            using SqliteConnection db = Factory.Open();
            return db.ExecuteScalar<long>("SELECT MAX(id) FROM product_lot");
        }

        public LotTaxRow LotTax(long lotId)
        {
            using SqliteConnection db = Factory.Open();
            return db.QuerySingle<LotTaxRow>("""
                SELECT tax_rate_id_snapshot AS RateId,tax_label_snapshot AS Label,tax_rate_pct_snapshot AS Pct,
                       unit_cost AS Cost,unit_tax AS Tax,unit_cost_incl_tax AS Incl
                FROM product_lot WHERE id=@lotId
                """, new { lotId });
        }

        // A reviewed count that finds more than the lot holds starts a new lot for the surplus.
        public Task<long> CountAsync(long lotId, Quantity surplus, CancellationToken ct) =>
            Database.WriteAsync(tx =>
            {
                (long onHand, long version) = tx.Connection.QuerySingle<(long, long)>(
                    "SELECT qty_on_hand,quantity_version FROM product_lot WHERE id=@lotId", new { lotId }, tx.Transaction);
                return new DirectStockEngine(new DocNumberService(Settings)).ReviewCount(tx, lotId, version, onHand,
                    onHand + surplus.Scaled, Today, "Recount", OperationGuard.NewId());
            }, ct);

        public void Dispose()
        {
            Settings.Dispose();
            Database.Dispose();
            SqliteConnectionFactory.ClearPoolForDataRoot(Root);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime Now => now;
        public DateOnly BusinessDate => DateOnly.FromDateTime(now);
    }
}
