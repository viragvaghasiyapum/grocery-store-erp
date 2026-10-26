using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class UpgradeTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Plan_IsNull_ForANewStoreAndACurrentOne()
    {
        using var store = new Store();
        store.Runner.Plan().ShouldBeNull();
        store.Runner.Run();
        store.Runner.Plan().ShouldBeNull();
    }

    [Fact]
    public void Plan_DescribesAnOlderStore_WithoutChangingIt()
    {
        using var store = new Store();
        store.Runner.RunTo(30);

        MigrationPlan plan = store.Runner.Plan(static _ => 50 * GiB).ShouldNotBeNull();

        plan.FromVersion.ShouldBe(30);
        plan.ToVersion.ShouldBe(MigrationRunner.LatestVersion);
        plan.DatabaseBytes.ShouldBeGreaterThan(0);
        plan.RequiredBytes.ShouldBeGreaterThanOrEqualTo(2 * plan.DatabaseBytes);
        plan.Estimate.ShouldBeGreaterThan(TimeSpan.Zero);
        plan.FreeBytes.ShouldBe(50 * GiB);
        plan.HasSpace.ShouldBeTrue();
        // The backup before schema 36 is the last copy of the stock ledger.
        plan.KeepsLedgerArchive.ShouldBeTrue();
        plan.UiLanguage.ShouldBe("en-IN");
        using SqliteConnection db = store.Factory.Open();
        db.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(30L);
    }

    [Fact]
    public void Plan_BlocksTooLittleSpace_AndAllowsAVolumeThatCannotReportIt()
    {
        using var store = new Store();
        store.Runner.RunTo(36);

        MigrationPlan full = store.Runner.Plan(static _ => 1024).ShouldNotBeNull();
        full.HasSpace.ShouldBeFalse();
        full.KeepsLedgerArchive.ShouldBeFalse();

        MigrationPlan unknown = store.Runner.Plan(static _ => throw new IOException("not ready")).ShouldNotBeNull();
        unknown.FreeBytes.ShouldBe(-1);
        unknown.HasSpace.ShouldBeTrue();
    }

    [Fact]
    public void Plan_RefusesAStoreFromANewerVersion()
    {
        using var store = new Store();
        store.Runner.Run();
        using (SqliteConnection db = store.Factory.Open())
            db.Execute($"PRAGMA user_version = {MigrationRunner.LatestVersion + 1}");

        Should.Throw<DomainException>(() => store.Runner.Plan()).Code.ShouldBe(ErrorCode.DatabaseNewerThanApp);
    }

    [Fact]
    public void Run_ReportsTheBackup_EachStepInOrder_AndTheEnd()
    {
        using var store = new Store();
        store.Runner.RunTo(33);
        var backup = new RecordingBackup();
        var progress = new RecordingProgress();

        new MigrationRunner(store.Factory, backup).Run(progress);

        int steps = MigrationRunner.LatestVersion - 33;
        progress.Reports.ShouldBe([
            new MigrationProgress(MigrationStage.Backup),
            .. Enumerable.Range(1, steps).Select(step => new MigrationProgress(MigrationStage.Upgrade, step, steps)),
            new MigrationProgress(MigrationStage.Finishing),
        ]);
        backup.Reasons.ShouldBe([$"{MigrationRunner.LedgerArchiveReason}-v33"]);
        using SqliteConnection db = store.Factory.Open();
        db.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(MigrationRunner.LatestVersion);
    }

    [Fact]
    public void Estimate_AndSpace_FollowTheStoreSizeAndItsHeavySteps()
    {
        // Measured: 28 to 29 rewrote every sale line, so it dominates an upgrade from schema 28.
        TimeSpan fromLedger = MigrationRunner.Estimate(28, MigrationRunner.LatestVersion, 2 * GiB);
        TimeSpan recent = MigrationRunner.Estimate(36, MigrationRunner.LatestVersion, 2 * GiB);
        fromLedger.ShouldBeGreaterThan(recent + TimeSpan.FromSeconds(60));
        MigrationRunner.Estimate(36, MigrationRunner.LatestVersion, 4 * GiB).ShouldBeGreaterThan(recent);

        long reserve = Math.Max(256L * 1024 * 1024, 2 * GiB / 10);
        MigrationRunner.RequiredBytes(36, MigrationRunner.LatestVersion, 2 * GiB).ShouldBe(4 * GiB + reserve);
        MigrationRunner.RequiredBytes(28, MigrationRunner.LatestVersion, 2 * GiB).ShouldBeGreaterThanOrEqualTo(4 * GiB + reserve);
    }

    // Every schema an installed version can have left, including the version-specific steps (16's
    // monthly rebuild, 19's dashboard window, 29's snapshots, 33's cutover, 36's ledger removal).
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(15)]
    [InlineData(19)]
    [InlineData(25)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(35)]
    [InlineData(36)]
    [InlineData(38)]
    public void UpgradeFromAnyEarlierVersion_EndsWithTheSchemaOfANewStore(int version)
    {
        using var fresh = new Store();
        fresh.Runner.Run();
        using var store = new Store();
        store.Runner.RunTo(version);

        store.Runner.Run();

        using SqliteConnection upgraded = store.Factory.Open();
        using SqliteConnection created = fresh.Factory.Open();
        upgraded.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(MigrationRunner.LatestVersion);
        Schema(upgraded).ShouldBe(Schema(created));
        upgraded.ExecuteScalar<string>("PRAGMA integrity_check").ShouldBe("ok");
        upgraded.Query("PRAGMA foreign_key_check").ShouldBeEmpty();
    }

    [Fact]
    public void FailedPreUpgradeBackup_LeavesTheStoreUnchanged()
    {
        using var store = new Store();
        store.Runner.RunTo(35);

        Should.Throw<DomainException>(() => new MigrationRunner(store.Factory, new FailingBackup()).Run())
            .Code.ShouldBe(ErrorCode.DiskFull);

        using SqliteConnection db = store.Factory.Open();
        db.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(35L);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_schema WHERE name='stock_movement'").ShouldBe(1L);
    }

    // The recovery the upgrade window describes: restore the verified backup taken before the
    // upgrade and upgrade again. The second upgrade carries the same legacy history forward, once.
    [Fact]
    public async Task RestoredPreUpgradeBackup_UpgradesAgainToTheSameDocuments()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var store = new Store();
        store.Runner.RunTo(28);
        using (SqliteConnection db = store.Factory.Open())
            db.Execute("""
                INSERT INTO category(name) VALUES ('Upgrade');
                INSERT INTO product(id,sku,name,category_id,uom_id)
                VALUES (1,'UPGRADE','Upgrade product',(SELECT id FROM category WHERE name='Upgrade'),
                        (SELECT id FROM uom WHERE code='PCS'));
                INSERT INTO product_lot(id,product_id,lot_code,received_date,unit_cost,selling_price,qty_received)
                VALUES (1,1,'ADJUSTED','2026-09-01',1000,2000,5000),(2,1,'DRIFTED','2026-09-01',1000,2000,3000);
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost,ref_type,note)
                VALUES ('2026-09-01',1,1,'OPENING',5000,1000,'opening_import','Opening'),
                       ('2026-09-02',1,1,'ADJUSTMENT',-1000,1000,'manual_adjustment','Shelf loss'),
                       ('2026-09-01',1,2,'OPENING',3000,1000,'opening_import','Opening');
                UPDATE product_lot SET qty_on_hand=2500 WHERE id=2;
                """);
        using var database = new DbExecutor(store.Factory);
        using var settings = new SettingsService(database, store.Paths, new AuditLog());
        await settings.LoadAsync(ct);
        var clock = new SystemClock(settings);
        var backups = new BackupService(store.Paths, store.Factory, clock, settings);
        new MigrationRunner(store.Factory, backups).Run();
        string[] firstUpgrade = Documents(store.Factory);
        string archive = Directory.EnumerateFiles(backups.BackupDirectory, $"*{MigrationRunner.LedgerArchiveReason}-v28*.zip").Single();

        backups.ApplyRestore(backups.InspectRestore(archive));
        store.Factory.EndExclusive(); // The app restarts after a restore.
        var restarted = new SqliteConnectionFactory(store.Paths);
        new BackupService(store.Paths, restarted, clock, settings).FinishPendingRestore();
        using (SqliteConnection restored = restarted.Open())
        {
            restored.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(28L);
            restored.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_movement").ShouldBe(3L);
        }
        new MigrationRunner(restarted, NullMigrationBackup.Instance).Run();

        Documents(restarted).ShouldBe(firstUpgrade);
        using SqliteConnection again = restarted.Open();
        LotSourceReconciliation.CheckAll(again, null, cancellationToken: ct).ShouldBeEmpty();
    }

    // Lots, carried-forward corrections and the upgrade's own audit entries, in order.
    private static string[] Documents(SqliteConnectionFactory factory)
    {
        using SqliteConnection db = factory.Open();
        return
        [
            .. db.Query<string>("SELECT id||':'||qty_on_hand FROM product_lot ORDER BY id"),
            .. db.Query<string>("SELECT source_kind||':'||lot_id||':'||quantity_delta FROM stock_correction ORDER BY id"),
            .. db.Query<string>("SELECT ref_type FROM legacy_adjustment_source ORDER BY correction_id"),
            .. db.Query<string>("SELECT action FROM audit_log WHERE entity='direct_stock' ORDER BY id"),
        ];
    }

    private static string[] Schema(SqliteConnection db) =>
        [.. db.Query<string>("SELECT type||' '||name||' '||COALESCE(sql,'') FROM sqlite_schema ORDER BY type,name")];

    // Schema 39: every price includes tax. A demo product that excluded it keeps its customer price,
    // which now includes the tax a bill added, capped at MRP; its lots count it as a price edit.
    [Theory]
    [InlineData("REGULAR", 105_000L, 104_000L, 105_000L, 105_000L)]
    [InlineData("COMPOSITION", 100_000L, 100_000L, 100_000L, 100_000L)]
    public void Migration39_MakesExcludingProductsInclusive_CappedAtMrp(string mode, long defaultPrice,
        long cappedLot, long uncappedLot, long noMrpLot)
    {
        using var store = new Store();
        store.Runner.RunTo(38);
        using (SqliteConnection db = store.Factory.Open())
            db.Execute("""
                UPDATE setting SET value=@mode WHERE key='tax.mode';
                INSERT INTO category(name) VALUES ('Demo');
                INSERT INTO product(id,sku,name,category_id,uom_id,tax_rate_id,price_includes_tax,
                    default_selling_price,default_mrp)
                VALUES (1,'EXCL','Excluded',(SELECT id FROM category WHERE name='Demo'),(SELECT id FROM uom WHERE code='PCS'),
                        (SELECT id FROM tax_rate WHERE rate_pct=5000),0,100000,110000),
                       (2,'INCL','Included',(SELECT id FROM category WHERE name='Demo'),(SELECT id FROM uom WHERE code='PCS'),
                        (SELECT id FROM tax_rate WHERE rate_pct=5000),1,100000,110000);
                INSERT INTO product_lot(id,product_id,lot_code,received_date,unit_cost,selling_price,mrp,qty_received)
                VALUES (1,1,'CAPPED','2026-09-01',60000,100000,104000,0),
                       (2,1,'ROOM','2026-09-01',60000,100000,120000,0),
                       (3,1,'NO-MRP','2026-09-01',60000,100000,NULL,0),
                       (4,2,'INCLUDED','2026-09-01',60000,100000,104000,0);
                """, new { mode });

        store.Runner.Run();

        using SqliteConnection upgraded = store.Factory.Open();
        upgraded.Query<long>("SELECT price_includes_tax FROM product ORDER BY id").ShouldBe([1L, 1L]);
        upgraded.ExecuteScalar<long>("SELECT default_selling_price FROM product WHERE id=1").ShouldBe(defaultPrice);
        upgraded.Query<long>("SELECT selling_price FROM product_lot ORDER BY id")
            .ShouldBe([cappedLot, uncappedLot, noMrpLot, 100_000L]);
        // A changed price is a lot edit, so a sale saved with the old price no longer matches the lot.
        long[] versions = mode == "REGULAR" ? [2L, 2L, 2L, 1L] : [1L, 1L, 1L, 1L];
        upgraded.Query<long>("SELECT detail_version FROM product_lot ORDER BY id").ShouldBe(versions);
        Should.Throw<SqliteException>(() => upgraded.Execute("UPDATE product SET price_includes_tax=0 WHERE id=2"));
    }

    [Fact]
    public void BarcodeLotIndex_ServesTheLotDetachAndTheForeignKeyCheck()
    {
        using var store = new Store();
        store.Runner.Run();
        using SqliteConnection db = store.Factory.Open();
        string Plan(string sql) => string.Join(" | ", db.Query("EXPLAIN QUERY PLAN " + sql)
            .Select(static row => (string)((IDictionary<string, object>)row)["detail"]));

        // trg_lot_barcode_detach_v29 runs this for every deleted lot; it used to scan every barcode.
        Plan("UPDATE product_barcode SET lot_id=NULL WHERE lot_id=1").ShouldContain("ix_product_barcode_lot");
        Plan("SELECT 1 FROM product_barcode WHERE lot_id=1 AND product_id=1").ShouldContain("ix_product_barcode_lot");
    }

    // A pre-upgrade backup that cannot be written, as on a full disk.
    private sealed class FailingBackup : IMigrationBackup
    {
        public void CreateBeforeMigration(string reason) => throw new DomainException(ErrorCode.DiskFull);
    }

    private sealed class RecordingBackup : IMigrationBackup
    {
        public List<string> Reasons { get; } = [];

        public void CreateBeforeMigration(string reason) => Reasons.Add(reason);
    }

    // Records each report on the reporting thread, unlike Progress<T>, which posts it later.
    private sealed class RecordingProgress : IProgress<MigrationProgress>
    {
        public List<MigrationProgress> Reports { get; } = [];

        public void Report(MigrationProgress value) => Reports.Add(value);
    }

    private sealed class Store : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"grocery-upgrade-{Guid.NewGuid():N}");

        public Store()
        {
            Paths = new AppPaths(new SettingsBootstrap(_root));
            Factory = new SqliteConnectionFactory(Paths);
            Runner = new MigrationRunner(Factory, NullMigrationBackup.Instance);
        }

        public AppPaths Paths { get; }
        public SqliteConnectionFactory Factory { get; }
        public MigrationRunner Runner { get; }

        public void Dispose()
        {
            Factory.ClearPool();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
