using Dapper;
using GroceryErp.Catalog;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Purchasing;
using GroceryErp.Sales;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

// A store created on the current schema, changed only through the services, never has a
// lot whose documents disagree with its quantity, so every lot can be counted.
public sealed class FreshStoreReconciliationTests
{
    [Fact]
    public async Task EveryStockWorkflow_OnAFreshStore_LeavesEveryLotReconciled_AndEveryLotCountable()
    {
        using var fixture = new FreshFixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        DateOnly today = fixture.Today;

        // Purchases: two lines become two lots; an untouched purchase is voided.
        long draft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Draft("FRESH-1", new Money(30),
            fixture.Line(fixture.ProductId, new Quantity(10)) with { FreeQuantity = new Quantity(2) },
            fixture.Line(fixture.ProductId, new Quantity(5))), ct);
        PostedPurchase purchase = await fixture.Purchasing.PostAsync(draft, OperationGuard.NewId(), ct);
        (long bought, long returnable) = (purchase.LotIds[0], purchase.LotIds[1]);
        long untouched = await fixture.Purchasing.SaveDraftAsync(null, fixture.Draft("FRESH-2", Money.Zero,
            fixture.Line(fixture.ProductId, new Quantity(3))), ct);
        await fixture.Purchasing.PostAsync(untouched, OperationGuard.NewId(), ct);
        await fixture.Purchasing.CancelAsync(untouched, today, OperationGuard.NewId(), ct);

        // Direct entry and the opening-stock import.
        long direct = await fixture.Inventory.CreateDirectLotAsync(fixture.Opening(10), "Found in store",
            OperationGuard.NewId(), ct);
        await fixture.Inventory.ImportOpeningStockAsync([fixture.Opening(10)], OperationGuard.NewId(), ct);
        long opening = fixture.LatestLot();

        // Sales: completed, voided, and returned to the original lot, to a recreated lot and as damage.
        long first = await fixture.SellAsync(bought, new Quantity(4), ct);
        await fixture.SellAsync(null, new Quantity(3), ct);
        long voided = await fixture.SellAsync(bought, Quantity.One, ct);
        await fixture.Sales.VoidAsync(voided, "Wrong bill", OperationGuard.NewId(), ct);
        long line = (await fixture.Sales.GetAsync(first, ct)).Lines.Single().Id;
        await fixture.ReturnAsync(first, line, resellable: true, ct);
        await fixture.Inventory.EditLotDetailsAsync(new(bought, fixture.DetailVersion(bought), new Money(140),
            new Money(160), null), ct);
        await fixture.ReturnAsync(first, line, resellable: true, ct);
        await fixture.ReturnAsync(first, line, resellable: false, ct);

        // Damage, a correction, and a correction that recreates a deleted zero lot.
        await fixture.MoneyOut.RecordDamageAsync(new(direct, today, Quantity.One, "BROKEN", Money.Zero, null),
            OperationGuard.NewId(), ct);
        DamageItem damage = (await fixture.MoneyOut.GetDamagesAsync(today, today, ct)).Single(item => item.Reason == "BROKEN");
        await fixture.MoneyOut.CorrectDamageAsync(damage, new(new Quantity(2), Money.Zero, "BROKEN", null),
            "Two were broken", OperationGuard.NewId(), ct);
        await fixture.MoneyOut.RecordDamageAsync(new(opening, today, new Quantity(10), "SPOILED", Money.Zero, null),
            OperationGuard.NewId(), ct);
        await fixture.Inventory.DeleteZeroLotAsync(opening, fixture.DetailVersion(opening), ct);
        damage = (await fixture.MoneyOut.GetDamagesAsync(today, today, ct)).Single(item => item.Reason == "SPOILED");
        await fixture.MoneyOut.CorrectDamageAsync(damage, new(new Quantity(9), Money.Zero, "SPOILED", null),
            "One was fine", OperationGuard.NewId(), ct);
        await fixture.MoneyOut.RecordBulkDamageAsync([new(direct, fixture.OnHand(direct), fixture.DetailVersion(direct),
            Quantity.One)], today, "LOST", OperationGuard.NewId(), ct);

