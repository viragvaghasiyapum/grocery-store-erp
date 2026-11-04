using System.Globalization;
using Dapper;
using GroceryErp.Assets;
using GroceryErp.Catalog;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Core.Validation;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using GroceryErp.Sales;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

// Each rule an operator can trip from a screen refuses with its own code, whose message names the
// reason in both languages. Only a value no screen can send keeps the general "check the entered
// values" message.
public sealed class OperatorErrorTests
{
    [Fact]
    public async Task Billing_RefusesWithTheReason()
    {
        using var fixture = new ErrorFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A scanned barcode can belong to an internal-use or an archived product.
        await Refused(ErrorCode.InternalUseNotSaleable, () =>
            fixture.Sales.AddProductAsync(null, fixture.InternalProductId, Quantity.One, cancellationToken: ct));
        long archived = fixture.Product("ARCHIVED", trackExpiry: false);
        await fixture.DirectLotAsync(archived, 1, ct);
        fixture.Execute("UPDATE product SET is_active=0 WHERE id=@archived", new { archived });
        await Refused(ErrorCode.ProductArchived, () =>
            fixture.Sales.AddProductAsync(null, archived, Quantity.One, cancellationToken: ct));

        // A blocked batch is named, whether it is chosen or already on the bill being paid.
        long lot = await fixture.DirectLotAsync(fixture.ProductId, 3, ct);
        long draft = await fixture.Sales.AddProductAsync(null, fixture.ProductId, Quantity.One, lot, cancellationToken: ct);
        Money total = (await fixture.Sales.GetAsync(draft, ct)).Totals.GrandTotal;
        fixture.Execute("UPDATE product_lot SET is_active=0 WHERE id=@lot", new { lot });
        string lotCode = fixture.LotCode(lot);
        (await Refused(ErrorCode.LotBlocked, () => fixture.Sales.AddProductAsync(null, fixture.ProductId,
            Quantity.One, lot, cancellationToken: ct))).Arguments.ShouldBe([lotCode]);
        (await Refused(ErrorCode.LotBlocked, () => fixture.Sales.CompleteAsync(
            new(draft, [new(PaymentMode.Cash, total)], OperationGuard.NewId()), ct))).Arguments.ShouldBe([lotCode]);
        fixture.Execute("UPDATE product_lot SET is_active=1 WHERE id=@lot", new { lot });

        // A batch emptied by another bill cannot be deleted while an open bill still uses it.
        long other = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(3), lot, cancellationToken: ct);
        Money otherTotal = (await fixture.Sales.GetAsync(other, ct)).Totals.GrandTotal;
        await fixture.Sales.CompleteAsync(new(other, [new(PaymentMode.Cash, otherTotal)], OperationGuard.NewId()), ct);
        await Refused(ErrorCode.LotOnOpenBill, () =>
            fixture.Inventory.DeleteZeroLotAsync(lot, fixture.DetailVersion(lot), ct));

        // A price label needs a batch with a price to divide by.
        long labelled = fixture.Product("LABEL", trackExpiry: false);
        await fixture.DirectLotAsync(labelled, 5, ct, price: Money.Zero);
        await Refused(ErrorCode.PriceLabelNeedsPrice, () =>
            fixture.Sales.AddPriceLabelAsync(null, labelled, new Money(50), cancellationToken: ct));

