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
    public async Task Return_ProrationReconciles_AndNonResellableStockBecomesDamage()
    {
        using var fixture = new SalesFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long saleId = await fixture.Sales.AddProductAsync(null, fixture.ProductId, new Quantity(2), cancellationToken: ct);
        await fixture.Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(236))],
            OperationGuard.NewId()), ct);
        SaleDetails sale = await fixture.Sales.GetAsync(saleId, ct);

        CompletedReturn returned = await fixture.Sales.ReturnAsync(new(saleId, fixture.Clock.BusinessDate,
            "Opened package", RefundMode.Cash, [new(sale.Lines[0].Id, Quantity.One, false)],
            OperationGuard.NewId()), ct);

        returned.Refund.ShouldBe(new Money(118));
        returned.Taxable.ShouldBe(new Money(100));
        returned.Tax.ShouldBe(new Money(18));
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId })
            .ShouldBe(8000L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM damage WHERE reason='CUSTOMER_RETURN'").ShouldBe(1L);
        DayEndSummary summary = await fixture.Sales.GetDayEndAsync(fixture.Clock.BusinessDate, ct);
        summary.CashExpected.ShouldBe(new Money(118));
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
            var settings = new SettingsService(Database, paths, new AuditLog());
            settings.LoadAsync().GetAwaiter().GetResult();
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
            using var transaction = connection.BeginTransaction();
            ledger.Post(new(connection, transaction), new(Clock.BusinessDate, ProductId, LotId, "OPENING",
                new Quantity(10), new Money(70), "test", 1));
            transaction.Commit();
        }

        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public CatalogService Catalog { get; }
        public InventoryService Inventory { get; }
        public SalesService Sales { get; }
        public FixedClock Clock { get; }
        public long CategoryId { get; }
        public long UnitId { get; }
        public long TaxId { get; }
        public long ProductId { get; }
        public long LotId { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime Now { get; } = now;
        public DateOnly BusinessDate { get; } = DateOnly.FromDateTime(now);
    }
}
