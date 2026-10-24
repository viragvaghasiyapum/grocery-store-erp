using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Backup;

/// <summary>The store's size and the space a compaction would need and reclaim.</summary>
public sealed record CompactionEstimate(long DatabaseBytes, long ReclaimableBytes, long CompactBytes,
    long RequiredFreeBytes, long AvailableFreeBytes)
{
    /// <summary>Gets whether the volumes have room for the compact copy and its verified backup.</summary>
    public bool HasSpace => AvailableFreeBytes >= RequiredFreeBytes;

    /// <summary>Gets whether enough space is unused to be worth compacting (64 MiB and a tenth of the file).</summary>
    public bool IsWorthwhile => ReclaimableBytes >= 64L * 1024 * 1024 && ReclaimableBytes * 10 >= DatabaseBytes;
}

public enum CompactionStage { Copying, Verifying, BackingUp, Swapping }

/// <summary>A compaction step; <see cref="Fraction"/> is known only while copying.</summary>
public sealed record CompactionProgress(CompactionStage Stage, double? Fraction);

public sealed record CompactionResult(long BytesBefore, long BytesAfter, string BackupPath, TimeSpan Elapsed);

// Compaction writes a copy of the store without its unused pages, proves the copy holds
// the same records, saves that copy as a verified backup, and swaps it in with the
// restore markers. It runs only after sign-in and before the shell opens, so nothing
// else uses the store. Until the swap the original is untouched: an interruption or a
// failed check leaves it in place and the request stays for the next launch. The
// original is moved aside and deleted on the next launch that opens the compact store.
public sealed partial class BackupService
{
    private const string CompactPurpose = "compact";
    private const int MaxCompactionAttempts = 2;
    private static readonly long Mebibyte = 1024L * 1024;

    private string CompactionRequestFile => Path.Combine(paths.DbDir, "compact.request.json");
    private string RestoreMarkerFile => Path.Combine(paths.DbDir, "restore.pending.json");

    /// <summary>Gets whether the operator asked for a compaction that has not run yet.</summary>
    public bool HasCompactionRequest => File.Exists(CompactionRequestFile);

    public CompactionEstimate EstimateCompaction()
    {
        long pageSize, pages, free;
        using (SqliteConnection connection = factory.Open())
        {
            pageSize = connection.ExecuteScalar<long>("PRAGMA page_size;");
            pages = connection.ExecuteScalar<long>("PRAGMA page_count;");
            free = connection.ExecuteScalar<long>("PRAGMA freelist_count;");
        }
        return Estimate(pageSize * pages, pageSize * free);
    }