        // Vendor returns: one cancelled, one kept.
        PostedVendorReturn cancelled = await fixture.Purchasing.PostVendorReturnAsync(new(draft, today, "CN-1",
            "Damaged in transit", [new(returnable, new Quantity(2), new Money(200), Money.Zero)]),
            OperationGuard.NewId(), ct);
        await fixture.Purchasing.CancelVendorReturnAsync(cancelled.Id, today, "Vendor refused", OperationGuard.NewId(), ct);
        await fixture.Purchasing.PostVendorReturnAsync(new(draft, today, "CN-2", "Short dated",
            [new(returnable, Quantity.One, new Money(100), Money.Zero)]), OperationGuard.NewId(), ct);

        // Internal use.
        long internalDraft = await fixture.Purchasing.SaveDraftAsync(null, fixture.Draft("FRESH-3", Money.Zero,
            fixture.Line(fixture.InternalProductId, new Quantity(4)) with { LineKind = "INTERNAL" }), ct);
        long internalLot = (await fixture.Purchasing.PostAsync(internalDraft, OperationGuard.NewId(), ct)).LotIds.Single();
        await fixture.Purchasing.ConsumeInternalAsync(internalLot, Quantity.One, today, "Shop use",
            OperationGuard.NewId(), ct);

        // Reviewed counts: a deficit and a surplus.
        await fixture.CountAsync(direct, fixture.OnHand(direct) - 1000, ct);
        await fixture.CountAsync(returnable, fixture.OnHand(returnable) + 2000, ct);

