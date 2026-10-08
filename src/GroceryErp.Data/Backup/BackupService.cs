using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Backup;

public sealed record BackupDbInfo(string File, long Bytes, string Sha256, string QuickCheck);
public sealed record BackupSummary(long Products, long Bills, string? LastInvoiceNo, string? LastSaleDate);
public sealed record BackupManifest(int Format, string App, string AppVersion, long SchemaVersion,
    string CreatedAt, string Reason, string Machine, BackupDbInfo Database, BackupSummary Summary);
public sealed record BackupInfo(string Path, long Bytes, BackupManifest Manifest);
public sealed record RestoreCandidate(string SourceZip, string StagedDatabase, BackupManifest Manifest);
public sealed record CurrentDatabaseInfo(long SchemaVersion, BackupSummary Summary);
public sealed record BackupHealth(DateTime? LastBackup, DateTime? LastUsbExport, bool BackedUpToday, int UsbAgeDays);

public sealed class BackupService(
    AppPaths paths,
    SqliteConnectionFactory factory,
    IClock clock,
    ISettings settings) : IMigrationBackup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _backupGate = new();

    public string BackupDirectory => settings.Current.BackupDirectory;
    public bool HasPendingRestore => File.Exists(Path.Combine(paths.DbDir, "restore.pending.json"));

    public BackupHealth GetHealth()
    {
        DateTime? latest = Directory.Exists(BackupDirectory)
            ? new DirectoryInfo(BackupDirectory).EnumerateFiles("GroceryErp_*.zip")
                .OrderByDescending(static file => file.LastWriteTime).Select(static file => (DateTime?)file.LastWriteTime).FirstOrDefault()
            : null;
        string raw = settings.Current.LastUsbExport;
        DateTime? usb = DateTime.TryParseExact(raw, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime parsed) ? parsed : null;
        int age = usb is null ? int.MaxValue : Math.Max(0, (clock.BusinessDate.ToDateTime(TimeOnly.MinValue) - usb.Value.Date).Days);
        return new(latest, usb, latest?.Date == clock.BusinessDate.ToDateTime(TimeOnly.MinValue), age);
    }

    public CurrentDatabaseInfo InspectCurrent()
    {
        (string check, long version, BackupSummary summary) = InspectDatabase(paths.DatabaseFile);
        if (check != "ok") throw new DomainException(ErrorCode.BackupInvalid, check);
        return new(version, summary);
    }

    public IReadOnlyList<string> ExportToUsb(string driveRoot)
    {
        lock (_backupGate) return ExportToUsbCore(driveRoot);
    }

    private List<string> ExportToUsbCore(string driveRoot)
    {
        string root = Path.GetFullPath(driveRoot);
        var drive = new DriveInfo(Path.GetPathRoot(root) ?? root);
        if (drive.DriveType != DriveType.Removable || !drive.IsReady ||
            !root.Equals(Path.GetFullPath(drive.RootDirectory.FullName), StringComparison.OrdinalIgnoreCase))
            throw new DomainException(ErrorCode.Validation, "Select a ready removable drive.");

        string source = BackupDirectory;
        if (!Directory.Exists(source)) Directory.CreateDirectory(source);
        if (!Directory.EnumerateFiles(source, "GroceryErp_*.zip").Any()) Create("manual");
        FileInfo[] backups = new DirectoryInfo(source).EnumerateFiles("GroceryErp_*.zip")
            .OrderByDescending(static file => file.LastWriteTimeUtc).ToArray();
        if (backups.Length == 0) throw new DomainException(ErrorCode.BackupInvalid, "No backup is available.");

        var selected = new List<FileInfo> { backups[0] };
        selected.AddRange(backups.GroupBy(static file => file.LastWriteTimeUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture))
            .Select(static group => group.First()).Where(file => file.FullName != backups[0].FullName));
        string destination = Path.Combine(root, "GroceryErp-Backups");
        Directory.CreateDirectory(destination);
        var copied = new List<string>(selected.Count);
        foreach (FileInfo file in selected)
        {
            BackupManifest original = Verify(file.FullName);
            string target = Path.Combine(destination, file.Name);
            bool matches = false;
            if (File.Exists(target))
            {
                try { matches = Verify(target).Database.Sha256 == original.Database.Sha256; }
                catch (DomainException) { }
            }
            if (!matches)
            {
                string partial = target + ".partial";
                File.Copy(file.FullName, partial, overwrite: true);
                Verify(partial);
                File.Move(partial, target, overwrite: true);
            }
            copied.Add(target);
        }
        File.WriteAllText(Path.Combine(destination, "README.txt"),
            "Grocery ERP backups\n\nIn the app, open Settings > Restore backup, select a .zip file here, compare the summary, and type RESTORE. Keep this drive away from the shop computer.\n");
        return copied;
    }

    public void CreateBeforeMigration(string reason) => Create(reason);

    public BackupInfo Create(string reason)
    {
        lock (_backupGate) return CreateCore(reason);
    }

    private BackupInfo CreateCore(string reason)
    {
        string safeReason = SanitizeReason(reason);
        string stamp = clock.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        string workingDirectory = Path.Combine(paths.TempDir, $"backup-{stamp}-{Guid.NewGuid():N}");
        string backupDirectory = settings.Current.BackupDirectory;
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(backupDirectory);
        try
        {
            string snapshot = Path.Combine(workingDirectory, "grocery.db");
            using (SqliteConnection connection = factory.Open())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $path";
                command.Parameters.AddWithValue("$path", snapshot);
                command.ExecuteNonQuery();
            }

            (string quickCheck, long schemaVersion, BackupSummary summary) = InspectDatabase(snapshot);
            if (!string.Equals(quickCheck, "ok", StringComparison.Ordinal))
                throw new DomainException(ErrorCode.BackupInvalid, quickCheck);

            var manifest = new BackupManifest(
                1, "GroceryErp", typeof(BackupService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                schemaVersion, clock.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), safeReason,
                Environment.MachineName,
                new("grocery.db", new FileInfo(snapshot).Length, Sha256Hex(snapshot), quickCheck), summary);
            string final = UniqueBackupPath(backupDirectory, stamp, safeReason);
            string partial = final + ".partial";
            using (ZipArchive archive = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(snapshot, "grocery.db", CompressionLevel.Fastest);
                using Stream json = archive.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
                JsonSerializer.Serialize(json, manifest, JsonOptions);
            }
            // A failed verification must never leave a final-looking backup behind.
            Verify(partial);
            File.Move(partial, final);
            Rotate(backupDirectory, settings.Current.BackupKeep, clock.Now.ToUniversalTime());
            return new(final, new FileInfo(final).Length, manifest);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    public static BackupManifest Verify(string zipPath)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            BackupManifest manifest = ReadManifest(archive);
            ZipArchiveEntry database = archive.GetEntry("grocery.db")
                ?? throw new DomainException(ErrorCode.BackupInvalid, "database missing");
            using Stream stream = database.Open();
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!hash.Equals(manifest.Database.Sha256, StringComparison.Ordinal) || database.Length != manifest.Database.Bytes)
                throw new DomainException(ErrorCode.BackupInvalid, "checksum or length mismatch");
            return manifest;
        }
        catch (DomainException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
        {
            throw new DomainException(ErrorCode.BackupInvalid, exception.Message);
        }
    }

    public RestoreCandidate InspectRestore(string zipPath)
    {
        BackupManifest verified = Verify(zipPath);
        if (verified.SchemaVersion > MigrationRunner.LatestVersion)
            throw new DomainException(ErrorCode.DatabaseNewerThanApp, verified.SchemaVersion, MigrationRunner.LatestVersion);
        string staging = Path.Combine(paths.TempDir, $"restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        string staged = Path.Combine(staging, "grocery.db");
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            (archive.GetEntry("grocery.db") ?? throw new DomainException(ErrorCode.BackupInvalid, "database missing"))
                .ExtractToFile(staged);
            if (!string.Equals(Sha256Hex(staged), verified.Database.Sha256, StringComparison.Ordinal))
                throw new DomainException(ErrorCode.BackupInvalid, "staged database checksum mismatch");
            (string quickCheck, long version, BackupSummary summary) = InspectDatabase(staged);
            if (quickCheck != "ok" || version != verified.SchemaVersion || summary != verified.Summary)
                throw new DomainException(ErrorCode.BackupInvalid, "database integrity, schema or summary mismatch");
            return new(zipPath, staged, verified);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    public void DiscardRestoreCandidate(RestoreCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string staging = Path.GetFullPath(Path.GetDirectoryName(candidate.StagedDatabase) ?? "");
        string expectedParent = Path.GetFullPath(paths.TempDir);
        if (Path.GetDirectoryName(staging)?.Equals(expectedParent, StringComparison.OrdinalIgnoreCase) == true &&
            Path.GetFileName(staging).StartsWith("restore-", StringComparison.Ordinal))
            TryDeleteDirectory(staging);
    }

    public void ApplyRestore(RestoreCandidate candidate)
    {
        lock (_backupGate) ApplyRestoreCore(candidate);
    }

    private void ApplyRestoreCore(RestoreCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (HasPendingRestore)
            throw new DomainException(ErrorCode.BackupInvalid, "a restore is already pending");
        Create("pre-restore");
        using (SqliteConnection connection = factory.Open())
            if (connection.ExecuteScalar<long>("PRAGMA wal_checkpoint(TRUNCATE);") != 0)
                throw new DomainException(ErrorCode.BackupInvalid, "the database is busy during restore");
        SqliteConnection.ClearAllPools();
        string stamp = clock.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        string replaced = Path.Combine(paths.DbDir, $"replaced-{stamp}");
        Directory.CreateDirectory(replaced);
        string replacedDatabase = Path.Combine(replaced, Path.GetFileName(paths.DatabaseFile));
        string marker = Path.Combine(paths.DbDir, "restore.pending.json");
        bool originalMoved = false;
        bool candidateApplied = false;

        try
        {
            // Write the recovery marker before the first rename. A power loss at any point
            // in the swap can then be rolled back before the database is opened on startup.
            WriteMarker(marker, new RestoreMarker(candidate.SourceZip, replaced, candidate.Manifest, "preparing"));
            MoveDatabaseSet(paths.DatabaseFile, replacedDatabase);
            originalMoved = true;
            File.Move(candidate.StagedDatabase, paths.DatabaseFile);
            candidateApplied = true;
            WriteMarker(marker, new RestoreMarker(candidate.SourceZip, replaced, candidate.Manifest, "applied"));
        }
        catch
        {
            if (candidateApplied) DeleteDatabaseSet(paths.DatabaseFile);
            if (originalMoved) MoveDatabaseSet(replacedDatabase, paths.DatabaseFile);
            File.Delete(marker);
            throw;
        }
        finally
        {
            string? staging = Path.GetDirectoryName(candidate.StagedDatabase);
            if (staging is not null) TryDeleteDirectory(staging);
        }
    }

    public void RecoverInterruptedRestoreBeforeStartup()
    {
        string marker = Path.Combine(paths.DbDir, "restore.pending.json");
        if (!File.Exists(marker)) return;
        RestoreMarker pending = ReadMarker(marker);
        // Old markers were written only after a successful swap.
        if (pending.Stage != "preparing") return;
        string replacedDatabase = SafeReplacedDatabase(pending);
        if (File.Exists(replacedDatabase) && File.Exists(paths.DatabaseFile))
        {
            // The staged database was installed, so the original set is wholly replaced.
            UndoPendingRestore();
            return;
        }
        // Power may have failed between the three original-file renames, or while undoing
        // them. Files still at the live path belong to the original database and are kept.
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(replacedDatabase + suffix) && !File.Exists(paths.DatabaseFile + suffix))
                File.Move(replacedDatabase + suffix, paths.DatabaseFile + suffix);
        // The original main database must be present after rejoining the set.
        if (!File.Exists(paths.DatabaseFile))
            throw new DomainException(ErrorCode.BackupInvalid, "both restore databases are missing");
        File.Delete(marker);
    }

    public void FinishPendingRestore()
    {
        string marker = Path.Combine(paths.DbDir, "restore.pending.json");
        if (!File.Exists(marker)) return;
        RestoreMarker pending = ReadMarker(marker);
        using SqliteConnection connection = factory.Open();
        if (connection.ExecuteScalar<string>("PRAGMA quick_check;") != "ok" ||
            connection.Query("PRAGMA foreign_key_check;").Any())
            throw new DomainException(ErrorCode.BackupInvalid, "restored database failed startup check");
        long schemaVersion = connection.ExecuteScalar<long>("PRAGMA user_version;");
        if (schemaVersion > MigrationRunner.LatestVersion)
            throw new DomainException(ErrorCode.DatabaseNewerThanApp, schemaVersion, MigrationRunner.LatestVersion);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        connection.Execute(
            "INSERT INTO audit_log(entity, entity_id, action, changes) VALUES ('restore', 0, 'RESTORE', @changes)",
            new { changes = JsonSerializer.Serialize(new { pending.SourceZip, pending.Manifest.SchemaVersion }, JsonOptions) },
            transaction);
        transaction.Commit();
        File.Delete(marker);
        PurgeExpiredRestoreData();
    }

    public void UndoPendingRestore()
    {
        string marker = Path.Combine(paths.DbDir, "restore.pending.json");
        if (!File.Exists(marker)) return;
        RestoreMarker pending = ReadMarker(marker);
        SqliteConnection.ClearAllPools();
        string current = paths.DatabaseFile;
        string replaced = SafeReplacedDatabase(pending);
        if (!File.Exists(replaced)) throw new DomainException(ErrorCode.BackupInvalid, "replaced database is missing");
        string failedDirectory = Path.Combine(paths.TempDir, $"restore-failed-{Guid.NewGuid():N}");
        string failedDatabase = Path.Combine(failedDirectory, Path.GetFileName(current));
        Directory.CreateDirectory(failedDirectory);
        bool currentMoved = false;
        bool replacedMoved = false;
        try
        {
            MoveDatabaseSet(current, failedDatabase);
            currentMoved = true;
            MoveDatabaseSet(replaced, current);
            replacedMoved = true;
            File.Delete(marker);
            TryDeleteDirectory(failedDirectory);
        }
        catch
        {
            if (replacedMoved) MoveDatabaseSet(current, replaced);
            if (currentMoved) MoveDatabaseSet(failedDatabase, current);
            throw;
        }
    }

    public void PurgeExpiredRestoreData()
    {
        DateTime cutoff = clock.Now.AddDays(-7);
        string? pendingDirectory = HasPendingRestore
            ? Path.GetDirectoryName(SafeReplacedDatabase(ReadMarker(Path.Combine(paths.DbDir, "restore.pending.json"))))
            : null;
        foreach (string directory in Directory.EnumerateDirectories(paths.DbDir, "replaced-*"))
            try
            {
                if (string.Equals(directory, pendingDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.GetLastWriteTime(directory) < cutoff) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }

    private static RestoreMarker ReadMarker(string path) =>
        JsonSerializer.Deserialize<RestoreMarker>(File.ReadAllText(path), JsonOptions)
        ?? throw new DomainException(ErrorCode.BackupInvalid, "restore marker is invalid");

    private string SafeReplacedDatabase(RestoreMarker marker)
    {
        string directory = Path.GetFullPath(marker.ReplacedDirectory);
        if (!string.Equals(Path.GetDirectoryName(directory), paths.DbDir, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(directory).StartsWith("replaced-", StringComparison.Ordinal))
            throw new DomainException(ErrorCode.BackupInvalid, "restore marker has an invalid location");
        return Path.Combine(directory, Path.GetFileName(paths.DatabaseFile));
    }

    private static void WriteMarker(string path, RestoreMarker marker)
    {
        string partial = path + ".partial";
        File.WriteAllText(partial, JsonSerializer.Serialize(marker, JsonOptions));
        File.Move(partial, path, overwrite: true);
    }

    private static BackupManifest ReadManifest(ZipArchive archive)
    {
        ZipArchiveEntry entry = archive.GetEntry("manifest.json")
            ?? throw new DomainException(ErrorCode.BackupInvalid, "manifest missing");
        using Stream stream = entry.Open();
        BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonOptions)
            ?? throw new DomainException(ErrorCode.BackupInvalid, "manifest is empty");
        if (manifest.Format != 1 || manifest.App != "GroceryErp" ||
            manifest.Database is null || manifest.Database.File != "grocery.db" ||
            manifest.Database.Bytes <= 0 || manifest.Database.Sha256 is null ||
            manifest.Database.Sha256.Length != 64 ||
            !manifest.Database.Sha256.All(static character => char.IsAsciiHexDigit(character)) ||
            manifest.Summary is null || manifest.SchemaVersion < 1 ||
            string.IsNullOrWhiteSpace(manifest.CreatedAt) || string.IsNullOrWhiteSpace(manifest.Reason))
            throw new DomainException(ErrorCode.BackupInvalid, "unknown backup format");
        return manifest;
    }

    private static (string QuickCheck, long Version, BackupSummary Summary) InspectDatabase(string databaseFile)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databaseFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        string quickCheck = connection.ExecuteScalar<string>("PRAGMA quick_check;")
            ?? throw new DomainException(ErrorCode.BackupInvalid, "quick check returned no result");
        if (connection.Query("PRAGMA foreign_key_check;").Any())
            throw new DomainException(ErrorCode.BackupInvalid, "foreign key check failed");
        long version = connection.ExecuteScalar<long>("PRAGMA user_version;");
        var summary = new BackupSummary(
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product"),
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale WHERE status = 'COMPLETED'"),
            connection.ExecuteScalar<string?>("SELECT invoice_no FROM sale WHERE status = 'COMPLETED' ORDER BY id DESC LIMIT 1"),
            connection.ExecuteScalar<string?>("SELECT MAX(sale_date) FROM sale WHERE status = 'COMPLETED'"));
        return (quickCheck, version, summary);
    }

    private static string Sha256Hex(string file)
    {
        using FileStream stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SanitizeReason(string reason)
    {
        string safe = new(reason.Where(static character => char.IsAsciiLetterOrDigit(character) || character == '-').ToArray());
        if (string.IsNullOrWhiteSpace(safe)) throw new ArgumentException("A backup reason is required.", nameof(reason));
        return safe;
    }

    private static string UniqueBackupPath(string directory, string stamp, string reason)
    {
        string path = Path.Combine(directory, $"GroceryErp_{stamp}_{reason}.zip");
        return File.Exists(path) ? Path.Combine(directory, $"GroceryErp_{stamp}_{reason}_{Guid.NewGuid():N}.zip") : path;
    }

    private static void Rotate(string directory, int keep, DateTime now)
    {
        foreach (FileInfo partial in new DirectoryInfo(directory).EnumerateFiles("*.partial")
                     .Where(file => file.LastWriteTimeUtc < now.AddDays(-1)))
            partial.Delete();
        FileInfo[] regular = new DirectoryInfo(directory).EnumerateFiles("GroceryErp_*.zip")
            .Where(static file => !file.Name.Contains("_pre-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static file => file.LastWriteTimeUtc).ToArray();
        var protectedPaths = regular.Take(keep).Select(static file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (FileInfo file in regular.Where(file => file.LastWriteTimeUtc >= now.AddMonths(-12)))
        {
            string month = file.LastWriteTimeUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (regular.First(candidate => candidate.LastWriteTimeUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture) == month).FullName == file.FullName)
                protectedPaths.Add(file.FullName);
        }
        foreach (FileInfo file in regular.Where(file => !protectedPaths.Contains(file.FullName)))
            file.Delete();

        foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("GroceryErp_*_pre-*.zip"))
            if (file.LastWriteTimeUtc < now.AddDays(-90)) file.Delete();
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void MoveDatabaseSet(string sourceDatabase, string destinationDatabase)
    {
        var moved = new List<(string Source, string Destination)>();
        try
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                string source = sourceDatabase + suffix;
                if (!File.Exists(source)) continue;
                string destination = destinationDatabase + suffix;
                File.Move(source, destination);
                moved.Add((source, destination));
            }
        }
        catch
        {
            foreach ((string source, string destination) in moved.AsEnumerable().Reverse())
                if (File.Exists(destination) && !File.Exists(source)) File.Move(destination, source);
            throw;
        }
    }

    private static void DeleteDatabaseSet(string database)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(database + suffix)) File.Delete(database + suffix);
    }

    private sealed record RestoreMarker(string SourceZip, string ReplacedDirectory, BackupManifest Manifest, string? Stage = null);
}
