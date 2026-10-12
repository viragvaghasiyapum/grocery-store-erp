using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class FoundationsTests
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void FreshDatabase_AppliesAllMigrationsAndSeedsRequiredTables()
    {
        using var fixture = new FoundationFixture();

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("PRAGMA user_version").ShouldBe((long)MigrationRunner.LatestVersion);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM setting").ShouldBeGreaterThan(0);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM operation").ShouldBe(0L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM uom").ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ExistingVersionFourDatabase_UpgradesWithoutLosingMoneyOutData()
    {
        string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-upgrade-{Guid.NewGuid():N}");
        try
        {
            var factory = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root)));
            string[] resources = typeof(MigrationRunner).Assembly.GetManifestResourceNames()
                .Where(static name => name.Contains(".db.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Take(4)
                .ToArray();
            resources.Length.ShouldBe(4);
            using (var connection = factory.Open())
            {
                foreach (string resource in resources)
                {
                    using Stream stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(resource)
                        ?? throw new InvalidOperationException(resource);
                    using var reader = new StreamReader(stream);
                    connection.Execute(reader.ReadToEnd());
                }
                long category = connection.ExecuteScalar<long>("SELECT id FROM expense_category ORDER BY id LIMIT 1");
                connection.Execute("""
                    INSERT INTO expense(expense_date,category_id,amount,mode,paid_to)
                    VALUES ('2026-09-01',@category,100000,'CASH','Existing row')
                    """, new { category });
                connection.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(4L);
            }

            new MigrationRunner(factory, NullMigrationBackup.Instance).Run();

            using var upgraded = factory.Open();
            upgraded.ExecuteScalar<long>("PRAGMA user_version").ShouldBe((long)MigrationRunner.LatestVersion);
            upgraded.ExecuteScalar<long>("SELECT COUNT(*) FROM expense").ShouldBe(1L);
            upgraded.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('damage') WHERE name='bulk_operation_id'").ShouldBe(1L);
        }
        finally
        {
            SqliteConnectionFactory.ClearPoolForDataRoot(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OperationGuard_SecondClaimReturnsOriginalReference()
    {
        using var fixture = new FoundationFixture();
        const string operationId = "0123456789abcdef0123456789abcdef";

        await fixture.Database.WriteAsync(transaction =>
        {
            OperationGuard.Claim(transaction, operationId, OpType.Damage).ShouldBeNull();
            OperationGuard.Complete(transaction, operationId, "damage", 42);
        }, TestContext.Current.CancellationToken);

        OperationRef? replay = await fixture.Database.WriteAsync(transaction =>
            OperationGuard.Claim(transaction, operationId, OpType.Damage), TestContext.Current.CancellationToken);
        replay.ShouldNotBeNull();
        replay!.RefType.ShouldBe("damage");
        replay.RefId.ShouldBe(42L);
    }

    [Fact]
    public async Task Backup_CreateVerifyAndInspectRestore_RoundTripsManifest()
    {
        using var fixture = new FoundationFixture();
        var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var clock = new GroceryErp.Core.Time.SystemClock(settings);
        var backup = new BackupService(fixture.Paths, fixture.Factory, clock, settings);

        BackupInfo created = backup.Create("manual");
        BackupManifest verified = BackupService.Verify(created.Path);
        RestoreCandidate candidate = backup.InspectRestore(created.Path);
        backup.ApplyRestore(candidate);
        backup.FinishPendingRestore();

        verified.SchemaVersion.ShouldBe((long)MigrationRunner.LatestVersion);
        candidate.Manifest.Database.Sha256.ShouldBe(verified.Database.Sha256);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity = 'restore' AND action = 'RESTORE'")
            .ShouldBe(1L);
    }

    [Fact]
    public async Task Restore_WaitsForOpenConnectionAndBlocksNewDatabaseWork()
    {
        using var fixture = new FoundationFixture();
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var backup = new BackupService(fixture.Paths, fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        RestoreCandidate candidate = backup.InspectRestore(backup.Create("manual").Path);
        using var held = fixture.Factory.Open();
        Task apply = Task.Run(() => backup.ApplyRestore(candidate), TestContext.Current.CancellationToken);
        try
        {
            SpinWait.SpinUntil(() => fixture.Factory.IsExclusive, TimeSpan.FromSeconds(10)).ShouldBeTrue();
            apply.IsCompleted.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => fixture.Factory.Open());
        }
        finally { held.Dispose(); }
        await apply.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        backup.UndoPendingRestore();
        using var reopened = fixture.Factory.Open();
        reopened.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
    }

    [Fact]
    public async Task RestoreUndo_PutsBackDatabaseThatWasReplaced()
    {
        using var fixture = new FoundationFixture();
        await fixture.Database.WriteAsync(transaction => transaction.Connection.Execute(
            "UPDATE setting SET value = 'before-backup' WHERE key = 'store.name'",
            transaction: transaction.Transaction), TestContext.Current.CancellationToken);
        var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var backup = new BackupService(
            fixture.Paths,
            fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings),
            settings);
        BackupInfo created = backup.Create("manual");

        await fixture.Database.WriteAsync(transaction => transaction.Connection.Execute(
            "UPDATE setting SET value = 'current-data' WHERE key = 'store.name'",
            transaction: transaction.Transaction), TestContext.Current.CancellationToken);
        backup.ApplyRestore(backup.InspectRestore(created.Path));
        backup.UndoPendingRestore();

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'store.name'")
            .ShouldBe("current-data");
        backup.HasPendingRestore.ShouldBeFalse();
    }

    [Fact]
    public async Task RecoverInterruptedRestore_PreparingMarkerRestoresOriginalBeforeStartup()
    {
        using var fixture = new FoundationFixture();
        var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var backup = new BackupService(fixture.Paths, fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        await fixture.Database.WriteAsync(transaction => transaction.Connection.Execute(
            "UPDATE setting SET value = 'backup-state' WHERE key = 'store.name'",
            transaction: transaction.Transaction), TestContext.Current.CancellationToken);
        BackupInfo saved = backup.Create("manual");
        await fixture.Database.WriteAsync(transaction => transaction.Connection.Execute(
            "UPDATE setting SET value = 'latest-state' WHERE key = 'store.name'",
            transaction: transaction.Transaction), TestContext.Current.CancellationToken);

        backup.ApplyRestore(backup.InspectRestore(saved.Path));
        string markerPath = Path.Combine(fixture.Paths.DbDir, "restore.pending.json");
        JsonNode marker = JsonNode.Parse(File.ReadAllText(markerPath))!;
        marker["stage"] = "preparing";
        File.WriteAllText(markerPath, marker.ToJsonString(ManifestJson));

        backup.RecoverInterruptedRestoreBeforeStartup();

        backup.HasPendingRestore.ShouldBeFalse();
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'store.name'")
            .ShouldBe("latest-state");
    }

    [Fact]
    public async Task InspectRestore_RejectsTamperedSummaryAndMissingManifest()
    {
        using var fixture = new FoundationFixture();
        var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var backup = new BackupService(fixture.Paths, fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        BackupInfo saved = backup.Create("manual");
        string tampered = Path.Combine(fixture.Paths.TempDir, "tampered.zip");
        File.Copy(saved.Path, tampered);
        using (ZipArchive archive = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.GetEntry("manifest.json")!;
            JsonNode manifest;
            using (Stream source = entry.Open()) manifest = JsonNode.Parse(source)!;
            entry.Delete();
            manifest["summary"]!["products"] = 123456;
            using Stream destination = archive.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(destination, manifest);
        }
        Should.Throw<GroceryErp.Core.Errors.DomainException>(() => backup.InspectRestore(tampered));

        using (ZipArchive archive = ZipFile.Open(tampered, ZipArchiveMode.Update))
            archive.GetEntry("manifest.json")!.Delete();
        Should.Throw<GroceryErp.Core.Errors.DomainException>(() => BackupService.Verify(tampered));
    }

    [Fact]
    public async Task RecoverInterruptedRestore_WhenOriginalFilesAreSplit_PreservesWal()
    {
        using var fixture = new FoundationFixture();
        var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var backup = new BackupService(fixture.Paths, fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        BackupInfo saved = backup.Create("manual");
        fixture.Factory.ClearPool();
        string replaced = Path.Combine(fixture.Paths.DbDir, "replaced-interrupted");
        Directory.CreateDirectory(replaced);
        byte[] wal = [1, 2, 3, 4];
        File.WriteAllBytes(fixture.Paths.DatabaseFile + "-wal", wal);
        File.Move(fixture.Paths.DatabaseFile, Path.Combine(replaced, "grocery.db"));
        string marker = Path.Combine(fixture.Paths.DbDir, "restore.pending.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            sourceZip = saved.Path,
            replacedDirectory = replaced,
            manifest = saved.Manifest,
            stage = "preparing",
        }, ManifestJson));

        backup.RecoverInterruptedRestoreBeforeStartup();

        File.Exists(fixture.Paths.DatabaseFile).ShouldBeTrue();
        File.ReadAllBytes(fixture.Paths.DatabaseFile + "-wal").ShouldBe(wal);
        backup.HasPendingRestore.ShouldBeFalse();
        File.Delete(fixture.Paths.DatabaseFile + "-wal");
    }

    [Fact]
    public async Task Settings_ConcurrentSavesKeepCompleteSnapshot()
    {
        using var fixture = new FoundationFixture();
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);

        await Task.WhenAll(
            settings.SaveAsync(new Dictionary<string, string> { ["store.name"] = "Concurrent Store" }, TestContext.Current.CancellationToken),
            settings.SaveAsync(new Dictionary<string, string> { ["ui.language"] = "gu-IN" }, TestContext.Current.CancellationToken));

        settings.Current.StoreName.ShouldBe("Concurrent Store");
        settings.Current.UiLanguage.ShouldBe("gu-IN");
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity = 'setting' AND action = 'UPDATE'")
            .ShouldBe(2L);
    }

    [Fact]
    public async Task StartupMaintenance_RunsIntegrityChecksAndPurgesExpiredRestoreData()
    {
        using var fixture = new FoundationFixture();
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var clock = new GroceryErp.Core.Time.SystemClock(settings);
        var backups = new BackupService(fixture.Paths, fixture.Factory, clock, settings);
        string expired = Path.Combine(fixture.Paths.DbDir, "replaced-expired");
        Directory.CreateDirectory(expired);
        Directory.SetLastWriteTime(expired, clock.Now.AddDays(-8));
        var maintenance = new MaintenanceService(
            fixture.Paths, fixture.Factory, fixture.Database, backups, clock, new AuditLog());

        await maintenance.RunAfterShellAsync(TestContext.Current.CancellationToken);

        Directory.Exists(expired).ShouldBeFalse();
        Directory.EnumerateFiles(backups.BackupDirectory, "*_daily.zip").ShouldNotBeEmpty();
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'maintenance.integrity_last_run'")
            .ShouldNotBeNullOrWhiteSpace();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity = 'maintenance' AND action = 'INTEGRITY_CHECK'")
            .ShouldBe(1L);

        await maintenance.RunAfterShellAsync(TestContext.Current.CancellationToken);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity = 'maintenance' AND action = 'INTEGRITY_CHECK'")
            .ShouldBe(1L);
    }

    [Fact]
    public void PasswordHasher_HashesAndVerifiesWithoutStoringPlaintext()
    {
        var hasher = new PasswordHasher();
        string stored = hasher.Hash("correct horse battery staple");

        stored.ShouldNotContain("correct horse battery staple");
        hasher.Verify("correct horse battery staple", stored).ShouldBeTrue();
        hasher.Verify("wrong password", stored).ShouldBeFalse();
    }

    [Fact]
    public async Task FirstRun_PersistsWizardConfiguration()
    {
        using var fixture = new FoundationFixture();
        var service = new FirstRunService(fixture.Database, new PasswordHasher());

        await service.CompleteAsync(new FirstRunRequest(
            "owner", "correct horse battery staple", "Test Store", "Address", "9999999999", "", "Gujarat",
            "REGULAR", "en-IN", fixture.Paths.DefaultBackupDir, "20", "21", "Receipt", 384,
            "Labels", "60x40", "A4", 14, "27", true, "IMPORT",
            FirstRunService.RequiredSeedKeys.ToDictionary(key => key, key =>
                SharedStrings.ResourceManager.GetString(key, System.Globalization.CultureInfo.GetCultureInfo("gu-IN"))!)),
            TestContext.Current.CancellationToken);

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'printer.receipt.width_dots'").ShouldBe("384");
        connection.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'backup.usb_reminder_days'").ShouldBe("14");
        connection.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'setup.start_choice'").ShouldBe("IMPORT");
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM app_user").ShouldBe(1L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM uom WHERE name_local IS NOT NULL").ShouldBe(8L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM expense_category WHERE name_local IS NOT NULL").ShouldBe(10L);
        connection.ExecuteScalar<string>("SELECT prefix FROM barcode_rule").ShouldBe("27");
    }

    [Fact]
    public void DataRootMove_CopiesAndChecksDatabaseBeforeSwitchingBootstrap()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), $"grocery-erp-move-{Guid.NewGuid():N}");
        string source = Path.Combine(sandbox, "source");
        string target = Path.Combine(sandbox, "target");
        string bootstrap = Path.Combine(sandbox, "bootstrap.json");
        try
        {
            var paths = new AppPaths(new SettingsBootstrap(source));
            var factory = new SqliteConnectionFactory(paths);
            new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
            using (var connection = factory.Open())
                connection.Execute("UPDATE setting SET value = @directory WHERE key = 'backup.dir'",
                    new { directory = paths.DefaultBackupDir });
            SettingsBootstrap.Save(source, bootstrap);
            DataRootRelocator.Schedule(source, target);
            SettingsBootstrap.Load(bootstrap).DataRoot.ShouldBe(source);
            SqliteConnectionFactory.ClearPoolForDataRoot(source);

            DataRootRelocator.ApplyPending(bootstrap).ShouldBeTrue();

            SettingsBootstrap.Load(bootstrap).DataRoot.ShouldBe(target);
            File.Exists(Path.Combine(target, "db", "grocery.db")).ShouldBeTrue();
            using var moved = new SqliteConnectionFactory(new AppPaths(SettingsBootstrap.Load(bootstrap))).Open();
            moved.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
            moved.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'backup.dir'")
                .ShouldBe(Path.Combine(target, "backups"));
            File.Exists(Path.Combine(source, "db", "grocery.db")).ShouldBeTrue();
        }
        finally
        {
            SqliteConnectionFactory.ClearPoolForDataRoot(source);
            SqliteConnectionFactory.ClearPoolForDataRoot(target);
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void DataRootMove_FinishesAfterCopyWasCommittedBeforeBootstrapSwitch()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), $"grocery-erp-move-recovery-{Guid.NewGuid():N}");
        string source = Path.Combine(sandbox, "source");
        string target = Path.Combine(sandbox, "target");
        string bootstrap = Path.Combine(sandbox, "bootstrap.json");
        try
        {
            var paths = new AppPaths(new SettingsBootstrap(source));
            var factory = new SqliteConnectionFactory(paths);
            new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
            using (var connection = factory.Open()) connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            SqliteConnectionFactory.ClearPoolForDataRoot(source);
            SettingsBootstrap.Save(source, bootstrap);
            DataRootRelocator.Schedule(source, target);
            string copiedDb = Path.Combine(target, "db", "grocery.db");
            Directory.CreateDirectory(Path.Combine(target, "db"));
            File.Copy(paths.DatabaseFile, copiedDb);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copiedDb)));
            File.WriteAllText(Path.Combine(target, "data-move.ready.json"), JsonSerializer.Serialize(new
            {
                Source = source,
                Target = target,
                DatabaseHash = hash,
            }));

            DataRootRelocator.ApplyPending(bootstrap).ShouldBeTrue();

            SettingsBootstrap.Load(bootstrap).DataRoot.ShouldBe(target);
            File.Exists(Path.Combine(source, "data-move.pending.json")).ShouldBeFalse();
            using var moved = new SqliteConnectionFactory(new AppPaths(SettingsBootstrap.Load(bootstrap))).Open();
            moved.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
        }
        finally
        {
            SqliteConnectionFactory.ClearPoolForDataRoot(source);
            SqliteConnectionFactory.ClearPoolForDataRoot(target);
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    private sealed class FoundationFixture : IDisposable
    {
        public FoundationFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-{Guid.NewGuid():N}");
            Paths = new AppPaths(new SettingsBootstrap(Root));
            Factory = new SqliteConnectionFactory(Paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }

        public void Dispose()
        {
            Database.Dispose();
            Factory.ClearPool();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
