using System.Data;
using System.Globalization;
using Dapper;

namespace GroceryErp.Data.Infrastructure;

public sealed class PnlSourceLine
{
    public string Month { get; init; } = "";
    public string Section { get; init; } = "";
    public string HeadCode { get; init; } = "";
    public long CategoryId { get; init; }
    public long Amount { get; init; }
    public long SourceCount { get; init; }
}

// Every P&L figure, live or finalized, comes from v_pnl_entry. Its date filter is
// pushed into each source branch, so a year touches only that year's dated rows.
public static class PnlSource
{
    public static List<PnlSourceLine> Lines(IDbConnection connection, DateOnly from, DateOnly to,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        var command = new CommandDefinition("""
            SELECT substr(entry_date,1,7) AS Month,section AS Section,head_code AS HeadCode,
                   category_id AS CategoryId,SUM(amount) AS Amount,SUM(source_count) AS SourceCount
            FROM v_pnl_entry WHERE entry_date BETWEEN @from AND @to
            GROUP BY substr(entry_date,1,7),section,head_code,category_id
            HAVING SUM(amount)<>0 OR SUM(source_count)<>0
            ORDER BY Month,CASE section WHEN 'INCOME' THEN 1 WHEN 'COGS' THEN 2 WHEN 'EXPENSE' THEN 3 ELSE 4 END,
                     head_code,category_id
            """, new { from = Iso(from), to = Iso(to) }, transaction, cancellationToken: cancellationToken);
        return connection.Query<PnlSourceLine>(command).ToList();
    }

    // The earliest dated P&L source. Each branch reads the first entry of its own date index.
    public static DateOnly? FirstEntryDate(IDbConnection connection, IDbTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string? first = connection.ExecuteScalar<string?>("""
            SELECT MIN(day) FROM (
              SELECT (SELECT MIN(day) FROM pnl_day) AS day
              UNION ALL SELECT (SELECT MIN(damage_date) FROM damage)
              UNION ALL SELECT (SELECT MIN(effective_date) FROM damage_correction)
              UNION ALL SELECT (SELECT MIN(correction_date) FROM stock_correction)
              UNION ALL SELECT (SELECT MIN(expense_date) FROM expense)
              UNION ALL SELECT (SELECT MIN(reversal_date) FROM purchase_expense_reversal)
              UNION ALL SELECT (SELECT MIN(reverse_date) FROM expense_correction)
              UNION ALL SELECT (SELECT MIN(period_month)||'-01' FROM salary_payment)
              UNION ALL SELECT (SELECT MIN(effective_date) FROM salary_correction)
              UNION ALL SELECT (SELECT MIN(period_month)||'-01' FROM asset_depreciation)
              UNION ALL SELECT (SELECT MIN(disposed_date) FROM asset)
              UNION ALL SELECT (SELECT MIN(return_date) FROM vendor_return)
            ) WHERE day IS NOT NULL
            """, transaction: transaction);
        return first is null ? null : DateOnly.ParseExact(first, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
