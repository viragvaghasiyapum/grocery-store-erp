using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Data.Services;

public sealed record AnnualCloseInfo(AnnualPeriod Period, DateOnly EditCutoff, DateOnly GeneratedOn,
    string GeneratedAt, int FormatVersion, int LineCount, long NetProfit, string Checksum);

public sealed class AnnualCloseLine
{
    public string Month { get; init; } = "";
    public string Section { get; init; } = "";
    public string HeadCode { get; init; } = "";
    public long CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public string? CategoryNameLocal { get; init; }
    public long Amount { get; init; }
    public long SourceCount { get; init; }
}

public sealed record AnnualCloseSnapshot(AnnualCloseInfo Info, IReadOnlyList<AnnualCloseLine> Lines);

// A locked calendar or fiscal year is finalized once: its head, category and month
// totals are saved with a checksum and never recomputed. Generation is idempotent
// and runs at startup, in idle maintenance, and on first request for a locked year.
public sealed class AnnualCloseService(DbExecutor database, PeriodGuard periods, IClock clock)
{
    public const int FormatVersion = 1;

    public async Task<int> EnsureFinalizedAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AnnualPeriod> missing = await database.ReadAsync(connection =>
            Missing(connection, null, periods.Today, periods.FiscalStartMonth), cancellationToken).ConfigureAwait(false);
        int created = 0;
        foreach (AnnualPeriod period in missing)
            if (await database.WriteAsync(transaction => Finalize(transaction, period, periods.Today, clock.Now),
                    cancellationToken, "annual-close").ConfigureAwait(false))
                created++;
        return created;
    }

    // Years that are locked on the business date and hold or follow the first P&L entry.
    public static IReadOnlyList<AnnualPeriod> Missing(IDbConnection connection, IDbTransaction? transaction,
        DateOnly businessDate, int fiscalStartMonth)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (PnlSource.FirstEntryDate(connection, transaction) is not { } first) return [];
        HashSet<(string, string)> existing = connection.Query<(string Kind, string Start)>(
            "SELECT period_kind AS Kind,year_start AS Start FROM annual_pnl_close", transaction: transaction)
            .ToHashSet();
        var missing = new List<AnnualPeriod>();
        foreach (AnnualPeriodKind kind in new[] { AnnualPeriodKind.Fiscal, AnnualPeriodKind.Calendar })
        {
            AnnualPeriod period = AnnualPeriod.Containing(kind, first, fiscalStartMonth);
            while (period.IsLockedOn(businessDate))
            {
                if (!existing.Contains((period.Code, Iso(period.Start)))) missing.Add(period);
                period = AnnualPeriod.FromStart(kind, period.Start.AddYears(1), fiscalStartMonth);
            }
        }
        return missing.OrderBy(static period => period.LockDate).ThenBy(static period => period.Kind).ToList();
    }

    public static bool Exists(IDbConnection connection, IDbTransaction? transaction, AnnualPeriod period)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteScalar<long>("""
            SELECT EXISTS(SELECT 1 FROM annual_pnl_close WHERE period_kind=@kind AND year_start=@start)
            """, new { kind = period.Code, start = Iso(period.Start) }, transaction) != 0;
    }

    public static bool Finalize(DbTx transaction, AnnualPeriod period, DateOnly businessDate, DateTime now)
    {
        if (!period.IsLockedOn(businessDate))
            throw new InvalidOperationException("Only a locked year can be finalized.");
        if (Exists(transaction.Connection, transaction.Transaction, period)) return false;
        List<PnlSourceLine> source = PnlSource.Lines(transaction.Connection, period.Start, period.End,
            transaction.Transaction);
        long[] categoryIds = source.Where(static line => line.CategoryId != 0)
            .Select(static line => line.CategoryId).Distinct().ToArray();
        Dictionary<long, (string Name, string? Local)> names = categoryIds.Length == 0 ? [] :
            transaction.Connection.Query<(long Id, string Name, string? Local)>(
                "SELECT id AS Id,name AS Name,name_local AS Local FROM expense_category WHERE id IN @categoryIds",
                new { categoryIds }, transaction.Transaction).ToDictionary(static row => row.Id, static row => (row.Name, row.Local));
        List<AnnualCloseLine> lines = source.Select(line => new AnnualCloseLine
        {
            Month = line.Month,
            Section = line.Section,
            HeadCode = line.HeadCode,
            CategoryId = line.CategoryId,
            CategoryName = line.CategoryId == 0 ? null : names.TryGetValue(line.CategoryId, out var name) ? name.Name : null,
            CategoryNameLocal = line.CategoryId == 0 ? null : names.TryGetValue(line.CategoryId, out var local) ? local.Local : null,
            Amount = line.Amount,
            SourceCount = line.SourceCount,
        }).OrderBy(static line => line, LineOrder.Instance).ToList();
        var info = new AnnualCloseInfo(period, period.EditCutoff, businessDate,
            now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), FormatVersion, lines.Count,
            NetProfit(lines), "");
        info = info with { Checksum = Checksum(info, lines) };
        transaction.Connection.Execute("""
            INSERT INTO annual_pnl_close(period_kind,year_start,year_end,edit_cutoff,generated_on,generated_at,
                format_version,line_count,net_profit,checksum)
            VALUES (@kind,@start,@end,@cutoff,@generatedOn,@generatedAt,@version,@count,@net,@checksum)
            """, new
        {
            kind = period.Code,
            start = Iso(period.Start),
            end = Iso(period.End),
            cutoff = Iso(info.EditCutoff),
            generatedOn = Iso(info.GeneratedOn),
            generatedAt = info.GeneratedAt,
            version = info.FormatVersion,
            count = info.LineCount,
            net = info.NetProfit,
            checksum = info.Checksum,
        }, transaction.Transaction);
        transaction.Connection.Execute("""
            INSERT INTO annual_pnl_line(period_kind,year_start,month,section,head_code,category_id,
                category_name,category_name_local,amount,source_count)
            VALUES (@kind,@start,@Month,@Section,@HeadCode,@CategoryId,@CategoryName,@CategoryNameLocal,
                @Amount,@SourceCount)
            """, lines.Select(line => new
        {
            kind = period.Code,
            start = Iso(period.Start),
            line.Month,
            line.Section,
            line.HeadCode,
            line.CategoryId,
            line.CategoryName,
            line.CategoryNameLocal,
            line.Amount,
            line.SourceCount,
        }), transaction.Transaction);
        return true;
    }

    // Loads and verifies a finalized year. A checksum mismatch means the saved report
    // was altered outside the application and must not be shown as final.
    public static AnnualCloseSnapshot? Load(IDbConnection connection, IDbTransaction? transaction,
        AnnualPeriod period)
    {
        ArgumentNullException.ThrowIfNull(connection);
        CloseRow? row = connection.QuerySingleOrDefault<CloseRow>(CloseSql + " WHERE period_kind=@kind AND year_start=@start",
            new { kind = period.Code, start = Iso(period.Start) }, transaction);
        if (row is null) return null;
        List<AnnualCloseLine> lines = connection.Query<AnnualCloseLine>("""
            SELECT month AS Month,section AS Section,head_code AS HeadCode,category_id AS CategoryId,
                   category_name AS CategoryName,category_name_local AS CategoryNameLocal,
                   amount AS Amount,source_count AS SourceCount
            FROM annual_pnl_line WHERE period_kind=@kind AND year_start=@start
            """, new { kind = period.Code, start = Iso(period.Start) }, transaction)
            .OrderBy(static line => line, LineOrder.Instance).ToList();
        AnnualCloseInfo info = row.ToModel();
        if (lines.Count != info.LineCount || !string.Equals(Checksum(info, lines), info.Checksum, StringComparison.Ordinal))
            throw new DomainException(ErrorCode.AnnualReportInvalid, period.Label);
        return new(info, lines);
    }

    public static IReadOnlyList<AnnualCloseInfo> Closes(IDbConnection connection, IDbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Query<CloseRow>(CloseSql + " ORDER BY year_start,period_kind", transaction: transaction)
            .Select(static row => row.ToModel()).ToList();
    }

    // Verifies every finalized year for maintenance; returns the labels that fail.
    public static IReadOnlyList<string> VerifyAll(IDbConnection connection)
    {
        var failed = new List<string>();
        foreach (AnnualCloseInfo close in Closes(connection, null))
            try { Load(connection, null, close.Period); }
            catch (DomainException exception) when (exception.Code == ErrorCode.AnnualReportInvalid)
            {
                failed.Add($"{close.Period.Code} {close.Period.Label}");
            }
        return failed;
    }

    public static long NetProfit(IEnumerable<AnnualCloseLine> lines) => lines.Sum(static line => line.Section switch
    {
        "INCOME" or "OTHER" => line.Amount,
        _ => -line.Amount,
    });

    public static string Checksum(AnnualCloseInfo info, IEnumerable<AnnualCloseLine> lines)
    {
        ArgumentNullException.ThrowIfNull(info);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{info.Period.Code}|{Iso(info.Period.Start)}|{Iso(info.Period.End)}|")
            .Append(CultureInfo.InvariantCulture, $"{Iso(info.EditCutoff)}|{Iso(info.GeneratedOn)}|{info.GeneratedAt}|")
            .Append(CultureInfo.InvariantCulture, $"{info.FormatVersion}|{info.LineCount}|{info.NetProfit}\n");
        foreach (AnnualCloseLine line in lines)
            text.Append(CultureInfo.InvariantCulture,
                $"{line.Month}|{line.Section}|{line.HeadCode}|{line.CategoryId}|{line.CategoryName}|{line.CategoryNameLocal}|{line.Amount}|{line.SourceCount}\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private const string CloseSql = """
        SELECT period_kind AS Kind,year_start AS Start,edit_cutoff AS EditCutoff,generated_on AS GeneratedOn,
               generated_at AS GeneratedAt,format_version AS FormatVersion,line_count AS LineCount,
               net_profit AS NetProfit,checksum AS Checksum
        FROM annual_pnl_close
        """;

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class CloseRow
    {
        public string Kind { get; init; } = "";
        public DateOnly Start { get; init; }
        public DateOnly EditCutoff { get; init; }
        public DateOnly GeneratedOn { get; init; }
        public string GeneratedAt { get; init; } = "";
        public int FormatVersion { get; init; }
        public int LineCount { get; init; }
        public long NetProfit { get; init; }
        public string Checksum { get; init; } = "";
        public AnnualCloseInfo ToModel() => new(
            AnnualPeriod.FromStart(AnnualPeriod.ParseKind(Kind), Start, Start.Month),
            EditCutoff, GeneratedOn, GeneratedAt, FormatVersion, LineCount, NetProfit, Checksum);
    }

    private sealed class LineOrder : IComparer<AnnualCloseLine>
    {
        public static LineOrder Instance { get; } = new();

        public int Compare(AnnualCloseLine? x, AnnualCloseLine? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int result = string.CompareOrdinal(x.Month, y.Month);
            if (result == 0) result = string.CompareOrdinal(x.Section, y.Section);
            if (result == 0) result = string.CompareOrdinal(x.HeadCode, y.HeadCode);
            return result == 0 ? x.CategoryId.CompareTo(y.CategoryId) : result;
        }
    }
}
