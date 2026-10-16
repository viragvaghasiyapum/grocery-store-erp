namespace GroceryErp.Reports;

internal static class ReportDefinitions
{
    public const string CategorySalesFromDay = """
        WITH totals AS (
          SELECT category_id,
                 SUM(CASE WHEN day BETWEEN @from AND @to THEN quantity ELSE 0 END) AS quantity,
                 SUM(CASE WHEN day BETWEEN @from AND @to THEN revenue ELSE 0 END) AS revenue,
                 SUM(CASE WHEN day BETWEEN @from AND @to THEN cost ELSE 0 END) AS cost,
                 SUM(CASE WHEN day BETWEEN @from AND @to THEN revenue-cost ELSE 0 END) AS margin,
                 SUM(CASE WHEN day BETWEEN @previousFrom AND @previousTo THEN revenue ELSE 0 END) AS previous_revenue,
                 SUM(CASE WHEN day BETWEEN @previousFrom AND @previousTo THEN revenue-cost ELSE 0 END) AS previous_margin
          FROM sale_report_day WHERE day BETWEEN @previousFrom AND @to
          GROUP BY category_id
        )
        SELECT c.name AS Category,c.name_local AS LocalName,
               t.quantity AS Quantity,t.revenue AS Revenue,t.cost AS Cost,t.margin AS Margin,
               CASE WHEN t.revenue=0 THEN 0 ELSE CAST(ROUND(t.margin*100000.0/t.revenue) AS INTEGER) END AS MarginPercent,
               t.previous_revenue AS PreviousRevenue,t.previous_margin AS PreviousMargin
        FROM totals t JOIN category c ON c.id=t.category_id
        ORDER BY Revenue DESC LIMIT @Limit
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

    public const string HourlySalesFromDay = """
        SELECT CASE CAST(weekday AS INTEGER) WHEN 0 THEN 'Sun' WHEN 1 THEN 'Mon'
                    WHEN 2 THEN 'Tue' WHEN 3 THEN 'Wed' WHEN 4 THEN 'Thu'
                    WHEN 5 THEN 'Fri' ELSE 'Sat' END AS Weekday,
               hour AS Hour,SUM(bills) AS Bills,SUM(revenue) AS Revenue
        FROM sale_hour_day WHERE day BETWEEN @from AND @to
        GROUP BY weekday,hour ORDER BY weekday,hour LIMIT @Limit
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

    public const string StockLedgerForLot = """
        SELECT m.move_date AS Date,p.sku AS Sku,p.name AS Product,l.lot_code AS Lot,m.type_code AS Type,
               m.qty AS Quantity,m.unit_cost AS UnitCost,m.ref_type AS ReferenceType,m.ref_id AS ReferenceId,m.note AS Note
        FROM stock_movement m JOIN product p ON p.id=m.product_id JOIN product_lot l ON l.id=m.lot_id
        WHERE m.lot_id=@LotId AND m.move_date BETWEEN @from AND @to
          AND (@ProductId IS NULL OR m.product_id=@ProductId)
        ORDER BY m.move_date,m.id LIMIT @Limit
        """;

