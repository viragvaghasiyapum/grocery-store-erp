using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Sales;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class DirectStockEngineTests
{
    private static readonly DirectStockEngine Engine = new();

    [Fact]
    public void LedgerUpgrade_CarriesHistoryForward_AndReconcilesEveryLot()
    {
        using var legacy = new LegacyStore();
        using (SqliteConnection db = legacy.Factory.Open())
        {
            db.Execute("""
                INSERT INTO category(name) VALUES ('Upgrade');
                INSERT INTO product(id,sku,name,category_id,uom_id)
                VALUES (1,'UPGRADE','Upgrade product',(SELECT id FROM category WHERE name='Upgrade'),
                        (SELECT id FROM uom WHERE code='PCS'));
                INSERT INTO product_lot(id,product_id,lot_code,received_date,unit_cost,selling_price,qty_received)
                VALUES (1,1,'ADJUSTED','2026-09-01',1000,2000,5000),
                       (2,1,'DRIFTED','2026-09-01',1000,2000,3000),
                       (3,1,'UNEXPLAINED','2026-09-01',1000,2000,2000);
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost,ref_type,note)
                VALUES ('2026-09-01',1,1,'OPENING',5000,1000,'opening_import','Opening'),
                       ('2026-09-02',1,1,'ADJUSTMENT',-1000,1000,'manual_adjustment','Shelf loss'),
                       ('2026-09-01',1,2,'OPENING',3000,1000,'opening_import','Opening'),
                       ('2026-09-01',1,3,'OPENING',2000,1000,'opening_import','Opening'),
                       ('2026-09-03',1,3,'SALE',-500,1000,NULL,'Movement without a sale document');
                UPDATE product_lot SET qty_on_hand=2500 WHERE id=2;
                INSERT INTO stock_count(id,count_date,status,snapshot_movement_id)
                VALUES (1,'2026-09-04','DRAFT',(SELECT MAX(id) FROM stock_movement));
                """);
        }
        new MigrationRunner(legacy.Factory, NullMigrationBackup.Instance).Run();

        using SqliteConnection upgraded = legacy.Factory.Open();
        upgraded.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(MigrationRunner.LatestVersion);
        upgraded.Query<long>("SELECT qty_on_hand FROM product_lot ORDER BY id").ShouldBe([4000L, 3000L, 1500L]);
        upgraded.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_movement").ShouldBe(5L);
        upgraded.ExecuteScalar<long>("SELECT movement_high_water FROM direct_stock_live").ShouldBe(5L);
        upgraded.ExecuteScalar<string>("SELECT status FROM stock_count WHERE id=1").ShouldBe("CANCELLED");
        upgraded.Query<(string Kind, long Lot, long Delta)>("""
            SELECT source_kind AS Kind,lot_id AS Lot,quantity_delta AS Delta
            FROM stock_correction ORDER BY id
            """).ShouldBe([("LEGACY_MOVEMENT", 1L, -1000L), ("CUTOVER_BALANCE", 3L, -500L)]);
        upgraded.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM v_pnl_entry WHERE head_code='STOCK_ADJUSTMENT'
            """).ShouldBe(1L);
        upgraded.ExecuteScalar<string>("""
            SELECT changes FROM audit_log WHERE entity='direct_stock' AND action='CUTOVER'
            """)!.ShouldContain("\"ledgerCacheRepairs\":1");
        LotSourceReconciliation.CheckAll(upgraded, null,
            cancellationToken: TestContext.Current.CancellationToken).ShouldBeEmpty();
        Should.Throw<SqliteException>(() => upgraded.Execute("""
            INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
            VALUES ('2026-10-01',1,1,'OPENING',1000,1000)
            """));
        Should.Throw<SqliteException>(() => upgraded.Execute(
            "UPDATE product_lot SET qty_on_hand=-1 WHERE id=1"));
    }

    [Fact]
    public async Task CashierSaleAndReviewedCount_UseSourceDocumentsOnly()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(ct);
        var clock = new FixedClock(new DateTime(2026, 10, 18, 12, 0, 0));
        var periods = new PeriodGuard(settings, clock);
        var countService = new DirectStockCountService(fixture.Database, Engine, periods);
        var sales = new SalesService(fixture.Database, new StockLedger(settings),
            new DocNumberService(settings), periods, settings, new AuditLog(), clock);
        long saleId = await sales.AddProductAsync(null, fixture.Product, Quantity.One,
            cancellationToken: ct);
        await sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(2))],
            OperationGuard.NewId()), ct);
        DirectCountLotPage page = await countService.GetPageAsync([fixture.Product], cancellationToken: ct);
        page.Items.Count.ShouldBe(1);
        page.Items[0].OnHand.ShouldBe(9000);
        await countService.PostReviewedAsync([
            new(page.Items[0].LotId, page.Items[0].Version, 9000, 8000,
                clock.BusinessDate, "Physical shelf count", OperationGuard.NewId(), "LOST")
        ], ct);
        using var db = fixture.Factory.Open();
        db.ExecuteScalar<string>("SELECT reason FROM damage WHERE lot_id=@lot",
            new { lot = fixture.Lot }).ShouldBe("LOST");
        db.ExecuteScalar<string>("SELECT product_name_snapshot FROM damage WHERE lot_id=@lot",
            new { lot = fixture.Lot }).ShouldBe("Direct stock");
        db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@lot",
            new { lot = fixture.Lot }).ShouldBe(8000);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_movement").ShouldBe(0);
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime Now => now;
        public DateOnly BusinessDate => DateOnly.FromDateTime(now);
    }

    [Fact]
    public async Task SelectedProductCount_PagesStayBounded_WhenSurplusCreatesANewLot()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Database.WriteAsync(tx =>
        {
            for (int index = 0; index < 51; index++)
                fixture.AddDirectLot(tx, "COUNT-" + index, 1000);
        }, ct);
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(ct);
        var counts = new DirectStockCountService(fixture.Database, Engine,
            new PeriodGuard(settings, new FixedClock(new DateTime(2026, 10, 18, 12, 0, 0))));
        DirectCountLotPage first = await counts.GetPageAsync([fixture.Product], limit: 50, cancellationToken: ct);
        first.Items.Count.ShouldBe(50);
        first.HasMore.ShouldBeTrue();
        await counts.PostReviewedAsync(first.Items.Select((lot, index) => new DirectCountReview(
            lot.LotId, lot.Version, lot.OnHand, lot.OnHand + (index == 0 ? 1000 : 0),
            new DateOnly(2026, 10, 18), "Shelf count", OperationGuard.NewId())).ToArray(), ct);
        DirectCountLotPage second = await counts.GetPageAsync([fixture.Product], first.NextLotId,
            first.MaxLotId, limit: 50, cancellationToken: ct);
        second.Items.Count.ShouldBe(2);
        second.HasMore.ShouldBeFalse();
        second.Items.All(lot => lot.LotId <= first.MaxLotId).ShouldBeTrue();
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task ReviewedCount_IsIdempotent_AndAStaleBatchChangesNothing()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string countOp = OperationGuard.NewId();
        await Should.ThrowAsync<DomainException>(async () => await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, 99, 10000, 9000,
                new DateOnly(2026, 10, 18), "Counted shelf", countOp), ct));
        long version = fixture.Version();
        long damageId = await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, version, 10000, 9000,
                new DateOnly(2026, 10, 18), "Counted shelf", countOp), ct);
        damageId.ShouldBeGreaterThan(0);
        (await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, version, 10000, 9000,
                new DateOnly(2026, 10, 18), "Counted shelf", countOp), ct)).ShouldBe(damageId);
        await Should.ThrowAsync<DomainException>(() => fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, version, 10000, 8000,
                new DateOnly(2026, 10, 18), "Counted shelf", countOp), ct));
        await Should.ThrowAsync<DomainException>(() => fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, version, 10000, 9000,
                new DateOnly(2026, 10, 18), "Counted shelf", countOp, "LOST"), ct));
        long surplusLot = await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, fixture.Version(), 9000, 11000,
                new DateOnly(2026, 10, 18), "Back room count", OperationGuard.NewId()), ct);
        surplusLot.ShouldNotBe(fixture.Lot);
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.Lot })
                .ShouldBe(9000);
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = surplusLot })
                .ShouldBe(2000);
            db.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@id",
                new { id = fixture.Product }).ShouldBe(11000);
        }
        await Should.ThrowAsync<DomainException>(async () => await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCountBatch(tx,
            [
                new(fixture.Lot, fixture.Version(), 9000, 8000, new DateOnly(2026, 10, 18),
                    "Batch count", OperationGuard.NewId()),
                new(surplusLot, 99, 2000, 1000, new DateOnly(2026, 10, 18),
                    "Stale batch count", OperationGuard.NewId()),
            ]), ct));
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.Lot })
                .ShouldBe(9000);
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM damage WHERE lot_id=@id", new { id = fixture.Lot })
                .ShouldBe(1);
        }
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task BackupDuringDirectWrite_VerifiesAndLeavesSourceBalanced()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(ct);
        var backup = new BackupService(fixture.Paths, fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        Task<BackupInfo> backupTask = Task.Run(() => backup.Create("manual", ct), ct);
        await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, fixture.Version(), 10000, 9000,
                new DateOnly(2026, 10, 18), "Concurrent backup count", OperationGuard.NewId()), ct);
        BackupInfo created = await backupTask;
        BackupService.Verify(created.Path, ct).SchemaVersion.ShouldBe(MigrationRunner.LatestVersion);
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task DiskFullAfterLotUpdate_RollsBackSaleAndQuantity()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(ct);
        var ledger = new StockLedger(settings);
        using (var connection = fixture.Factory.Open())
        {
            long pages = connection.ExecuteScalar<long>("PRAGMA page_count");
            connection.Execute($"PRAGMA max_page_count = {pages + 16}");
        }
        DomainException failure = await Should.ThrowAsync<DomainException>(() =>
            fixture.Database.WriteAsync(tx =>
            {
                long sale = tx.Connection.ExecuteScalar<long>("""
                    INSERT INTO sale(invoice_no,sale_date) VALUES ('FULL-DIRECT','2026-10-18') RETURNING id
                    """, transaction: tx.Transaction);
                long line = tx.Connection.ExecuteScalar<long>("""
                    INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,
                      unit_price,taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
                    VALUES (@sale,1,@product,@lot,'Direct stock',2000,2000,4000,0,4000,1000,2000)
                    RETURNING id
                    """, new { sale, product = fixture.Product, lot = fixture.Lot }, tx.Transaction);
                ledger.Post(tx, new(new DateOnly(2026, 10, 18), fixture.Product, fixture.Lot, "SALE",
                    Quantity.FromScaled(-2000), new Money(1), "sale_line", line));
                tx.Connection.Execute("""
                    INSERT INTO audit_log(entity,entity_id,action,changes)
                    VALUES ('fault',0,'FULL',@changes)
                    """, new { changes = new string('x', 2_000_000) }, tx.Transaction);
            }, ct));
        failure.Code.ShouldBe(ErrorCode.DiskFull);
        using (var connection = fixture.Factory.Open())
        {
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale WHERE invoice_no='FULL-DIRECT'")
                .ShouldBe(0);
            connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@lot",
                new { lot = fixture.Lot }).ShouldBe(10000);
        }
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "direct-stock-" + Guid.NewGuid().ToString("N"));
        private readonly StockLedger _ledger;
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public AppPaths Paths { get; }
        public long Product { get; }
        public long Lot { get; }

        public Fixture()
        {
            Paths = new AppPaths(new SettingsBootstrap(_root));
            Factory = new SqliteConnectionFactory(Paths);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Database = new DbExecutor(Factory);
            _ledger = new StockLedger(new SettingsService(Database, Paths, new AuditLog()));
            using var db = Factory.Open();
            long category = db.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Test') RETURNING id");
            long uom = db.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
            Product = db.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id)
                VALUES ('DS-1','Direct stock',@category,@uom) RETURNING id
                """, new { category, uom });
            using SqliteTransaction transaction = db.BeginTransaction();
            Lot = AddDirectLot(new(db, transaction), "DS-L1", 10000, "2027-10-18");
            transaction.Commit();
        }

        public long AddDirectLot(DbTx tx, string code, long quantity, string? expiry = null)
        {
            long lot = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,
                  unit_cost,selling_price,qty_received)
                VALUES (@product,@code,'2026-10-18',@expiry,1000,2000,@quantity) RETURNING id
                """, new { product = Product, code, expiry, quantity }, tx.Transaction);
            _ledger.Post(tx, new StockMovementInput(new DateOnly(2026, 10, 18), Product, lot, "DIRECT_ENTRY",
                Quantity.FromScaled(quantity), new Money(1), "direct_stock_origin", lot));
            return lot;
        }

        public long Version()
        {
            using var db = Factory.Open();
            return db.ExecuteScalar<long>("SELECT quantity_version FROM product_lot WHERE id=@id", new { id = Lot });
        }

        public void Dispose()
        {
            Database.Dispose();
            Factory.ClearPool();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    // A store created by a ledger-era release, before Phase 1 rebuilt its tables.
    private sealed class LegacyStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"grocery-ledger-{Guid.NewGuid():N}");

        internal LegacyStore()
        {
            Factory = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(_root)));
            using SqliteConnection db = Factory.Open();
            string[] scripts = typeof(MigrationRunner).Assembly.GetManifestResourceNames()
                .Where(static name => name.Contains(".db.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Take(28).ToArray();
            foreach (string script in scripts)
            {
                using Stream stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(script)
                    ?? throw new InvalidOperationException(script);
                using var reader = new StreamReader(stream);
                db.Execute(reader.ReadToEnd());
            }
            db.Execute("PRAGMA user_version=28");
        }

        internal SqliteConnectionFactory Factory { get; }

        public void Dispose()
        {
            Factory.ClearPool();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