    /// <summary>Records the operator's request; the next launch compacts after sign-in.</summary>
    public void RequestCompaction()
    {
        CompactionEstimate estimate = EstimateCompaction();
        if (!estimate.HasSpace)
            throw new DomainException(ErrorCode.DiskFull, estimate.RequiredFreeBytes, estimate.AvailableFreeBytes);
        WriteRequest(new CompactionRequest(clock.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), 0));
    }

    public void CancelCompactionRequest()
    {
        if (File.Exists(CompactionRequestFile)) File.Delete(CompactionRequestFile);
    }

    public CompactionResult Compact(IProgress<CompactionProgress>? progress, CancellationToken cancellationToken)
    {
        lock (_backupGate)
        {
            if (HasPendingRestore)
                throw new DomainException(ErrorCode.BackupInvalid, "a restore is pending");
            CompactionRequest request = ReadRequest() ?? new(clock.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), 0);
            if (request.Attempts >= MaxCompactionAttempts)
            {
                // Two interrupted attempts mean something keeps stopping it; stop retrying.
                CancelCompactionRequest();
                throw new DomainException(ErrorCode.CompactionFailed);
            }
            WriteRequest(request with { Attempts = request.Attempts + 1 });
            long started = Stopwatch.GetTimestamp();
            string stamp = clock.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            string workingDirectory = Path.Combine(paths.TempDir, $"compact-{stamp}-{Guid.NewGuid():N}");
            factory.BeginExclusive();
            try
            {
                foreach (string stale in Directory.EnumerateDirectories(paths.TempDir, "compact-*"))
                    TryDeleteDirectory(stale);
                Directory.CreateDirectory(workingDirectory);
                string snapshot = Path.Combine(workingDirectory, "grocery.db");
                long bytesBefore;
                using (SqliteConnection live = factory.OpenExclusive())
                using (CancellationTokenRegistration interrupt = cancellationToken.Register(
                           () => SQLitePCL.raw.sqlite3_interrupt(live.Handle)))
                {
                    if (live.ExecuteScalar<long>("PRAGMA wal_checkpoint(TRUNCATE);") != 0)
                        throw new DomainException(ErrorCode.DatabaseBusy);
                    long pageSize = live.ExecuteScalar<long>("PRAGMA page_size;");
                    bytesBefore = pageSize * live.ExecuteScalar<long>("PRAGMA page_count;");
                    CompactionEstimate estimate = Estimate(bytesBefore,
                        pageSize * live.ExecuteScalar<long>("PRAGMA freelist_count;"));
                    if (!estimate.HasSpace)
                        throw new DomainException(ErrorCode.DiskFull, estimate.RequiredFreeBytes, estimate.AvailableFreeBytes);
                    progress?.Report(new(CompactionStage.Copying, 0));
                    // The copy's growing file size shows how far VACUUM INTO has come.
                    var watch = new Timer(_ => progress?.Report(new(CompactionStage.Copying,
                        Math.Min(1d, (double)FileLength(snapshot) / Math.Max(1, estimate.CompactBytes)))),
                        null, 500, 500);
                    try
                    {
                        using SqliteCommand command = live.CreateCommand();
                        command.CommandText = "VACUUM INTO $path";
                        command.Parameters.AddWithValue("$path", snapshot);
                        command.ExecuteNonQuery();
                    }
                    finally
                    {
                        // A tick still running must not report copying after the next step.
                        using var stopped = new ManualResetEvent(false);
                        if (watch.Dispose(stopped)) stopped.WaitOne();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new(CompactionStage.Verifying, null));
                    VerifyCompactCopy(live, snapshot, cancellationToken);
                }
                progress?.Report(new(CompactionStage.BackingUp, null));
                (string quickCheck, long version, BackupSummary summary) = InspectDatabase(snapshot, cancellationToken);
                if (!string.Equals(quickCheck, "ok", StringComparison.Ordinal))
                    throw new DomainException(ErrorCode.BackupInvalid, quickCheck);
                string backupDirectory = settings.Current.BackupDirectory;
                Directory.CreateDirectory(backupDirectory);
                BackupInfo backup = ArchiveSnapshot(snapshot, "pre-compact", stamp, backupDirectory,
                    quickCheck, version, summary, static _ => { }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Past this point the swap runs to completion; the markers make it recoverable.
                progress?.Report(new(CompactionStage.Swapping, null));
                InstallCompacted(snapshot, backup, stamp, bytesBefore);
                CancelCompactionRequest();
                return new(bytesBefore, new FileInfo(paths.DatabaseFile).Length, backup.Path,
                    Stopwatch.GetElapsedTime(started));
            }
            catch (OperationCanceledException)
            {
                CancelCompactionRequest();
                throw;
            }
            catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
            {
                CancelCompactionRequest();
                throw new OperationCanceledException("Compaction was cancelled.", exception, cancellationToken);
            }
            finally
            {
                TryDeleteDirectory(workingDirectory);
                if (factory.OwnsExclusive && !HasPendingRestore) factory.EndExclusive();
            }
        }
    }

    /// <summary>Completes a compaction swap that was interrupted after the compact copy was installed.</summary>
    public void FinishPendingCompaction()
    {
        if (!File.Exists(RestoreMarkerFile)) return;
        RestoreMarker pending = ReadMarker(RestoreMarkerFile);
        if (pending.Purpose != CompactPurpose || pending.Stage != "applied") return;
        bool acquired = !factory.OwnsExclusive;
        if (acquired) factory.BeginExclusive();
        try { FinishCompaction(pending, bytesBefore: null); }
        finally { if (factory.OwnsExclusive && !HasPendingRestore) factory.EndExclusive(); }
    }

    /// <summary>Deletes originals kept by an earlier compaction once the compact store has opened again.</summary>
    public void PurgeCompactedOriginals()
    {
        if (HasPendingRestore) return;
        foreach (string directory in Directory.EnumerateDirectories(paths.DbDir, "replaced-*"))
            if (File.Exists(Path.Combine(directory, "compacted.json"))) TryDeleteDirectory(directory);
    }

    private CompactionEstimate Estimate(long databaseBytes, long reclaimableBytes)
    {
        long compactBytes = Math.Max(0, databaseBytes - reclaimableBytes);
        long reserve = Math.Max(256 * Mebibyte, databaseBytes / 10);
        string databaseVolume = Path.GetPathRoot(Path.GetFullPath(paths.DbDir))!;
        string backupVolume = Path.GetPathRoot(Path.GetFullPath(settings.Current.BackupDirectory))!;
        // The copy is written beside the store; its verified zip, never larger than the
        // copy, goes to the backup folder.
        long databaseFree = FreeSpace(databaseVolume);
        bool sharedVolume = string.Equals(databaseVolume, backupVolume, StringComparison.OrdinalIgnoreCase);
        if (sharedVolume)
            return new(databaseBytes, reclaimableBytes, compactBytes, 2 * compactBytes + reserve, databaseFree);
        long backupFree = FreeSpace(backupVolume);
        long required = compactBytes + reserve;
        // Report the tighter volume, so the shortfall shown is the one that blocks.
        return new(databaseBytes, reclaimableBytes, compactBytes, required, Math.Min(databaseFree, backupFree));
    }

    // The copy must hold exactly what the store holds: same schema, every row, the business
    // totals that tie documents to lots, every P&L total and the same annual snapshot results.
    private static void VerifyCompactCopy(SqliteConnection live, string copyFile, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = copyFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var copy = new SqliteConnection(builder.ToString());
        copy.Open();
        using CancellationTokenRegistration interrupt = cancellationToken.Register(
            () => SQLitePCL.raw.sqlite3_interrupt(copy.Handle));
        string integrity = copy.ExecuteScalar<string>("PRAGMA integrity_check;") ?? "";
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
            throw new DomainException(ErrorCode.BackupInvalid, integrity);
        if (copy.Query("PRAGMA foreign_key_check;").Any())
            throw new DomainException(ErrorCode.BackupInvalid, "foreign key check failed");
        Same("schema version", live, copy, static connection => connection.ExecuteScalar<long>("PRAGMA user_version;"));
        Same("schema", live, copy, static connection => string.Join('\n', connection.Query<string>("""
            SELECT type||' '||name||' '||tbl_name||' '||COALESCE(sql,'') FROM sqlite_schema ORDER BY type,name
            """)));
        foreach (string table in live.Query<string>("""
                     SELECT name FROM sqlite_schema WHERE type='table' AND sql NOT LIKE 'CREATE VIRTUAL TABLE%' ORDER BY name
                     """))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string count = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            Same(table, live, copy, connection => connection.ExecuteScalar<long>(count));
        }
        Same("business totals", live, copy, static connection => connection.ExecuteScalar<string>("""
            SELECT json_array(
              (SELECT COALESCE(SUM(qty_on_hand),0) FROM product_lot),
              (SELECT COALESCE(SUM(quantity),0)||'/'||COALESCE(SUM(cost_value),0) FROM product_stock_total),
              (SELECT COALESCE(SUM(grand_total),0) FROM sale),(SELECT COALESCE(SUM(qty),0) FROM sale_line),
              (SELECT COALESCE(SUM(refund_total),0) FROM sale_return),(SELECT COALESCE(SUM(qty),0) FROM sale_return_line),
              (SELECT COALESCE(SUM(qty+free_qty),0) FROM purchase_line),(SELECT COALESCE(SUM(qty),0) FROM damage),
              (SELECT COALESCE(SUM(quantity_delta),0) FROM stock_correction),(SELECT COALESCE(SUM(amount),0) FROM expense))
            """));
        Same("P&L totals", live, copy, static connection => string.Join('\n', connection.Query<string>("""
            SELECT entry_date||' '||section||' '||head_code||' '||category_id||' '||SUM(amount)||' '||SUM(source_count)
            FROM v_pnl_entry GROUP BY entry_date,section,head_code,category_id
            ORDER BY entry_date,section,head_code,category_id
            """)));
        Same("annual reports", live, copy, static connection => string.Join('\n', AnnualCloseService.VerifyAll(connection)));
    }

    private static void Same<T>(string what, SqliteConnection live, SqliteConnection copy, Func<SqliteConnection, T> read)
    {
        if (!EqualityComparer<T>.Default.Equals(read(live), read(copy)))
            throw new DomainException(ErrorCode.BackupInvalid, $"the compact copy differs: {what}");
    }

    private void InstallCompacted(string snapshot, BackupInfo backup, string stamp, long bytesBefore)
    {
        factory.ClearPool();
        string replaced = Path.Combine(paths.DbDir, $"replaced-{stamp}");
        if (Directory.Exists(replaced)) replaced += "-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(replaced);
        File.WriteAllText(Path.Combine(replaced, "compacted.json"), JsonSerializer.Serialize(new { backup.Path }));
        string replacedDatabase = Path.Combine(replaced, Path.GetFileName(paths.DatabaseFile));
        var pending = new RestoreMarker(backup.Path, replaced, backup.Manifest, "preparing", Purpose: CompactPurpose);
        try
        {
            WriteMarker(RestoreMarkerFile, pending);
            RestoreBoundary?.Invoke("compact-marker-preparing");
            MoveDatabaseSet(paths.DatabaseFile, replacedDatabase, "compact-original-moved");
            WriteMarker(RestoreMarkerFile, pending with { Stage = "original-moved" });
            RestoreBoundary?.Invoke("compact-marker-original-moved");
            File.Move(snapshot, paths.DatabaseFile);
            RestoreBoundary?.Invoke("compact-installed");
            WriteMarker(RestoreMarkerFile, pending with { Stage = "applied" });
            RestoreBoundary?.Invoke("compact-marker-applied");
        }
        catch
        {
            if (File.Exists(RestoreMarkerFile) && ReadMarker(RestoreMarkerFile).Stage is "preparing" or "original-moved")
                RecoverInterruptedRestoreCore(RestoreMarkerFile);
            throw;
        }
        FinishCompaction(pending with { Stage = "applied" }, bytesBefore);
    }

    // Opens the installed copy afresh. If it fails, the original returns while the barrier
    // still excludes every other connection.
    private void FinishCompaction(RestoreMarker pending, long? bytesBefore)
    {
        try
        {
            using SqliteConnection connection = factory.OpenExclusive();
            // VACUUM INTO writes a rollback-journal file; the store runs in WAL mode.
            if (!string.Equals(connection.ExecuteScalar<string>("PRAGMA journal_mode=WAL;"), "wal", StringComparison.OrdinalIgnoreCase))
                throw new DomainException(ErrorCode.BackupInvalid, "the compact store could not enter WAL mode");
            (string quickCheck, long version, BackupSummary summary) = InspectDatabase(connection);
            if (quickCheck != "ok" || version != pending.Manifest.SchemaVersion || summary != pending.Manifest.Summary)
                throw new DomainException(ErrorCode.BackupInvalid, "the compact store failed its startup check");
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            connection.Execute("""
                INSERT INTO audit_log(entity,entity_id,action,changes) VALUES ('database',0,'COMPACT',@changes)
                """, new
            {
                changes = JsonSerializer.Serialize(new
                {
                    bytesBefore,
                    bytesAfter = new FileInfo(paths.DatabaseFile).Length,
                    backup = Path.GetFileName(pending.SourceZip),
                }, JsonOptions),
            }, transaction);
            transaction.Commit();
            File.Delete(RestoreMarkerFile);
            CancelCompactionRequest();
        }
        catch (Exception) when (File.Exists(RestoreMarkerFile))
        {
            UndoPendingRestore();
            throw new DomainException(ErrorCode.CompactionFailed);
        }
    }

    // DriveInfo cannot measure every network mount; a full volume still fails the write,
    // and the original store is untouched until the verified copy is swapped in.
    private long FreeSpace(string volume)
    {
        try { return AvailableFreeSpace(volume); }
        catch (Exception exception) when (exception is ArgumentException or DriveNotFoundException or IOException)
        {
            return long.MaxValue;
        }
    }

    private static long FileLength(string file)
    {
        try { return File.Exists(file) ? new FileInfo(file).Length : 0; }
        catch (IOException) { return 0; }
    }

    private CompactionRequest? ReadRequest()
    {
        if (!File.Exists(CompactionRequestFile)) return null;
        try { return JsonSerializer.Deserialize<CompactionRequest>(File.ReadAllText(CompactionRequestFile), JsonOptions); }
        catch (JsonException) { return null; }
    }

    private void WriteRequest(CompactionRequest request)
    {
        string partial = CompactionRequestFile + ".partial";
        File.WriteAllText(partial, JsonSerializer.Serialize(request, JsonOptions));
        File.Move(partial, CompactionRequestFile, overwrite: true);
    }

    private sealed record CompactionRequest(string RequestedAt, int Attempts);
}