    public const string StockLedgerForProduct = """
        SELECT m.move_date AS Date,p.sku AS Sku,p.name AS Product,l.lot_code AS Lot,m.type_code AS Type,
               m.qty AS Quantity,m.unit_cost AS UnitCost,m.ref_type AS ReferenceType,m.ref_id AS ReferenceId,m.note AS Note
        FROM stock_movement m JOIN product p ON p.id=m.product_id JOIN product_lot l ON l.id=m.lot_id
        WHERE m.product_id=@ProductId AND m.move_date BETWEEN @from AND @to
        ORDER BY m.move_date,m.id LIMIT @Limit
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

    public const string CategorySalesFromTopWindow = """
        WITH current_movements AS (
          SELECT product_id,qty,revenue,cost FROM dashboard_top_summary WHERE movements>0
          UNION ALL
          SELECT sl.product_id,@deltaSign*sl.qty,@deltaSign*sl.taxable_amount,
                 @deltaSign*sl.cost_amount
          FROM sale s JOIN sale_line sl ON sl.sale_id=s.id
          WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @deltaFrom AND @deltaTo
          UNION ALL
          SELECT sl.product_id,-@deltaSign*rl.qty,-@deltaSign*rl.taxable_amount,
                 -@deltaSign*rl.cost_amount
          FROM sale_return r JOIN sale_return_line rl ON rl.sale_return_id=r.id
          JOIN sale_line sl ON sl.id=rl.sale_line_id
          WHERE r.return_date BETWEEN @deltaFrom AND @deltaTo
        ), current_period AS (
          SELECT c.id,SUM(m.qty) AS quantity,SUM(m.revenue) AS revenue,
                 SUM(m.cost) AS cost,SUM(m.revenue-m.cost) AS margin
          FROM current_movements m JOIN product p ON p.id=m.product_id
          JOIN category c ON c.id=p.category_id GROUP BY c.id
        ), previous_movements AS (
          SELECT sl.product_id,sl.taxable_amount AS revenue,sl.cost_amount AS cost
          FROM sale s JOIN sale_line sl ON sl.sale_id=s.id
          WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @previousFrom AND @previousTo
          UNION ALL
          SELECT sl.product_id,-rl.taxable_amount,-rl.cost_amount
          FROM sale_return r JOIN sale_return_line rl ON rl.sale_return_id=r.id
          JOIN sale_line sl ON sl.id=rl.sale_line_id
          WHERE r.return_date BETWEEN @previousFrom AND @previousTo
        ), previous_period AS (
          SELECT c.id,SUM(m.revenue) AS revenue,SUM(m.revenue-m.cost) AS margin
          FROM previous_movements m JOIN product p ON p.id=m.product_id
          JOIN category c ON c.id=p.category_id GROUP BY c.id
        )
        SELECT c.name AS Category,c.name_local AS LocalName,
               COALESCE(cp.quantity,0) AS Quantity,COALESCE(cp.revenue,0) AS Revenue,
               COALESCE(cp.cost,0) AS Cost,COALESCE(cp.margin,0) AS Margin,
               CASE WHEN COALESCE(cp.revenue,0)=0 THEN 0
                    ELSE CAST(ROUND(cp.margin*100000.0/cp.revenue) AS INTEGER) END AS MarginPercent,
               COALESCE(pp.revenue,0) AS PreviousRevenue,
               COALESCE(pp.margin,0) AS PreviousMargin
        FROM (SELECT id FROM current_period UNION SELECT id FROM previous_period) ids
        JOIN category c ON c.id=ids.id LEFT JOIN current_period cp ON cp.id=ids.id
        LEFT JOIN previous_period pp ON pp.id=ids.id
        ORDER BY Revenue DESC LIMIT @Limit
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

