using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class LedgerRemovalTests
{
    private static readonly string[] RemovedObjects =
        ["stock_movement", "stock_movement_type", "direct_stock_live", "stock_count", "stock_count_line",
         "stock_count_scope", "v_product_stock", "v_lot_cache_mismatch"];

    [Fact]
    public void LatestSchema_HasNoLedger_AndKeepsLegacyAdjustmentSources()
    {
        using var store = new Store();
        store.Runner.Run();
        using SqliteConnection db = store.Factory.Open();
        db.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(MigrationRunner.LatestVersion);
        db.Query<string>("SELECT name FROM sqlite_schema WHERE name IN @RemovedObjects",
            new { RemovedObjects }).ShouldBeEmpty();
        // product_lot's original DDL keeps an old column comment; no view, trigger or index uses the ledger.
        db.Query<string>("""
            SELECT name FROM sqlite_schema WHERE type IN ('view','trigger','index') AND sql LIKE '%stock_movement%'
            """).ShouldBeEmpty();
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_schema WHERE name='legacy_adjustment_source'").ShouldBe(1L);
        db.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM setting WHERE key IN ('stock.allow_negative','maintenance.stock_rebuild_pending',
              'expiry.popup_on_login','expiry.last_popup_date')
            """).ShouldBe(0L);
        db.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
        db.Query("PRAGMA foreign_key_check").ShouldBeEmpty();
    }

    [Fact]
    public void Upgrade_RefusesToDropTheLedger_WhenAMovementFollowedTheCutover()
    {
        using var store = new Store();
        store.Runner.RunTo(35);
        using (SqliteConnection db = store.Factory.Open())
        {
            long lot = Store.AddLot(db);
            // A foreign writer that bypassed the cutover trigger.
            db.Execute("""
                DROP TRIGGER trg_direct_no_movement;
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-10-01',(SELECT product_id FROM product_lot WHERE id=@lot),@lot,'ADJUSTMENT',1000,1000);
                """, new { lot });
        }

        Should.Throw<InvalidOperationException>(store.Runner.Run).Message.ShouldContain("after the direct-stock upgrade");

        using SqliteConnection after = store.Factory.Open();
        after.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(35L);
        after.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_movement").ShouldBe(1L);
    }

    [Fact]
    public void Upgrade_RefusesToDropTheLedger_WhenALegacyAdjustmentLostItsCorrection()
    {
        using var store = new Store();
        store.Runner.RunTo(35);
        using (SqliteConnection db = store.Factory.Open())
        {
            long lot = Store.AddLot(db);
            // An adjustment that schema 33 should have carried forward, but whose correction is missing.
            db.Execute("""
                DROP TRIGGER trg_direct_no_movement;
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost,ref_type)
                VALUES ('2026-10-01',(SELECT product_id FROM product_lot WHERE id=@lot),@lot,'ADJUSTMENT',
                        -500,1000,'manual_adjustment');
                UPDATE direct_stock_live SET movement_high_water=(SELECT MAX(id) FROM stock_movement);
                """, new { lot });
        }

        Should.Throw<InvalidOperationException>(store.Runner.Run).Message.ShouldContain("no carried-forward correction");

        using SqliteConnection after = store.Factory.Open();
        after.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(35L);
    }

    [Fact]
    public async Task LedgerArchiveBackup_IsTakenBeforeTheDrop_AndRotationKeepsIt()
    {
        using var store = new Store();
        store.Runner.RunTo(35);
        using var database = new DbExecutor(store.Factory);
        using var settings = new SettingsService(database, store.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var backups = new BackupService(store.Paths, store.Factory, new SystemClock(settings), settings);

        new MigrationRunner(store.Factory, backups).Run();

        string archive = Directory.EnumerateFiles(backups.BackupDirectory, "*.zip").Single();
        Path.GetFileName(archive).ShouldContain("_" + MigrationRunner.LedgerArchiveReason + "-v35");
        BackupService.Verify(archive, TestContext.Current.CancellationToken).SchemaVersion.ShouldBe(35L);
        // Ordinary pre-* safety backups go after 90 days; the ledger archive stays.
        string oldSafety = Path.Combine(backups.BackupDirectory, "GroceryErp_2025-01-01_000000_pre-restore.zip");
        File.Copy(archive, oldSafety);
        File.SetLastWriteTimeUtc(oldSafety, DateTime.UtcNow.AddDays(-200));
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddDays(-200));
        backups.Create("manual", TestContext.Current.CancellationToken);
        File.Exists(archive).ShouldBeTrue();
        File.Exists(oldSafety).ShouldBeFalse();
    }

    [Fact]
    public async Task LotQuantities_RejectAChangeAgainstItsDocumentsDirection()
    {
        using var store = new Store();
        store.Runner.Run();
        using var database = new DbExecutor(store.Factory);
        using var settings = new SettingsService(database, store.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var lots = new LotQuantities(settings);
        long lot;
        long product;
        using (SqliteConnection db = store.Factory.Open())
        {
            lot = Store.AddLot(db);
            product = db.ExecuteScalar<long>("SELECT product_id FROM product_lot WHERE id=@lot", new { lot });
        }
        var date = new DateOnly(2026, 10, 18);

        foreach ((LotChangeKind kind, Quantity quantity) in new[]
                 {
                     (LotChangeKind.Sale, Quantity.One), (LotChangeKind.Purchase, new Quantity(-1)),
                     (LotChangeKind.CustomerReturn, new Quantity(-1)), (LotChangeKind.Damage, Quantity.One),
                     (LotChangeKind.Opening, Quantity.Zero),
                 })
            (await Should.ThrowAsync<DomainException>(() => database.WriteAsync(tx =>
                lots.Change(tx, new(date, product, lot, kind, quantity)), TestContext.Current.CancellationToken)))
                .Code.ShouldBe(ErrorCode.Validation);
        (await Should.ThrowAsync<DomainException>(() => database.WriteAsync(tx =>
            lots.Change(tx, new(date, product, lot, LotChangeKind.Damage, new Quantity(-2))),
            TestContext.Current.CancellationToken))).Code.ShouldBe(ErrorCode.InsufficientStock);
        await database.WriteAsync(tx => lots.Change(tx, new(date, product, lot, LotChangeKind.Sale, new Quantity(-1))),
            TestContext.Current.CancellationToken);
        using SqliteConnection after = store.Factory.Open();
        after.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@lot", new { lot }).ShouldBe(0L);
    }

    private sealed class Store : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"grocery-ledger-removal-{Guid.NewGuid():N}");

        public Store()
        {
            Paths = new AppPaths(new SettingsBootstrap(_root));
            Factory = new SqliteConnectionFactory(Paths);
            Runner = new MigrationRunner(Factory, NullMigrationBackup.Instance);
        }

        public AppPaths Paths { get; }
        public SqliteConnectionFactory Factory { get; }
        public MigrationRunner Runner { get; }

        // A product with one direct lot holding one unit.
        public static long AddLot(SqliteConnection db)
        {
            long product = db.ExecuteScalar<long>("""
                INSERT INTO category(name) VALUES ('Ledger');
                INSERT INTO product(sku,name,category_id,uom_id,track_expiry)
                VALUES ('LEDGER-1','Ledger product',last_insert_rowid(),(SELECT id FROM uom WHERE code='PCS'),0)
                RETURNING id
                """);
            return db.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,selling_price,qty_received,qty_on_hand)
                VALUES (@product,'LEDGER-L1','2026-10-01',1000,2000,1000,1000) RETURNING id
                """, new { product });
        }

        public void Dispose()
        {
            Factory.ClearPool();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
