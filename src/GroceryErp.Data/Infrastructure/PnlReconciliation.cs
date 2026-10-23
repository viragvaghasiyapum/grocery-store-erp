using System.Data;
using System.Globalization;
using Dapper;
using GroceryErp.Core.Time;

namespace GroceryErp.Data.Infrastructure;

// Background check that the open-period P&L day projection still equals its sale
// and return sources. Finalized years read their saved snapshots instead, so only
// days whose calendar and fiscal years are both open are compared. A drifted
// projection is rebuilt offline with MigrationRunner.RebuildPnlDays.
public static class PnlReconciliation
{
    public static IEnumerable<string> CheckOpenDays(IDbConnection connection, DateOnly businessDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        int fiscalStartMonth = connection.ExecuteScalar<int?>(
            "SELECT CAST(value AS INTEGER) FROM setting WHERE key='fy.start_month'") is int month and >= 1 and <= 12
            ? month : 4;
        string from = AnnualPeriod.FirstOpenMonth(businessDate, fiscalStartMonth).FirstDay
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var command = new CommandDefinition("""
            WITH s AS MATERIALIZED (
              SELECT sale_date AS day,COUNT(*) AS bills,SUM(taxable_total) AS taxable,
                     SUM(round_off) AS rounding,SUM(cost_total) AS cost
              FROM sale WHERE status='COMPLETED' AND sale_date>=@from GROUP BY sale_date
            ), r AS MATERIALIZED (
              SELECT return_date AS day,COUNT(*) AS returns,SUM(taxable_total) AS taxable,
                     SUM(cost_total) AS cost,SUM(refund_total-taxable_total-tax_total) AS rounding
              FROM sale_return WHERE return_date>=@from GROUP BY return_date
            ), days AS (
              SELECT day FROM s UNION SELECT day FROM r UNION SELECT day FROM pnl_day WHERE day>=@from
            )
            SELECT d.day FROM days d
            LEFT JOIN s ON s.day=d.day LEFT JOIN r ON r.day=d.day LEFT JOIN pnl_day p ON p.day=d.day
            WHERE COALESCE(p.bills,0)<>COALESCE(s.bills,0) OR COALESCE(p.taxable,0)<>COALESCE(s.taxable,0)
               OR COALESCE(p.rounding,0)<>COALESCE(s.rounding,0) OR COALESCE(p.cost,0)<>COALESCE(s.cost,0)
               OR COALESCE(p.returns,0)<>COALESCE(r.returns,0)
               OR COALESCE(p.return_taxable,0)<>COALESCE(r.taxable,0)
               OR COALESCE(p.return_cost,0)<>COALESCE(r.cost,0)
               OR COALESCE(p.return_rounding,0)<>COALESCE(r.rounding,0)
            ORDER BY d.day
            """, new { from }, cancellationToken: cancellationToken);
        return connection.Query<string>(command);
    }
}