            [ReportKind.ItemSales] = new("Item-wise sales", true, """
                WITH movements AS (
                  SELECT sl.product_id,sl.product_name,sl.product_name_local,sl.qty,sl.taxable_amount,sl.cost_amount
                  FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                  WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @from AND @to
                  UNION ALL
                  SELECT sl.product_id,sl.product_name,sl.product_name_local,-rl.qty,-rl.taxable_amount,-rl.cost_amount
                  FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
                  JOIN sale_line sl ON sl.id=rl.sale_line_id WHERE r.return_date BETWEEN @from AND @to
                )
                SELECT c.name AS Category,p.sku AS Sku,MAX(m.product_name) AS Product,MAX(m.product_name_local) AS LocalName,
                       SUM(m.qty) AS Quantity,SUM(m.taxable_amount) AS Revenue,SUM(m.cost_amount) AS Cost,
                       SUM(m.taxable_amount-m.cost_amount) AS Margin
                FROM movements m JOIN product p ON p.id=m.product_id JOIN category c ON c.id=p.category_id
                WHERE (@ProductId IS NULL OR m.product_id=@ProductId)
                GROUP BY c.id,m.product_id ORDER BY c.name,Revenue DESC LIMIT @Limit
                """, [T("Category", "Category"), T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), M("Revenue", "Revenue"), M("Cost", "Cost"), M("Margin", "Margin")]),

            [ReportKind.CategorySales] = new("Category-wise sales and margin", true, """
                WITH movements AS (
                  SELECT sl.product_id,s.sale_date AS entry_date,sl.qty,sl.taxable_amount,sl.cost_amount
                  FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                  WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @previousFrom AND @to
                  UNION ALL
                  SELECT sl.product_id,r.return_date,-rl.qty,-rl.taxable_amount,-rl.cost_amount
                  FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
                  JOIN sale_line sl ON sl.id=rl.sale_line_id WHERE r.return_date BETWEEN @previousFrom AND @to
                ), product_totals AS (
                  SELECT m.product_id,
                         SUM(CASE WHEN m.entry_date BETWEEN @from AND @to THEN m.qty ELSE 0 END) AS quantity,
                         SUM(CASE WHEN m.entry_date BETWEEN @from AND @to THEN m.taxable_amount ELSE 0 END) AS revenue,
                         SUM(CASE WHEN m.entry_date BETWEEN @from AND @to THEN m.cost_amount ELSE 0 END) AS cost,
                         SUM(CASE WHEN m.entry_date BETWEEN @from AND @to THEN m.taxable_amount-m.cost_amount ELSE 0 END) AS margin,
                         SUM(CASE WHEN m.entry_date BETWEEN @previousFrom AND @previousTo THEN m.taxable_amount ELSE 0 END) AS previous_revenue,
                         SUM(CASE WHEN m.entry_date BETWEEN @previousFrom AND @previousTo THEN m.taxable_amount-m.cost_amount ELSE 0 END) AS previous_margin
                  FROM movements m GROUP BY m.product_id
                ), totals AS (
                  SELECT c.id,c.name,c.name_local,
                         SUM(m.quantity) AS quantity,SUM(m.revenue) AS revenue,
                         SUM(m.cost) AS cost,SUM(m.margin) AS margin,
                         SUM(m.previous_revenue) AS previous_revenue,
                         SUM(m.previous_margin) AS previous_margin
                  FROM product_totals m JOIN product p ON p.id=m.product_id JOIN category c ON c.id=p.category_id
                  GROUP BY c.id
                )
                SELECT name AS Category,name_local AS LocalName,
                       quantity AS Quantity,revenue AS Revenue,cost AS Cost,margin AS Margin,
                       CASE WHEN revenue=0 THEN 0 ELSE CAST(ROUND(margin*100000.0/revenue) AS INTEGER) END AS MarginPercent,
                       previous_revenue AS PreviousRevenue,previous_margin AS PreviousMargin
                FROM totals ORDER BY Revenue DESC LIMIT @Limit
                """, [T("Category", "Category"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), M("Revenue", "Revenue"), M("Cost", "Cost"), M("Margin", "Margin"), P("MarginPercent", "Margin %"), M("PreviousRevenue", "Previous revenue"), M("PreviousMargin", "Previous margin")]),

            [ReportKind.HourlySales] = new("Hourly sales heatmap", true, """
                SELECT CASE CAST(strftime('%w',completed_at) AS INTEGER) WHEN 0 THEN 'Sun' WHEN 1 THEN 'Mon' WHEN 2 THEN 'Tue' WHEN 3 THEN 'Wed' WHEN 4 THEN 'Thu' WHEN 5 THEN 'Fri' ELSE 'Sat' END AS Weekday,
                       CAST(strftime('%H',completed_at) AS INTEGER) AS Hour,COUNT(*) AS Bills,SUM(grand_total) AS Revenue
                FROM sale WHERE status='COMPLETED' AND sale_date BETWEEN @from AND @to AND completed_at IS NOT NULL
                GROUP BY strftime('%w',completed_at),strftime('%H',completed_at) ORDER BY strftime('%w',completed_at),Hour LIMIT @Limit
                """, [T("Weekday", "Weekday"), I("Hour", "Hour"), I("Bills", "Bills"), M("Revenue", "Revenue")]),

            [ReportKind.StockValue] = new("Stock value", false, """
                SELECT c.name AS Category,p.sku AS Sku,p.name AS Product,p.name_local AS LocalName,
                       t.quantity AS Quantity,t.cost_value AS CostValue,
                       t.selling_value AS SellingValue,t.mrp_value AS MrpValue
                FROM product_stock_total t JOIN product p ON p.id=t.product_id
                JOIN category c ON c.id=p.category_id
                WHERE t.quantity<>0 OR t.cost_value<>0 OR t.selling_value<>0 OR t.mrp_value<>0
                ORDER BY c.name,p.name LIMIT @Limit
                """, [T("Category", "Category"), T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), M("CostValue", "Cost value"), M("SellingValue", "Selling value"), M("MrpValue", "MRP value")]),

            [ReportKind.StockLedger] = new("Stock ledger", true, """
                SELECT m.move_date AS Date,p.sku AS Sku,p.name AS Product,l.lot_code AS Lot,m.type_code AS Type,
                       m.qty AS Quantity,m.unit_cost AS UnitCost,m.ref_type AS ReferenceType,m.ref_id AS ReferenceId,m.note AS Note
                FROM stock_movement m JOIN product p ON p.id=m.product_id JOIN product_lot l ON l.id=m.lot_id
                WHERE m.move_date BETWEEN @from AND @to AND (@ProductId IS NULL OR m.product_id=@ProductId) AND (@LotId IS NULL OR m.lot_id=@LotId)
                ORDER BY m.move_date,m.id LIMIT @Limit
                """, [D("Date", "Date"), T("Sku", "SKU"), T("Product", "Product"), T("Lot", "Lot"), T("Type", "Type"), Q("Quantity", "Quantity"), M("UnitCost", "Unit cost"), T("ReferenceType", "Reference type"), I("ReferenceId", "Reference"), T("Note", "Note")]),

            [ReportKind.LowStock] = new("Low stock and reorder", false, """
                WITH page AS MATERIALIZED (
                    SELECT p.id,p.sku,p.name,p.name_local,p.reorder_level,t.quantity
                    FROM product_stock_total t JOIN product p ON p.id=t.product_id
                    WHERE p.is_active=1 AND t.is_low=1 ORDER BY p.name LIMIT @Limit
                )
                SELECT page.sku AS Sku,page.name AS Product,page.name_local AS LocalName,
                       page.quantity AS Quantity,page.reorder_level AS ReorderLevel,
                       COALESCE((SELECT SUM(d.qty)/30 FROM sale_product_day d
                                 WHERE d.product_id=page.id
                                   AND d.day BETWEEN date(@to,'-29 day') AND @to),0) AS AverageDailySales,
                       CASE WHEN page.reorder_level>page.quantity THEN page.reorder_level-page.quantity ELSE 0 END AS SuggestedOrder
                FROM page ORDER BY page.name
                """, [T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), Q("ReorderLevel", "Reorder level"), Q("AverageDailySales", "30-day avg/day"), Q("SuggestedOrder", "Suggested order")]),

            [ReportKind.SlowMovers] = new("Slow movers", false, """
                SELECT p.sku AS Sku,p.name AS Product,p.name_local AS LocalName,
                       t.quantity AS Quantity,t.cost_value AS Value,l.last_day AS LastSold
                FROM product_stock_total t JOIN product p ON p.id=t.product_id
                LEFT JOIN sale_product_last l ON l.product_id=p.id
                WHERE p.is_active=1 AND t.quantity>0
                  AND (l.last_day IS NULL OR l.last_day<@slowFrom)
                ORDER BY t.cost_value DESC LIMIT @Limit
                """, [T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), M("Value", "Value tied up"), D("LastSold", "Last sold")]),

            [ReportKind.Expiry] = new("Expiry", false, """
                SELECT e.bucket AS Status,e.sku AS Sku,e.name AS Product,e.name_local AS LocalName,e.lot_code AS Lot,
                       e.batch_no AS Batch,e.expiry_date AS Expiry,e.days_left AS DaysLeft,e.qty_on_hand AS Quantity,
                       e.value_at_cost AS CostValue,v.name AS Vendor
                FROM v_expiry_alert e LEFT JOIN vendor v ON v.id=e.vendor_id
                WHERE (@Status IS NULL OR e.bucket=@Status)
                ORDER BY e.days_left,e.name LIMIT @Limit
                """, [T("Status", "Status"), T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), T("Lot", "Lot"), T("Batch", "Batch"), D("Expiry", "Expiry"), I("DaysLeft", "Days left"), Q("Quantity", "Quantity"), M("CostValue", "Cost value"), T("Vendor", "Vendor")]),

            [ReportKind.Damages] = new("Damages", true, """
                SELECT Date,Document,Product,LocalName,Category,Reason,Vendor,Quantity,CostValue,Recovered
                FROM (
                    SELECT d.damage_date AS Date,d.doc_no AS Document,p.name AS Product,p.name_local AS LocalName,
                           c.name AS Category,d.reason AS Reason,v.name AS Vendor,d.qty AS Quantity,
                           d.cost_value AS CostValue,d.recovered_amount AS Recovered,d.id AS SortId
                    FROM damage d JOIN product p ON p.id=d.product_id JOIN category c ON c.id=p.category_id
                    JOIN product_lot l ON l.id=d.lot_id LEFT JOIN vendor v ON v.id=l.vendor_id
                    WHERE d.damage_date BETWEEN @from AND @to
                    UNION ALL
                    SELECT dc.correction_date,d.doc_no || ' / correction',p.name,p.name_local,c.name,
                           dc.new_reason,v.name,dc.new_qty-dc.previous_qty,
                           CAST(ROUND(dc.new_qty*d.unit_cost/1000.0) AS INTEGER)
                             -CAST(ROUND(dc.previous_qty*d.unit_cost/1000.0) AS INTEGER),
                           dc.new_recovered-dc.previous_recovered,dc.id
                    FROM damage_correction dc JOIN damage d ON d.id=dc.damage_id
                    JOIN product p ON p.id=d.product_id JOIN category c ON c.id=p.category_id
                    JOIN product_lot l ON l.id=d.lot_id LEFT JOIN vendor v ON v.id=l.vendor_id
                    WHERE dc.correction_date BETWEEN @from AND @to
                ) ORDER BY Date,SortId LIMIT @Limit
                """, [D("Date", "Date"), T("Document", "Document"), T("Product", "Product"), T("LocalName", "Local name"), T("Category", "Category"), T("Reason", "Reason"), T("Vendor", "Vendor"), Q("Quantity", "Quantity"), M("CostValue", "Cost value"), M("Recovered", "Recovered")]),

            [ReportKind.Expenses] = new("Expenses by category", true, """
                SELECT strftime('%Y-%m',e.expense_date) AS Month,ec.name AS Category,ec.name_local AS LocalName,
                       COUNT(*) AS Entries,SUM(e.amount) AS Amount
                FROM expense e JOIN expense_category ec ON ec.id=e.category_id WHERE e.expense_date BETWEEN @from AND @to
                GROUP BY Month,ec.id ORDER BY Month,ec.name LIMIT @Limit
                """, [T("Month", "Month"), T("Category", "Category"), T("LocalName", "Local name"), I("Entries", "Entries"), M("Amount", "Amount")]),

            [ReportKind.SalaryRegister] = new("Salary register", true, """
                SELECT e.name AS Employee,e.role AS Role,sp.period_month AS Month,
                       SUM(CASE WHEN sp.kind='SALARY' THEN sp.amount+COALESCE((SELECT SUM(sc.amount_delta)
                           FROM salary_correction sc WHERE sc.salary_payment_id=sp.id),0) ELSE 0 END) AS Salary,
                       SUM(CASE WHEN sp.kind<>'SALARY' THEN sp.amount+COALESCE((SELECT SUM(sc.amount_delta)
                           FROM salary_correction sc WHERE sc.salary_payment_id=sp.id),0) ELSE 0 END) AS Other,
                       SUM(sp.amount+COALESCE((SELECT SUM(sc.amount_delta) FROM salary_correction sc
                           WHERE sc.salary_payment_id=sp.id),0)) AS Total
                FROM salary_payment sp JOIN employee e ON e.id=sp.employee_id
                WHERE sp.period_month BETWEEN @firstMonth AND @lastMonth
                GROUP BY e.id,sp.period_month ORDER BY e.name,sp.period_month LIMIT @Limit
                """, [T("Employee", "Employee"), T("Role", "Role"), T("Month", "Month"), M("Salary", "Salary"), M("Other", "Other"), M("Total", "Total")]),

            [ReportKind.AssetRegister] = new("Asset register and depreciation", false, """
                SELECT a.name AS Asset,a.category AS Category,a.purchase_date AS PurchaseDate,a.in_use_from AS InUseFrom,
                       a.method AS Method,a.rate_pct AS Rate,a.cost AS Cost,
                       COALESCE((SELECT SUM(d.amount) FROM asset_depreciation d WHERE d.asset_id=a.id AND d.period_month<=substr(@to,1,7)),0) AS AccumulatedDepreciation,
                       a.cost-COALESCE((SELECT SUM(d.amount) FROM asset_depreciation d WHERE d.asset_id=a.id AND d.period_month<=substr(@to,1,7)),0) AS BookValue,
                       a.disposed_date AS DisposedDate,a.disposal_value AS DisposalValue
                FROM asset a ORDER BY a.name LIMIT @Limit
                """, [T("Asset", "Asset"), T("Category", "Category"), D("PurchaseDate", "Purchased"), D("InUseFrom", "In use"), T("Method", "Method"), P("Rate", "Rate %"), M("Cost", "Cost"), M("AccumulatedDepreciation", "Accumulated depreciation"), M("BookValue", "Book value"), D("DisposedDate", "Disposed"), M("DisposalValue", "Disposal value")]),

            [ReportKind.VendorPayables] = new("Vendor payables", false, """
                SELECT v.name AS Vendor,v.gstin AS Gstin,v.phone AS Phone,v.opening_balance AS OpeningBalance,
                       t.purchases AS Purchases,t.payments AS Payments,t.returns AS Returns,
                       v.opening_balance+t.purchases-t.payments-t.returns AS BalanceDue
                FROM vendor v JOIN vendor_balance_total t ON t.vendor_id=v.id
                WHERE (@VendorId IS NULL OR v.id=@VendorId) ORDER BY BalanceDue DESC LIMIT @Limit
                """, [T("Vendor", "Vendor"), T("Gstin", "GSTIN"), T("Phone", "Phone"), M("OpeningBalance", "Opening balance"), M("Purchases", "Purchases"), M("Payments", "Payments"), M("Returns", "Supplier credits"), M("BalanceDue", "Balance due")]),

            [ReportKind.PurchaseRegister] = new("Purchase register", true, """
                SELECT p.received_date AS Date,p.doc_no AS Document,v.name AS Vendor,v.gstin AS Gstin,p.vendor_invoice_no AS VendorInvoice,
                       p.subtotal AS Subtotal,p.discount_total AS Discount,p.tax_total AS Tax,p.other_charges AS OtherCharges,p.grand_total AS Total
                FROM purchase p JOIN vendor v ON v.id=p.vendor_id WHERE p.status='POSTED' AND p.received_date BETWEEN @from AND @to
                  AND (@VendorId IS NULL OR p.vendor_id=@VendorId) ORDER BY p.received_date,p.id LIMIT @Limit
                """, [D("Date", "Date"), T("Document", "Document"), T("Vendor", "Vendor"), T("Gstin", "GSTIN"), T("VendorInvoice", "Vendor invoice"), M("Subtotal", "Subtotal"), M("Discount", "Discount"), M("Tax", "Tax"), M("OtherCharges", "Other charges"), M("Total", "Total")]),

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

            [ReportKind.AuditLog] = new("Audit log", true, """
                SELECT substr(at,1,10) AS Date,at AS Timestamp,entity AS Entity,entity_id AS EntityId,
                       action AS Action,changes AS Details FROM audit_log
                WHERE at>=@from AND at<date(@to,'+1 day')
                  AND (@Entity IS NULL OR entity=@Entity) AND (@Action IS NULL OR action=@Action)
                ORDER BY id DESC LIMIT @Limit
                """, [D("Date", "Date"), T("Timestamp", "Timestamp"), T("Entity", "Entity"), I("EntityId", "Entity ID"), T("Action", "Action"), T("Details", "Details")]),
        };
}
