using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using GroceryErp.Core.Errors;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Infrastructure;

/// <summary>Moves a closed database on the next startup and changes the bootstrap only after validation.</summary>
public static class DataRootRelocator
{
    private const long CopyReserveBytes = 64L * 1024 * 1024;
    private const string MarkerName = "data-move.pending.json";
    private const string ReadyMarkerName = "data-move.ready.json";
    private const string StageMarkerName = "data-move.stage.json";
    private sealed record PendingMove(string Source, string Target);
    private sealed record ReadyMove(string Source, string Target, string DatabaseHash);

    public static bool HasPending(string sourceRoot) => File.Exists(Path.Combine(sourceRoot, MarkerName));

    public static void Schedule(string sourceRoot, string targetRoot)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot));
        ValidateCandidate(source, target);
        if (Directory.Exists(target + ".moving"))
            throw new DomainException(ErrorCode.DataMoveStagingInUse, target + ".moving");
        string marker = Path.Combine(source, MarkerName);
        if (File.Exists(marker)) throw new DomainException(ErrorCode.DataMovePending);
        File.WriteAllText(marker, JsonSerializer.Serialize(new PendingMove(source, target)));
    }

    public static void ValidateCandidate(string sourceRoot, string targetRoot)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot));
        ValidateTarget(source, target);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new DomainException(ErrorCode.DataFolderNotEmpty);
    }

    public static bool ApplyPending(string? bootstrapFile = null) =>
        ApplyPending(bootstrapFile, static volume => new DriveInfo(volume).AvailableFreeSpace);

    internal static bool ApplyPending(string? bootstrapFile, Func<string, long> availableFreeSpace)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(SettingsBootstrap.Load(bootstrapFile).DataRoot ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GroceryErpData")));
        string marker = Path.Combine(source, MarkerName);
        if (!File.Exists(marker)) return false;
        PendingMove pending = JsonSerializer.Deserialize<PendingMove>(File.ReadAllText(marker))
            ?? throw new DomainException(ErrorCode.Validation, "Invalid data-move marker.");
        if (!string.Equals(source, pending.Source, StringComparison.OrdinalIgnoreCase))
            throw new DomainException(ErrorCode.Validation, "Data-move source does not match the bootstrap.");
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pending.Target));
        ValidateTarget(source, target);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            FinalizeReadyMove(source, target, bootstrapFile, marker);
            return true;
        }

        string staging = target + ".moving";
        if (Directory.Exists(staging))
        {
            string stageMarker = Path.Combine(staging, StageMarkerName);
            PendingMove? staged = File.Exists(stageMarker)
                ? JsonSerializer.Deserialize<PendingMove>(File.ReadAllText(stageMarker)) : null;
            if (staged is null || !staged.Source.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                !staged.Target.Equals(target, StringComparison.OrdinalIgnoreCase))
                throw new DomainException(ErrorCode.DataMoveStagingInUse, staging);
        }
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, StageMarkerName), JsonSerializer.Serialize(pending));
        try
        {
            CopyDirectory(source, staging, availableFreeSpace);
            VerifyCopy(source, staging);
            RewriteDefaultBackupPath(staging, source, target);
            string database = Path.Combine(staging, "db", "grocery.db");
            File.WriteAllText(Path.Combine(staging, ReadyMarkerName), JsonSerializer.Serialize(
                new ReadyMove(source, target, HashFile(database))));
            if (Directory.Exists(target)) Directory.Delete(target);
            // Windows indexers can briefly retain a handle after VerifyCopy closes the database.
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Move(staging, target); break; }
                catch (IOException) when (attempt < 5) { Thread.Sleep(100 * (attempt + 1)); }
                catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(100 * (attempt + 1)); }
            }
            SettingsBootstrap.Save(target, bootstrapFile);
            File.Delete(marker);
            File.Delete(Path.Combine(target, ReadyMarkerName));
            File.Delete(Path.Combine(target, StageMarkerName));
            return true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Data move staging retained for a verified retry: {0}", exception);
            throw;
        }
    }

    private static void FinalizeReadyMove(string source, string target, string? bootstrapFile, string pendingMarker)
    {
        string readyPath = Path.Combine(target, ReadyMarkerName);
        if (!File.Exists(readyPath))
            throw new DomainException(ErrorCode.Validation, "The new data folder is no longer empty.");
        ReadyMove ready = JsonSerializer.Deserialize<ReadyMove>(File.ReadAllText(readyPath))
            ?? throw new DomainException(ErrorCode.Validation, "Invalid data-move completion marker.");
        string database = Path.Combine(target, "db", "grocery.db");
        if (!ready.Source.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            !ready.Target.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(database) || !ready.DatabaseHash.Equals(HashFile(database), StringComparison.Ordinal))
            throw new DomainException(ErrorCode.BackupInvalid, "The copied database changed before the move completed.");
        CheckDatabase(database);
        SettingsBootstrap.Save(target, bootstrapFile);
        File.Delete(pendingMarker);
        File.Delete(readyPath);
        File.Delete(Path.Combine(target, StageMarkerName));
    }

    private static void ValidateTarget(string source, string target)
    {
        AppPaths.RejectCloudSynchronizedPath(target);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new DomainException(ErrorCode.DataFolderNotSeparate);
        if (!File.Exists(Path.Combine(source, "db", "grocery.db")))
            throw new DomainException(ErrorCode.Validation, "The current database is missing.");
    }

    private static void CopyDirectory(string source, string destination, Func<string, long> availableFreeSpace)
    {
        string volume = Path.GetPathRoot(Path.GetFullPath(destination))!;
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals(MarkerName, StringComparison.Ordinal)) continue;
            string copy = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy) ?? destination);
            string partial = copy + ".partial";
            if (!File.Exists(copy) || new FileInfo(file).Length != new FileInfo(copy).Length ||
                !HashFile(file).Equals(HashFile(copy), StringComparison.Ordinal))
            {
                try
                {
                    long free = availableFreeSpace(volume);
                    long needed = new FileInfo(file).Length;
                    if (free < needed || free - needed < CopyReserveBytes)
                        throw new DomainException(ErrorCode.DiskFull);
                }
                catch (Exception exception) when (exception is ArgumentException or DriveNotFoundException or NotSupportedException)
                {
                    // DriveInfo cannot measure some mounts. File.Copy still reports an actual write failure.
                }
                File.Copy(file, partial, overwrite: true);
                File.Move(partial, copy, overwrite: true);
            }
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static void VerifyCopy(string source, string destination)
    {
        foreach (string original in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(original).Equals(MarkerName, StringComparison.Ordinal)) continue;
            string copy = Path.Combine(destination, Path.GetRelativePath(source, original));
            if (!File.Exists(copy))
                throw new DomainException(ErrorCode.BackupInvalid, "Data copy is incomplete.");
            if (!HashFile(original).Equals(HashFile(copy), StringComparison.Ordinal))
                throw new DomainException(ErrorCode.BackupInvalid, "Data copy checksum mismatch.");
        }
        CheckDatabase(Path.Combine(destination, "db", "grocery.db"));
    }

    private static void CheckDatabase(string database)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        if (connection.ExecuteScalar<string>("PRAGMA quick_check;") != "ok" || connection.Query("PRAGMA foreign_key_check;").Any())
            throw new DomainException(ErrorCode.BackupInvalid, "Copied database failed integrity checks.");
    }

    private static string HashFile(string file)
    {
        using FileStream stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void RewriteDefaultBackupPath(string destination, string source, string target)
    {
        string database = Path.Combine(destination, "db", "grocery.db");
        var builder = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        string? stored = connection.ExecuteScalar<string?>("SELECT value FROM setting WHERE key = 'backup.dir'");
        if (stored is not null && (stored.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            stored.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            string relative = Path.GetRelativePath(source, stored);
            connection.Execute("UPDATE setting SET value = @value WHERE key = 'backup.dir'",
                new { value = Path.Combine(target, relative) });
        }
    }
}
