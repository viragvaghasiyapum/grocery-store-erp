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

public sealed class CompactionTests
{
    [Fact]
    public void Compact_ReclaimsFreePages_KeepsEveryRecord_AndKeepsTheOriginalUntilTheNextLaunch()
    {
        using var fixture = new CompactionFixture();
        CompactionEstimate before = fixture.Backups.EstimateCompaction();
        before.ReclaimableBytes.ShouldBeGreaterThan(4L * 1024 * 1024);
        before.HasSpace.ShouldBeTrue();
        fixture.Backups.RequestCompaction();
        fixture.Backups.HasCompactionRequest.ShouldBeTrue();
        var stages = new List<CompactionStage>();

        CompactionResult result = fixture.Backups.Compact(new InlineProgress(step => stages.Add(step.Stage)),
            TestContext.Current.CancellationToken);

        result.BytesAfter.ShouldBeLessThan(result.BytesBefore - before.ReclaimableBytes / 2);
        stages.Distinct().ShouldBe([CompactionStage.Copying, CompactionStage.Verifying,
            CompactionStage.BackingUp, CompactionStage.Swapping]);
        fixture.Backups.HasCompactionRequest.ShouldBeFalse();
        fixture.Backups.HasPendingRestore.ShouldBeFalse();
        fixture.Factory.IsExclusive.ShouldBeFalse();
        using (SqliteConnection compacted = fixture.Factory.Open())
        {
            compacted.ExecuteScalar<long>("PRAGMA freelist_count").ShouldBe(0L);
            compacted.ExecuteScalar<string>("PRAGMA journal_mode").ShouldBe("wal");
            compacted.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
            compacted.ExecuteScalar<string>("SELECT value FROM setting WHERE key='store.name'").ShouldBe("Compaction store");
            compacted.ExecuteScalar<long>("SELECT COUNT(*) FROM category").ShouldBe(fixture.Categories);
            compacted.ExecuteScalar<string>("SELECT changes FROM audit_log WHERE action='COMPACT'")!
                .ShouldContain("\"bytesBefore\"");
        }
        BackupService.Verify(result.BackupPath, TestContext.Current.CancellationToken).Reason.ShouldBe("pre-compact");
        // The original stays beside the store until a later launch opens the compact copy.
        string original = Directory.EnumerateDirectories(fixture.Paths.DbDir, "replaced-*").Single();
        File.Exists(Path.Combine(original, "grocery.db")).ShouldBeTrue();
        fixture.Backups.PurgeCompactedOriginals();
        Directory.Exists(original).ShouldBeFalse();
        Directory.EnumerateDirectories(fixture.Paths.TempDir, "compact-*").ShouldBeEmpty();
    }

    [Fact]
    public void Compact_WithoutDiskSpace_StopsBeforeChangingTheStore()
    {
        using var fixture = new CompactionFixture();
        fixture.Backups.AvailableFreeSpace = static _ => 0;
        long bytes = new FileInfo(fixture.Paths.DatabaseFile).Length;

        Should.Throw<DomainException>(fixture.Backups.RequestCompaction).Code.ShouldBe(ErrorCode.DiskFull);
        Should.Throw<DomainException>(() => fixture.Backups.Compact(null, TestContext.Current.CancellationToken))
            .Code.ShouldBe(ErrorCode.DiskFull);

        new FileInfo(fixture.Paths.DatabaseFile).Length.ShouldBe(bytes);
        fixture.Factory.IsExclusive.ShouldBeFalse();
        Directory.EnumerateFiles(fixture.Backups.BackupDirectory).ShouldBeEmpty();
        Directory.EnumerateDirectories(fixture.Paths.TempDir, "compact-*").ShouldBeEmpty();
        using SqliteConnection store = fixture.Factory.Open();
        store.ExecuteScalar<long>("PRAGMA freelist_count").ShouldBeGreaterThan(0L);
    }

    [Fact]
    public void Compact_Cancelled_KeepsTheStoreAndDropsTheRequest()
    {
        using var fixture = new CompactionFixture();
        fixture.Backups.RequestCompaction();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(() => fixture.Backups.Compact(null, cancellation.Token));

        fixture.Backups.HasCompactionRequest.ShouldBeFalse();
        fixture.Factory.IsExclusive.ShouldBeFalse();
        Directory.EnumerateFiles(fixture.Backups.BackupDirectory).ShouldBeEmpty();
        using SqliteConnection store = fixture.Factory.Open();
        store.ExecuteScalar<long>("PRAGMA freelist_count").ShouldBeGreaterThan(0L);
    }