        // The edited lot's return and the deleted lot's damage correction each recreated a lot.
        using (SqliteConnection db = fixture.Factory.Open())
            db.Query<(string, long)>("""
                SELECT origin_kind,COUNT(*) FROM product_lot GROUP BY origin_kind ORDER BY origin_kind
                """).ShouldBe([("CUSTOMER_RETURN", 1L), ("DIRECT", 3L), ("PURCHASE_LINE", 4L)]);
        await fixture.Database.WriteAsync(tx => LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction).ToArray().ShouldBeEmpty(), ct);
        // Every lot, including the recreated and surplus lots, accepts a count that matches it.
        foreach (long lot in fixture.Lots())
            await fixture.CountAsync(lot, fixture.OnHand(lot), ct);
        await fixture.Database.WriteAsync(tx => LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction).ToArray().ShouldBeEmpty(), ct);

        // The weekly check (integrity, every lot, open P&L days, saved years) finds nothing.
        var maintenance = new MaintenanceService(fixture.Paths, fixture.Factory, fixture.Database,
            new BackupService(fixture.Paths, fixture.Factory, fixture.Clock, fixture.Settings), fixture.Clock, new AuditLog());
        await maintenance.RunAfterShellAsync(ct);
        using SqliteConnection connection = fixture.Factory.Open();
        connection.ExecuteScalar<string>("SELECT changes FROM audit_log WHERE action='INTEGRITY_CHECK'")!
            .ShouldContain("\"result\":\"ok\"");
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM lot_source_issue").ShouldBe(0);
    }

    private sealed class FreshFixture : IDisposable
    {
        public FreshFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-fresh-{Guid.NewGuid():N}");
            var paths = new AppPaths(new SettingsBootstrap(Root));
            Paths = paths;
            Factory = new SqliteConnectionFactory(paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            Clock = new FixedClock(new DateTime(2026, 10, 26, 11, 0, 0));
            var periods = new PeriodGuard(Settings, Clock);
            var ledger = new LotQuantities(Settings);
            var numbers = new DocNumberService(Settings);
            Inventory = new InventoryService(Database, ledger, numbers, periods, new AuditLog());
            Purchasing = new PurchasingService(Database, ledger, numbers, periods, Settings, new AuditLog());
            Sales = new SalesService(Database, ledger, numbers, periods, Settings, new AuditLog(), Clock);
            MoneyOut = new MoneyOutService(Database, ledger, numbers, periods, new AuditLog());

            using SqliteConnection connection = Factory.Open();
            long category = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Food') RETURNING id");
            long unit = connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
            GstRate = connection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=5000");
            ProductId = connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,hsn_code,tax_rate_id,track_expiry,
                    default_selling_price,default_mrp)
                VALUES ('FRESH-1','Fresh product',@category,@unit,'1006',@GstRate,0,150000,160000) RETURNING id
                """, new { category, unit, GstRate });
            InternalProductId = connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,tax_rate_id,track_expiry,product_class)
                VALUES ('FRESH-RAW','Packing material',@category,@unit,@GstRate,0,'INTERNAL') RETURNING id
                """, new { category, unit, GstRate });
            VendorId = Purchasing.CreateVendorAsync(new("Supplier", null, "9999999999", null, null, null, "Ahmedabad",
                "Gujarat", "380001", "24ABCDE1234F1Z5", null, null, null, 30, Money.Zero, null)).GetAwaiter().GetResult();
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public FixedClock Clock { get; }
        public InventoryService Inventory { get; }
        public PurchasingService Purchasing { get; }
        public SalesService Sales { get; }
        public MoneyOutService MoneyOut { get; }
        public long GstRate { get; }
        public long ProductId { get; }
        public long InternalProductId { get; }
        public long VendorId { get; }
        public DateOnly Today => Clock.BusinessDate;

        public PurchaseLineInput Line(long productId, Quantity quantity) =>
            new(productId, null, null, null, quantity, Quantity.Zero, new Money(100), Money.Zero, GstRate,
                new Money(160), new Money(150));

        public PurchaseDraftInput Draft(string invoice, Money otherCharges, params PurchaseLineInput[] lines) =>
            new(VendorId, invoice, Today, Today, null, otherCharges, null, lines);

        public OpeningStockLine Opening(decimal quantity) =>
            new(ProductId, new Quantity(quantity), new Money(80), new Money(150), new Money(160), null, null, Today);

        public async Task<long> SellAsync(long? lotId, Quantity quantity, CancellationToken ct)
        {
            long saleId = await Sales.AddProductAsync(null, ProductId, quantity, preferredLotId: lotId,
                cancellationToken: ct);
            Money total = (await Sales.GetAsync(saleId, ct)).Totals.GrandTotal;
            await Sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, total)], OperationGuard.NewId()), ct);
            return saleId;
        }

        public Task<CompletedReturn> ReturnAsync(long saleId, long lineId, bool resellable, CancellationToken ct) =>
            Sales.ReturnAsync(new(saleId, Today, "Returned", RefundMode.Cash,
                [new(lineId, Quantity.One, resellable)], OperationGuard.NewId()), ct);

        public Task<long> CountAsync(long lotId, long counted, CancellationToken ct) =>
            Database.WriteAsync(tx =>
            {
                (long onHand, long version) = tx.Connection.QuerySingle<(long, long)>(
                    "SELECT qty_on_hand,quantity_version FROM product_lot WHERE id=@lotId", new { lotId }, tx.Transaction);
                return new DirectStockEngine(new DocNumberService(Settings)).ReviewCount(tx, lotId, version, onHand,
                    counted, Today, "Shelf count", OperationGuard.NewId());
            }, ct);

        public long LatestLot() => Scalar("SELECT MAX(id) FROM product_lot", null);
        public long OnHand(long lotId) => Scalar("SELECT qty_on_hand FROM product_lot WHERE id=@lotId", new { lotId });
        public long DetailVersion(long lotId) => Scalar("SELECT detail_version FROM product_lot WHERE id=@lotId", new { lotId });

        public long[] Lots()
        {
            using SqliteConnection db = Factory.Open();
            return db.Query<long>("SELECT id FROM product_lot ORDER BY id").ToArray();
        }

        private long Scalar(string sql, object? parameters)
        {
            using SqliteConnection db = Factory.Open();
            return db.ExecuteScalar<long>(sql, parameters);
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
