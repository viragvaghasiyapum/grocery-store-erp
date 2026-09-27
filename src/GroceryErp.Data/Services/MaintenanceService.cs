using System.Globalization;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Services;

public sealed record StartupHealth(bool FreeDiskWarning, string? LastSaleDate, bool LotCacheHealthy);

/// Runs the checks and maintenance that are intentionally kept outside the UI thread.
public sealed class MaintenanceService(
    AppPaths paths,
    SqliteConnectionFactory factory,
    DbExecutor database,
    BackupService backups,
    IClock clock,
    IAuditLog auditLog)
{
    public async Task<StartupHealth> CheckBeforeShellAsync(CancellationToken cancellationToken = default)
    {
        (string? lastSaleDate, bool lotCacheHealthy) = await database.ReadAsync(connection =>
        {
            string? lastDate = connection.ExecuteScalar<string?>("SELECT MAX(sale_date) FROM sale WHERE status = 'COMPLETED'");
            bool healthy = !connection.Query("SELECT lot_id FROM v_lot_cache_mismatch LIMIT 1").Any();
            return (lastDate, healthy);
        }, cancellationToken).ConfigureAwait(false);

        return new(HasLowDiskSpace(paths.Root), lastSaleDate, lotCacheHealthy);
    }

    public Task OverrideClockWarningAsync(string lastSaleDate, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            auditLog.Write(transaction, "clock", 0, "CLOCK_WARNING_OVERRIDE", new { lastSaleDate, businessDate = clock.BusinessDate });
        }, cancellationToken);

    public async Task RunAfterShellAsync(CancellationToken cancellationToken = default)
    {
        await database.WriteAsync(transaction =>
        {
            DateTime cutoff = clock.Now.AddDays(-30);
            OperationGuard.Purge(transaction, cutoff);
        }, cancellationToken).ConfigureAwait(false);

        CleanupTemporaryFiles();
        backups.PurgeExpiredRestoreData();
        if (!HasBackupToday())
            backups.Create("daily");

        await database.WriteAsync(transaction =>
        {
            transaction.Connection.Execute("PRAGMA optimize;", transaction: transaction.Transaction);
            string integrity = transaction.Connection.ExecuteScalar<string>(
                "PRAGMA integrity_check;", transaction: transaction.Transaction) ?? "";
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new DomainException(ErrorCode.BackupInvalid, integrity);
            // FTS5 keeps a separate index; checking it with SQLite's supported command catches
            // stale or corrupt product search data before a user relies on search results.
            transaction.Connection.Execute(
                "INSERT INTO product_fts(product_fts) VALUES ('integrity-check');",
                transaction: transaction.Transaction);
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Shutdown()
    {
        backups.Create("close");
        using SqliteConnection connection = factory.Open();
        connection.Execute("PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public static bool HasLowDiskSpace(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        try { return new DriveInfo(root).AvailableFreeSpace < 1_000_000_000L; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool HasBackupToday()
    {
        string directory = backups.BackupDirectory;
        if (!Directory.Exists(directory)) return false;
        string stamp = clock.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Directory.EnumerateFiles(directory, $"GroceryErp_{stamp}_*.zip").Any();
    }

    private void CleanupTemporaryFiles()
    {
        if (!Directory.Exists(paths.TempDir)) return;
        DateTime cutoff = clock.Now.AddDays(-1);
        foreach (string directory in Directory.EnumerateDirectories(paths.TempDir))
            try
            {
                if (Directory.GetLastWriteTime(directory) < cutoff) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

        string backupDirectory = backups.BackupDirectory;
        if (!Directory.Exists(backupDirectory)) return;
        foreach (string partial in Directory.EnumerateFiles(backupDirectory, "*.partial"))
            try
            {
                if (File.GetLastWriteTime(partial) < cutoff) File.Delete(partial);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }
}

public sealed record StartupState(StartupHealth Health, AppSettings Settings);

public sealed class StartupPipeline(
    SqliteConnectionFactory factory,
    MigrationRunner migrations,
    SettingsService settings,
    MaintenanceService maintenance)
{
    public async Task<StartupState> RunAsync(CancellationToken cancellationToken = default)
    {
        DatabaseCapabilities.Verify(factory);
        migrations.Run();
        await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        StartupHealth health = await maintenance.CheckBeforeShellAsync(cancellationToken).ConfigureAwait(false);
        return new(health, settings.Current);
    }
}

public sealed class ShutdownPipeline(MaintenanceService maintenance)
{
    public Task RunAsync(CancellationToken cancellationToken = default) => Task.Run(maintenance.Shutdown, cancellationToken);
}
