namespace GroceryErp.Reports;

internal static class ReportDefinitions
{
    // Both stock aggregates read most lots, so they scan the table in order: on the 223,272-lot
    // rehearsal store an index walk with a row lookup per lot took 1.5-2.0 s, the scan 0.1-0.2 s.
    public const string StockValueWithoutExpired = """
        WITH available AS MATERIALIZED (
          SELECT l.product_id,SUM(l.qty_on_hand) AS quantity,
                 SUM(CAST(ROUND(l.qty_on_hand*l.unit_cost/1000.0) AS INTEGER)) AS cost_value,
                 SUM(CAST(ROUND(l.qty_on_hand*l.selling_price/1000.0) AS INTEGER)) AS selling_value,
                 SUM(CAST(ROUND(l.qty_on_hand*COALESCE(l.mrp,l.selling_price)/1000.0) AS INTEGER)) AS mrp_value
          FROM product_lot l NOT INDEXED
          WHERE l.qty_on_hand>0 AND (l.expiry_date IS NULL OR l.expiry_date>=@stockAsOf)
          GROUP BY l.product_id
        )
        SELECT c.name AS Category,p.sku AS Sku,p.name AS Product,p.name_local AS LocalName,
               p.product_class AS Class,t.quantity AS Quantity,t.cost_value AS CostValue,
               t.selling_value AS SellingValue,t.mrp_value AS MrpValue
        FROM available t JOIN product p ON p.id=t.product_id
        JOIN category c ON c.id=p.category_id
        ORDER BY c.name,p.name,p.id LIMIT @Limit
        """;

    // Unsold stock in lots that expired before the as-of date. The current-stock
    // report states it either way so both of its totals can be read side by side.
    public const string ExpiredStockTotals = """
        SELECT COUNT(*) AS Lots,COALESCE(SUM(qty_on_hand),0) AS Quantity,
               COALESCE(SUM(CAST(ROUND(qty_on_hand*unit_cost/1000.0) AS INTEGER)),0) AS CostValue
        FROM product_lot NOT INDEXED
        WHERE qty_on_hand>0 AND expiry_date IS NOT NULL AND expiry_date<@stockAsOf
        """;

    public const string GstSummaryFromDay = """
        SELECT 'OUTWARD' AS Direction,hsn AS Hsn,rate AS Rate,
               SUM(revenue) AS Taxable,SUM(tax) AS Tax
        FROM sale_report_day WHERE day BETWEEN @from AND @to GROUP BY hsn,rate
        UNION ALL
        SELECT 'INWARD',Hsn,Rate,SUM(Taxable),SUM(Tax)
        FROM (
          SELECT COALESCE(v.gstin,'') AS Hsn,pl.tax_rate_pct AS Rate,
                 CAST(ROUND(pl.qty*pl.unit_cost/1000.0) AS INTEGER)-pl.discount AS Taxable,
                 pl.tax_amount AS Tax
          FROM purchase_line pl JOIN purchase p ON p.id=pl.purchase_id JOIN vendor v ON v.id=p.vendor_id
          WHERE p.status='POSTED' AND p.received_date BETWEEN @from AND @to
          UNION ALL
          SELECT COALESCE(v.gstin,''),pl.tax_rate_pct,-vrl.credit_before_tax,-vrl.tax_credit
          FROM vendor_return_line vrl JOIN vendor_return vr ON vr.id=vrl.vendor_return_id
          JOIN vendor v ON v.id=vr.vendor_id JOIN product_lot l ON l.id=vrl.lot_id
          JOIN purchase_line pl ON pl.id=l.purchase_line_id
          WHERE vr.return_date BETWEEN @from AND @to
        ) inward_lines GROUP BY Hsn,Rate
        ORDER BY Direction,Hsn,Rate LIMIT @Limit
        """;

