using System.Globalization;

namespace GroceryErp.Data.Infrastructure;

/// <summary>
/// The lot Billing sells a product from next, the first one <c>LotQuantities.AllocateFefo</c> takes:
/// an active lot with stock, the earliest expiry first, then the earliest received, lots without an
/// expiry last, and, while <c>pos.block_expired</c> is on (the default), none past its expiry date.
/// The Products list and Billing's list of name matches show its selling price, MRP and purchase
/// price including tax, and a price-label scan sells from it.
/// </summary>
public static class SellableLot
{
    /// <summary>
    /// The ID of that lot for the product <paramref name="productId"/> (an SQL expression), or NULL when
    /// the product has no stock it can sell. <paramref name="from"/> is the parameter that holds
    /// <see cref="From"/>. Two seeks of ix_lot_fefo: the first lot with an expiry from that date, else
    /// the first lot without one. Expired lots ahead of it are skipped by the seek, not read: on the
    /// full-size store, where one product holds 802 expired lots, a page of 200 products took 0.3 ms,
    /// against 1.5 ms walking the index past them and 81 ms sorting each product's lots.
    /// </summary>
    public static string NextIdSql(string productId, string from) => $"""
        COALESCE(
          (SELECT nl.id FROM product_lot nl INDEXED BY ix_lot_fefo
           WHERE nl.product_id={productId} AND nl.qty_on_hand>0 AND nl.is_active=1
             AND (nl.expiry_date IS NULL)=0 AND nl.expiry_date>={from}
           ORDER BY nl.expiry_date,nl.received_date,nl.id LIMIT 1),
          (SELECT nl.id FROM product_lot nl INDEXED BY ix_lot_fefo
           WHERE nl.product_id={productId} AND nl.qty_on_hand>0 AND nl.is_active=1
             AND (nl.expiry_date IS NULL)=1
           ORDER BY nl.expiry_date,nl.received_date,nl.id LIMIT 1))
        """;

    /// <summary>
    /// The earliest expiry date a sale on <paramref name="businessDate"/> may take, as an ISO date: that
    /// date while expired lots are blocked, else any date.
    /// </summary>
    public static string From(bool blockExpired, DateOnly businessDate) =>
        blockExpired ? businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "0000-01-01";
}
