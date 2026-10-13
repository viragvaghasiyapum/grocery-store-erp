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
using GroceryErp.Sales;
using Microsoft.Data.Sqlite;
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
    public async Task Restore_BarrierCoversEveryFactoryAndLiveInspection()
    {
        using var fixture = new FoundationFixture();
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var backup = new BackupService(fixture.Paths, fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        var secondFactory = new SqliteConnectionFactory(fixture.Paths);
        RestoreCandidate candidate = backup.InspectRestore(backup.Create("manual").Path);
        using var held = secondFactory.Open();
        Task apply = Task.Run(() => backup.ApplyRestore(candidate), TestContext.Current.CancellationToken);
        try
        {
            SpinWait.SpinUntil(() => secondFactory.IsExclusive, TimeSpan.FromSeconds(10)).ShouldBeTrue();
            apply.IsCompleted.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => secondFactory.Open());
            Should.Throw<InvalidOperationException>(() => backup.InspectCurrent());
            await Should.ThrowAsync<InvalidOperationException>(() => fixture.Database.ReadAsync(
                connection => connection.ExecuteScalar<long>("SELECT 1"), TestContext.Current.CancellationToken));
            await Should.ThrowAsync<InvalidOperationException>(() => fixture.Database.WriteAsync(
                transaction => transaction.Connection.ExecuteScalar<long>("SELECT 1", transaction: transaction.Transaction),
                TestContext.Current.CancellationToken));
        }
        finally { held.Dispose(); }
        await apply.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // Simulate the new process after a restore restart: its factory must
        // acquire a barrier before it can move the prior database back.
        fixture.Factory.EndExclusive();
        var restarted = new BackupService(fixture.Paths, secondFactory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        restarted.UndoPendingRestore();
        using var reopened = secondFactory.Open();
        reopened.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
    }

    [Fact]
    public async Task Restore_DrainsActiveWriteAndRejectsWriteAlreadyQueuedBehindIt()
    {
        using var fixture = new FoundationFixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task active = fixture.Database.WriteAsync(tx =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken))
                throw new TimeoutException("Write was not released.");
            tx.Connection.Execute("UPDATE setting SET value = 'committed-first' WHERE key = 'store.name'",
                transaction: tx.Transaction);
        }, TestContext.Current.CancellationToken);
        entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
        Task queued = fixture.Database.WriteAsync(tx => tx.Connection.Execute(
            "UPDATE setting SET value = 'must-not-run' WHERE key = 'store.name'", transaction: tx.Transaction),
            TestContext.Current.CancellationToken);
        Task barrier = Task.Run(fixture.Factory.BeginExclusive, TestContext.Current.CancellationToken);
        try
        {
            SpinWait.SpinUntil(() => fixture.Factory.IsExclusive, TimeSpan.FromSeconds(10)).ShouldBeTrue();
            barrier.IsCompleted.ShouldBeFalse();
        }
        finally { release.Set(); }
        await active.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await barrier.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() => queued);
            using var exclusive = fixture.Factory.OpenExclusive();
            exclusive.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'store.name'")
                .ShouldBe("committed-first");
        }
        finally { fixture.Factory.EndExclusive(); }
    }

    [Fact]
    public async Task Restore_WaitsForActivePrintWorkAndRejectsNewJournalRefresh()
    {
        using var fixture = new FoundationFixture();
        var journal = new PrintJobJournal(new SqliteConnectionFactory(fixture.Paths));
        IDisposable printWork = journal.EnterPrintWork();
        Task barrier = Task.Run(fixture.Factory.BeginExclusive, TestContext.Current.CancellationToken);
        try
        {
            SpinWait.SpinUntil(() => fixture.Factory.IsExclusive, TimeSpan.FromSeconds(10)).ShouldBeTrue();
            barrier.IsCompleted.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => journal.ReviewCount());
        }
        finally { printWork.Dispose(); }
        await barrier.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try { Should.Throw<InvalidOperationException>(() => journal.EnterPrintWork()); }
        finally { fixture.Factory.EndExclusive(); }
        journal.ReviewCount().ShouldBe(0);
    }

    [Fact]
    public async Task Restore_DiskFullAfterCandidateMoveRecoversOriginalAndReleasesBarrier()
    {
        using var fixture = new FoundationFixture();
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var backup = new BackupService(fixture.Paths, fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        RestoreCandidate candidate = backup.InspectRestore(backup.Create("manual").Path);
        await fixture.Database.WriteAsync(tx => tx.Connection.Execute(
            "UPDATE setting SET value = 'original-after-backup' WHERE key = 'store.name'",
            transaction: tx.Transaction), TestContext.Current.CancellationToken);
        backup.RestoreBoundary = step =>
        {
            if (step == "candidate-installed")
                throw new IOException("The disk is full.", unchecked((int)0x80070070));
        };

        Should.Throw<IOException>(() => backup.ApplyRestore(candidate));
        backup.HasPendingRestore.ShouldBeFalse();
        using var recovered = fixture.Factory.Open();
        recovered.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'store.name'")
            .ShouldBe("original-after-backup");
        recovered.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
        recovered.Query("PRAGMA foreign_key_check").ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_RetriesOnlyBeforeWorkWhenExternalWriterReleasesLock()
    {
        using var fixture = new FoundationFixture();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Paths.DatabaseFile,
            Pooling = false,
        };
        using var external = new SqliteConnection(builder.ToString());
        external.Open();
        using var lockTransaction = external.BeginTransaction(deferred: false);
        int executions = 0;
        Task write = fixture.Database.WriteAsync(transaction =>
        {
            Interlocked.Increment(ref executions);
            transaction.Connection.Execute("UPDATE setting SET value = 'after-lock' WHERE key = 'store.name'",
                transaction: transaction.Transaction);
        }, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(5400), TestContext.Current.CancellationToken);
        lockTransaction.Rollback();
        await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        executions.ShouldBe(1);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'store.name'").ShouldBe("after-lock");
    }

    [Fact]
    public async Task FullDatabase_ReportsDiskFullWithoutCommittingPartialWrite()
    {
        using var fixture = new FoundationFixture();
        using (var connection = fixture.Factory.Open())
        {
            long pages = connection.ExecuteScalar<long>("PRAGMA page_count");
            connection.Execute($"PRAGMA max_page_count = {pages}");
        }
        var failure = await Should.ThrowAsync<GroceryErp.Core.Errors.DomainException>(() =>
            fixture.Database.WriteAsync(transaction =>
                transaction.Connection.Execute("""
                    INSERT INTO audit_log(entity, entity_id, action, changes)
                    VALUES ('fault', 0, 'FULL', @changes)
                    """, new { changes = new string('x', 2_000_000) }, transaction.Transaction),
                TestContext.Current.CancellationToken));
        failure.Code.ShouldBe(GroceryErp.Core.Errors.ErrorCode.DiskFull);
        using var check = fixture.Factory.Open();
        check.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity = 'fault'").ShouldBe(0L);
    }

    [Fact]
    public async Task CorruptStore_ReportsRecoveryAction()
    {
        using var fixture = new FoundationFixture();
        fixture.Factory.ClearPool();
        using (var file = new FileStream(fixture.Paths.DatabaseFile, FileMode.Open, FileAccess.Write, FileShare.None))
            file.Write(new byte[16]);
        var failure = await Should.ThrowAsync<GroceryErp.Core.Errors.DomainException>(() =>
            fixture.Database.ReadAsync(connection => connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale"),
                TestContext.Current.CancellationToken));
        failure.Code.ShouldBe(GroceryErp.Core.Errors.ErrorCode.DatabaseCorrupt);
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
    public async Task Restore_RejectsStagedFileChangedAfterInspectionAndReopensStore()
    {
        using var fixture = new FoundationFixture();
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var backup = new BackupService(fixture.Paths, fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        RestoreCandidate candidate = backup.InspectRestore(backup.Create("manual").Path);
        try
        {
            using (var file = File.Open(candidate.StagedDatabase, FileMode.Append)) file.WriteByte(1);
            Should.Throw<GroceryErp.Core.Errors.DomainException>(() => backup.ApplyRestore(candidate));
            backup.HasPendingRestore.ShouldBeFalse();
            using var connection = fixture.Factory.Open();
            connection.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
        }
        finally { backup.DiscardRestoreCandidate(candidate); }
    }

    [Fact]
    public async Task PendingRestore_ColdLaunchFinishesUnderFreshFactoryBarrier()
    {
        using var fixture = new FoundationFixture();
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var clock = new GroceryErp.Core.Time.SystemClock(settings);
        var backup = new BackupService(fixture.Paths, fixture.Factory, clock, settings);
        backup.ApplyRestore(backup.InspectRestore(backup.Create("manual").Path));
        fixture.Factory.EndExclusive(); // Simulate process exit after the swap.
        var freshFactory = new SqliteConnectionFactory(fixture.Paths);
        var resumed = new BackupService(fixture.Paths, freshFactory, clock, settings);
        resumed.FinishPendingRestore();
        resumed.HasPendingRestore.ShouldBeFalse();
        using var connection = freshFactory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity = 'restore'").ShouldBe(1L);
    }

    [Fact]
    public async Task PendingRestore_CorruptInstalledStoreAutomaticallyRecoversPreviousStore()
    {
        using var fixture = new FoundationFixture();
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var clock = new GroceryErp.Core.Time.SystemClock(settings);
        var backup = new BackupService(fixture.Paths, fixture.Factory, clock, settings);
        RestoreCandidate candidate = backup.InspectRestore(backup.Create("manual").Path);
        await fixture.Database.WriteAsync(transaction => transaction.Connection.Execute(
            "UPDATE setting SET value = 'original-latest' WHERE key = 'store.name'",
            transaction: transaction.Transaction), TestContext.Current.CancellationToken);
        backup.ApplyRestore(candidate);
        fixture.Factory.EndExclusive(); // Simulate a stopped process before validation.
        using (var file = new FileStream(fixture.Paths.DatabaseFile, FileMode.Open, FileAccess.Write, FileShare.None))
            file.Write(new byte[16]);
        var freshFactory = new SqliteConnectionFactory(fixture.Paths);
        var resumed = new BackupService(fixture.Paths, freshFactory, clock, settings);
        var failure = Should.Throw<GroceryErp.Core.Errors.DomainException>(() => resumed.FinishPendingRestore());
        failure.Code.ShouldBe(GroceryErp.Core.Errors.ErrorCode.RestoreRecovered);
        resumed.HasPendingRestore.ShouldBeFalse();
        using var connection = freshFactory.Open();
        connection.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'store.name'")
            .ShouldBe("original-latest");
    }

    [Fact]
    public async Task RecoverInterruptedRestore_InstalledCandidateRestoresOriginalBeforeStartup()
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
        marker["stage"] = "original-moved";
        File.WriteAllText(markerPath, marker.ToJsonString(ManifestJson));

        fixture.Factory.EndExclusive(); // The interrupted process no longer owns the barrier.
        var restarted = new BackupService(fixture.Paths, new SqliteConnectionFactory(fixture.Paths),
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        restarted.RecoverInterruptedRestoreBeforeStartup();

        backup.HasPendingRestore.ShouldBeFalse();
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'store.name'")
            .ShouldBe("latest-state");
    }

    [Fact]
    public async Task RecoverInterruptedRestore_MarkerBeforeFirstMoveKeepsOriginal()
    {
        using var fixture = new FoundationFixture();
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var backup = new BackupService(fixture.Paths, fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        BackupInfo saved = backup.Create("manual");
        string replaced = Path.Combine(fixture.Paths.DbDir, "replaced-before-first-move");
        Directory.CreateDirectory(replaced);
        string marker = Path.Combine(fixture.Paths.DbDir, "restore.pending.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            sourceZip = saved.Path,
            replacedDirectory = replaced,
            manifest = saved.Manifest,
            stage = "preparing",
        }, ManifestJson));
        backup.RecoverInterruptedRestoreBeforeStartup();
        backup.HasPendingRestore.ShouldBeFalse();
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
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
