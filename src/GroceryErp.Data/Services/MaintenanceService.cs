using System.Diagnostics;
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
        using var timing = PerformanceMetrics.TrackMaintenance("startup");
        (string? lastSaleDate, bool lotCacheHealthy) = await database.ReadAsync(connection =>
        {
            string? lastDate = connection.ExecuteScalar<string?>("SELECT MAX(sale_date) FROM sale WHERE status = 'COMPLETED'");
            string? clean = connection.ExecuteScalar<string?>(
                "SELECT value FROM setting WHERE key='maintenance.clean_shutdown'");
            if (connection.ExecuteScalar<string?>(
                    "SELECT value FROM setting WHERE key='maintenance.stock_rebuild_pending'") == "1")
                return (lastDate, false);
            if (clean == "1") return (lastDate, true);
            // An interrupted session needs a bounded pre-login check. The full
            // reconciliation runs after the shell has been idle.
            bool healthy = connection.ExecuteScalar<long>("""
                WITH recent_lots AS MATERIALIZED (
                    SELECT DISTINCT lot_id FROM (
                        SELECT lot_id FROM stock_movement ORDER BY id DESC LIMIT 512
                    )
                )
                SELECT NOT EXISTS (
                    SELECT 1 FROM recent_lots r JOIN product_lot l ON l.id=r.lot_id
                    WHERE l.qty_on_hand <> COALESCE(
                        (SELECT SUM(m.qty) FROM stock_movement m WHERE m.lot_id=r.lot_id),0)
                )
                """) != 0;
            return (lastDate, healthy);
        }, cancellationToken).ConfigureAwait(false);

        return new(HasLowDiskSpace(paths.Root), lastSaleDate, lotCacheHealthy);
    }

    public Task BeginSessionAsync(CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            // This commit is durable before the shell can accept a sale. A crash leaves
            // the marker dirty, while a previous dirty session also queues full repair.
            transaction.Connection.Execute("""
                INSERT INTO setting(key,value) VALUES ('maintenance.recovery_pending',
                    CASE WHEN (SELECT value FROM setting WHERE key='maintenance.clean_shutdown')='1'
                         THEN COALESCE((SELECT value FROM setting WHERE key='maintenance.recovery_pending'),'0')
                         ELSE '1' END)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                INSERT INTO setting(key,value) VALUES ('maintenance.clean_shutdown','0')
                ON CONFLICT(key) DO UPDATE SET value='0';
                """, transaction: transaction.Transaction);
        }, cancellationToken);

    public Task OverrideClockWarningAsync(string lastSaleDate, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            auditLog.Write(transaction, "clock", 0, "CLOCK_WARNING_OVERRIDE", new { lastSaleDate, businessDate = clock.BusinessDate });
        }, cancellationToken);

    public async Task RunAfterShellAsync(CancellationToken cancellationToken = default)
    {
        using var timing = PerformanceMetrics.TrackMaintenance("after-shell");
        await database.WriteAsync(transaction =>
        {
            DateTime cutoff = clock.Now.AddDays(-30);
            OperationGuard.Purge(transaction, cutoff);
        }, cancellationToken).ConfigureAwait(false);

        CleanupTemporaryFiles();
        backups.PurgeExpiredRestoreData();
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasBackupToday())
            backups.Create("daily", cancellationToken);

        if (await IsWeeklyIntegrityCheckDueAsync(cancellationToken).ConfigureAwait(false))
            await RunWeeklyIntegrityChecksAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> IsWeeklyIntegrityCheckDueAsync(CancellationToken cancellationToken) =>
        database.ReadAsync(connection =>
        {
            string? raw = connection.ExecuteScalar<string?>(
                "SELECT value FROM setting WHERE key = 'maintenance.integrity_last_run'");
            string? recovery = connection.ExecuteScalar<string?>(
                "SELECT value FROM setting WHERE key='maintenance.recovery_pending'");
            return recovery == "1" || !DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                       DateTimeStyles.None, out DateOnly previous) || previous > clock.BusinessDate ||
                   previous.AddDays(7) <= clock.BusinessDate;
        }, cancellationToken);

    private async Task RunWeeklyIntegrityChecksAsync(CancellationToken cancellationToken)
    {
        using var timing = PerformanceMetrics.TrackMaintenance("weekly-integrity");
        await database.ReadAsync(connection =>
        {
            string integrity = connection.ExecuteScalar<string>("PRAGMA integrity_check;") ?? "";
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new DomainException(ErrorCode.BackupInvalid, integrity);
            if (connection.Query("PRAGMA foreign_key_check;").Any())
                throw new DomainException(ErrorCode.BackupInvalid, "foreign key check failed");
            if (connection.Query("SELECT lot_id FROM v_lot_cache_mismatch LIMIT 1").Any())
                throw new DomainException(ErrorCode.BackupInvalid, "stock cache mismatch");
            return true;
        }, cancellationToken).ConfigureAwait(false);

        await database.WriteAsync(transaction =>
        {
            using CancellationTokenRegistration interrupt = cancellationToken.Register(
                () => SQLitePCL.raw.sqlite3_interrupt(transaction.Connection.Handle));
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // FTS integrity-check is a write; serialize it with POS writes.
                transaction.Connection.Execute("INSERT INTO product_fts(product_fts) VALUES ('integrity-check');",
                    transaction: transaction.Transaction);
            }
            catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("FTS integrity check was canceled.", exception, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            string date = clock.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            transaction.Connection.Execute("""
                INSERT INTO setting(key, value) VALUES ('maintenance.integrity_last_run', @date)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                ; INSERT INTO setting(key,value) VALUES ('maintenance.recovery_pending','0')
                ON CONFLICT(key) DO UPDATE SET value='0'
                """, new { date }, transaction.Transaction);
            auditLog.Write(transaction, "maintenance", 0, "INTEGRITY_CHECK", new { date, result = "ok" });
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Shutdown()
    {
        using var timing = PerformanceMetrics.TrackMaintenance("shutdown");
        if (!backups.HasRecentVerifiedScheduledBackup(TimeSpan.FromHours(12))) backups.Create("close");
        factory.SampleWalBytes();
        using SqliteConnection connection = factory.Open();
        connection.Execute("PRAGMA optimize;");
        long started = Stopwatch.GetTimestamp();
        try { connection.Execute("PRAGMA wal_checkpoint(TRUNCATE);"); }
        finally { PerformanceMetrics.CheckpointMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
        connection.Execute("""
            INSERT INTO setting(key,value) VALUES ('maintenance.clean_shutdown','1')
            ON CONFLICT(key) DO UPDATE SET value='1'
            """);
        factory.SampleWalBytes();
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
        await maintenance.BeginSessionAsync(cancellationToken).ConfigureAwait(false);
        return new(health, settings.Current);
    }
}

public sealed class ShutdownPipeline(MaintenanceService maintenance)
{
    public Task RunAsync(CancellationToken cancellationToken = default) => Task.Run(maintenance.Shutdown, cancellationToken);
}
