using Dapper;
using GroceryErp.Core.Errors;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Infrastructure;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
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
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("""
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -64000;
            """);
        return connection;
    }

    public void EnsureWal()
    {
        using SqliteConnection connection = Open();
        string? mode = connection.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Could not enable WAL (got '{mode}').");
    }
}

public readonly record struct DbTx(SqliteConnection Connection, SqliteTransaction Transaction);

public sealed class DbExecutor(SqliteConnectionFactory factory) : IDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public Task<T> ReadAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteConnection connection = factory.Open();
            return work(connection);
        }, cancellationToken);
    }

    public async Task<T> WriteAsync<T>(Func<DbTx, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using SqliteConnection connection = factory.Open();
                using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
                T result = work(new(connection, transaction));
                transaction.Commit();
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteErrorMapper.Map(exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task WriteAsync(Action<DbTx> work, CancellationToken cancellationToken = default) =>
        WriteAsync(transaction => { work(transaction); return true; }, cancellationToken);

    public void Dispose() => _writeGate.Dispose();
}

public static class SqliteErrorMapper
{
    private static readonly (string Fragment, ErrorCode Code)[] Rules =
    [
        ("insufficient stock in lot", ErrorCode.InsufficientStock),
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
