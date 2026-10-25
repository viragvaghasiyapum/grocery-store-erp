using Dapper;
using GroceryErp.Core.Errors;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Infrastructure;

/// <summary>
/// An upgrade an existing store needs before this version can open it, measured before anything
/// changes so the owner can confirm it.
/// </summary>
/// <param name="FreeBytes">Free space on the data volume, or -1 when the volume cannot report it.</param>
/// <param name="KeepsLedgerArchive">The backup taken first is the last copy of the stock ledger and is kept.</param>
/// <param name="UiLanguage">The store's interface language, so the confirmation is shown in it.</param>
public sealed record MigrationPlan(int FromVersion, int ToVersion, long DatabaseBytes, long FreeBytes,
    long RequiredBytes, TimeSpan Estimate, bool KeepsLedgerArchive, string? UiLanguage)
{
    public bool HasSpace => FreeBytes < 0 || FreeBytes >= RequiredBytes;
}

public enum MigrationStage { Backup, Upgrade, Finishing }

/// <summary>Where a running upgrade is: the backup first, then each schema step in turn.</summary>
public sealed record MigrationProgress(MigrationStage Stage, int Step = 0, int Steps = 0);

public sealed partial class MigrationRunner
{
    /// <summary>
    /// Describes the upgrade the store needs, or returns null when it is current or new. Reads
    /// only; a store newer than this version is refused as it is by <see cref="Run"/>.
    /// </summary>
    public MigrationPlan? Plan(Func<string, long>? freeSpace = null)
    {
        if (!File.Exists(factory.DatabaseFile)) return null;
        DapperSetup.Register();
        long current;
        string? language;
        using (SqliteConnection connection = factory.Open())
        {
            current = connection.ExecuteScalar<long>("PRAGMA user_version;");
            if (connection.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='direct_stock_sandbox'") != 0)
                throw new DomainException(ErrorCode.DatabaseNewerThanApp, current + 1, LatestVersion);
            if (current > LatestVersion)
                throw new DomainException(ErrorCode.DatabaseNewerThanApp, current, LatestVersion);
            // A new store is created, not upgraded; there is nothing to back up or confirm.
            if (current is 0 || current == LatestVersion) return null;
            language = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='setting'") == 0 ? null
                : connection.ExecuteScalar<string?>("SELECT value FROM setting WHERE key='ui.language'");
        }
        int from = (int)current;
        long bytes = FileBytes(factory.DatabaseFile) + FileBytes(factory.DatabaseFile + "-wal");
        long free;
        try
        {
            string volume = Path.GetPathRoot(Path.GetFullPath(factory.DatabaseFile))!;
            free = (freeSpace ?? (static root => new DriveInfo(root).AvailableFreeSpace))(volume);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException
                                              or UnauthorizedAccessException)
        {
            // DriveInfo cannot measure every network mount; a full volume still fails the backup.
            free = -1;
        }
        return new(from, LatestVersion, bytes, free, RequiredBytes(from, LatestVersion, bytes),
            Estimate(from, LatestVersion, bytes), from < 36, language);
    }

    // Seconds per GiB of store for each step, measured on the full-size rehearsal store on the
    // development PC (i7-13700HX): 25 to 28 took 40.6 s and 28 to 29 88.3 s on 2.3-2.4 GB, and 31
    // to 33 69.2 s on 2.9 GB; the other steps took seconds or less. The verified backup taken first
    // ran at 10-40 s per GiB depending on the disk cache, so the slower figure is used. A slower
    // shop PC takes longer, which the confirmation says.
    internal static TimeSpan Estimate(int from, int to, long databaseBytes)
    {
        double gib = databaseBytes / (1024d * 1024 * 1024);
        double seconds = 5 + 40 * gib;
        for (int version = from + 1; version <= to; version++)
            seconds += gib * version switch
            {
                29 => 38,
                32 => 22,
                26 or 27 or 28 => 6,
                30 or 31 or 33 => 1.5,
                <= 25 => 1,
                36 => 0.7,
                _ => 0.1,
            };
        return TimeSpan.FromSeconds(seconds);
    }

    // The backup copies the store and then archives the copy on the same volume, as
    // BackupService.HasBackupCapacity requires; afterwards only the archive stays while the steps
    // write their WAL. Migration 29 rewrote every sale line in one transaction, and its WAL grew to
    // about the size of the store.
    internal static long RequiredBytes(int from, int to, long databaseBytes)
    {
        long reserve = Math.Max(256L * 1024 * 1024, databaseBytes / 10);
        long wal = from < 29 && to >= 29 ? databaseBytes : 64L * 1024 * 1024;
        return Math.Max(2 * databaseBytes, databaseBytes / 2 + wal) + reserve;
    }

    private static long FileBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