        // Customers typed at the till.
        await Refused(ErrorCode.InvalidPhone, () => fixture.Sales.CreateCustomerAsync("Ravi", "12345", null, ct));
        const string badGstin = "24ABCDE1234F1X5";
        IndianIds.IsGstin(badGstin).ShouldBeFalse();
        await Refused(ErrorCode.InvalidGstin, () => fixture.Sales.CreateCustomerAsync("Ravi", null, badGstin, ct));
        await fixture.Sales.CreateCustomerAsync("Ravi", "9876543210", null, ct);
        await Refused(ErrorCode.CustomerPhoneExists, () => fixture.Sales.CreateCustomerAsync("Ravi Shah", "9876543210", null, ct));
    }

    [Fact]
    public async Task ProductForm_RefusesWithTheReason()
    {
        using var fixture = new ErrorFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        ProductInput existing = (await fixture.Catalog.GetAsync(fixture.ProductId, ct)).Product;
        await fixture.DirectLotAsync(fixture.ProductId, 2, ct);

        await Refused(ErrorCode.LooseNeedsFractionalUnit, () => fixture.Catalog.CreateAsync(
            fixture.NewProduct("LOOSE-PCS") with { IsLoose = true, PluCode = "101" }, ct));
        await Refused(ErrorCode.ProductUnitLocked, () => fixture.Catalog.UpdateAsync(fixture.ProductId,
            existing with { UomId = fixture.KgUnitId }, ct));
        await Refused(ErrorCode.ProductClassLocked, () => fixture.Catalog.UpdateAsync(fixture.ProductId,
            existing with { ProductClass = "INTERNAL" }, ct));
        await Refused(ErrorCode.ExpiryRequired, () => fixture.Catalog.CreateWithDirectLotAsync(
            fixture.NewProduct("FIRST-LOT") with { TrackExpiry = true },
            new NewProductLotInput(Quantity.One, new Money(80), new Money(150), new Money(160), null, null,
                fixture.Today, "Opening"),
            fixture.Ledger, fixture.Numbers, fixture.Periods, OperationGuard.NewId(), ct));
        await Refused(ErrorCode.EntryChanged, () => fixture.Catalog.SetActiveAsync(fixture.ProductId, true,
            expectedActive: false, ct));

        // Values another product already uses are named instead of failing as an unknown error.
        await Refused(ErrorCode.SkuExists, () => fixture.Catalog.CreateAsync(fixture.NewProduct(existing.Sku), ct));
        await fixture.Catalog.CreateAsync(fixture.NewProduct("BAR-1") with
        { Barcodes = [new ProductBarcodeInput("SHELF-0001", Quantity.One, "CODE128", IsPrimary: true)] }, ct);
        await Refused(ErrorCode.BarcodeExists, () => fixture.Catalog.CreateAsync(fixture.NewProduct("BAR-2") with
        { Barcodes = [new ProductBarcodeInput("SHELF-0001", Quantity.One, "CODE128", IsPrimary: true)] }, ct));
        ProductInput loose = fixture.NewProduct("LOOSE-1") with { IsLoose = true, PluCode = "102", UomId = fixture.KgUnitId };
        await fixture.Catalog.CreateAsync(loose, ct);
        await Refused(ErrorCode.PluExists, () => fixture.Catalog.CreateAsync(loose with { Sku = "LOOSE-2" }, ct));

        (await Refused(ErrorCode.TooManyRows, () => fixture.Catalog.ResolveIdsAsync(new ProductSearchFilter(), 1, ct)))
            .Arguments.ShouldBe([1]);
    }

    [Fact]
    public async Task StockScreens_RefuseWithTheReason()
    {
        using var fixture = new ErrorFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Refused(ErrorCode.ExpiryRequired, () => fixture.DirectLotAsync(fixture.TrackedProductId, 1, ct));
        long tracked = await fixture.DirectLotAsync(fixture.TrackedProductId, 1, ct, expiry: fixture.Today.AddDays(90));
        await Refused(ErrorCode.ExpiryRequired, () => fixture.Inventory.EditLotDetailsAsync(new(tracked,
            fixture.DetailVersion(tracked), new Money(150), new Money(160), null), ct));
        await Refused(ErrorCode.InvalidDate, () => fixture.Inventory.EditLotDetailsAsync(new(tracked,
            fixture.DetailVersion(tracked), new Money(150), new Money(160), fixture.Today.AddDays(-1)), ct));

        await Refused(ErrorCode.FilterRangeReversed, () => fixture.Inventory.GetLotPageAsync(
            new LotFilter { MinPrice = new Money(10), MaxPrice = new Money(5) }, null, cancellationToken: ct));
        long fresh = await fixture.DirectLotAsync(fixture.ProductId, 4, ct);
        (await Refused(ErrorCode.TooManyRows, () => fixture.Inventory.ResolveLotIdsAsync(new LotFilter(), 1, ct)))
            .Arguments.ShouldBe([1]);

        await Refused(ErrorCode.ExpiredReasonNeedsExpiredLot, () => fixture.MoneyOut.RecordBulkDamageAsync(
            [new(fresh, fixture.OnHand(fresh), fixture.DetailVersion(fresh), Quantity.One)], fixture.Today, "EXPIRED",
            OperationGuard.NewId(), ct));
        await Refused(ErrorCode.EntryChanged, () => fixture.MoneyOut.RecordBulkDamageAsync(
            [new(fresh, fixture.OnHand(fresh) + 1000, fixture.DetailVersion(fresh), Quantity.One)], fixture.Today,
            "BROKEN", OperationGuard.NewId(), ct));

        long retired = await fixture.Catalog.CreateTaxRateAsync("Old cess 3%", 3m, ct);
        await fixture.Catalog.UpdateTaxRateAsync(retired, "Old cess 3%", 3m, isActive: false, ct);
        (await Refused(ErrorCode.TaxRateInactive, () => fixture.DirectLotAsync(fixture.ProductId, 1, ct, taxRateId: retired)))
            .Arguments.ShouldBe(["Old cess 3%"]);

        // A lot that already differs from its documents (only data from an older release can) is named.
        fixture.Execute("UPDATE product_lot SET qty_on_hand=qty_on_hand+1000 WHERE id=@fresh", new { fresh });
        (await Refused(ErrorCode.CountLotDiffersFromDocuments, () => fixture.Database.WriteAsync(tx =>
        {
            (long onHand, long version) = tx.Connection.QuerySingle<(long, long)>(
                "SELECT qty_on_hand,quantity_version FROM product_lot WHERE id=@fresh", new { fresh }, tx.Transaction);
            return fixture.Engine.ReviewCount(tx, fresh, version, onHand, onHand, fixture.Today, "Shelf count",
                OperationGuard.NewId());
        }, ct))).Arguments.ShouldBe([fixture.LotCode(fresh)]);
    }

    [Fact]
    public async Task PurchasesAndVendors_RefuseWithTheReason()
    {
        using var fixture = new ErrorFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Refused(ErrorCode.PurchaseLineTypeMismatch, () => fixture.Purchasing.SaveDraftAsync(null,
            fixture.Draft("TYPE", fixture.Line(fixture.ProductId, 1) with { LineKind = "INTERNAL" }), ct));
        await Refused(ErrorCode.ExpiryRequired, () => fixture.Purchasing.SaveDraftAsync(null,
            fixture.Draft("EXPIRY", fixture.Line(fixture.TrackedProductId, 1)), ct));
        await Refused(ErrorCode.DiscountExceedsLine, () => fixture.Purchasing.SaveDraftAsync(null,
            fixture.Draft("DISCOUNT", fixture.Line(fixture.ProductId, 1) with { Discount = new Money(101) }), ct));
        (await Refused(ErrorCode.PurchaseTooManyLines, () => fixture.Purchasing.SaveDraftAsync(null,
            fixture.Draft("LONG", [.. Enumerable.Repeat(fixture.Line(fixture.ProductId, 1), PurchasingService.MaxPurchaseLines + 1)]),
            ct))).Arguments.ShouldBe([PurchasingService.MaxPurchaseLines]);

        // An Expense line of zero is refused when posted; a retired category when saved.
        long category = fixture.Scalar("SELECT MIN(id) FROM expense_category", null);
        PurchaseLineInput expense = fixture.Line(fixture.ProductId, 1) with
        { LineKind = "EXPENSE", ExpenseCategoryId = category, Rate = Money.Zero, SellingPrice = Money.Zero };
        long zero = await fixture.Purchasing.SaveDraftAsync(null, fixture.Draft("ZERO", expense), ct);
        await Refused(ErrorCode.ExpenseLineAmountRequired, () => fixture.Purchasing.PostAsync(zero, OperationGuard.NewId(), ct));
        string categoryName = fixture.Text("SELECT name FROM expense_category WHERE id=@category", new { category });
        await fixture.MoneyOut.SaveExpenseCategoryAsync(category, categoryName, null, active: false, ct);
        await Refused(ErrorCode.ExpenseCategoryInactive, () => fixture.Purchasing.SaveDraftAsync(null,
            fixture.Draft("RETIRED", expense with { Rate = new Money(100) }), ct));
        await Refused(ErrorCode.NameExists, () => fixture.MoneyOut.SaveExpenseCategoryAsync(null, categoryName.ToUpperInvariant(), null, cancellationToken: ct));

        // A return cannot take more than the batch has left.
        long purchase = await fixture.Purchasing.SaveDraftAsync(null, fixture.Draft("RETURN", fixture.Line(fixture.ProductId, 2)), ct);
        PostedPurchase posted = await fixture.Purchasing.PostAsync(purchase, OperationGuard.NewId(), ct);
        await Refused(ErrorCode.VendorReturnExceedsLot, () => fixture.Purchasing.PostVendorReturnAsync(new(purchase,
            fixture.Today, "CN-1", "Damaged in transit", [new(posted.LotIds.Single(), new Quantity(3), new Money(300), Money.Zero)]),
            OperationGuard.NewId(), ct));

        // Vendor identifiers.
        await Refused(ErrorCode.InvalidGstin, () => fixture.Purchasing.CreateVendorAsync(ErrorFixture.Vendor("Bad GSTIN", "24ABCDE1234F1X5", null), ct));
        await Refused(ErrorCode.InvalidPan, () => fixture.Purchasing.CreateVendorAsync(ErrorFixture.Vendor("Bad PAN", null, "ABCDE12345"), ct));
        await Refused(ErrorCode.VendorGstinExists, () => fixture.Purchasing.CreateVendorAsync(ErrorFixture.Vendor("Same GSTIN", ErrorFixture.Gstin, null), ct));
    }

    [Fact]
    public async Task AssetsAndDataFolders_RefuseWithTheReason()
    {
        using var fixture = new ErrorFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;

        long asset = await fixture.Assets.SaveAssetAsync(null, new("Scale", "Equipment", new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1), new Money(12000), null, DepreciationMethod.Wdv, new Percent(15), Money.Zero, null), ct);
        (await Refused(ErrorCode.AssetDisposalBeforeInUse, () => fixture.Assets.DisposeAsync(asset, new DateOnly(2026, 9, 30),
            new Money(5000), OperationGuard.NewId(), ct))).Arguments.ShouldBe(["01-10-2026"]);
        await fixture.Assets.DisposeAsync(asset, new DateOnly(2026, 10, 20), new Money(5000), OperationGuard.NewId(), ct);
        await Refused(ErrorCode.AssetAlreadyDisposed, () => fixture.Assets.DisposeAsync(asset, new DateOnly(2026, 10, 21),
            new Money(5000), OperationGuard.NewId(), ct));

        string outside = Path.Combine(Path.GetTempPath(), $"grocery-erp-move-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "note.txt"), "in use");
            Refused(ErrorCode.DataFolderNotEmpty, () => DataRootRelocator.ValidateCandidate(fixture.Root, outside));
            Refused(ErrorCode.DataFolderNotSeparate, () =>
                DataRootRelocator.ValidateCandidate(fixture.Root, Path.Combine(fixture.Root, "moved")));
            string staged = Path.Combine(outside, "staged");
            Directory.CreateDirectory(staged + ".moving");
            Refused(ErrorCode.DataMoveStagingInUse, () => DataRootRelocator.Schedule(fixture.Root, staged))
                .Arguments.ShouldBe([staged + ".moving"]);
            DataRootRelocator.Schedule(fixture.Root, Path.Combine(outside, "first"));
            Refused(ErrorCode.DataMovePending, () => DataRootRelocator.Schedule(fixture.Root, Path.Combine(outside, "second")));
        }
        finally
        {
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }

        // The synced folder is read from the environment; this one exists only for this check.
        string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        string synced = Path.Combine(Path.GetTempPath(), $"grocery-erp-onedrive-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("OneDrive", synced);
            Refused(ErrorCode.CloudFolderNotAllowed, () => AppPaths.RejectCloudSynchronizedPath(Path.Combine(synced, "Shop")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDrive", oneDrive);
        }

        // The data drive is a fixed drive, never a removable one.
        Refused(ErrorCode.UsbDriveNotReady, () => fixture.Backups.ExportToUsb(Path.GetPathRoot(fixture.Root)!));
    }

    private static async Task<DomainException> Refused(ErrorCode code, Func<Task> action)
    {
        DomainException refused = await Should.ThrowAsync<DomainException>(action);
        return Specific(refused, code);
    }

    private static DomainException Refused(ErrorCode code, Action action) =>
        Specific(Should.Throw<DomainException>(action), code);

    // Formats the message as the screens do and checks that it is complete and not the general one.
    private static DomainException Specific(DomainException refused, ErrorCode code)
    {
        refused.Code.ShouldBe(code);
        foreach (string name in new[] { "en-IN", "gu-IN" })
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(name);
            string message = string.Format(CultureInfo.InvariantCulture,
                TranslationSource.Instance.Get($"Error_{code}", culture), [.. refused.Arguments]);
            message.ShouldNotContain("{");
            message.ShouldNotBe(TranslationSource.Instance.Get("Error_Validation", culture));
        }
        return refused;
    }

    private sealed class ErrorFixture : IDisposable
    {
        public const string Gstin = "24ABCDE1234F1Z5";

        public ErrorFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-errors-{Guid.NewGuid():N}");
            var paths = new AppPaths(new SettingsBootstrap(Root));
            Factory = new SqliteConnectionFactory(paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            var clock = new FixedClock(new DateTime(2026, 10, 26, 11, 0, 0));
            Today = clock.BusinessDate;
            Periods = new PeriodGuard(Settings, clock);
            Ledger = new LotQuantities(Settings);
            Numbers = new DocNumberService(Settings);
            Catalog = new CatalogService(Database, new AuditLog(), Settings, clock);
            Inventory = new InventoryService(Database, Ledger, Numbers, Periods, new AuditLog());
            Purchasing = new PurchasingService(Database, Ledger, Numbers, Periods, Settings, new AuditLog());
            Sales = new SalesService(Database, Ledger, Numbers, Periods, Settings, new AuditLog(), clock);
            MoneyOut = new MoneyOutService(Database, Ledger, Numbers, Periods, new AuditLog());
            Assets = new AssetService(Database, Settings, Periods, new AuditLog(), clock);
            Engine = new DirectStockEngine(new DocNumberService(Settings));
            Backups = new BackupService(paths, Factory, clock, Settings);

            CategoryId = Scalar("INSERT INTO category(name) VALUES ('Food') RETURNING id", null);
            UnitId = Scalar("SELECT id FROM uom WHERE code='PCS'", null);
            KgUnitId = Scalar("SELECT id FROM uom WHERE code='KG'", null);
            GstRate = Scalar("SELECT id FROM tax_rate WHERE rate_pct=5000", null);
            ProductId = Product("RICE", trackExpiry: false);
            TrackedProductId = Product("MILK", trackExpiry: true);
            InternalProductId = Product("BAGS", trackExpiry: false, productClass: "INTERNAL");
            VendorId = Purchasing.CreateVendorAsync(Vendor("Supplier", Gstin, null)).GetAwaiter().GetResult();
        }

        public string Root { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public DateOnly Today { get; }
        public PeriodGuard Periods { get; }
        public LotQuantities Ledger { get; }
        public DocNumberService Numbers { get; }
        public CatalogService Catalog { get; }
        public InventoryService Inventory { get; }
        public PurchasingService Purchasing { get; }
        public SalesService Sales { get; }
        public MoneyOutService MoneyOut { get; }
        public AssetService Assets { get; }
        public DirectStockEngine Engine { get; }
        public BackupService Backups { get; }
        public long CategoryId { get; }
        public long UnitId { get; }
        public long KgUnitId { get; }
        public long GstRate { get; }
        public long ProductId { get; }
        public long TrackedProductId { get; }
        public long InternalProductId { get; }
        public long VendorId { get; }

        public long Product(string sku, bool trackExpiry, string productClass = "SALEABLE") => Scalar("""
            INSERT INTO product(sku,name,category_id,uom_id,hsn_code,tax_rate_id,track_expiry,
                default_selling_price,default_mrp,product_class)
            VALUES (@sku,@sku,@CategoryId,@UnitId,'1006',@GstRate,@trackExpiry,150000,160000,@productClass) RETURNING id
            """, new { sku, CategoryId, UnitId, GstRate, trackExpiry = trackExpiry ? 1 : 0, productClass });

        public ProductInput NewProduct(string sku) => new(sku, sku, null, null, CategoryId, null, UnitId, "1006",
            GstRate, false, null, false, null, null, Quantity.Zero, new Money(150), new Money(160), []);

        public Task<long> DirectLotAsync(long productId, decimal quantity, CancellationToken ct,
            Money? price = null, DateOnly? expiry = null, long? taxRateId = null) =>
            Inventory.CreateDirectLotAsync(new OpeningStockLine(productId, new Quantity(quantity), new Money(80),
                price ?? new Money(150), new Money(160), expiry, null, Today)
            { TaxRateId = taxRateId }, "Found on the shelf", OperationGuard.NewId(), ct);

        public PurchaseLineInput Line(long productId, decimal quantity) =>
            new(productId, null, null, null, new Quantity(quantity), Quantity.Zero, new Money(100), Money.Zero, GstRate,
                new Money(160), new Money(150));

        public PurchaseDraftInput Draft(string invoice, params PurchaseLineInput[] lines) =>
            new(VendorId, invoice, Today, Today, null, Money.Zero, null, lines);

        public static VendorInput Vendor(string name, string? gstin, string? pan) =>
            new(name, null, "9999999999", null, null, null, "Ahmedabad", "Gujarat", "380001", gstin, pan, null, null,
                30, Money.Zero, null);

        public string LotCode(long lotId) => Text("SELECT lot_code FROM product_lot WHERE id=@lotId", new { lotId });
        public long OnHand(long lotId) => Scalar("SELECT qty_on_hand FROM product_lot WHERE id=@lotId", new { lotId });
        public long DetailVersion(long lotId) => Scalar("SELECT detail_version FROM product_lot WHERE id=@lotId", new { lotId });

        public void Execute(string sql, object parameters)
        {
            using SqliteConnection db = Factory.Open();
            db.Execute(sql, parameters);
        }

        public long Scalar(string sql, object? parameters)
        {
            using SqliteConnection db = Factory.Open();
            return db.ExecuteScalar<long>(sql, parameters);
        }

        public string Text(string sql, object parameters)
        {
            using SqliteConnection db = Factory.Open();
            return db.ExecuteScalar<string>(sql, parameters)!;
        }

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
