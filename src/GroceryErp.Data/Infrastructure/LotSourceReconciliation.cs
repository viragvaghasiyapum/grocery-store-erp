using System.Data;
using System.Globalization;
using Dapper;

namespace GroceryErp.Data.Infrastructure;

public sealed record LotSourceMismatch(long LotId, long ProductId, long Actual, long Expected);

// One lot's source documents: received from its origin, then sold, returned to it,
// damaged, returned to the vendor, used internally and corrected.
public sealed class LotSourceBalance
{
    public long LotId { get; init; }
    public long ProductId { get; init; }
    public long Actual { get; init; }
    public long Received { get; init; }
    public long Sold { get; set; }
    public long Returned { get; set; }
    public long Damaged { get; set; }
    public long VendorReturned { get; set; }
    public long InternalUse { get; set; }
    public long Corrections { get; set; }
    public long Expected => Received - Sold + Returned - Damaged - VendorReturned - InternalUse + Corrections;
    public long Difference => Actual - Expected;
}

// A lot's expected quantity comes only from its source documents: its origin, completed
// sales, returns, damage, vendor returns, internal use and stock corrections.
// Interactive writes never run this; the upgrade, maintenance and reviewed counts do.
//
// A resellable return names the lot it restocked. A legacy or damaged return names
// none and counts against the sold lot; a damaged one is offset by its damage row.
// A CUSTOMER_RETURN lot's creating line is already its received quantity.
public static class LotSourceReconciliation
{
    private const string LotSql = """
        SELECT l.id AS LotId,l.product_id AS ProductId,l.qty_on_hand AS Actual,
          COALESCE(CASE l.origin_kind
            WHEN 'PURCHASE_LINE' THEN (SELECT CASE WHEN p.status='POSTED' THEN pl.qty+pl.free_qty ELSE 0 END
              FROM purchase_line pl JOIN purchase p ON p.id=pl.purchase_id WHERE pl.id=l.origin_ref_id)
            WHEN 'DIRECT' THEN (SELECT quantity FROM direct_stock_origin WHERE id=l.origin_ref_id)
            ELSE l.qty_received END,0)
          - COALESCE((SELECT SUM(sl.qty) FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
              WHERE sl.lot_id=l.id AND s.status='COMPLETED'),0)
          + COALESCE((SELECT SUM(rl.qty) FROM sale_return_line rl
              WHERE rl.restock_lot_id=l.id
                AND NOT (l.origin_kind='CUSTOMER_RETURN' AND rl.id=l.origin_ref_id)),0)
          + COALESCE((SELECT SUM(rl.qty) FROM sale_line sl
              JOIN sale_return_line rl ON rl.sale_line_id=sl.id
              WHERE sl.lot_id=l.id AND rl.restock_lot_id IS NULL),0)
          - COALESCE((SELECT SUM(d.qty) FROM damage d WHERE d.lot_id=l.id),0)
          - COALESCE((SELECT SUM(vl.qty) FROM vendor_return_line vl
              WHERE vl.lot_id=l.id AND NOT EXISTS(SELECT 1 FROM vendor_return_cancellation c
                WHERE c.vendor_return_id=vl.vendor_return_id)),0)
          - COALESCE((SELECT SUM(i.quantity) FROM internal_use_record i WHERE i.lot_id_snapshot=l.id),0)
          + COALESCE((SELECT SUM(c.quantity_delta) FROM stock_correction c WHERE c.lot_id=l.id),0)
          AS Expected
        FROM product_lot l WHERE l.id=@lotId
        """;

    private const string BaseSql = """
        SELECT l.id AS LotId,l.product_id AS ProductId,l.qty_on_hand AS Actual,
          COALESCE(CASE l.origin_kind
            WHEN 'PURCHASE_LINE' THEN CASE WHEN p.status='POSTED' THEN pl.qty+pl.free_qty ELSE 0 END
            WHEN 'DIRECT' THEN d.quantity ELSE l.qty_received END,0) AS Received
        FROM product_lot l
        LEFT JOIN purchase_line pl ON pl.id=l.origin_ref_id AND l.origin_kind='PURCHASE_LINE'
        LEFT JOIN purchase p ON p.id=pl.purchase_id
        LEFT JOIN direct_stock_origin d ON d.id=l.origin_ref_id AND l.origin_kind='DIRECT'
        """;

    // Each component reads only the page's lots through an index on its lot column;
    // {0} is the page predicate on that column.
    private static readonly (string Component, string Sql)[] ComponentSql =
    [
        ("Sold", """
            SELECT sl.lot_id AS LotId,SUM(sl.qty) AS Value FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
            WHERE sl.lot_id {0} AND s.status='COMPLETED' GROUP BY sl.lot_id
            """),
        ("Returned", """
            SELECT sl.lot_id AS LotId,SUM(rl.qty) AS Value FROM sale_line sl
            JOIN sale_return_line rl ON rl.sale_line_id=sl.id
            WHERE sl.lot_id {0} AND rl.restock_lot_id IS NULL GROUP BY sl.lot_id
            """),
        ("Returned", """
            SELECT rl.restock_lot_id AS LotId,SUM(rl.qty) AS Value
            FROM sale_return_line rl JOIN product_lot l ON l.id=rl.restock_lot_id
            WHERE rl.restock_lot_id {0} AND NOT (l.origin_kind='CUSTOMER_RETURN' AND rl.id=l.origin_ref_id)
            GROUP BY rl.restock_lot_id
            """),
        ("Damaged", "SELECT lot_id AS LotId,SUM(qty) AS Value FROM damage WHERE lot_id {0} GROUP BY lot_id"),
        ("VendorReturned", """
            SELECT vl.lot_id AS LotId,SUM(vl.qty) AS Value FROM vendor_return_line vl
            WHERE vl.lot_id {0} AND NOT EXISTS(
              SELECT 1 FROM vendor_return_cancellation c WHERE c.vendor_return_id=vl.vendor_return_id)
            GROUP BY vl.lot_id
            """),
        ("InternalUse", """
            SELECT lot_id_snapshot AS LotId,SUM(quantity) AS Value FROM internal_use_record
            WHERE lot_id_snapshot {0} GROUP BY lot_id_snapshot
            """),
        ("Corrections", """
            SELECT lot_id AS LotId,SUM(quantity_delta) AS Value FROM stock_correction
            WHERE lot_id {0} GROUP BY lot_id
            """),
    ];

