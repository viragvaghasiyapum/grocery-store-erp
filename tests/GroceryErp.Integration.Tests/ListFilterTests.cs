using System.Globalization;
using Dapper;
using GroceryErp.Catalog;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Purchasing;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

/// <summary>
/// Phase 7 list filters: every shape returns exactly the matching rows across keyset pages,
/// agrees with its count and ID resolution, and the executed SQL uses the intended index.
/// </summary>
public sealed class ListFilterTests
{
    [Fact]
    public async Task ProductFilters_PagesCountAndResolveAgreeForEveryShape()
    {
        using var fixture = new FilterFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        ProductSearchFilter Filter(string? query = null) =>
            new(query, Limit: 4, ExpiryAsOf: fixture.Today) { ExactFirst = true };
        var shapes = new (string Name, ProductSearchFilter Filter, Func<Seed, bool> Expected)[]
        {
            ("empty", Filter(), static seed => seed.Active),
            ("broad", Filter("rice"), static seed => seed.Active && seed.Name.Contains("rice", StringComparison.OrdinalIgnoreCase)),
            ("rare", Filter("Kesar Mango Pulp"), static seed => seed.Active && seed.Name == "Kesar Mango Pulp"),
            ("no match", Filter("zqxjv"), static _ => false),
            ("gujarati", Filter("ચોખા"), static seed => seed.Active && seed.Local?.Contains("ચોખા", StringComparison.Ordinal) == true),
            ("short terms", Filter("Basmati rice 5 kg"), static seed => seed.Active && seed.Name.StartsWith("Basmati rice 5 kg", StringComparison.Ordinal)),
            ("internal", Filter() with { ProductClass = "INTERNAL" }, static seed => seed.Active && seed.Internal),
            ("archived", Filter() with { ArchivedOnly = true }, static seed => !seed.Active),
            ("any status", Filter() with { IncludeArchived = true }, static _ => true),
            ("category", Filter() with { CategoryId = fixture.Grains }, seed => seed.Active && seed.Category == fixture.Grains),
            ("brand", Filter() with { BrandId = fixture.Brand }, static seed => seed.Active && seed.Branded),
            ("has stock", Filter() with { HasStockOnly = true }, static seed => seed.Active && seed.Stock > 0),
            ("expired", Filter() with { ExpiryFilter = "EXPIRED" }, static seed => seed.Active && seed.Expired),
            ("expiring", Filter() with { ExpiryFilter = "EXPIRING" }, static seed => seed.Active && seed.Expiring),
            ("vendor", Filter() with { VendorId = fixture.VendorId }, static seed => seed.Active && seed.FromVendor),
            ("no barcode", Filter() with { NoBarcodeOnly = true }, static seed => seed.Active && seed.Barcode is null),
            ("missing local", Filter() with { MissingLocalNameOnly = true }, static seed => seed.Active && seed.Local is null),
            ("loose", Filter() with { LooseOnly = true }, static seed => seed.Active && seed.Loose),
            ("category and broad", Filter("rice") with { CategoryId = fixture.Grains, HasStockOnly = true },
                seed => seed.Active && seed.Category == fixture.Grains && seed.Stock > 0 &&
                        seed.Name.Contains("rice", StringComparison.OrdinalIgnoreCase)),
        };
        foreach ((string name, ProductSearchFilter filter, Func<Seed, bool> expected) in shapes)
        {
            long[] want = fixture.Seeds.Where(expected).OrderBy(static seed => seed.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static seed => seed.Id).Select(static seed => seed.Id).ToArray();
            var pages = new List<long>();
            ProductSearchPage page = await fixture.Catalog.SearchPageAsync(filter, cancellationToken: ct);
            pages.AddRange(page.Items.Select(static item => item.Id));
            while (page.HasMore)
            {
                page = await fixture.Catalog.SearchPageAsync(filter, page.NextName, page.NextId, ct);
                pages.AddRange(page.Items.Select(static item => item.Id));
            }
            pages.ShouldBe(want, ignoreOrder: true, customMessage: $"{name}: pages");
            pages.Distinct().Count().ShouldBe(pages.Count, $"{name}: a row repeated across pages");
            (await fixture.Catalog.CountAsync(filter, ct)).ShouldBe(want.Length, $"{name}: count");
            (await fixture.Catalog.ResolveIdsAsync(filter, cancellationToken: ct)).ShouldBe(want, customMessage: $"{name}: resolve");
        }

        // An exact SKU or barcode leads the first page even though its name sorts later.
        Seed tail = fixture.Seeds.Where(static seed => seed.Active).OrderBy(static seed => seed.Name, StringComparer.OrdinalIgnoreCase).Last();
        (await fixture.Catalog.SearchPageAsync(Filter(tail.Sku), cancellationToken: ct)).Items[0].Id.ShouldBe(tail.Id);
        Seed scanned = fixture.Seeds.First(static seed => seed.Active && seed.Barcode is not null);
        (await fixture.Catalog.SearchPageAsync(Filter(scanned.Barcode), cancellationToken: ct)).Items[0].Id.ShouldBe(scanned.Id);
    }

