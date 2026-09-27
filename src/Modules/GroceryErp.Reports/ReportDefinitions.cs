namespace GroceryErp.Reports;

internal static class ReportDefinitions
{
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
                       s.taxable_total AS Taxable,s.tax_total AS Tax,s.round_off AS RoundOff,s.grand_total AS Total,
                       COALESCE((SELECT group_concat(mode, ', ') FROM (SELECT DISTINCT mode FROM sale_payment WHERE sale_id=s.id ORDER BY mode)),'') AS PaymentMode
                FROM sale s WHERE s.sale_date BETWEEN @from AND @to AND s.status IN ('COMPLETED','VOID')
                  AND (@Status IS NULL OR s.status=@Status)
                  AND (@PaymentMode IS NULL OR EXISTS (SELECT 1 FROM sale_payment sp WHERE sp.sale_id=s.id AND sp.mode=@PaymentMode))
                ORDER BY s.sale_date,s.id LIMIT @Limit
                """, [D("Date", "Date"), T("Invoice", "Invoice"), T("Status", "Status"), I("Items", "Items"), M("Taxable", "Taxable"), M("Tax", "Tax"), M("RoundOff", "Round off"), M("Total", "Total"), T("PaymentMode", "Payment mode")]),

            [ReportKind.ItemSales] = new("Item-wise sales", true, """
                SELECT c.name AS Category,p.sku AS Sku,MAX(sl.product_name) AS Product,MAX(sl.product_name_local) AS LocalName,
                       SUM(sl.qty) AS Quantity,SUM(sl.taxable_amount) AS Revenue,SUM(sl.cost_amount) AS Cost,
                       SUM(sl.taxable_amount-sl.cost_amount) AS Margin
                FROM sale_line sl JOIN sale s ON s.id=sl.sale_id JOIN product p ON p.id=sl.product_id JOIN category c ON c.id=p.category_id
                WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @from AND @to
                GROUP BY c.id,sl.product_id ORDER BY c.name,Revenue DESC LIMIT @Limit
                """, [T("Category", "Category"), T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), M("Revenue", "Revenue"), M("Cost", "Cost"), M("Margin", "Margin")]),

            [ReportKind.CategorySales] = new("Category-wise sales and margin", true, """
                WITH current_period AS (
                  SELECT c.id,c.name,c.name_local,SUM(sl.qty) AS quantity,SUM(sl.taxable_amount) AS revenue,
                         SUM(sl.cost_amount) AS cost,SUM(sl.taxable_amount-sl.cost_amount) AS margin
                  FROM sale_line sl JOIN sale s ON s.id=sl.sale_id JOIN product p ON p.id=sl.product_id JOIN category c ON c.id=p.category_id
                  WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @from AND @to GROUP BY c.id
                ), previous_period AS (
                  SELECT c.id,SUM(sl.taxable_amount) AS revenue,SUM(sl.taxable_amount-sl.cost_amount) AS margin
                  FROM sale_line sl JOIN sale s ON s.id=sl.sale_id JOIN product p ON p.id=sl.product_id JOIN category c ON c.id=p.category_id
                  WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @previousFrom AND @previousTo GROUP BY c.id
                )
                SELECT cp.name AS Category,cp.name_local AS LocalName,cp.quantity AS Quantity,cp.revenue AS Revenue,
                       cp.cost AS Cost,cp.margin AS Margin,
                       CASE WHEN cp.revenue=0 THEN 0 ELSE CAST(ROUND(cp.margin*100000.0/cp.revenue) AS INTEGER) END AS MarginPercent,
                       COALESCE(pp.revenue,0) AS PreviousRevenue,COALESCE(pp.margin,0) AS PreviousMargin
                FROM current_period cp LEFT JOIN previous_period pp ON pp.id=cp.id ORDER BY cp.revenue DESC LIMIT @Limit
                """, [T("Category", "Category"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), M("Revenue", "Revenue"), M("Cost", "Cost"), M("Margin", "Margin"), P("MarginPercent", "Margin %"), M("PreviousRevenue", "Previous revenue"), M("PreviousMargin", "Previous margin")]),

            [ReportKind.HourlySales] = new("Hourly sales heatmap", true, """
                SELECT CASE CAST(strftime('%w',completed_at) AS INTEGER) WHEN 0 THEN 'Sun' WHEN 1 THEN 'Mon' WHEN 2 THEN 'Tue' WHEN 3 THEN 'Wed' WHEN 4 THEN 'Thu' WHEN 5 THEN 'Fri' ELSE 'Sat' END AS Weekday,
                       CAST(strftime('%H',completed_at) AS INTEGER) AS Hour,COUNT(*) AS Bills,SUM(grand_total) AS Revenue
                FROM sale WHERE status='COMPLETED' AND sale_date BETWEEN @from AND @to AND completed_at IS NOT NULL
                GROUP BY strftime('%w',completed_at),strftime('%H',completed_at) ORDER BY strftime('%w',completed_at),Hour LIMIT @Limit
                """, [T("Weekday", "Weekday"), I("Hour", "Hour"), I("Bills", "Bills"), M("Revenue", "Revenue")]),

            [ReportKind.StockValue] = new("Stock value", false, """
                SELECT c.name AS Category,p.sku AS Sku,p.name AS Product,p.name_local AS LocalName,
                       SUM(l.qty_on_hand) AS Quantity,
                       SUM(CAST(ROUND(l.qty_on_hand*l.unit_cost/1000.0) AS INTEGER)) AS CostValue,
                       SUM(CAST(ROUND(l.qty_on_hand*l.selling_price/1000.0) AS INTEGER)) AS SellingValue,
                       SUM(CAST(ROUND(l.qty_on_hand*COALESCE(l.mrp,l.selling_price)/1000.0) AS INTEGER)) AS MrpValue
                FROM product_lot l JOIN product p ON p.id=l.product_id JOIN category c ON c.id=p.category_id
                WHERE l.qty_on_hand<>0 GROUP BY p.id ORDER BY c.name,p.name LIMIT @Limit
                """, [T("Category", "Category"), T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), M("CostValue", "Cost value"), M("SellingValue", "Selling value"), M("MrpValue", "MRP value")]),

            [ReportKind.StockLedger] = new("Stock ledger", true, """
                SELECT m.move_date AS Date,p.sku AS Sku,p.name AS Product,l.lot_code AS Lot,m.type_code AS Type,
                       m.qty AS Quantity,m.unit_cost AS UnitCost,m.ref_type AS ReferenceType,m.ref_id AS ReferenceId,m.note AS Note
                FROM stock_movement m JOIN product p ON p.id=m.product_id JOIN product_lot l ON l.id=m.lot_id
                WHERE m.move_date BETWEEN @from AND @to AND (@ProductId IS NULL OR m.product_id=@ProductId) AND (@LotId IS NULL OR m.lot_id=@LotId)
                ORDER BY m.move_date,m.id LIMIT @Limit
                """, [D("Date", "Date"), T("Sku", "SKU"), T("Product", "Product"), T("Lot", "Lot"), T("Type", "Type"), Q("Quantity", "Quantity"), M("UnitCost", "Unit cost"), T("ReferenceType", "Reference type"), I("ReferenceId", "Reference"), T("Note", "Note")]),

            [ReportKind.LowStock] = new("Low stock and reorder", false, """
                SELECT ps.sku AS Sku,ps.name AS Product,p.name_local AS LocalName,ps.stock_qty AS Quantity,ps.reorder_level AS ReorderLevel,
                       COALESCE((SELECT SUM(sl.qty)/30 FROM sale_line sl JOIN sale s ON s.id=sl.sale_id WHERE sl.product_id=ps.product_id AND s.status='COMPLETED' AND s.sale_date BETWEEN date(@to,'-29 day') AND @to),0) AS AverageDailySales,
                       CASE WHEN ps.reorder_level>ps.stock_qty THEN ps.reorder_level-ps.stock_qty ELSE 0 END AS SuggestedOrder
                FROM v_product_stock ps JOIN product p ON p.id=ps.product_id WHERE ps.is_low=1 ORDER BY ps.name LIMIT @Limit
                """, [T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), Q("ReorderLevel", "Reorder level"), Q("AverageDailySales", "30-day avg/day"), Q("SuggestedOrder", "Suggested order")]),

            [ReportKind.SlowMovers] = new("Slow movers", false, """
                SELECT p.sku AS Sku,p.name AS Product,p.name_local AS LocalName,ps.stock_qty AS Quantity,ps.stock_value AS Value,
                       MAX(s.sale_date) AS LastSold
                FROM v_product_stock ps JOIN product p ON p.id=ps.product_id
                LEFT JOIN sale_line sl ON sl.product_id=p.id LEFT JOIN sale s ON s.id=sl.sale_id AND s.status='COMPLETED'
                WHERE ps.stock_qty>0 GROUP BY p.id HAVING MAX(s.sale_date) IS NULL OR MAX(s.sale_date)<@slowFrom
                ORDER BY Value DESC LIMIT @Limit
                """, [T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), Q("Quantity", "Quantity"), M("Value", "Value tied up"), D("LastSold", "Last sold")]),

            [ReportKind.Expiry] = new("Expiry", false, """
                SELECT e.bucket AS Status,e.sku AS Sku,e.name AS Product,e.name_local AS LocalName,e.lot_code AS Lot,
                       e.batch_no AS Batch,e.expiry_date AS Expiry,e.days_left AS DaysLeft,e.qty_on_hand AS Quantity,
                       e.value_at_cost AS CostValue,v.name AS Vendor
                FROM v_expiry_alert e LEFT JOIN vendor v ON v.id=e.vendor_id ORDER BY e.days_left,e.name LIMIT @Limit
                """, [T("Status", "Status"), T("Sku", "SKU"), T("Product", "Product"), T("LocalName", "Local name"), T("Lot", "Lot"), T("Batch", "Batch"), D("Expiry", "Expiry"), I("DaysLeft", "Days left"), Q("Quantity", "Quantity"), M("CostValue", "Cost value"), T("Vendor", "Vendor")]),

            [ReportKind.Damages] = new("Damages", true, """
                SELECT d.damage_date AS Date,d.doc_no AS Document,p.name AS Product,p.name_local AS LocalName,c.name AS Category,
                       d.reason AS Reason,v.name AS Vendor,d.qty AS Quantity,d.cost_value AS CostValue,d.recovered_amount AS Recovered
                FROM damage d JOIN product p ON p.id=d.product_id JOIN category c ON c.id=p.category_id
                JOIN product_lot l ON l.id=d.lot_id LEFT JOIN vendor v ON v.id=l.vendor_id
                WHERE d.damage_date BETWEEN @from AND @to ORDER BY d.damage_date,d.id LIMIT @Limit
                """, [D("Date", "Date"), T("Document", "Document"), T("Product", "Product"), T("LocalName", "Local name"), T("Category", "Category"), T("Reason", "Reason"), T("Vendor", "Vendor"), Q("Quantity", "Quantity"), M("CostValue", "Cost value"), M("Recovered", "Recovered")]),

            [ReportKind.Expenses] = new("Expenses by category", true, """
                SELECT strftime('%Y-%m',e.expense_date) AS Month,ec.name AS Category,ec.name_local AS LocalName,
                       COUNT(*) AS Entries,SUM(e.amount) AS Amount
                FROM expense e JOIN expense_category ec ON ec.id=e.category_id WHERE e.expense_date BETWEEN @from AND @to
                GROUP BY Month,ec.id ORDER BY Month,ec.name LIMIT @Limit
                """, [T("Month", "Month"), T("Category", "Category"), T("LocalName", "Local name"), I("Entries", "Entries"), M("Amount", "Amount")]),

            [ReportKind.SalaryRegister] = new("Salary register", true, """
                SELECT e.name AS Employee,e.role AS Role,sp.period_month AS Month,
                       SUM(CASE WHEN sp.kind='SALARY' THEN sp.amount ELSE 0 END) AS Salary,
                       SUM(CASE WHEN sp.kind<>'SALARY' THEN sp.amount ELSE 0 END) AS Other,SUM(sp.amount) AS Total
                FROM salary_payment sp JOIN employee e ON e.id=sp.employee_id
                WHERE date(sp.period_month||'-01','+1 month','-1 day') BETWEEN @from AND @to
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
                       COALESCE((SELECT SUM(p.grand_total) FROM purchase p WHERE p.vendor_id=v.id AND p.status='POSTED'),0) AS Purchases,
                       COALESCE((SELECT SUM(vp.amount) FROM vendor_payment vp WHERE vp.vendor_id=v.id),0) AS Payments,
                       b.balance_due AS BalanceDue
                FROM v_vendor_balance b JOIN vendor v ON v.id=b.vendor_id
                WHERE (@VendorId IS NULL OR v.id=@VendorId) ORDER BY BalanceDue DESC LIMIT @Limit
                """, [T("Vendor", "Vendor"), T("Gstin", "GSTIN"), T("Phone", "Phone"), M("OpeningBalance", "Opening balance"), M("Purchases", "Purchases"), M("Payments", "Payments"), M("BalanceDue", "Balance due")]),

            [ReportKind.PurchaseRegister] = new("Purchase register", true, """
                SELECT p.received_date AS Date,p.doc_no AS Document,v.name AS Vendor,v.gstin AS Gstin,p.vendor_invoice_no AS VendorInvoice,
                       p.subtotal AS Subtotal,p.discount_total AS Discount,p.tax_total AS Tax,p.other_charges AS OtherCharges,p.grand_total AS Total
                FROM purchase p JOIN vendor v ON v.id=p.vendor_id WHERE p.status='POSTED' AND p.received_date BETWEEN @from AND @to
                  AND (@VendorId IS NULL OR p.vendor_id=@VendorId) ORDER BY p.received_date,p.id LIMIT @Limit
                """, [D("Date", "Date"), T("Document", "Document"), T("Vendor", "Vendor"), T("Gstin", "GSTIN"), T("VendorInvoice", "Vendor invoice"), M("Subtotal", "Subtotal"), M("Discount", "Discount"), M("Tax", "Tax"), M("OtherCharges", "Other charges"), M("Total", "Total")]),

            [ReportKind.GstSummary] = new("GST summary (accountant)", true, """
                SELECT 'OUTWARD' AS Direction,COALESCE(sl.hsn_code,'') AS Hsn,sl.tax_rate_pct AS Rate,
                       SUM(sl.taxable_amount)-COALESCE((SELECT SUM(rl.taxable_amount) FROM sale_return_line rl JOIN sale_line rsl ON rsl.id=rl.sale_line_id JOIN sale_return r ON r.id=rl.sale_return_id WHERE COALESCE(rsl.hsn_code,'')=COALESCE(sl.hsn_code,'') AND rsl.tax_rate_pct=sl.tax_rate_pct AND r.return_date BETWEEN @from AND @to),0) AS Taxable,
                       SUM(sl.tax_amount)-COALESCE((SELECT SUM(rl.tax_amount) FROM sale_return_line rl JOIN sale_line rsl ON rsl.id=rl.sale_line_id JOIN sale_return r ON r.id=rl.sale_return_id WHERE COALESCE(rsl.hsn_code,'')=COALESCE(sl.hsn_code,'') AND rsl.tax_rate_pct=sl.tax_rate_pct AND r.return_date BETWEEN @from AND @to),0) AS Tax
                FROM sale_line sl JOIN sale s ON s.id=sl.sale_id WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @from AND @to
                GROUP BY COALESCE(sl.hsn_code,''),sl.tax_rate_pct
                UNION ALL
                SELECT 'INWARD',COALESCE(v.gstin,''),pl.tax_rate_pct,SUM(CAST(ROUND(pl.qty*pl.unit_cost/1000.0) AS INTEGER)-pl.discount),SUM(pl.tax_amount)
                FROM purchase_line pl JOIN purchase p ON p.id=pl.purchase_id JOIN vendor v ON v.id=p.vendor_id
                WHERE p.status='POSTED' AND p.received_date BETWEEN @from AND @to GROUP BY COALESCE(v.gstin,''),pl.tax_rate_pct
                ORDER BY Direction,Hsn,Rate LIMIT @Limit
                """, [T("Direction", "Direction"), T("Hsn", "HSN / vendor GSTIN"), P("Rate", "GST rate"), M("Taxable", "Taxable value"), M("Tax", "Tax")]),

            [ReportKind.AuditLog] = new("Audit log", true, """
                SELECT substr(at,1,10) AS Date,at AS Timestamp,entity AS Entity,entity_id AS EntityId,
                       action AS Action,changes AS Details FROM audit_log
                WHERE substr(at,1,10) BETWEEN @from AND @to
                  AND (@Entity IS NULL OR entity=@Entity) AND (@Action IS NULL OR action=@Action)
                ORDER BY id DESC LIMIT @Limit
                """, [D("Date", "Date"), T("Timestamp", "Timestamp"), T("Entity", "Entity"), I("EntityId", "Entity ID"), T("Action", "Action"), T("Details", "Details")]),
        };
}