    public const string ItemSalesForProduct = """
        WITH movements AS (
          SELECT sl.product_id,sl.product_name,sl.product_name_local,sl.qty,sl.taxable_amount,sl.cost_amount
          FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
          WHERE sl.product_id=@ProductId AND s.status='COMPLETED' AND s.sale_date BETWEEN @from AND @to
          UNION ALL
          SELECT sl.product_id,sl.product_name,sl.product_name_local,-rl.qty,-rl.taxable_amount,-rl.cost_amount
          FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
          JOIN sale_return r ON r.id=rl.sale_return_id
          WHERE sl.product_id=@ProductId AND r.return_date BETWEEN @from AND @to
        )
        SELECT c.name AS Category,p.sku AS Sku,MAX(m.product_name) AS Product,
               MAX(m.product_name_local) AS LocalName,SUM(m.qty) AS Quantity,
               SUM(m.taxable_amount) AS Revenue,SUM(m.cost_amount) AS Cost,
               SUM(m.taxable_amount-m.cost_amount) AS Margin
        FROM movements m JOIN product p ON p.id=m.product_id JOIN category c ON c.id=p.category_id
        GROUP BY c.id,m.product_id ORDER BY c.name,Revenue DESC LIMIT @Limit
        """;

    // The dashboard's exact rolling window can answer an overlapping item report.
    // Only the at-most-seven-day edge is read from sale/return lines.
    public const string ItemSalesFromTopWindow = """
        WITH movements AS (
          SELECT product_id,name,local_name,local_is_null,movements,qty,revenue,cost
          FROM dashboard_top_summary WHERE movements>0
          UNION ALL
          SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
                 sl.product_name_local IS NULL,@deltaSign,@deltaSign*sl.qty,
                 @deltaSign*sl.taxable_amount,@deltaSign*sl.cost_amount
          FROM sale s JOIN sale_line sl ON sl.sale_id=s.id
          WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @deltaFrom AND @deltaTo
          UNION ALL
          SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
                 sl.product_name_local IS NULL,@deltaSign,-@deltaSign*rl.qty,
                 -@deltaSign*rl.taxable_amount,-@deltaSign*rl.cost_amount
          FROM sale_return r JOIN sale_return_line rl ON rl.sale_return_id=r.id
          JOIN sale_line sl ON sl.id=rl.sale_line_id
          WHERE r.return_date BETWEEN @deltaFrom AND @deltaTo
        ), snapshots AS (
          SELECT product_id,name,local_name,local_is_null,
                 SUM(qty) AS qty,SUM(revenue) AS revenue,SUM(cost) AS cost
          FROM movements GROUP BY product_id,name,local_name,local_is_null
          HAVING SUM(movements)>0
        ), totals AS (
          SELECT product_id,MAX(name) AS product_name,
                 MAX(CASE WHEN local_is_null=0 THEN local_name END) AS product_name_local,
                 SUM(qty) AS qty,SUM(revenue) AS revenue,SUM(cost) AS cost
          FROM snapshots GROUP BY product_id
        )
        SELECT c.name AS Category,p.sku AS Sku,t.product_name AS Product,
               t.product_name_local AS LocalName,t.qty AS Quantity,
               t.revenue AS Revenue,t.cost AS Cost,t.revenue-t.cost AS Margin
        FROM totals t JOIN product p ON p.id=t.product_id JOIN category c ON c.id=p.category_id
        WHERE @ProductId IS NULL OR t.product_id=@ProductId
        ORDER BY c.name,Revenue DESC LIMIT @Limit
        """;

    private static ReportColumn T(string key, string heading) => new(key, heading, ReportValueKind.Text);
    private static ReportColumn D(string key, string heading) => new(key, heading, ReportValueKind.Date);
    private static ReportColumn I(string key, string heading) => new(key, heading, ReportValueKind.Count);
    private static ReportColumn Q(string key, string heading) => new(key, heading, ReportValueKind.Quantity);
    private static ReportColumn M(string key, string heading) => new(key, heading, ReportValueKind.Money);
    private static ReportColumn P(string key, string heading) => new(key, heading, ReportValueKind.Percent);