    [Fact]
    public async Task ProductSearchSql_UsesTheIndexesEachShapeNeeds()
    {
        using var fixture = new FilterFixture(commonProducts: 220);
        CancellationToken ct = TestContext.Current.CancellationToken;
        ProductSearchFilter Filter(string? query = null) => new(query, Limit: 8, ExpiryAsOf: fixture.Today);

        // A common term walks names in order; a rare one starts from its matches.
        (await fixture.PlansOf(() => fixture.Catalog.SearchPageAsync(Filter("common"), cancellationToken: ct), "visible"))
            .ShouldContain(static plan => plan.Contains("USING INDEX ix_product_name", StringComparison.Ordinal));
        (await fixture.PlansOf(() => fixture.Catalog.SearchPageAsync(Filter("Kesar Mango"), cancellationToken: ct), "visible"))
            .ShouldContain(static plan => plan.Contains("VIRTUAL TABLE", StringComparison.Ordinal));
        // Short words filter the full-text candidates instead of scanning every product.
        string[] shortTerms = await fixture.PlansOf(() => fixture.Catalog.SearchPageAsync(Filter("Basmati rice 5 kg"),
            cancellationToken: ct), "visible");
        shortTerms.ShouldContain(static plan => plan.Contains("VIRTUAL TABLE", StringComparison.Ordinal));
        (await fixture.Catalog.SearchPageAsync(Filter("Basmati rice 5 kg"), cancellationToken: ct)).Items
            .Select(static item => item.Name).ShouldAllBe(static name => name.StartsWith("Basmati rice 5 kg", StringComparison.Ordinal));
        // The small partial indexes answer the internal-use and archived views.
        (await fixture.PlansOf(() => fixture.Catalog.SearchPageAsync(Filter() with { ProductClass = "INTERNAL" },
            cancellationToken: ct), "visible")).ShouldContain(static plan => plan.Contains("ix_product_internal_name", StringComparison.Ordinal));
        (await fixture.PlansOf(() => fixture.Catalog.SearchPageAsync(Filter() with { ArchivedOnly = true },
            cancellationToken: ct), "visible")).ShouldContain(static plan => plan.Contains("ix_product_archived_name", StringComparison.Ordinal));
        // A deep page seeks the name index from its cursor.
        ProductSearchPage first = await fixture.Catalog.SearchPageAsync(Filter(), cancellationToken: ct);
        (await fixture.PlansOf(() => fixture.Catalog.SearchPageAsync(Filter(), first.NextName, first.NextId, ct), "visible"))
            .ShouldContain(static plan => plan.Contains("ix_product_name (name>?)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LotFilters_StayWithinTheProductIndexAndAgreeWithCount()
    {
        using var fixture = new FilterFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long product = fixture.LotProduct;
        LotFilter Lots() => new() { ProductId = product, AsOf = fixture.Today };
        var shapes = new (string Name, LotFilter Filter, int Expected)[]
        {
            ("all", Lots(), 7),
            ("available", Lots() with { Status = "AVAILABLE" }, 4),
            ("zero", Lots() with { Status = "ZERO" }, 2),
            ("blocked", Lots() with { Status = "BLOCKED" }, 1),
            ("expired", Lots() with { Expiry = "EXPIRED" }, 2),
            ("expiring", Lots() with { Expiry = "EXPIRING" }, 1),
            ("no expiry", Lots() with { Expiry = "NONE" }, 1),
            ("direct", Lots() with { Source = "DIRECT" }, 7),
            ("price", Lots() with { MinPrice = new Money(25), MaxPrice = new Money(35) }, 3),
            ("batch", Lots() with { Query = "B-2" }, 1),
            ("no match", Lots() with { Query = "zqxjv" }, 0),
        };
        foreach ((string name, LotFilter filter, int expected) in shapes)
        {
            var ids = new List<long>();
            LotPage page = await fixture.Inventory.GetLotPageAsync(filter, null, 2, ct);
            ids.AddRange(page.Items.Select(static lot => lot.Id));
            while (page.HasMore)
            {
                page = await fixture.Inventory.GetLotPageAsync(filter, page.NextId, 2, ct);
                ids.AddRange(page.Items.Select(static lot => lot.Id));
            }
            ids.Count.ShouldBe(expected, name);
            ids.SequenceEqual(ids.OrderByDescending(static id => id)).ShouldBeTrue($"{name}: newest first");
            (await fixture.Inventory.CountLotsAsync(filter, ct)).ShouldBe(expected, name);
            (await fixture.Inventory.ResolveLotIdsAsync(filter, cancellationToken: ct)).ShouldBe(ids, customMessage: name);
            // One product's lots never scan the whole lot table.
            string[] plans = await fixture.PlansOf(() => fixture.Inventory.GetLotPageAsync(filter, null, 2, ct), "product_lot l");
            plans.ShouldNotContain(static plan => plan.Contains("SCAN l", StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public async Task PurchaseFilters_MatchAndUseTheirIndexes()
    {
        using var fixture = new FilterFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        PurchasePage all = await fixture.Purchasing.GetPurchasePageAsync(new PurchaseFilter(), null, null, 100, ct);
        all.Items.Count.ShouldBe(5);
        async Task<long[]> Ids(PurchaseFilter filter)
        {
            var ids = new List<long>();
            PurchasePage page = await fixture.Purchasing.GetPurchasePageAsync(filter, null, null, 2, ct);
            ids.AddRange(page.Items.Select(static item => item.Id));
            while (page.HasMore)
            {
                page = await fixture.Purchasing.GetPurchasePageAsync(filter, page.NextDate, page.NextId, 2, ct);
                ids.AddRange(page.Items.Select(static item => item.Id));
            }
            (await fixture.Purchasing.CountPurchasesAsync(filter, ct)).ShouldBe(ids.Count);
            (await fixture.Purchasing.ResolvePurchaseIdsAsync(filter, cancellationToken: ct)).ShouldBe(ids);
            return [.. ids];
        }
        (await Ids(new PurchaseFilter())).ShouldBe(all.Items.Select(static item => item.Id).ToArray());
        (await Ids(new PurchaseFilter { Status = "DRAFT" })).ShouldBe([fixture.DraftPurchase]);
        (await Ids(new PurchaseFilter { Status = "CANCELLED" })).ShouldBe([fixture.VoidPurchase]);
        (await Ids(new PurchaseFilter { LineKind = "EXPENSE" })).ShouldBe([fixture.ExpensePurchase]);
        (await Ids(new PurchaseFilter { LineKind = "PRODUCT", Status = "POSTED" })).Length.ShouldBe(2);
        (await Ids(new PurchaseFilter { DocumentQuery = fixture.ExpenseDocument.ToLowerInvariant() })).ShouldBe([fixture.ExpensePurchase]);
        (await Ids(new PurchaseFilter { DocumentQuery = "inv-fil" })).Length.ShouldBe(5);
        (await Ids(new PurchaseFilter { DocumentQuery = "NO-SUCH-BILL" })).ShouldBeEmpty();
        // Both mango products: the posted purchases and the void one still list their lines.
        (await Ids(new PurchaseFilter { ProductQuery = "Mango" })).Length.ShouldBe(3);
        (await Ids(new PurchaseFilter { ProductQuery = "ચોખા" })).ShouldBeEmpty();
        (await Ids(new PurchaseFilter { ProductQuery = "zqxjv" })).ShouldBeEmpty();
        (await Ids(new PurchaseFilter { VendorId = fixture.VendorId, Status = "POSTED", ReturnableOnly = true })).Length.ShouldBe(2);

        (await fixture.PlansOf(() => fixture.Purchasing.GetPurchasePageAsync(new PurchaseFilter { Status = "DRAFT" }, null, null, 2, ct),
            "FROM purchase p")).ShouldContain(static plan => plan.Contains("ix_purchase_status_date_v30", StringComparison.Ordinal));
        (await fixture.PlansOf(() => fixture.Purchasing.GetPurchasePageAsync(
            new PurchaseFilter { VendorId = fixture.VendorId, Status = "POSTED" }, null, null, 2, ct), "FROM purchase p"))
            .ShouldContain(static plan => plan.Contains("ix_purchase_vendor_posted_date", StringComparison.Ordinal));
        (await fixture.PlansOf(() => fixture.Purchasing.GetPurchasePageAsync(new PurchaseFilter { DocumentQuery = "INV-FIL-1" },
            null, null, 2, ct), "FROM purchase p")).ShouldContain(static plan => plan.Contains("ix_purchase_invoice_nocase", StringComparison.Ordinal));
        (await fixture.PlansOf(() => fixture.Purchasing.GetPurchasePageAsync(new PurchaseFilter { ProductQuery = "Mango" },
            null, null, 2, ct), "FROM purchase p")).ShouldContain(static plan => plan.Contains("VIRTUAL TABLE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiredWriteOffPreview_FindsActiveAndBlockedExpiredLots()
    {
        using var fixture = new FilterFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        IReadOnlyList<ExpiredWriteOffLot> lots = await fixture.MoneyOut.PreviewExpiredWriteOffAsync(
            [fixture.LotProduct, fixture.Seeds[0].Id], fixture.Today, ct);
        // The lot product holds one active and one blocked expired lot with stock.
        lots.Count.ShouldBe(2);
        lots.Select(static lot => lot.LotCode).ShouldBe(["LOT-EXPIRED", "LOT-EXPIRED-BLOCKED"], ignoreOrder: true);
        lots.Sum(static lot => lot.Quantity.Scaled).ShouldBe(new Quantity(5).Scaled + new Quantity(1).Scaled);
    }

    // Stock, vendor and expiry come from the store's lots after every purchase is posted, so they
    // check the service's projections and subqueries against the plain lot rows.
    private sealed record Seed(long Id, string Sku, string Name, string? Local, long Category, bool Branded, bool Internal,
        bool Active, bool Loose, string? Barcode)
    {
        public long Stock { get; init; }
        public bool Expired { get; init; }
        public bool Expiring { get; init; }
        public bool FromVendor { get; init; }
    }

    private sealed class FilterFixture : IDisposable
    {
        public FilterFixture(int commonProducts = 0)
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-filters-{Guid.NewGuid():N}");
            var paths = new AppPaths(new SettingsBootstrap(Root));
            Factory = new SqliteConnectionFactory(paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            var clock = new FixedClock(Today);
            var numbers = new DocNumberService(Settings);
            var periods = new PeriodGuard(Settings, clock);
            var ledger = new LotQuantities(Settings);
            Catalog = new CatalogService(Database, new AuditLog(), Settings);
            Inventory = new InventoryService(Database, ledger, numbers, periods, new AuditLog());
            Purchasing = new PurchasingService(Database, ledger, numbers, periods, Settings, new AuditLog());
            MoneyOut = new MoneyOutService(Database, ledger, numbers, periods, new AuditLog());
            VendorId = Purchasing.CreateVendorAsync(new("Filter Supplier", null, "9999999999", null, null, null, "Ahmedabad",
                "Gujarat", "380001", null, null, null, null, 30, Money.Zero, null)).GetAwaiter().GetResult();

            using (var connection = Factory.Open())
            {
                Grains = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Grains') RETURNING id");
                long fruit = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Fruit') RETURNING id");
                Brand = connection.ExecuteScalar<long>("INSERT INTO brand(name) VALUES ('Kohinoor') RETURNING id");
                long pieces = connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
                long kilograms = connection.ExecuteScalar<long>("SELECT id FROM uom WHERE allow_fraction=1 ORDER BY id LIMIT 1");
                long tax = connection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=5000");
                string date(int days) => Today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Seed Add(string name, string? local = null, long? category = null, bool branded = false, bool @internal = false,
                    bool active = true, bool loose = false, long stock = 0, string? expiry = null, bool fromVendor = false,
                    bool barcode = true)
                {
                    string sku = "F" + (Seeds.Count + 1).ToString("000", CultureInfo.InvariantCulture);
                    long id = connection.ExecuteScalar<long>("""
                        INSERT INTO product(sku,name,name_local,category_id,brand_id,uom_id,tax_rate_id,is_loose,
                                            product_class,default_selling_price)
                        VALUES (@sku,@name,@local,@category,@brand,@uom,@tax,@loose,@productClass,30000) RETURNING id
                        """, new
                    {
                        sku,
                        name,
                        local,
                        category = category ?? fruit,
                        brand = branded ? Brand : (long?)null,
                        uom = loose ? kilograms : pieces,
                        tax,
                        loose = loose ? 1 : 0,
                        productClass = @internal ? "INTERNAL" : "SALEABLE",
                    });
                    string? code = barcode ? "89" + id.ToString("0000000000", CultureInfo.InvariantCulture) : null;
                    if (code is not null)
                        connection.Execute("INSERT INTO product_barcode(barcode,product_id,is_primary) VALUES (@code,@id,1)", new { code, id });
                    if (stock > 0)
                    {
                        string? expiryDate = expiry switch { "EXPIRED" => date(-5), "EXPIRING" => date(10), _ => date(400) };
                        long lot = connection.ExecuteScalar<long>("""
                            INSERT INTO product_lot(product_id,lot_code,batch_no,vendor_id,received_date,expiry_date,
                                                    unit_cost,selling_price,qty_received)
                            VALUES (@id,'L-'||@sku,'B-'||@sku,@vendor,@received,@expiryDate,20000,30000,@stock) RETURNING id
                            """, new { id, sku, vendor = fromVendor ? VendorId : (long?)null, received = date(-30), expiryDate, stock });
                        connection.Execute("UPDATE product_lot SET qty_on_hand=qty_received WHERE id=@lot", new { lot });
                    }
                    if (!active) connection.Execute("UPDATE product SET is_active=0 WHERE id=@id", new { id });
                    var seed = new Seed(id, sku, name, local, category ?? fruit, branded, @internal, active, loose, code);
                    Seeds.Add(seed);
                    return seed;
                }
                Add("Basmati rice 5 kg", "બાસમતી ચોખા", Grains, branded: true, stock: 5000, fromVendor: true);
                Add("Basmati rice 1 kg", "બાસમતી ચોખા", Grains, branded: true, stock: 2000, expiry: "EXPIRING");
                Add("Brown rice 1 kg", "બ્રાઉન ચોખા", Grains, stock: 1000, expiry: "EXPIRED");
                Add("Rice flour 500 g", category: Grains, barcode: false);
                Add("Puffed rice", "મમરા", Grains, loose: true, stock: 3500, fromVendor: true);
                Add("Kesar Mango Pulp", "કેસર કેરી", stock: 1000, fromVendor: true);
                Add("Alphonso Mango", "હાફૂસ કેરી", barcode: false);
                Add("Banana chips", stock: 4000, expiry: "EXPIRED");
                Add("Shop floor cleaner", @internal: true, stock: 2000);
                Add("Old rice cracker", active: false);
                Add("Zesty lime pickle", "લીંબુ અથાણું", stock: 1000, expiry: "EXPIRING");
                for (int index = 0; index < commonProducts; index++)
                    Add($"Common item {index:000}", category: Grains, barcode: false);

                // One product with seven lots covering every lot filter.
                LotProduct = Add("Lot filter product", barcode: false).Id;
                void Lot(string code, string batch, string? expiry, long received, long onHand, long price, bool active = true)
                {
                    long lot = connection.ExecuteScalar<long>("""
                        INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,unit_cost,selling_price,qty_received)
                        VALUES (@LotProduct,@code,@batch,@receivedDate,@expiry,10000,@price,@received) RETURNING id
                        """, new { LotProduct, code, batch, receivedDate = date(-60), expiry, received, price });
                    connection.Execute("UPDATE product_lot SET qty_on_hand=@onHand, is_active=@active WHERE id=@lot",
                        new { lot, onHand, active = active ? 1 : 0 });
                }
                Lot("LOT-FRESH", "B-1", date(200), 10000, 10000, 30000);
                Lot("LOT-EXPIRING", "B-2", date(5), 10000, 10000, 40000);
                Lot("LOT-EXPIRED", "B-3", date(-10), 10000, 5000, 20000);
                Lot("LOT-EXPIRED-BLOCKED", "B-4", date(-3), 10000, 1000, 28000, active: false);
                Lot("LOT-NO-EXPIRY", "B-5", null, 10000, 3000, 32000);
                Lot("LOT-EMPTY", "B-6", date(100), 10000, 0, 50000);
                Lot("LOT-EMPTY-OLD", "B-7", date(-100), 10000, 0, 10000);
            }

            // Purchases: three posted product, internal and expense purchases, a draft and a void.
            long mango = Seeds.Single(static seed => seed.Name == "Kesar Mango Pulp").Id;
            long alphonso = Seeds.Single(static seed => seed.Name == "Alphonso Mango").Id;
            long cleaner = Seeds.Single(static seed => seed.Internal).Id;
            long expenseCategory;
            using (var connection = Factory.Open())
                expenseCategory = connection.ExecuteScalar<long>("SELECT id FROM expense_category ORDER BY id LIMIT 1");
            PurchaseLineInput Line(long product, string kind = "PRODUCT") => new(product, "PB-" + product, null, Today.AddDays(300),
                new Quantity(4), Quantity.Zero, new Money(20), Money.Zero, new Percent(5), new Money(40), new Money(30))
            { LineKind = kind, ExpenseCategoryId = kind == "EXPENSE" ? expenseCategory : null };
            long Post(string invoice, int daysAgo, params PurchaseLineInput[] lines)
            {
                long draft = Purchasing.SaveDraftAsync(null, new PurchaseDraftInput(VendorId, invoice, Today.AddDays(-daysAgo),
                    Today.AddDays(-daysAgo), null, Money.Zero, null, lines)).GetAwaiter().GetResult();
                return Purchasing.PostAsync(draft, OperationGuard.NewId()).GetAwaiter().GetResult().Id;
            }
            Post("INV-FIL-1", 9, Line(mango));
            Post("INV-FIL-2", 7, Line(alphonso), Line(mango));
            VoidPurchase = Post("INV-FIL-3", 5, Line(alphonso));
            Purchasing.CancelAsync(VoidPurchase, Today, OperationGuard.NewId()).GetAwaiter().GetResult();
            ExpensePurchase = Post("INV-FIL-4", 3, Line(cleaner, "EXPENSE"));
            using (var connection = Factory.Open())
                ExpenseDocument = connection.ExecuteScalar<string>("SELECT doc_no FROM purchase WHERE id=@ExpensePurchase",
                    new { ExpensePurchase })!;
            DraftPurchase = Purchasing.SaveDraftAsync(null, new PurchaseDraftInput(VendorId, "INV-FIL-5", Today, Today, null,
                Money.Zero, null, [Line(cleaner, "INTERNAL")])).GetAwaiter().GetResult();

            using (var connection = Factory.Open())
            {
                string today = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string soon = Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                for (int index = 0; index < Seeds.Count; index++)
                {
                    long id = Seeds[index].Id;
                    Seeds[index] = Seeds[index] with
                    {
                        Stock = connection.ExecuteScalar<long>("SELECT COALESCE(SUM(qty_on_hand),0) FROM product_lot WHERE product_id=@id", new { id }),
                        Expired = connection.ExecuteScalar<bool>("""
                            SELECT EXISTS(SELECT 1 FROM product_lot WHERE product_id=@id AND qty_on_hand>0 AND expiry_date<@today)
                            """, new { id, today }),
                        Expiring = connection.ExecuteScalar<bool>("""
                            SELECT EXISTS(SELECT 1 FROM product_lot WHERE product_id=@id AND qty_on_hand>0
                                          AND expiry_date>=@today AND expiry_date<=@soon)
                            """, new { id, today, soon }),
                        FromVendor = connection.ExecuteScalar<bool>(
                            "SELECT EXISTS(SELECT 1 FROM product_lot WHERE product_id=@id AND vendor_id=@VendorId)", new { id, VendorId }),
                    };
                }
                // The app keeps planner statistics with PRAGMA optimize; give this store the same. A
                // five-row purchase table would rightly be scanned, so purchases carry the full
                // rehearsal store's statistics (35,100 purchases) for the plan checks.
                connection.Execute("ANALYZE main;");
                connection.Execute("""
                    DELETE FROM sqlite_stat1 WHERE tbl='purchase';
                    INSERT INTO sqlite_stat1(tbl,idx,stat) VALUES
                      ('purchase','ix_purchase_status_date_v30','35100 2001 17 1'),
                      ('purchase','ix_purchase_vendor_posted_date','35100 167 2 1'),
                      ('purchase','ix_purchase_vendor','35100 167 167'),
                      ('purchase','ix_purchase_date','35100 33'),
                      ('purchase','ix_purchase_invoice_nocase','35100 1'),
                      ('purchase','sqlite_autoindex_purchase_2','35100 167 1'),
                      ('purchase','sqlite_autoindex_purchase_1','35100 1');
                    ANALYZE sqlite_schema;
                    """);
            }
        }

        public DateOnly Today { get; } = new(2026, 9, 26);
        public string Root { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public CatalogService Catalog { get; }
        public InventoryService Inventory { get; }
        public PurchasingService Purchasing { get; }
        public MoneyOutService MoneyOut { get; }
        public long VendorId { get; }
        public long Grains { get; }
        public long Brand { get; }
        public long LotProduct { get; }
        public long VoidPurchase { get; }
        public long ExpensePurchase { get; }
        public string ExpenseDocument { get; } = string.Empty;
        public long DraftPurchase { get; }
        public List<Seed> Seeds { get; } = [];

        /// <summary>Runs a read, then returns EXPLAIN QUERY PLAN for each executed statement containing <paramref name="marker"/>.</summary>
        public async Task<string[]> PlansOf<T>(Func<Task<T>> read, string marker)
        {
            var executed = new System.Collections.Concurrent.ConcurrentQueue<string>();
            Factory.SqlTrace = (_, sql) => executed.Enqueue(sql);
            try { await read(); }
            finally { Factory.SqlTrace = null; }
            using var connection = Factory.Open();
            return executed.Where(sql => sql.Contains(marker, StringComparison.Ordinal) &&
                    (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                     sql.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase)))
                .Select(sql => string.Join(" | ", connection.Query("EXPLAIN QUERY PLAN " + sql)
                    .Select(static row => Convert.ToString(((IDictionary<string, object>)row)["detail"], CultureInfo.InvariantCulture))))
                .ToArray();
        }

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
