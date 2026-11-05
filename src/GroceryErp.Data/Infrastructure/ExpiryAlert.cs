namespace GroceryErp.Data.Infrastructure;

/// <summary>
/// The one expiry rule behind the dashboard's Expired, Critical and Soon tiles, the Products badge,
/// the Products and Lots expiry filters, the lot labels and the purchase short-expiry warning, so each
/// list holds exactly the lots its count names.
/// </summary>
/// <remarks>
/// <para><b>Expired</b>: a lot with stock whose expiry date is before the as-of date, blocked or not
/// and whatever its product's status, because each one still has to be written off or returned.</para>
/// <para><b>Expiring</b>: an active lot with stock whose expiry date falls from the as-of date through
/// its alert window; critical within seven days, soon after that. The window is
/// the product's alert days, else its category's, else the parent category's, else the
/// <c>expiry.default_alert_days</c> setting, else 30. The dashboard counts the expiring lots of active
/// products, because the Products list its tiles open shows active products.</para>
/// </remarks>
public static class ExpiryAlert
{
    private const string DefaultDaysSql =
        "COALESCE(CAST((SELECT value FROM setting WHERE key='expiry.default_alert_days') AS INTEGER),30)";

    /// <summary>
    /// The alert days of the product aliased <paramref name="product"/>. Only that alias has to be in
    /// scope: its category and the parent are read by key.
    /// </summary>
    public static string DaysSql(string product) => $"""
        COALESCE({product}.expiry_alert_days,
          (SELECT COALESCE(ac.expiry_alert_days,apc.expiry_alert_days) FROM category ac
           LEFT JOIN category apc ON apc.id=ac.parent_id WHERE ac.id={product}.category_id),
          {DefaultDaysSql})
        """;

    /// <summary>The last day of that product's alert window from <paramref name="date"/>, an ISO date expression.</summary>
    public static string WindowEndSql(string product, string date) =>
        $"COALESCE(date({date},'+'||{DaysSql(product)}||' days'),'9999-12-31')";

    /// <summary>The last day of the default alert window from <paramref name="date"/>, the window of a product with no alert days of its own or from its categories.</summary>
    public static string DefaultWindowEndSql(string date) => $"date({date},'+'||{DefaultDaysSql}||' days')";

    /// <summary>
    /// Every expiring lot as of <paramref name="date"/>, as rows of <c>lot_id</c>, <c>product_id</c> and
    /// <c>expiry_date</c>, for reads across all products. Lots up to the default window are read through
    /// the expiry index and checked against their own window. Past it, only products whose window is
    /// wider can have expiring lots, so their lots are read through the FEFO index. The cost follows
    /// the lots inside the default window and the number of wider-window products, not the widest
    /// window: one product alerting a year ahead made a single range read a year of lots (0.35 s on the
    /// full-size store). The wider products are found in one pass over the product table (about 4 ms at
    /// 47,000 products) and, per wider category, through the category index; the CROSS JOIN keeps the
    /// categories outer, which left to itself SQLite scanned every product again for (25 ms more).
    /// <paramref name="activeProductsOnly"/> leaves out archived products' lots.
    /// </summary>
    public static string ExpiringLotsSql(string date, bool activeProductsOnly)
    {
        // The unary plus keeps the expiry index as the outer loop. As a plain term SQLite started from
        // an automatic index on active products: 2.4 s instead of 26 ms on the full-size store.
        string active = activeProductsOnly ? " AND +p.is_active=1" : string.Empty;
        string defaultEnd = DefaultWindowEndSql(date);
        return $"""
            SELECT l.id AS lot_id,l.product_id,l.expiry_date FROM product_lot l JOIN product p ON p.id=l.product_id
            WHERE {ExpiringSql("l", "p", date)} AND l.expiry_date<={defaultEnd}{active}
            UNION ALL
            SELECT l.id,l.product_id,l.expiry_date FROM (
                SELECT wp.id FROM product wp WHERE wp.expiry_alert_days>{DefaultDaysSql}
                UNION
                SELECT wp.id FROM category wc LEFT JOIN category wpc ON wpc.id=wc.parent_id
                CROSS JOIN product wp ON wp.category_id=wc.id AND wp.expiry_alert_days IS NULL
                WHERE COALESCE(wc.expiry_alert_days,wpc.expiry_alert_days)>{DefaultDaysSql}
            ) wide CROSS JOIN product p ON p.id=wide.id
            CROSS JOIN product_lot l INDEXED BY ix_lot_fefo ON l.product_id=wide.id
            WHERE {ExpiringSql("l", "p", date)} AND (l.expiry_date IS NULL)=0 AND l.expiry_date>{defaultEnd}{active}
            """;
    }

    /// <summary>
    /// Whether product <paramref name="product"/> has an expiring lot as of <paramref name="date"/>: its
    /// earliest active lot with stock that has not yet expired falls inside its alert window. One FEFO
    /// index seek per product, and the window is worked out only for a product that has such a lot.
    /// Its cost follows the products checked, so it suits a narrowed list, or a page in name order
    /// that fills quickly; <see cref="ExpiringLotsSql"/> costs what the lots inside the window cost.
    /// Testing each lot instead worked out the window before each seek, twice the cost per product.
    /// </summary>
    public static string HasExpiringLotSql(string product, string date) => $"""
        (SELECT el.expiry_date<={WindowEndSql(product, date)} FROM product_lot el INDEXED BY ix_lot_fefo
         WHERE el.product_id={product}.id AND el.qty_on_hand>0 AND el.is_active=1 AND (el.expiry_date IS NULL)=0
           AND el.expiry_date>={date} ORDER BY el.expiry_date LIMIT 1)
        """;

    /// <summary>Whether lot <paramref name="lot"/> of product <paramref name="product"/> is expiring as of <paramref name="date"/>.</summary>
    public static string ExpiringSql(string lot, string product, string date) =>
        $"{lot}.qty_on_hand>0 AND {lot}.is_active=1 AND {lot}.expiry_date IS NOT NULL " +
        $"AND {lot}.expiry_date>={date} AND {lot}.expiry_date<={WindowEndSql(product, date)}";

    /// <summary>
    /// Whether lot <paramref name="lot"/> is expired as of <paramref name="date"/>. A missing expiry
    /// never compares, so a read that wants the partial expiry index adds <c>expiry_date IS NOT NULL</c>.
    /// </summary>
    public static string ExpiredSql(string lot, string date) =>
        $"{lot}.qty_on_hand>0 AND {lot}.expiry_date<{date}";

    /// <summary>The last day an expiring lot is critical, seven days from <paramref name="date"/>.</summary>
    public static string CriticalEndSql(string date) => $"date({date},'+7 days')";
}
