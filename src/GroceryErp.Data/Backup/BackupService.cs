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

public sealed class BackupService(
    AppPaths paths,
    SqliteConnectionFactory factory,
    IClock clock,
    ISettings settings) : IMigrationBackup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string BackupDirectory => settings.Current.BackupDirectory;
    public bool HasPendingRestore => File.Exists(Path.Combine(paths.DbDir, "restore.pending.json"));

    public void CreateBeforeMigration(string reason) => Create(reason);

    public BackupInfo Create(string reason)
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
            File.Move(partial, final);
            Verify(final);
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
            (string quickCheck, long version, _) = InspectDatabase(staged);
            if (quickCheck != "ok" || version != verified.SchemaVersion)
                throw new DomainException(ErrorCode.BackupInvalid, "database integrity or schema mismatch");
            return new(zipPath, staged, verified);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    public void ApplyRestore(RestoreCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Create("pre-restore");
        SqliteConnection.ClearAllPools();
        string stamp = clock.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        string replaced = Path.Combine(paths.DbDir, $"replaced-{stamp}");
        Directory.CreateDirectory(replaced);
        string replacedDatabase = Path.Combine(replaced, Path.GetFileName(paths.DatabaseFile));
        bool originalMoved = false;
        bool candidateApplied = false;

        try
        {
            MoveDatabaseSet(paths.DatabaseFile, replacedDatabase);
            originalMoved = true;
            File.Move(candidate.StagedDatabase, paths.DatabaseFile);
            candidateApplied = true;
            string marker = Path.Combine(paths.DbDir, "restore.pending.json");
            File.WriteAllText(marker, JsonSerializer.Serialize(new RestoreMarker(candidate.SourceZip, replaced, candidate.Manifest), JsonOptions));
        }
        catch
        {
            if (candidateApplied) DeleteDatabaseSet(paths.DatabaseFile);
            if (originalMoved) MoveDatabaseSet(replacedDatabase, paths.DatabaseFile);
            throw;
        }
        finally
        {
            string? staging = Path.GetDirectoryName(candidate.StagedDatabase);
            if (staging is not null) TryDeleteDirectory(staging);
        }
    }

    public void FinishPendingRestore()
    {
        string marker = Path.Combine(paths.DbDir, "restore.pending.json");
        if (!File.Exists(marker)) return;
        RestoreMarker pending = JsonSerializer.Deserialize<RestoreMarker>(File.ReadAllText(marker), JsonOptions)
            ?? throw new DomainException(ErrorCode.BackupInvalid, "restore marker is invalid");
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
        RestoreMarker pending = JsonSerializer.Deserialize<RestoreMarker>(File.ReadAllText(marker), JsonOptions)
            ?? throw new DomainException(ErrorCode.BackupInvalid, "restore marker is invalid");
        SqliteConnection.ClearAllPools();
        string current = paths.DatabaseFile;
        string replaced = Path.Combine(pending.ReplacedDirectory, Path.GetFileName(current));
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
        foreach (string directory in Directory.EnumerateDirectories(paths.DbDir, "replaced-*"))
            try
            {
                if (Directory.GetLastWriteTime(directory) < cutoff) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }

    private static BackupManifest ReadManifest(ZipArchive archive)
    {
        ZipArchiveEntry entry = archive.GetEntry("manifest.json")
            ?? throw new DomainException(ErrorCode.BackupInvalid, "manifest missing");
        using Stream stream = entry.Open();
        BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonOptions)
            ?? throw new DomainException(ErrorCode.BackupInvalid, "manifest is empty");
        if (manifest.Format != 1 || manifest.App != "GroceryErp" || manifest.Database.File != "grocery.db")
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

    private sealed record RestoreMarker(string SourceZip, string ReplacedDirectory, BackupManifest Manifest);
}
