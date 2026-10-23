using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Dapper;
using GroceryErp.Core.Errors;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Infrastructure;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly string _walFile;
    // All factories for one file (including print-journal and test factories) share
    // the restore barrier. A per-instance counter would permit a late connection
    // from a second factory to open while the files are being renamed.
    private static readonly ConcurrentDictionary<string, ConnectionBarrier> Barriers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConnectionBarrier _barrier;
    private sealed class ConnectionBarrier
    {
        public int ActiveConnections;
        public SqliteConnectionFactory? ExclusiveOwner;
    }
    private static readonly AsyncLocal<string?> TraceOperation = new();
    private SQLitePCL.strdelegate_trace? _traceCallback;

    // Audit-only hook. SQLite supplies expanded SQL, so callers must enable this
    // only on disposable synthetic stores and keep the output out of app logs.
    public Action<string, string>? SqlTrace { get; set; }

    public bool IsExclusive { get { lock (_barrier) return _barrier.ExclusiveOwner is not null; } }
    public bool OwnsExclusive { get { lock (_barrier) return _barrier.ExclusiveOwner == this; } }
    public int ActiveConnections { get { lock (_barrier) return _barrier.ActiveConnections; } }


    // Clearing every SQLite pool can interrupt work against an unrelated store in the
    // same process (including parallel tests). Keep cleanup scoped to this database.
    public void ClearPool()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    public static void ClearPoolForDataRoot(string root)
    {
        new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root))).ClearPool();
    }

    internal IDisposable? BeginSqlTraceOperation(string operation)
    {
        if (SqlTrace is null) return null;
        string? previous = TraceOperation.Value;
        TraceOperation.Value = operation;
        return new SqlTraceScope(previous);
    }

    private sealed class SqlTraceScope(string? previous) : IDisposable
    {
        public void Dispose() => TraceOperation.Value = previous;
    }

    public SqliteConnectionFactory(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _walFile = paths.DatabaseFile + "-wal";
        _barrier = Barriers.GetOrAdd(Path.GetFullPath(paths.DatabaseFile), static _ => new ConnectionBarrier());
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    internal SqliteConnectionFactory(string databaseFile, bool pooling = true)
    {
        _walFile = databaseFile + "-wal";
        _barrier = Barriers.GetOrAdd(Path.GetFullPath(databaseFile), static _ => new ConnectionBarrier());
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = pooling,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        EnterTrackedWork();
        return OpenCore(tracked: true);
    }

    // Long-running work such as sending a saved print job participates in the
    // restore drain even while it does not hold a SQLite connection.
    public IDisposable EnterOperation()
    {
        EnterTrackedWork();
        return new OperationLease(_barrier);
    }

    private void EnterTrackedWork()
    {
        lock (_barrier)
        {
            if (_barrier.ExclusiveOwner is not null)
                throw new InvalidOperationException("The database is being restored. Restart the application before continuing.");
            _barrier.ActiveConnections++;
        }
    }

    private sealed class OperationLease(ConnectionBarrier barrier) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (barrier)
            {
                barrier.ActiveConnections--;
                Monitor.PulseAll(barrier);
            }
        }
    }

    public SqliteConnection OpenExclusive()
    {
        lock (_barrier)
            if (_barrier.ExclusiveOwner != this || _barrier.ActiveConnections != 0)
                throw new InvalidOperationException("An exclusive restore barrier is required.");
        return OpenCore(tracked: false);
    }

    public void BeginExclusive()
    {
        lock (_barrier)
        {
            if (_barrier.ExclusiveOwner is not null) throw new InvalidOperationException("A database restore is already in progress.");
            _barrier.ExclusiveOwner = this;
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (_barrier.ActiveConnections > 0)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_barrier, remaining))
                {
                    _barrier.ExclusiveOwner = null;
                    Monitor.PulseAll(_barrier);
                    throw new TimeoutException("Database work did not finish before the restore window. Close open work and retry.");
                }
            }
        }
    }

    public void EndExclusive()
    {
        lock (_barrier)
        {
            if (_barrier.ExclusiveOwner != this)
                throw new InvalidOperationException("This factory does not own the restore barrier.");
            _barrier.ExclusiveOwner = null;
            Monitor.PulseAll(_barrier);
        }
    }

    private SqliteConnection OpenCore(bool tracked)
    {
        var connection = new SqliteConnection(_connectionString);
        int released = 0;
        void Release()
        {
            if (!tracked || Interlocked.Exchange(ref released, 1) != 0) return;
            lock (_barrier)
            {
                _barrier.ActiveConnections--;
                Monitor.PulseAll(_barrier);
            }
        }
        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState == ConnectionState.Closed) Release();
        };
        connection.Disposed += (_, _) => Release();
        try
        {
            connection.Open();
            connection.Execute("""
                PRAGMA synchronous = FULL;
                PRAGMA busy_timeout = 5000;
                PRAGMA temp_store = MEMORY;
                PRAGMA cache_size = -8192;
                """);
            if (SqlTrace is not null)
            {
                _traceCallback ??= (_, sql) => SqlTrace?.Invoke(TraceOperation.Value ?? "direct", sql);
                SQLitePCL.raw.sqlite3_trace(connection.Handle, _traceCallback, null);
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            Release();
            throw;
        }
    }

    public void EnsureWal()
    {
        using SqliteConnection connection = Open();
        string? mode = connection.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Could not enable WAL (got '{mode}').");
    }

    public void SampleWalBytes()
    {
        try { PerformanceMetrics.WalBytes.Record(File.Exists(_walFile) ? new FileInfo(_walFile).Length : 0); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public readonly record struct DbTx(SqliteConnection Connection, SqliteTransaction Transaction);

public sealed class DbExecutor : IDisposable
{
    private readonly SqliteConnectionFactory factory;
    private static readonly Meter Meter = new("GroceryErp.Database");
    private static readonly Histogram<double> ReadMilliseconds = Meter.CreateHistogram<double>("db.read.duration", "ms");
    private static readonly Histogram<double> WriteMilliseconds = Meter.CreateHistogram<double>("db.write.duration", "ms");
    private static readonly Histogram<double> WriteWaitMilliseconds = Meter.CreateHistogram<double>("db.write.wait", "ms");
    private static readonly Histogram<int> ReturnedRows = Meter.CreateHistogram<int>("db.read.rows");
    private static readonly Histogram<int> ConcurrentReaders = Meter.CreateHistogram<int>("db.read.concurrent");
    private static readonly ObservableGauge<int> ActiveReaderGauge = Meter.CreateObservableGauge("db.read.active", () => Volatile.Read(ref _activeReaders));
    private static int _activeReaders;
    private static long _writeSamples;
    private readonly SemaphoreSlim _readGate;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public DbExecutor(SqliteConnectionFactory factory, int readConcurrency = 4)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readConcurrency);
        this.factory = factory;
        _readGate = new SemaphoreSlim(readConcurrency, readConcurrency);
    }

    public async Task<T> ReadAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken = default,
        [CallerMemberName] string operation = "")
    {
        ArgumentNullException.ThrowIfNull(work);
        long queued = Stopwatch.GetTimestamp();
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PerformanceMetrics.ReadQueueMilliseconds.Record(Stopwatch.GetElapsedTime(queued).TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation));
        ConcurrentReaders.Record(Interlocked.Increment(ref _activeReaders));
        long started = Stopwatch.GetTimestamp();
        try
        {
            return await Task.Run(() =>
            {
                using var traceScope = factory.BeginSqlTraceOperation(operation);
                cancellationToken.ThrowIfCancellationRequested();
                long opening = Stopwatch.GetTimestamp();
                using SqliteConnection connection = factory.Open();
                connection.DefaultTimeout = 30;
                using CancellationTokenRegistration interrupt = cancellationToken.Register(
                    () => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
                cancellationToken.ThrowIfCancellationRequested();
                PerformanceMetrics.ReadOpenMilliseconds.Record(Stopwatch.GetElapsedTime(opening).TotalMilliseconds,
                    new KeyValuePair<string, object?>("operation", operation));
                long working = Stopwatch.GetTimestamp();
                T result = work(connection);
                cancellationToken.ThrowIfCancellationRequested();
                PerformanceMetrics.ReadWorkMilliseconds.Record(Stopwatch.GetElapsedTime(working).TotalMilliseconds,
                    new KeyValuePair<string, object?>("operation", operation));
                object? rows = result is null ? null : result is ICollection ? result : ReadRowsProperty<T>.Property?.GetValue(result);
                if (rows is ICollection collection) ReturnedRows.Record(collection.Count,
                    new KeyValuePair<string, object?>("operation", operation));
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Database read was canceled.", exception, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorMapper.Map(exception);
        }
        finally
        {
            ReadMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", operation));
            Interlocked.Decrement(ref _activeReaders);
            _readGate.Release();
        }
    }

    public async Task<T> WriteAsync<T>(Func<DbTx, T> work, CancellationToken cancellationToken = default,
        [CallerMemberName] string operation = "")
    {
        ArgumentNullException.ThrowIfNull(work);
        long queued = Stopwatch.GetTimestamp();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        WriteWaitMilliseconds.Record(Stopwatch.GetElapsedTime(queued).TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation));
        long started = Stopwatch.GetTimestamp();
        try
        {
            return await Task.Run(() =>
            {
                using var traceScope = factory.BeginSqlTraceOperation(operation);
                using SqliteConnection connection = factory.Open();
                // Retrying BEGIN is safe: the delegate has not executed and there is
                // no possible committed outcome to replay. Never retry the delegate
                // or Commit, whose outcome may be ambiguous after an I/O failure.
                using SqliteTransaction transaction = BeginImmediateWithRetry(connection, cancellationToken);
                long transactionStarted = Stopwatch.GetTimestamp();
                try
                {
                    T result = work(new(connection, transaction));
                    transaction.Commit();
                    return result;
                }
                finally
                {
                    PerformanceMetrics.TransactionMilliseconds.Record(
                        Stopwatch.GetElapsedTime(transactionStarted).TotalMilliseconds,
                        new KeyValuePair<string, object?>("operation", operation));
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorMapper.Map(exception);
        }
        finally
        {
            WriteMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", operation));
            if (Interlocked.Increment(ref _writeSamples) % 128 == 0) factory.SampleWalBytes();
            _writeGate.Release();
        }
    }

    public Task WriteAsync(Action<DbTx> work, CancellationToken cancellationToken = default,
        [CallerMemberName] string operation = "") =>
        WriteAsync(transaction => { work(transaction); return true; }, cancellationToken, operation);

    public void Dispose()
    {
        _readGate.Dispose();
        _writeGate.Dispose();
    }

    private static SqliteTransaction BeginImmediateWithRetry(SqliteConnection connection, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return connection.BeginTransaction(deferred: false); }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 && attempt < 2)
            {
                PerformanceMetrics.BusyRetries.Add(1);
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    private static class ReadRowsProperty<T>
    {
        public static readonly System.Reflection.PropertyInfo? Property =
            typeof(T).GetProperty("Items") ?? typeof(T).GetProperty("Rows") ?? typeof(T).GetProperty("Lines");
    }
}

public static class SqliteErrorMapper
{
    private static readonly (string Fragment, ErrorCode Code)[] Rules =
    [
        ("insufficient stock in lot", ErrorCode.InsufficientStock),
        ("direct stock forbids negative stock", ErrorCode.InsufficientStock),
        ("return exceeds original sale", ErrorCode.ReturnQtyExceedsSold),
        ("completed sale values are immutable", ErrorCode.SaleFinalized),
        ("posted return is immutable", ErrorCode.SaleFinalized),
        ("posted return lines are immutable", ErrorCode.SaleFinalized),
        ("stock_movement is append-only", ErrorCode.LedgerAppendOnly),
        ("sale is finalized", ErrorCode.SaleFinalized),
        ("completed sale is read-only", ErrorCode.SaleFinalized),
        ("completed sales cannot be deleted", ErrorCode.SaleFinalized),
        ("invalid sale status change", ErrorCode.SaleFinalized),
        ("qty sign does not match movement type", ErrorCode.Validation),
        ("purchase is posted/cancelled", ErrorCode.PurchaseFinalized),
        ("selling_price <= mrp", ErrorCode.PriceAboveMrp),
        ("purchase.vendor_id, purchase.vendor_invoice_no", ErrorCode.DuplicateVendorBill),
        ("= date(", ErrorCode.InvalidDate),
        ("FOREIGN KEY constraint failed", ErrorCode.Validation),
    ];

    public static Exception Map(SqliteException exception)
    {
        if (exception.SqliteErrorCode is 5 or 6) return new DomainException(ErrorCode.DatabaseBusy);
        if (exception.SqliteErrorCode == 10) return new DomainException(ErrorCode.DatabaseIo);
        if (exception.SqliteErrorCode == 13) return new DomainException(ErrorCode.DiskFull);
        if (exception.SqliteErrorCode is 11 or 26) return new DomainException(ErrorCode.DatabaseCorrupt);
        if (exception.SqliteErrorCode == 19)
            foreach ((string fragment, ErrorCode code) in Rules)
                if (exception.Message.Contains(fragment, StringComparison.Ordinal))
                {
                    var mapped = new DomainException(code);
                    mapped.Data["sqlite"] = exception.Message;
                    return mapped;
                }
        return exception;
    }
}