    public static IReadOnlyDictionary<ReportKind, ReportDefinition> All { get; } =
        new Dictionary<ReportKind, ReportDefinition>
        {
            [ReportKind.SalesRegister] = new("Sales register", true, """
                SELECT s.sale_date AS Date,s.invoice_no AS Invoice,s.status AS Status,s.line_count AS Items,
                       CASE WHEN s.status='VOID' THEN 0 ELSE s.taxable_total END AS Taxable,
                       CASE WHEN s.status='VOID' THEN 0 ELSE s.tax_total END AS Tax,
                       CASE WHEN s.status='VOID' THEN 0 ELSE s.round_off END AS RoundOff,
                       CASE WHEN s.status='VOID' THEN 0 ELSE s.grand_total END AS Total,
                       s.grand_total AS OriginalTotal,
                       COALESCE((SELECT group_concat(mode, ', ') FROM (SELECT DISTINCT mode FROM sale_payment WHERE sale_id=s.id ORDER BY mode)),'') AS PaymentMode
                FROM sale s WHERE s.sale_date BETWEEN @from AND @to AND s.status IN ('COMPLETED','VOID')
                  AND (@Status IS NULL OR s.status=@Status)
                  AND (@PaymentMode IS NULL OR EXISTS (SELECT 1 FROM sale_payment sp WHERE sp.sale_id=s.id AND sp.mode=@PaymentMode))
                ORDER BY s.sale_date,s.id LIMIT @Limit
                """, [D("Date", "Date"), T("Invoice", "Invoice"), T("Status", "Status"), I("Items", "Items"), M("Taxable", "Taxable"), M("Tax", "Tax"), M("RoundOff", "Round off"), M("Total", "Total"), M("OriginalTotal", "Original bill"), T("PaymentMode", "Payment mode")]),

            // Totals are grouped by product before the product and category joins: joining first made
            // SQLite index and sort every sale line (1.0 GB and 18.7 s for all dates on the full-size
            // store); this sorts narrower rows once (10 s and 95 MiB with the sort spilled to disk).
            [ReportKind.ItemSales] = new("Item-wise sales", true, """
                WITH movements AS (
                  SELECT sl.product_id,sl.product_name,sl.product_name_local,sl.qty,sl.taxable_amount,sl.cost_amount
                  FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                  WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @from AND @to
                  UNION ALL
                  SELECT sl.product_id,sl.product_name,sl.product_name_local,-rl.qty,-rl.taxable_amount,-rl.cost_amount
                  FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
                  JOIN sale_line sl ON sl.id=rl.sale_line_id WHERE r.return_date BETWEEN @from AND @to
                ), totals AS MATERIALIZED (
                  SELECT product_id,MAX(product_name) AS name,MAX(product_name_local) AS local_name,
                         SUM(qty) AS quantity,SUM(taxable_amount) AS revenue,SUM(cost_amount) AS cost
                  FROM movements WHERE (@ProductId IS NULL OR product_id=@ProductId) GROUP BY product_id
                )
                SELECT c.name AS Category,p.sku AS Sku,t.name AS Product,t.local_name AS LocalName,
                       t.quantity AS Quantity,t.revenue AS Revenue,t.cost AS Cost,t.revenue-t.cost AS Margin
                FROM totals t JOIN product p ON p.id=t.product_id JOIN category c ON c.id=p.category_id
                ORDER BY c.name,Revenue DESC LIMIT @Limit
                """, [T("Category", "Category"), T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), M("Revenue", "Revenue"), M("Cost", "Cost"), M("Margin", "Margin")]),

