using Dapper;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class FoundationsTests
{
    [Fact]
    public void FreshDatabase_AppliesAllMigrationsAndSeedsRequiredTables()
    {
        using var fixture = new FoundationFixture();

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(3L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM setting").ShouldBeGreaterThan(0);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM operation").ShouldBe(0L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM uom").ShouldBeGreaterThan(0);
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

        verified.SchemaVersion.ShouldBe(3L);
        candidate.Manifest.Database.Sha256.ShouldBe(verified.Database.Sha256);
        File.Exists(candidate.StagedDatabase).ShouldBeTrue();
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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