    public static LotSourceMismatch? CheckLot(IDbConnection connection, IDbTransaction? transaction, long lotId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        MismatchRow? row = connection.QuerySingleOrDefault<MismatchRow>(LotSql, new { lotId }, transaction);
        return row is null || row.Actual == row.Expected ? null : new(row.LotId, row.ProductId, row.Actual, row.Expected);
    }

    // Memory stays proportional to one page of lots.
    public static IEnumerable<LotSourceMismatch> CheckAll(IDbConnection connection, IDbTransaction? transaction,
        int pageSize = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (pageSize is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        long after = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<LotSourceBalance> rows = PageAfter(connection, transaction, after, pageSize);
            if (rows.Count == 0) yield break;
            foreach (LotSourceBalance row in rows)
                if (row.Actual != row.Expected) yield return new(row.LotId, row.ProductId, row.Actual, row.Expected);
            after = rows[^1].LotId;
        }
    }

    // Lots with IDs above the cursor, in ascending ID order.
    public static List<LotSourceBalance> PageAfter(IDbConnection connection, IDbTransaction? transaction,
        long after, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<LotSourceBalance> rows = connection.Query<LotSourceBalance>(
            BaseSql + " WHERE l.id>@after ORDER BY l.id LIMIT @pageSize", new { after, pageSize }, transaction).ToList();
        return rows.Count == 0 ? rows : AddComponents(connection, transaction, rows,
            "BETWEEN @first AND @last", new { first = rows[0].LotId, last = rows[^1].LotId });
    }

    // Newest lots below the cursor, optionally of one product, in descending ID order.
    public static List<LotSourceBalance> PageBefore(IDbConnection connection, IDbTransaction? transaction,
        long before, int pageSize, long? productId = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // Separate statements, so one product's page seeks its lots through the product index.
        List<LotSourceBalance> rows = connection.Query<LotSourceBalance>(BaseSql + (productId is null
            ? " WHERE l.id<@before ORDER BY l.id DESC LIMIT @pageSize"
            : " WHERE l.product_id=@productId AND l.id<@before ORDER BY l.id DESC LIMIT @pageSize"),
            new { before, pageSize, productId }, transaction).ToList();
        if (rows.Count == 0) return rows;
        // A product's lots are spread across the ID range, so they are named exactly.
        return productId is null
            ? AddComponents(connection, transaction, rows, "BETWEEN @first AND @last",
                new { first = rows[^1].LotId, last = rows[0].LotId })
            : AddComponents(connection, transaction, rows, "IN @ids",
                new { ids = rows.Select(static row => row.LotId).ToArray() });
    }

    // The named lots that still exist, newest first; used to recheck recorded differences.
    public static List<LotSourceBalance> ForLots(IDbConnection connection, IDbTransaction? transaction,
        IReadOnlyCollection<long> lotIds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(lotIds);
        if (lotIds.Count == 0) return [];
        List<LotSourceBalance> rows = connection.Query<LotSourceBalance>(
            BaseSql + " WHERE l.id IN @ids ORDER BY l.id DESC", new { ids = lotIds }, transaction).ToList();
        return rows.Count == 0 ? rows : AddComponents(connection, transaction, rows, "IN @ids",
            new { ids = rows.Select(static row => row.LotId).ToArray() });
    }

    private static List<LotSourceBalance> AddComponents(IDbConnection connection, IDbTransaction? transaction,
        List<LotSourceBalance> rows, string predicate, object parameters)
    {
        var byLot = rows.ToDictionary(static row => row.LotId);
        foreach ((string component, string sql) in ComponentSql)
            foreach (ComponentRow value in connection.Query<ComponentRow>(
                         string.Format(CultureInfo.InvariantCulture, sql, predicate), parameters, transaction))
            {
                if (!byLot.TryGetValue(value.LotId, out LotSourceBalance? row)) continue;
                switch (component)
                {
                    case "Sold": row.Sold += value.Value; break;
                    case "Returned": row.Returned += value.Value; break;
                    case "Damaged": row.Damaged += value.Value; break;
                    case "VendorReturned": row.VendorReturned += value.Value; break;
                    case "InternalUse": row.InternalUse += value.Value; break;
                    default: row.Corrections += value.Value; break;
                }
            }
        return rows;
    }

    private sealed class MismatchRow
    {
        public long LotId { get; init; }
        public long ProductId { get; init; }
        public long Actual { get; init; }
        public long Expected { get; init; }
    }

    private sealed class ComponentRow
    {
        public long LotId { get; init; }
        public long Value { get; init; }
    }
}