            [ReportKind.StockValue] = new("Stock value", false, """
                SELECT c.name AS Category,p.sku AS Sku,p.name AS Product,p.name_local AS LocalName,
                       p.product_class AS Class,t.quantity AS Quantity,t.cost_value AS CostValue,
                       t.selling_value AS SellingValue,t.mrp_value AS MrpValue
                FROM product_stock_total t JOIN product p ON p.id=t.product_id
                JOIN category c ON c.id=p.category_id
                WHERE t.quantity<>0 OR t.cost_value<>0 OR t.selling_value<>0 OR t.mrp_value<>0
                ORDER BY c.name,p.name,p.id LIMIT @Limit
                """, [T("Category", "Category"), T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), T("Class", "Type"), Q("Quantity", "Quantity"), M("CostValue", "Cost value"), M("SellingValue", "Selling value"), M("MrpValue", "MRP value")]),

            // Computed per lot from source documents by ReportService; the columns are fixed here.
            [ReportKind.StockReconciliation] = new("Purchased vs used reconciliation", false, "",
                [T("Product", "Product"), T("Sku", "SKU"), T("Lot", "Lot"), T("Source", "Source"), D("Received", "Received"),
                 Q("ReceivedQty", "Received qty"), Q("Sold", "Sold"), Q("Returned", "Returned to stock"), Q("Damaged", "Damaged"),
                 Q("InternalUse", "Internal use"), Q("VendorReturned", "Returned to vendor"), Q("Corrections", "Corrections"),
                 Q("Expected", "Expected"), Q("Available", "Available"), Q("Difference", "Difference")]),

            [ReportKind.PurchaseRegister] = new("Purchase register", true, """
                SELECT p.received_date AS Date,p.doc_no AS Document,v.name AS Vendor,v.gstin AS Gstin,p.vendor_invoice_no AS VendorInvoice,
                       p.subtotal AS Subtotal,p.discount_total AS Discount,p.tax_total AS Tax,p.other_charges AS OtherCharges,p.grand_total AS Total
                FROM purchase p JOIN vendor v ON v.id=p.vendor_id WHERE p.status='POSTED' AND p.received_date BETWEEN @from AND @to
                  AND (@VendorId IS NULL OR p.vendor_id=@VendorId) ORDER BY p.received_date,p.id LIMIT @Limit
                """, [D("Date", "Date"), T("Document", "Document"), T("Vendor", "Vendor"), T("Gstin", "GSTIN"), T("VendorInvoice", "Vendor invoice"), M("Subtotal", "Subtotal"), M("Discount", "Discount"), M("Tax", "Tax"), M("OtherCharges", "Other charges"), M("Total", "Total")]),

            // ReportService reads outward tax from GstSummaryFromDay; this source-line form is the
            // reference ScaleAudit's summary-parity check compares that projection against.
            [ReportKind.GstSummary] = new("GST summary (accountant)", true, """
                SELECT 'OUTWARD' AS Direction, Hsn, Rate, SUM(Taxable) AS Taxable, SUM(Tax) AS Tax
                FROM (
                  SELECT COALESCE(sl.hsn_code,'') AS Hsn,sl.tax_rate_pct AS Rate,
                         sl.taxable_amount AS Taxable,sl.tax_amount AS Tax
                  FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                  WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @from AND @to
                  UNION ALL
                  SELECT COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
                         -rl.taxable_amount,-rl.tax_amount
                  FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
                  JOIN sale_line sl ON sl.id=rl.sale_line_id
                  WHERE r.return_date BETWEEN @from AND @to
                ) outward_lines GROUP BY Hsn,Rate
                UNION ALL
                SELECT 'INWARD',Hsn,Rate,SUM(Taxable),SUM(Tax)
                FROM (
                  SELECT COALESCE(v.gstin,'') AS Hsn,pl.tax_rate_pct AS Rate,
                         CAST(ROUND(pl.qty*pl.unit_cost/1000.0) AS INTEGER)-pl.discount AS Taxable,
                         pl.tax_amount AS Tax
                  FROM purchase_line pl JOIN purchase p ON p.id=pl.purchase_id JOIN vendor v ON v.id=p.vendor_id
                  WHERE p.status='POSTED' AND p.received_date BETWEEN @from AND @to
                  UNION ALL
                  SELECT COALESCE(v.gstin,''),pl.tax_rate_pct,-vrl.credit_before_tax,-vrl.tax_credit
                  FROM vendor_return_line vrl JOIN vendor_return vr ON vr.id=vrl.vendor_return_id
                  JOIN vendor v ON v.id=vr.vendor_id JOIN product_lot l ON l.id=vrl.lot_id
                  JOIN purchase_line pl ON pl.id=l.purchase_line_id
                  WHERE vr.return_date BETWEEN @from AND @to
                ) inward_lines GROUP BY Hsn,Rate
                ORDER BY Direction,Hsn,Rate LIMIT @Limit
                """, [T("Direction", "Direction"), T("Hsn", "HSN / vendor GSTIN"), P("Rate", "GST rate"), M("Taxable", "Taxable value"), M("Tax", "Tax")]),
        };
}
