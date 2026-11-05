using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using GroceryErp.Core.Errors;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Infrastructure;

public interface IMigrationBackup
{
    void CreateBeforeMigration(string reason);
}

public sealed class NullMigrationBackup : IMigrationBackup
{
    public static NullMigrationBackup Instance { get; } = new();
    private NullMigrationBackup() { }
    public void CreateBeforeMigration(string reason) { }
}

public sealed partial class MigrationRunner(SqliteConnectionFactory factory, IMigrationBackup backup)
{
    public const int LatestVersion = 40;

    /// <summary>Backup reason for the last store that still holds the stock ledger; rotation keeps it.</summary>
    public const string LedgerArchiveReason = "pre-ledger-removal";

    public void Run(IProgress<MigrationProgress>? progress = null) => RunTo(LatestVersion, progress);

    // Tests and the scale audit stop at an older version to rehearse a later upgrade.
    internal void RunTo(int targetVersion, IProgress<MigrationProgress>? progress = null)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetVersion, LatestVersion);
        DapperSetup.Register();
        factory.EnsureWal();
        List<Script> scripts = LoadEmbedded();
        using SqliteConnection connection = factory.Open();
        long current = connection.ExecuteScalar<long>("PRAGMA user_version;");
        if (connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='direct_stock_sandbox'") != 0)
            throw new DomainException(ErrorCode.DatabaseNewerThanApp, current + 1, LatestVersion);
        if (current > LatestVersion)
            throw new DomainException(ErrorCode.DatabaseNewerThanApp, current, LatestVersion);
        if (current >= targetVersion) return;
        // The backup before schema 36 is the only remaining copy of the stock ledger.
        if (current > 0)
        {
            progress?.Report(new(MigrationStage.Backup));
            backup.CreateBeforeMigration(current < 36 && targetVersion >= 36
                ? $"{LedgerArchiveReason}-v{current}" : $"pre-migration-v{current}");
        }

        Script[] steps = [.. scripts.Where(item => item.Version > current && item.Version <= targetVersion)];
        for (int step = 0; step < steps.Length; step++)
        {
            Script script = steps[step];
            progress?.Report(new(MigrationStage.Upgrade, step + 1, steps.Length));
            if (script.Version == 29)
                connection.Execute("PRAGMA foreign_keys=OFF;");
            // Whole-history rebuilds sort millions of sale lines; spill them to disk
            // instead of holding the sorter in process memory.
            if (script.Version is 29 or 32)
                connection.Execute("PRAGMA temp_store=FILE;");
            try
            {
                using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
                string sql = script.Sql;
                if (script.Version == 18 && connection.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM pragma_table_info('pnl_day') WHERE name='sales'",
                        transaction: transaction) != 0)
                    sql = sql.Replace("ALTER TABLE pnl_day ADD COLUMN sales INTEGER NOT NULL DEFAULT 0;",
                        string.Empty, StringComparison.Ordinal);
                if (script.Version == 24)
                    foreach (string column in new[] { "selling_value", "mrp_value" })
                        if (connection.ExecuteScalar<long>(
                                "SELECT COUNT(*) FROM pragma_table_info('product_stock_total') WHERE name=@column",
                                new { column }, transaction) != 0)
                            sql = sql.Replace($"ALTER TABLE product_stock_total ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0;",
                                string.Empty, StringComparison.Ordinal);
                PhaseOneMigration? phaseOne = script.Version == 29
                    ? PhaseOneMigration.Prepare(connection, transaction) : null;
                int closedCounts = script.Version == 33 ? DirectStockCutover.Prepare(connection, transaction) : 0;
                IReadOnlyList<LedgerRemoval.PnlTotal>? pnlBefore = script.Version == 36
                    ? LedgerRemoval.Prepare(connection, transaction) : null;
                connection.Execute(sql, transaction: transaction);
                if (phaseOne is not null)
                    phaseOne.Finish(connection, transaction);
                if (script.Version == 33)
                    DirectStockCutover.Finish(connection, transaction, closedCounts);
                if (pnlBefore is not null)
                    LedgerRemoval.Finish(connection, transaction, pnlBefore);
                // Migration 019 runs before settings or the clock exist; the dashboard moves this
                // first window to the business date on its first refresh.
#pragma warning disable RS0030
                if (script.Version == 19)
                    DashboardTopProjection.Rebuild(connection, transaction, DateOnly.FromDateTime(DateTime.Now));
#pragma warning restore RS0030
                if (script.Version != 16)
                    connection.Execute(
                        $"PRAGMA user_version = {script.Version.ToString(CultureInfo.InvariantCulture)};",
                        transaction: transaction);
                transaction.Commit();
            }
            finally
            {
                if (script.Version == 29)
                    connection.Execute("PRAGMA foreign_keys=ON;");
                if (script.Version is 29 or 32)
                    connection.Execute("PRAGMA temp_store=MEMORY;");
            }
            if (script.Version == 16)
            {
                // Each month commits independently. If interrupted, user_version stays
                // at 15; the idempotent schema script and rebuild restart on next launch.
                RebuildPnlDays(connection);
                connection.Execute("PRAGMA user_version = 16;");
            }
        }
        progress?.Report(new(MigrationStage.Finishing));
        connection.Execute("PRAGMA optimize;");
    }

    // Offline repair path. A verified backup is taken by the production backup
    // implementation, then the restore barrier drains all readers and writers.
    public void RebuildPnlDays()
    {
        using (SqliteConnection check = factory.Open())
            if (!IsCurrentOperationalSchema(check))
                throw new InvalidOperationException("Migrate the database before rebuilding P&L days.");
        backup.CreateBeforeMigration("pre-pnl-day-rebuild");
        factory.BeginExclusive();
        try
        {
            using SqliteConnection connection = factory.OpenExclusive();
            RebuildPnlDays(connection);
        }
        finally
        {
            factory.EndExclusive();
        }
    }

    // Offline repair for a drifted top-products cache. The transaction either
    // replaces the complete window or leaves the previous one intact.
    public void RebuildDashboardTop(DateOnly through)
    {
        using (SqliteConnection check = factory.Open())
            if (!IsCurrentOperationalSchema(check))
                throw new InvalidOperationException("Migrate the database before rebuilding dashboard products.");
        backup.CreateBeforeMigration("pre-dashboard-top-rebuild");
        factory.BeginExclusive();
        try
        {
            using SqliteConnection connection = factory.OpenExclusive();
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            DashboardTopProjection.Rebuild(connection, transaction, through);
            transaction.Commit();
        }
        finally
        {
            factory.EndExclusive();
        }
    }

    private static bool IsCurrentOperationalSchema(SqliteConnection connection) =>
        connection.ExecuteScalar<long>("PRAGMA user_version") == LatestVersion;

    private static void RebuildPnlDays(SqliteConnection connection)
    {
        bool includeSales = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM pragma_table_info('pnl_day') WHERE name='sales'") != 0;
        bool includeReturnRounding = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM pragma_table_info('pnl_day') WHERE name='return_rounding'") != 0;
        string? first = connection.ExecuteScalar<string>("""
            SELECT MIN(day) FROM (SELECT MIN(sale_date) AS day FROM sale WHERE status='COMPLETED'
                                 UNION ALL SELECT MIN(return_date) FROM sale_return)
            """);
        string? last = connection.ExecuteScalar<string>("""
            SELECT MAX(day) FROM (SELECT MAX(sale_date) AS day FROM sale WHERE status='COMPLETED'
                                 UNION ALL SELECT MAX(return_date) FROM sale_return)
            """);
        using (SqliteTransaction reset = connection.BeginTransaction(deferred: false))
        {
            connection.Execute("DELETE FROM pnl_day", transaction: reset);
            reset.Commit();
        }
        if (first is null || last is null) return;
        DateOnly month = DateOnly.ParseExact(first[..7] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateOnly finalMonth = DateOnly.ParseExact(last[..7] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        while (month <= finalMonth)
        {
            string from = month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string until = month.Year == 9999 && month.Month == 12 ? "9999-13-01" :
                month.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            using SqliteTransaction chunk = connection.BeginTransaction(deferred: false);
            connection.Execute(includeSales ? """
                INSERT INTO pnl_day(day,bills,taxable,rounding,cost,sales)
                SELECT sale_date,COUNT(*),SUM(taxable_total),SUM(round_off),SUM(cost_total),SUM(grand_total)
                FROM sale WHERE status='COMPLETED' AND sale_date>=@from AND sale_date<@until
                GROUP BY sale_date
                """ : """
                INSERT INTO pnl_day(day,bills,taxable,rounding,cost)
                SELECT sale_date,COUNT(*),SUM(taxable_total),SUM(round_off),SUM(cost_total)
                FROM sale WHERE status='COMPLETED' AND sale_date>=@from AND sale_date<@until
                GROUP BY sale_date
                """, new { from, until }, chunk);
            connection.Execute(includeReturnRounding ? """
                INSERT INTO pnl_day(day,returns,return_taxable,return_cost,return_rounding)
                SELECT return_date,COUNT(*),SUM(taxable_total),SUM(cost_total),
                       SUM(refund_total-taxable_total-tax_total)
                FROM sale_return WHERE return_date>=@from AND return_date<@until
                GROUP BY return_date
                ON CONFLICT(day) DO UPDATE SET
                    returns=excluded.returns,return_taxable=excluded.return_taxable,
                    return_cost=excluded.return_cost,return_rounding=excluded.return_rounding
                """ : """
                INSERT INTO pnl_day(day,returns,return_taxable,return_cost)
                SELECT return_date,COUNT(*),SUM(taxable_total),SUM(cost_total)
                FROM sale_return WHERE return_date>=@from AND return_date<@until
                GROUP BY return_date
                ON CONFLICT(day) DO UPDATE SET
                    returns=excluded.returns,return_taxable=excluded.return_taxable,
                    return_cost=excluded.return_cost
                """, new { from, until }, chunk);
            chunk.Commit();
            if (month == finalMonth) break;
            month = month.AddMonths(1);
        }
    }

    private static List<Script> LoadEmbedded()
    {
        Assembly assembly = typeof(MigrationRunner).Assembly;
        var scripts = new List<Script>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(static name => name.Contains(".db.", StringComparison.Ordinal)))
        {
            Match match = ScriptName().Match(resource);
            if (!match.Success) continue;
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Migration resource '{resource}' is missing.");
            using var reader = new StreamReader(stream);
            string sql = UserVersionStatement().Replace(reader.ReadToEnd(), string.Empty);
            scripts.Add(new(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), resource, sql));
        }

        scripts.Sort(static (left, right) => left.Version.CompareTo(right.Version));
        if (scripts.Count != LatestVersion || scripts[^1].Version != LatestVersion ||
            !scripts.Select(static item => item.Version).SequenceEqual(Enumerable.Range(1, LatestVersion)))
            throw new InvalidOperationException("Embedded migrations must be contiguous from 001 through the latest version.");
        return scripts;
    }

    private sealed record Script(int Version, string Name, string Sql);

    [GeneratedRegex(@"\.(\d{3})_[^.]+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptName();

    [GeneratedRegex(@"^\s*PRAGMA\s+user_version\s*=\s*\d+\s*;[^\r\n]*", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex UserVersionStatement();
}

public static class DatabaseCapabilities
{
    public static void Verify(SqliteConnectionFactory factory)
    {
        using SqliteConnection connection = factory.Open();
        var version = Version.Parse(connection.ExecuteScalar<string>("SELECT sqlite_version();")
            ?? throw new InvalidOperationException("SQLite did not report a version."));
        if (version < new Version(3, 37))
            throw new InvalidOperationException($"SQLite 3.37 or later is required; found {version}.");
        connection.Execute("CREATE VIRTUAL TABLE temp.capability_fts USING fts5(x, tokenize='trigram'); DROP TABLE temp.capability_fts;");
    }
}
