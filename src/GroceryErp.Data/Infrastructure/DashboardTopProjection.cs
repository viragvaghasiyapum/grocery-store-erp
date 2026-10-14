using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Infrastructure;

public static class DashboardTopProjection
{
    public static void Rebuild(SqliteConnection connection, SqliteTransaction transaction, DateOnly through)
    {
        string to = through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string from = through.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        connection.Execute("DELETE FROM dashboard_top_window; DELETE FROM dashboard_top_summary;",
            transaction: transaction);
        connection.Execute("INSERT INTO dashboard_top_window(id,from_day,to_day) VALUES (1,@from,@to)",
            new { from, to }, transaction);
        connection.Execute("""
            INSERT INTO dashboard_top_summary
              (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
            SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
                   sl.product_name_local IS NULL,COUNT(*),SUM(sl.qty),
                   SUM(sl.taxable_amount),SUM(sl.cost_amount)
            FROM sale s JOIN sale_line sl ON sl.sale_id=s.id
            WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @from AND @to
            GROUP BY sl.product_id,sl.product_name,sl.product_name_local IS NULL,
                     COALESCE(sl.product_name_local,'')
            """, new { from, to }, transaction, commandTimeout: 120);
        connection.Execute("""
            INSERT INTO dashboard_top_summary
              (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
            SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
                   sl.product_name_local IS NULL,COUNT(*),-SUM(rl.qty),
                   -SUM(rl.taxable_amount),-SUM(rl.cost_amount)
            FROM sale_return r JOIN sale_return_line rl ON rl.sale_return_id=r.id
            JOIN sale_line sl ON sl.id=rl.sale_line_id
            WHERE r.return_date BETWEEN @from AND @to
            GROUP BY sl.product_id,sl.product_name,sl.product_name_local IS NULL,
                     COALESCE(sl.product_name_local,'')
            ON CONFLICT(product_id,name,local_name,local_is_null) DO UPDATE SET
              movements=movements+excluded.movements,qty=qty+excluded.qty,
              revenue=revenue+excluded.revenue,cost=cost+excluded.cost
            """, new { from, to }, transaction, commandTimeout: 120);
    }
}