    [Fact]
    public void Compact_InterruptedSwap_RestoresTheOriginal_AndTheNextAttemptSucceeds()
    {
        using var fixture = new CompactionFixture();
        fixture.Backups.RequestCompaction();
        fixture.Backups.RestoreBoundary = step =>
        {
            if (step == "compact-installed") throw new IOException("The disk is full.", unchecked((int)0x80070070));
        };

        Should.Throw<IOException>(() => fixture.Backups.Compact(null, TestContext.Current.CancellationToken));

        fixture.Backups.HasPendingRestore.ShouldBeFalse();
        fixture.Factory.IsExclusive.ShouldBeFalse();
        fixture.Backups.HasCompactionRequest.ShouldBeTrue();
        using (SqliteConnection original = fixture.Factory.Open())
        {
            original.ExecuteScalar<long>("PRAGMA freelist_count").ShouldBeGreaterThan(0L);
            original.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
        }
        fixture.Backups.RestoreBoundary = null;
        fixture.Backups.Compact(null, TestContext.Current.CancellationToken).BytesAfter
            .ShouldBeLessThan(new FileInfo(Path.Combine(
                Directory.EnumerateDirectories(fixture.Paths.DbDir, "replaced-*")
                    .Single(directory => File.Exists(Path.Combine(directory, "grocery.db"))), "grocery.db")).Length);
        fixture.Backups.HasCompactionRequest.ShouldBeFalse();
    }

    [Fact]
    public void Compact_CrashAfterTheSwap_IsFinishedAtTheNextLaunch()
    {
        using var fixture = new CompactionFixture();
        fixture.Backups.RequestCompaction();
        fixture.Backups.RestoreBoundary = step =>
        {
            if (step == "compact-marker-applied") throw new IOException("Power lost.");
        };
        Should.Throw<IOException>(() => fixture.Backups.Compact(null, TestContext.Current.CancellationToken));
        // The process ended with the swap marker in place; the next launch starts without the barrier.
        fixture.Backups.HasPendingRestore.ShouldBeTrue();
        fixture.Factory.EndExclusive();
        fixture.Backups.RestoreBoundary = null;

        fixture.Backups.FinishPendingCompaction();

        fixture.Backups.HasPendingRestore.ShouldBeFalse();
        fixture.Backups.HasCompactionRequest.ShouldBeFalse();
        fixture.Factory.IsExclusive.ShouldBeFalse();
        using SqliteConnection compacted = fixture.Factory.Open();
        compacted.ExecuteScalar<long>("PRAGMA freelist_count").ShouldBe(0L);
        compacted.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE action='COMPACT'").ShouldBe(1L);
        compacted.ExecuteScalar<long>("SELECT COUNT(*) FROM category").ShouldBe(fixture.Categories);
    }

    [Fact]
    public void Compact_StopsRetrying_AfterTwoInterruptedAttempts()
    {
        using var fixture = new CompactionFixture();
        fixture.Backups.RequestCompaction();
        fixture.Backups.RestoreBoundary = step =>
        {
            if (step == "compact-marker-preparing") throw new IOException("Interrupted.");
        };
        Should.Throw<IOException>(() => fixture.Backups.Compact(null, TestContext.Current.CancellationToken));
        Should.Throw<IOException>(() => fixture.Backups.Compact(null, TestContext.Current.CancellationToken));

        Should.Throw<DomainException>(() => fixture.Backups.Compact(null, TestContext.Current.CancellationToken))
            .Code.ShouldBe(ErrorCode.CompactionFailed);
        fixture.Backups.HasCompactionRequest.ShouldBeFalse();
        using SqliteConnection store = fixture.Factory.Open();
        store.ExecuteScalar<string>("PRAGMA quick_check").ShouldBe("ok");
    }

    [Theory]
    [InlineData(3000, 1000, true)]
    [InlineData(3000, 63, false)]
    [InlineData(3000, 200, false)]
    [InlineData(500, 64, true)]
    public void Estimate_OffersCompaction_OnlyForMuchUnusedSpace(long databaseMib, long reclaimableMib, bool worthwhile)
    {
        const long mib = 1024L * 1024;
        new CompactionEstimate(databaseMib * mib, reclaimableMib * mib, (databaseMib - reclaimableMib) * mib, 0, 0)
            .IsWorthwhile.ShouldBe(worthwhile);
    }

    private sealed class InlineProgress(Action<CompactionProgress> report) : IProgress<CompactionProgress>
    {
        public void Report(CompactionProgress value) => report(value);
    }

    // A store whose dropped scratch table left many free pages, as the ledger removal does.
    private sealed class CompactionFixture : IDisposable
    {
        public CompactionFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-compact-{Guid.NewGuid():N}");
            Paths = new AppPaths(new SettingsBootstrap(Root));
            Factory = new SqliteConnectionFactory(Paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            using (SqliteConnection connection = Factory.Open())
            {
                connection.Execute("""
                    UPDATE setting SET value='Compaction store' WHERE key='store.name';
                    CREATE TABLE scratch(payload TEXT NOT NULL);
                    WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<4000)
                    INSERT INTO scratch(payload) SELECT hex(randomblob(1000)) FROM n;
                    DROP TABLE scratch;
                    WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<25)
                    INSERT INTO category(name) SELECT 'Compaction '||i FROM n;
                    PRAGMA wal_checkpoint(TRUNCATE);
                    """);
                Categories = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM category");
            }
            Settings = new SettingsService(Database, Paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            Backups = new BackupService(Paths, Factory, new SystemClock(Settings), Settings);
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public BackupService Backups { get; }
        public long Categories { get; }

        public void Dispose()
        {
            Settings.Dispose();
            Database.Dispose();
            Factory.ClearPool();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
