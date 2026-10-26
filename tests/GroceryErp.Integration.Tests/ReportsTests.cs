using System.Diagnostics;
using ClosedXML.Excel;
using Dapper;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using GroceryErp.Reports;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class ReportsTests
{
    [Fact]
    public async Task CurrentStockReport_CanExcludeExpiredLotsWithoutChangingStoredTotals()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,
                    unit_cost,selling_price,qty_received,qty_on_hand)
                VALUES (1,'L-EXPIRED','2026-08-01','2026-09-01',100000,120000,2000,2000),
                       (1,'L-FRESH','2026-09-01','2026-12-01',100000,120000,3000,3000)
                """);
        CancellationToken ct = TestContext.Current.CancellationToken;
        TabularReport all = await fixture.Reports.GetReportAsync(ReportKind.StockValue,
            new ReportFilter(IncludeExpired: true), ct);
        TabularReport current = await fixture.Reports.GetReportAsync(ReportKind.StockValue,
            new ReportFilter(IncludeExpired: false), ct);
        ((long)all.Rows.Single().Values["Quantity"]!).ShouldBe(6000);
        ((long)current.Rows.Single().Values["Quantity"]!).ShouldBe(4000);
        string csv = Path.Combine(fixture.Root, "current-stock.csv");
        (await fixture.Reports.ExportCsvAsync(ReportKind.StockValue,
            new ReportFilter(IncludeExpired: false), csv, cancellationToken: ct)).ShouldBe(1);
        string[] stockLines = File.ReadAllLines(csv);
        stockLines[0].ShouldContain("Expired lots excluded");
        stockLines[2].ShouldContain(",4,");
        stockLines[^1].ShouldStartWith("\"Total\"");
    }

    [Fact]
    public void SaleProductDay_TracksCompletedSalesReturnsAndVoids()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using var connection = fixture.Factory.Open();
        connection.Execute("UPDATE product SET reorder_level=2000 WHERE id=1");
        void AssertSourceParity() => connection.ExecuteScalar<long>("""
            WITH expected AS (
                SELECT sl.product_id,s.sale_date AS day,COUNT(*) AS entries,SUM(sl.qty) AS qty
                FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                WHERE s.status='COMPLETED' GROUP BY sl.product_id,s.sale_date
            )
            SELECT COUNT(*) FROM (
                SELECT * FROM (SELECT * FROM expected EXCEPT SELECT * FROM sale_product_day)
                UNION ALL
                SELECT * FROM (SELECT * FROM sale_product_day EXCEPT SELECT * FROM expected)
            )
            """).ShouldBe(0L);

        connection.Execute("""
            INSERT INTO sale(invoice_no,sale_date,status) VALUES ('LOW-1','2026-09-10','DRAFT');
            INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,
                taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
            VALUES (2,1,1,1,'Rice',3000,100000,300000,0,300000,100000,300000);
            """);
        AssertSourceParity();
        connection.Execute("UPDATE sale SET status='COMPLETED',taxable_total=300000,grand_total=300000 WHERE id=2");
        AssertSourceParity();
        connection.QuerySingle<long>("SELECT qty FROM sale_product_day WHERE product_id=1 AND day='2026-09-10'")
            .ShouldBe(3000L);

        connection.Execute("""
            INSERT INTO sale_return(return_no,sale_id,return_date,refund_mode,taxable_total,
                tax_total,refund_total,cost_total)
            VALUES ('LOW-R',2,'2026-09-11','CASH',100000,0,100000,100000);
            INSERT INTO sale_return_line(sale_return_id,sale_line_id,qty,taxable_amount,
                tax_amount,refund_amount,cost_amount)
            VALUES (2,1,1000,100000,0,100000,100000);
            """);
        AssertSourceParity();
        connection.QuerySingle<long>("SELECT qty FROM sale_product_day WHERE product_id=1 AND day='2026-09-10'")
            .ShouldBe(3000L);

        connection.Execute("UPDATE sale SET status='VOID',void_reason='Correction' WHERE id=2");
        AssertSourceParity();
    }

    [Fact]
    public void SaleReportDay_TracksReturnsBackdatingVoidsAndCategoryChanges()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using var connection = fixture.Factory.Open();
        // A line keeps its sale-time category; a legacy line without one follows the
        // live product, or 0 once that product is deleted.
        void AssertSourceParity() => connection.ExecuteScalar<long>("""
            WITH movements AS (
              SELECT s.sale_date AS day,COALESCE(sl.category_id_snapshot,p.category_id,0) AS category_id,
                     COALESCE(sl.hsn_code,'') AS hsn,
                     sl.tax_rate_pct AS rate,sl.qty AS quantity,sl.taxable_amount AS revenue,
                     sl.tax_amount AS tax,sl.cost_amount AS cost
              FROM sale_line sl JOIN sale s ON s.id=sl.sale_id LEFT JOIN product p ON p.id=sl.product_id
              WHERE s.status='COMPLETED'
              UNION ALL
              SELECT r.return_date,COALESCE(sl.category_id_snapshot,p.category_id,0),COALESCE(sl.hsn_code,''),
                     sl.tax_rate_pct,-rl.qty,-rl.taxable_amount,-rl.tax_amount,-rl.cost_amount
              FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
              JOIN sale_line sl ON sl.id=rl.sale_line_id LEFT JOIN product p ON p.id=sl.product_id
            ), expected AS (
              SELECT day,category_id,hsn,rate,COUNT(*) AS entries,
                     SUM(quantity) AS quantity,SUM(revenue) AS revenue,SUM(tax) AS tax,SUM(cost) AS cost
              FROM movements GROUP BY day,category_id,hsn,rate
            )
            SELECT COUNT(*) FROM (
              SELECT * FROM (SELECT * FROM expected EXCEPT SELECT * FROM sale_report_day)
              UNION ALL
              SELECT * FROM (SELECT * FROM sale_report_day EXCEPT SELECT * FROM expected)
            )
            """).ShouldBe(0L);

        long category = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('New group') RETURNING id");
        connection.Execute("INSERT INTO sale(invoice_no,sale_date,status) VALUES ('S2','2026-08-31','DRAFT')");
        connection.Execute("""
            INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,
                tax_rate_pct,hsn_code,taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
            VALUES (2,1,1,1,'Rice',1000,105000,5000,'1006',100000,5000,105000,60000,60000)
            """);
        AssertSourceParity();
        connection.Execute("UPDATE sale_line SET hsn_code='1234',tax_rate_pct=12000,taxable_amount=120000,tax_amount=12000,line_total=132000 WHERE id=1");
        connection.Execute("UPDATE sale SET status='COMPLETED',taxable_total=120000,tax_total=12000,grand_total=132000 WHERE id=2");
        AssertSourceParity();
        connection.Execute("""
            INSERT INTO sale_return(return_no,sale_id,return_date,refund_mode,taxable_total,tax_total,refund_total,cost_total)
            VALUES ('R2',2,'2026-09-12','CASH',20000,1000,21000,12000)
            """);
        connection.Execute("""
            INSERT INTO sale_return_line(sale_return_id,sale_line_id,qty,taxable_amount,tax_amount,refund_amount,cost_amount)
            VALUES (2,1,200,20000,1000,21000,12000)
            """);
        AssertSourceParity();
        connection.Execute("UPDATE sale_return SET return_date='2026-10-01' WHERE id=2");
        AssertSourceParity();
        connection.Execute("UPDATE product SET category_id=@category WHERE id=1", new { category });
        AssertSourceParity();
        connection.Execute("UPDATE sale SET status='VOID',void_reason='Correction' WHERE id=2");
        AssertSourceParity();
        connection.Execute("DELETE FROM sale_return_line WHERE id=1");
        AssertSourceParity();

        // A sale saved with its category, returned after the product moved, stays in that category.
        long saved = connection.ExecuteScalar<long>("""
            INSERT INTO sale(invoice_no,sale_date,status) VALUES ('S3','2026-09-15','DRAFT') RETURNING id
            """);
        long savedLine = connection.ExecuteScalar<long>("""
            INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,
                tax_rate_pct,hsn_code,taxable_amount,tax_amount,line_total,unit_cost,cost_amount,category_id_snapshot)
            VALUES (@saved,1,1,1,'Rice',1000,105000,5000,'1006',100000,5000,105000,60000,60000,@category)
            RETURNING id
            """, new { saved, category });
        connection.Execute("UPDATE sale SET status='COMPLETED',taxable_total=100000,tax_total=5000,grand_total=105000 WHERE id=@saved",
            new { saved });
        long original = connection.ExecuteScalar<long>("SELECT id FROM category WHERE id<>@category ORDER BY id LIMIT 1",
            new { category });
        connection.Execute("UPDATE product SET category_id=@original WHERE id=1", new { original });
        AssertSourceParity();
        connection.Execute("""
            INSERT INTO sale_return(return_no,sale_id,return_date,refund_mode,taxable_total,tax_total,refund_total,cost_total)
            VALUES ('R3',@saved,'2026-09-16','CASH',100000,5000,105000,60000);
            INSERT INTO sale_return_line(sale_return_id,sale_line_id,qty,taxable_amount,tax_amount,refund_amount,cost_amount)
            VALUES (last_insert_rowid(),@savedLine,1000,100000,5000,105000,60000);
            """, new { saved, savedLine });
        AssertSourceParity();
        connection.ExecuteScalar<long>("""
            SELECT COALESCE(SUM(revenue),0) FROM sale_report_day
            WHERE category_id=@category AND day BETWEEN '2026-09-15' AND '2026-09-16'
            """, new { category }).ShouldBe(0L);

        // Deleting a product moves only its legacy, unsaved-category rows to bucket 0.
        long legacyProduct = connection.ExecuteScalar<long>("""
            INSERT INTO product(sku,name,category_id,uom_id) SELECT 'GONE','Gone',category_id,uom_id
            FROM product WHERE id=1 RETURNING id
            """);
        long legacyLot = connection.ExecuteScalar<long>("""
            INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,selling_price)
            VALUES (@legacyProduct,'GONE-LOT','2026-09-01',1000,2000) RETURNING id
            """, new { legacyProduct });
        long legacySale = connection.ExecuteScalar<long>("""
            INSERT INTO sale(invoice_no,sale_date,status) VALUES ('S4','2026-09-17','DRAFT') RETURNING id
            """);
        connection.Execute("""
            INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,
                taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
            VALUES (@legacySale,1,@legacyProduct,@legacyLot,'Gone',1000,2000,2000,0,2000,1000,1000);
            UPDATE sale SET status='COMPLETED',taxable_total=2000,grand_total=2000 WHERE id=@legacySale;
            """, new { legacySale, legacyProduct, legacyLot });
        AssertSourceParity();
        connection.Execute("DELETE FROM product_lot WHERE id=@legacyLot; DELETE FROM product WHERE id=@legacyProduct;",
            new { legacyLot, legacyProduct });
        AssertSourceParity();
        connection.ExecuteScalar<long>("SELECT revenue FROM sale_report_day WHERE category_id=0 AND day='2026-09-17'")
            .ShouldBe(2000L);
    }

    [Fact]
    public async Task FullCsvExport_StreamsBeyondInteractiveRowLimitAndKeepsNumericValues()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO sale(invoice_no,sale_date,status,line_count,taxable_total,tax_total,
                                 round_off,grand_total,cost_total)
                VALUES ('S2','2026-09-11','COMPLETED',1,2000000,0,0,2000000,1000000),
                       ('S3','2026-09-12','COMPLETED',1,3000000,0,0,3000000,1500000);
                """);
        var filter = new ReportFilter(From: new(2026, 9, 1), To: new(2026, 9, 30), Limit: 1);
        TabularReport interactive = await fixture.Reports.GetReportAsync(ReportKind.SalesRegister,
            filter, TestContext.Current.CancellationToken);
        interactive.Rows.Count.ShouldBe(1);
        interactive.IsTruncated.ShouldBeTrue();

        string path = Path.Combine(fixture.Root, "full.csv");
        long rows = await fixture.Reports.ExportCsvAsync(ReportKind.SalesRegister,
            filter, path, cancellationToken: TestContext.Current.CancellationToken);
        rows.ShouldBe(3);
        string[] lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        lines.Length.ShouldBe(6);
        lines[0].ShouldContain("2026-09-01 – 2026-09-30");
        lines[2].ShouldContain(",1000,");
        lines[4].ShouldContain(",3000,");
        lines[5].ShouldStartWith("\"Total\"");
        Directory.GetFiles(fixture.Root, "*.partial").ShouldBeEmpty();

        await File.WriteAllTextAsync(path, "previous export", TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Reports.ExportCsvAsync(
            ReportKind.SalesRegister, filter, path, cancellationToken: cancelled.Token));
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe("previous export");
        Directory.GetFiles(fixture.Root, "*.partial").ShouldBeEmpty();
    }

    [Fact]
    public async Task PnlAggregate_MatchesEntryViewForFullAndPartialMonths()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO sale(invoice_no,sale_date,status,taxable_total,grand_total,cost_total)
                VALUES('PNL-2025','2025-12-31','COMPLETED',100000,100000,60000),
                      ('PNL-2026','2026-04-01','COMPLETED',200000,200000,110000);
                INSERT INTO sale_return(return_no,sale_id,return_date,refund_mode,taxable_total,refund_total,cost_total)
                VALUES('PNL-RETURN',(SELECT id FROM sale WHERE invoice_no='PNL-2025'),
                       '2026-01-02','CASH',25000,25000,15000);
                """);
        foreach ((DateOnly from, DateOnly to) in new[]
                 { (new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
                   (new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 30)),
                   (new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 15)),
                   (new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 30)),
                   (new DateOnly(2025, 12, 15), new DateOnly(2026, 4, 15)),
                   (new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 31)) })
        {
            PnlStatement actual = await fixture.Reports.GetPnlAsync(from, to, TestContext.Current.CancellationToken);
            var lines = actual.Income.Concat(actual.CostOfGoodsSold).Concat(actual.Expenses)
                .Concat(actual.OtherIncome).ToDictionary(
                    line => $"{line.Section}:{line.HeadCode}:{line.ReferenceId}", line => line.Amount.Scaled);
            using var connection = fixture.Factory.Open();
            var expected = connection.Query<PnlAggregateRow>("""
                SELECT section AS Section,head_code AS HeadCode,NULLIF(category_id,0) AS ReferenceId,SUM(amount) AS Amount
                FROM v_pnl_entry WHERE entry_date BETWEEN @from AND @to
                GROUP BY section,head_code,category_id
                """, new
            {
                from = from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                to = to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            })
                .ToDictionary(row => $"{row.Section}:{row.HeadCode}:{row.ReferenceId}", row => row.Amount);
            lines.ShouldBe(expected, ignoreOrder: true);
        }
    }

    private sealed class PnlAggregateRow
    {
        public string Section { get; init; } = "";
        public string HeadCode { get; init; } = "";
        public long? ReferenceId { get; init; }
        public long Amount { get; init; }
    }

    [Fact]
    public async Task PnlMonthlyMatrix_MatchesEntryViewAcrossPartialAndFullMonths()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO sale(invoice_no,sale_date,status,taxable_total,grand_total,cost_total)
                VALUES('MATRIX-OLD','2025-12-31','COMPLETED',100000,100000,60000);
                INSERT INTO sale_return(return_no,sale_id,return_date,refund_mode,taxable_total,refund_total,cost_total)
                VALUES('MATRIX-RETURN',(SELECT id FROM sale WHERE invoice_no='MATRIX-OLD'),
                       '2026-01-02','CASH',25000,25000,15000);
                """);
        foreach ((DateOnly from, DateOnly to) in new[]
                 { (new DateOnly(2025, 12, 15), new DateOnly(2026, 9, 30)),
                   (new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 15)),
                   (new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 30)),
                   (new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 31)) })
        {
            PnlMatrix actual = await fixture.Reports.GetPnlMatrixAsync(from, to, TestContext.Current.CancellationToken);
            using var connection = fixture.Factory.Open();
            var expected = connection.Query<PnlMatrixReferenceRow>("""
                SELECT section AS Section,head_code AS HeadCode,NULLIF(category_id,0) AS ReferenceId,
                       substr(entry_date,1,7) AS Month,SUM(amount) AS Amount
                FROM v_pnl_entry WHERE entry_date BETWEEN @from AND @to
                GROUP BY section,head_code,category_id,substr(entry_date,1,7)
                """, new
            {
                from = from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                to = to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            })
                .ToDictionary(row => (row.Section, row.HeadCode, row.ReferenceId, row.Month), row => row.Amount);
            var allCells = actual.Rows
                .Where(row => row.HeadCode is not ("NET_SALES" or "GROSS_PROFIT" or "TOTAL_EXPENSES" or "NET_PROFIT"))
                .SelectMany(row => actual.MonthKeys.Select((month, index) => new
                { Key = (row.Section, row.HeadCode, row.ReferenceId, month), Amount = row.Months[index].Scaled }))
                .ToList();
            allCells.Where(cell => !expected.ContainsKey(cell.Key))
                .ShouldAllBe(cell => cell.Amount == 0);
            var observed = allCells
                .Where(cell => expected.ContainsKey(cell.Key))
                .ToDictionary(cell => cell.Key, cell => cell.Amount);
            observed.ShouldBe(expected, ignoreOrder: true);
        }
    }

    [Fact]
    public async Task StockCorrections_FeedTheStockAdjustmentHead_ButBalanceAndDamageCorrectionsDoNot()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO stock_correction(correction_date,product_id,lot_id,quantity_delta,unit_cost,source_kind,reason)
                VALUES ('2026-09-20',1,1,1000,100000,'MANUAL','Found on shelf'),
                       ('2026-09-21',1,1,-500,100000,'LOT_CORRECTION','Receiving count'),
                       ('2026-09-21',1,1,1000,100000,'CUTOVER_BALANCE','Upgrade'),
                       ('2026-09-22',1,1,1000,100000,'DAMAGE_CORRECTION','Recount');
                """);
        PnlStatement pnl = await fixture.Reports.GetPnlAsync(new(2026, 9, 20), new(2026, 9, 22),
            TestContext.Current.CancellationToken);
        PnlMatrix matrix = await fixture.Reports.GetPnlMatrixAsync(new(2026, 9, 20), new(2026, 9, 22),
            TestContext.Current.CancellationToken);
        using var check = fixture.Factory.Open();
        check.ExecuteScalar<long>("""
            SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='STOCK_ADJUSTMENT'
              AND entry_date BETWEEN '2026-09-20' AND '2026-09-22'
            """).ShouldBe(-50000L);
        pnl.Expenses.Single(static line => line.HeadCode == "STOCK_ADJUSTMENT").Amount.ShouldBe(new Money(-50));
        matrix.Rows.Single(static row => row.HeadCode == "STOCK_ADJUSTMENT").Total.ShouldBe(new Money(-50));
    }

    private sealed class PnlMatrixReferenceRow
    {
        public string Section { get; init; } = "";
        public string HeadCode { get; init; } = "";
        public long? ReferenceId { get; init; }
        public string Month { get; init; } = "";
        public long Amount { get; init; }
    }

    [Fact]
    public async Task PnlDay_TracksCompletionVoidAndBackdatedReturnCorrections()
    {
        using var fixture = new ReportFixture();
        using var connection = fixture.Factory.Open();
        connection.Execute("""
            INSERT INTO sale(id,sale_date,status,taxable_total,tax_total,round_off,grand_total,cost_total)
            VALUES(900,'2026-08-31','DRAFT',100000,0,0,100000,60000);
            UPDATE sale SET sale_date='2026-09-01' WHERE id=900;
            UPDATE sale SET status='COMPLETED',invoice_no='PNL-900' WHERE id=900;
            INSERT INTO sale_return(id,return_no,sale_id,return_date,refund_mode,taxable_total,refund_total,cost_total)
            VALUES(901,'PNL-R-901',900,'2026-09-02','CASH',20000,20000,12000);
            UPDATE sale_return SET return_date='2026-08-31',taxable_total=25000,
                refund_total=25000,cost_total=15000 WHERE id=901;
            """);
        connection.QuerySingle<long>("SELECT bills FROM pnl_day WHERE day='2026-09-01'").ShouldBe(1);
        connection.QuerySingle<long>("SELECT sales FROM pnl_day WHERE day='2026-09-01'").ShouldBe(100000);
        connection.QuerySingle<long>("SELECT returns FROM pnl_day WHERE day='2026-08-31'").ShouldBe(1);
        connection.QuerySingle<long>("SELECT returns FROM pnl_day WHERE day='2026-09-02'").ShouldBe(0);
        DashboardSummary beforeVoidDashboard = await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken);
        beforeVoidDashboard.Trend.Count.ShouldBe(1);
        beforeVoidDashboard.Trend[0].Sales.Scaled.ShouldBe(100000);
        beforeVoidDashboard.Trend[0].GrossProfit.Scaled.ShouldBe(40000);
        PnlStatement beforeVoid = await fixture.Reports.GetPnlAsync(new(2026, 8, 1), new(2026, 9, 30),
            TestContext.Current.CancellationToken);
        beforeVoid.NetSales.Scaled.ShouldBe(75000);

        connection.Execute("UPDATE sale SET status='VOID',void_reason='Correction' WHERE id=900");
        connection.QuerySingle<long>("SELECT bills FROM pnl_day WHERE day='2026-09-01'").ShouldBe(0);
        connection.QuerySingle<long>("SELECT sales FROM pnl_day WHERE day='2026-09-01'").ShouldBe(0);
        (await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken)).Trend.Count.ShouldBe(0);
        PnlStatement afterVoid = await fixture.Reports.GetPnlAsync(new(2026, 8, 1), new(2026, 9, 30),
            TestContext.Current.CancellationToken);
        afterVoid.NetSales.Scaled.ShouldBe(-25000);
        connection.Execute("DELETE FROM sale_return WHERE id=901");
        connection.QuerySingle<long>("SELECT returns FROM pnl_day WHERE day='2026-08-31'").ShouldBe(0);
        PnlStatement afterDelete = await fixture.Reports.GetPnlAsync(new(2026, 8, 1), new(2026, 9, 30),
            TestContext.Current.CancellationToken);
        afterDelete.NetSales.Scaled.ShouldBe(0);
    }

    [Fact]
    public async Task DashboardTopWindow_TracksCompletionReturnVoidAndDateRollover()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using var connection = fixture.Factory.Open();
        connection.Execute("""
            INSERT INTO sale(id,sale_date,status) VALUES (900,'2026-09-27','DRAFT');
            INSERT INTO sale_line(id,sale_id,line_no,product_id,lot_id,product_name,qty,
                unit_price,taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
            VALUES (901,900,1,1,1,'Rice',1000,100000,100000,0,100000,60000,60000);
            UPDATE sale SET status='COMPLETED',invoice_no='TOP-900',line_count=1,
                taxable_total=100000,grand_total=100000,cost_total=60000 WHERE id=900;
            """);
        DashboardSummary completed = await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken);
        completed.TopProductsByRevenue.Single().Revenue.Scaled.ShouldBe(100000);
        connection.Execute("""
            INSERT INTO sale_return(id,return_no,sale_id,return_date,refund_mode,
                taxable_total,refund_total,cost_total)
            VALUES (902,'TOP-R-902',900,'2026-09-27','CASH',50000,50000,30000);
            INSERT INTO sale_return_line(id,sale_return_id,sale_line_id,qty,taxable_amount,
                tax_amount,refund_amount,cost_amount)
            VALUES (903,902,901,500,50000,0,50000,30000);
            """);
        DashboardSummary returned = await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken);
        returned.TopProductsByRevenue.Single().Revenue.Scaled.ShouldBe(50000);
        returned.TopProductsByRevenue.Single().Quantity.Scaled.ShouldBe(500);
        connection.Execute("UPDATE sale SET status='VOID',void_reason='Correction' WHERE id=900");
        (await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken))
            .TopProductsByRevenue.Single().Revenue.Scaled.ShouldBe(-50000);
        connection.Execute("UPDATE sale_return SET return_date='2026-10-20' WHERE id=902");
        (await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken))
            .TopProductsByRevenue.ShouldBeEmpty();

        var later = new ReportService(fixture.Database, fixture.Settings, new FixedClock(new(2026, 10, 20)));
        (await later.GetDashboardAsync(TestContext.Current.CancellationToken))
            .TopProductsByRevenue.Single().Revenue.Scaled.ShouldBe(-50000);
        connection.Execute("UPDATE sale_return_line SET qty=400,taxable_amount=40000,cost_amount=24000 WHERE id=903");
        (await later.GetDashboardAsync(TestContext.Current.CancellationToken))
            .TopProductsByRevenue.Single().Revenue.Scaled.ShouldBe(-40000);
        connection.Execute("DELETE FROM sale_return_line WHERE id=903");
        (await later.GetDashboardAsync(TestContext.Current.CancellationToken))
            .TopProductsByRevenue.ShouldBeEmpty();
        (await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken))
            .TopProductsByRevenue.ShouldBeEmpty();
        connection.Execute("""
            INSERT INTO dashboard_top_summary
              (product_id,name,local_name,local_is_null,movements,qty,revenue,cost)
            VALUES (1,'Stale','',1,1,1000,100000,60000)
            """);
        connection.Dispose();
        new MigrationRunner(fixture.Factory, NullMigrationBackup.Instance)
            .RebuildDashboardTop(new(2026, 10, 20));
        (await later.GetDashboardAsync(TestContext.Current.CancellationToken))
            .TopProductsByRevenue.ShouldBeEmpty();
    }

    [Fact]
    public async Task GoldenScenario_PnlAndMonthlyMatrixMatchExpectedAmounts()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();

        PnlStatement statement = await fixture.Reports.GetPnlAsync(new(2026, 9, 1), new(2026, 9, 30), TestContext.Current.CancellationToken);
        PnlMatrix matrix = await fixture.Reports.GetPnlMatrixAsync(new(2026, 9, 1), new(2026, 9, 30), TestContext.Current.CancellationToken);
        TabularReport sales = await fixture.Reports.GetReportAsync(ReportKind.SalesRegister,
            new(From: new(2026, 9, 1), To: new(2026, 9, 30)), TestContext.Current.CancellationToken);

        statement.NetSales.Value.ShouldBe(900m);
        statement.GrossProfit.Value.ShouldBe(360m);
        statement.TotalExpenses.Value.ShouldBe(95m);
        statement.NetProfit.Value.ShouldBe(265m);
        statement.GrossMargin.Value.ShouldBe(40m);
        matrix.MonthKeys.ShouldBe(["2026-09"]);
        matrix.Rows.Single(static row => row.HeadCode == "SALES").Total.Value.ShouldBe(1000m);
        matrix.Rows.Single(static row => row.HeadCode == "NET_PROFIT").Total.Value.ShouldBe(265m);
        matrix.Rows.Single(static row => row.HeadCode == "GROSS_PROFIT").Months[0].Value.ShouldBe(360m);
        sales.Rows.Count.ShouldBe(1);
        string pnlPath = Path.Combine(fixture.Root, "pnl.xlsx");
        fixture.Output.ExportPnlExcel(statement, pnlPath);
        using var pnlWorkbook = new XLWorkbook(pnlPath);
        IXLWorksheet pnlSheet = pnlWorkbook.Worksheet(1);
        int grossRow = pnlSheet.RowsUsed().Single(row => row.Cell(1).GetString() == "Gross profit").RowNumber();
        pnlSheet.Cell(grossRow, 3).GetDouble().ShouldBe(40d);
        pnlSheet.Cell(grossRow, 3).Style.NumberFormat.Format.ShouldBe("#,##0.000");
    }

    [Fact]
    public async Task ItemSalesWindow_ExcludesPriorMonthAndMatchesSourceAfterReturn()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using (var connection = fixture.Factory.Open())
        {
            long product = connection.ExecuteScalar<long>("SELECT id FROM product WHERE sku='RICE'");
            long lot = connection.ExecuteScalar<long>("SELECT id FROM product_lot WHERE product_id=@product", new { product });
            foreach (var item in new[] { (Date: "2026-08-30", Name: "Old rice", Invoice: "OLD"),
                         (Date: "2026-09-10", Name: "New rice", Invoice: "NEW") })
            {
                long sale = connection.ExecuteScalar<long>("""
                    INSERT INTO sale(sale_date,line_count,taxable_total,grand_total,cost_total)
                    VALUES (@date,1,100000,100000,60000) RETURNING id
                    """, new { date = item.Date });
                connection.Execute("""
                    INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,
                        taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
                    VALUES (@sale,1,@product,@lot,@name,1000,100000,100000,0,100000,60000,60000)
                    """, new { sale, product, lot, name = item.Name });
                connection.Execute("UPDATE sale SET status='COMPLETED',invoice_no=@invoice WHERE id=@sale",
                    new { sale, invoice = item.Invoice });
                if (item.Invoice != "NEW") continue;
                long returned = connection.ExecuteScalar<long>("""
                    INSERT INTO sale_return(return_no,sale_id,return_date,refund_mode,taxable_total,
                        refund_total,cost_total)
                    VALUES ('RET-NEW',@sale,'2026-09-12','CASH',25000,25000,15000) RETURNING id
                    """, new { sale });
                long line = connection.ExecuteScalar<long>("SELECT id FROM sale_line WHERE sale_id=@sale", new { sale });
                connection.Execute("""
                    INSERT INTO sale_return_line(sale_return_id,sale_line_id,qty,taxable_amount,
                        tax_amount,refund_amount,cost_amount)
                    VALUES (@returned,@line,250,25000,0,25000,15000)
                    """, new { returned, line });
            }
        }
        await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken);
        var filter = new ReportFilter(From: new(2026, 9, 1), To: new(2026, 9, 27), Limit: 100);
        TabularReport projected = await fixture.Reports.GetReportAsync(ReportKind.ItemSales, filter,
            TestContext.Current.CancellationToken);
        using (var connection = fixture.Factory.Open())
            connection.Execute("DELETE FROM dashboard_top_window");
        TabularReport source = await fixture.Reports.GetReportAsync(ReportKind.ItemSales, filter,
            TestContext.Current.CancellationToken);
        System.Text.Json.JsonSerializer.Serialize(projected.Rows).ShouldBe(
            System.Text.Json.JsonSerializer.Serialize(source.Rows));
        projected.Totals.ShouldBe(source.Totals);
        long selectedProduct;
        using (var connection = fixture.Factory.Open())
            selectedProduct = connection.ExecuteScalar<long>("SELECT id FROM product WHERE sku='RICE'");
        TabularReport selected = await fixture.Reports.GetReportAsync(ReportKind.ItemSales,
            filter with { ProductId = selectedProduct }, TestContext.Current.CancellationToken);
        System.Text.Json.JsonSerializer.Serialize(selected.Rows).ShouldBe(
            System.Text.Json.JsonSerializer.Serialize(source.Rows));
        string selectedCsv = Path.Combine(fixture.Root, "selected-item-sales.csv");
        (await fixture.Reports.ExportCsvAsync(ReportKind.ItemSales,
            filter with { ProductId = selectedProduct }, selectedCsv,
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
        File.ReadLines(selectedCsv).Count().ShouldBe(4);
        projected.Rows[0].Values["Product"].ShouldBe("New rice");
        Convert.ToInt64(projected.Rows[0].Values["Revenue"], System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(75000);
    }

    [Fact]
    public async Task DashboardAndEveryReport_RunAgainstFreshSchema()
    {
        using var fixture = new ReportFixture();

        DashboardSummary dashboard = await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken);
        dashboard.TodaySales.Value.ShouldBe(0m);
        foreach (ReportKind kind in Enum.GetValues<ReportKind>())
        {
            TabularReport report = await fixture.Reports.GetReportAsync(kind,
                new(From: new(2026, 9, 1), To: new(2026, 9, 30)), TestContext.Current.CancellationToken);
            report.Title.ShouldNotBeNullOrWhiteSpace();
            string csvPath = Path.Combine(fixture.Root, $"report-{kind}.csv");
            long exported = await fixture.Reports.ExportCsvAsync(kind,
                new(From: new(2026, 9, 1), To: new(2026, 9, 30)), csvPath,
                cancellationToken: TestContext.Current.CancellationToken);
            exported.ShouldBeGreaterThanOrEqualTo(0);
            File.Exists(csvPath).ShouldBeTrue();
            // A context line, the header, every row and, for reports with amounts, a totals row.
            long lineCount = File.ReadLines(csvPath).LongCount();
            lineCount.ShouldBeInRange(exported + 2, exported + 3);
        }
    }

    [Fact]
    public async Task DashboardCore_PreservesSalesExpiryAndActiveStockTotals()
    {
        using var fixture = new ReportFixture();
        DateOnly businessDate = new(2026, 9, 27);
        using (var connection = fixture.Factory.Open())
        {
            connection.Execute("""
                INSERT INTO sale(invoice_no,sale_date,status,taxable_total,tax_total,grand_total,cost_total)
                VALUES ('TODAY-1','2026-09-27','COMPLETED',90000,10000,100000,60000),
                       ('TODAY-2','2026-09-27','COMPLETED',180000,20000,200000,140000),
                       ('MONTH','2026-09-10','COMPLETED',400000,0,400000,250000),
                       ('PREVIOUS','2026-08-10','COMPLETED',300000,0,300000,200000),
                       ('DRAFT','2026-09-27','DRAFT',900000,0,900000,100000);
                """);
            long category = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Dashboard') RETURNING id");
            long uom = connection.ExecuteScalar<long>("SELECT id FROM uom ORDER BY id LIMIT 1");
            long active = connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,reorder_level,expiry_alert_days)
                VALUES ('D-A','Active',@category,@uom,5000,30) RETURNING id
                """, new { category, uom });
            long inactive = connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,is_active,reorder_level,expiry_alert_days)
                VALUES ('D-I','Inactive',@category,@uom,0,5000,30) RETURNING id
                """, new { category, uom });
            DateOnly localToday = businessDate;
            connection.Execute("""
                INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,unit_cost,selling_price,qty_received,qty_on_hand)
                VALUES (@active,'EXPIRED','2026-09-01',@expired,2000,3000,1000,1000),
                       (@active,'CRITICAL','2026-09-01',@critical,2000,3000,1000,1000),
                       (@active,'SOON','2026-09-01',@soon,2000,3000,1000,1000),
                       (@active,'OUTSIDE','2026-09-01',@outside,2000,3000,1000,1000),
                       (@inactive,'INACTIVE','2026-09-01',@expired,1000,2000,1000,1000);
                """, new
            {
                active,
                inactive,
                expired = localToday.AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                critical = localToday.AddDays(3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                soon = localToday.AddDays(15).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                outside = localToday.AddDays(40).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        DashboardSummary dashboard = await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken);
        dashboard.AsOf.ShouldBe(businessDate);
        dashboard.TodaySales.Value.ShouldBe(300m);
        dashboard.TodayTaxableSales.Value.ShouldBe(270m);
        dashboard.TodayBills.ShouldBe(2);
        dashboard.AverageBill.Value.ShouldBe(150m);
        dashboard.TodayGrossProfit.Value.ShouldBe(70m);
        dashboard.MonthSales.Value.ShouldBe(700m);
        dashboard.PreviousComparableMonthSales.Value.ShouldBe(300m);
        dashboard.ExpiredLots.ShouldBe(2);
        dashboard.CriticalLots.ShouldBe(1);
        dashboard.SoonLots.ShouldBe(1);
        dashboard.LowStockProducts.ShouldBe(1);
        dashboard.StockValue.Value.ShouldBe(8m);
        dashboard.Payables.Value.ShouldBe(0m);

        var emptyDay = new ReportService(fixture.Database, fixture.Settings,
            new FixedClock(new DateOnly(2030, 1, 1)));
        DashboardSummary noSales = await emptyDay.GetDashboardAsync(TestContext.Current.CancellationToken);
        noSales.TodaySales.Value.ShouldBe(0m);
        noSales.TodayBills.ShouldBe(0);
        noSales.TodayGrossProfit.Value.ShouldBe(0m);
        noSales.LowStockProducts.ShouldBe(1);
        noSales.StockValue.Value.ShouldBe(8m);

        async Task AssertExpiryParity()
        {
            (long Expired, long Critical, long Soon) expected;
            using (var connection = fixture.Factory.Open())
                expected = connection.QuerySingle<(long Expired, long Critical, long Soon)>($"""
                    SELECT COUNT(*) FILTER (WHERE bucket='EXPIRED'),
                           COUNT(*) FILTER (WHERE bucket='CRITICAL'),
                           COUNT(*) FILTER (WHERE bucket='SOON')
                    FROM ({ExpiryAlertAsOfSql})
                    """, new { asOf = "2026-09-27" });
            DashboardSummary actual = await fixture.Reports.GetDashboardAsync(TestContext.Current.CancellationToken);
            actual.ExpiredLots.ShouldBe((int)expected.Expired);
            actual.CriticalLots.ShouldBe((int)expected.Critical);
            actual.SoonLots.ShouldBe((int)expected.Soon);
        }

        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                UPDATE product SET expiry_alert_days=NULL WHERE sku IN ('D-A','D-I');
                UPDATE category SET expiry_alert_days=5 WHERE name='Dashboard';
                """);
        await AssertExpiryParity();

        using (var connection = fixture.Factory.Open())
            connection.Execute("UPDATE product_lot SET is_active=0 WHERE lot_code='EXPIRED'");
        await AssertExpiryParity();

        using (var connection = fixture.Factory.Open())
        {
            connection.Execute("""
                UPDATE category SET expiry_alert_days=NULL WHERE name='Dashboard';
                UPDATE product SET expiry_alert_days=60 WHERE sku='D-A';
                """);
            connection.Execute("""
                INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,unit_cost,
                                        selling_price,qty_received,qty_on_hand)
                VALUES (@productId,'LATE','2026-09-01',@expiry,2000,3000,1000,1000),
                       (@productId,'SOLD-OUT','2026-09-01',@expiry,2000,3000,1000,0)
                """, new
            {
                productId = connection.ExecuteScalar<long>("SELECT id FROM product WHERE sku='D-A'"),
                expiry = businessDate.AddDays(45)
                    .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        await AssertExpiryParity();
    }

    [Fact]
    public async Task GstSummary_IncludesReturnsWhenOriginalSaleIsOutsidePeriod()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using (var connection = fixture.Factory.Open())
        {
            connection.Execute("""
                INSERT INTO sale(sale_date,status) VALUES ('2026-09-25','DRAFT');
                INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,
                    tax_rate_pct,hsn_code,taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
                VALUES (2,1,1,1,'Rice',1000,105000,5000,'1006',100000,5000,105000,60000,60000);
                UPDATE sale SET invoice_no='S2',status='COMPLETED',line_count=1,
                    taxable_total=100000,tax_total=5000,grand_total=105000,cost_total=60000
                WHERE id=2;
                INSERT INTO sale_return(return_no,sale_id,return_date,refund_mode,taxable_total,tax_total,refund_total,cost_total)
                VALUES ('R2',2,'2026-10-02','CASH',100000,5000,105000,60000);
                INSERT INTO sale_return_line(sale_return_id,sale_line_id,qty,taxable_amount,tax_amount,
                    refund_amount,cost_amount)
                VALUES (2,1,1000,100000,5000,105000,60000);
                """);
        }

        TabularReport report = await fixture.Reports.GetReportAsync(ReportKind.GstSummary,
            new(From: new(2026, 10, 1), To: new(2026, 10, 31)), TestContext.Current.CancellationToken);

        ReportRow outward = report.Rows.Single(row => (string)row.Values["Direction"]! == "OUTWARD");
        outward.Values["Hsn"].ShouldBe("1006");
        outward.Values["Taxable"].ShouldBe(-100000L);
        outward.Values["Tax"].ShouldBe(-5000L);
        TabularReport items = await fixture.Reports.GetReportAsync(ReportKind.ItemSales,
            new(From: new(2026, 10, 1), To: new(2026, 10, 31)), TestContext.Current.CancellationToken);
        ReportRow item = items.Rows.First(row => row.Values.GetValueOrDefault("Sku")?.ToString() == "RICE");
        item.Values["Quantity"].ShouldBe(-1000L);
        item.Values["Revenue"].ShouldBe(-100000L);
    }

    [Fact]
    public async Task SalesRegister_VoidAmountsDoNotInflateTotals_AndLimitIsDisclosed()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO sale(invoice_no,sale_date,status,void_reason,line_count,
                    taxable_total,grand_total,cost_total)
                VALUES ('S-VOID','2026-09-02','VOID','Entry correction',1,200000,200000,120000);
                """);

        var filter = new ReportFilter(From: new(2026, 9, 1), To: new(2026, 9, 30));
        TabularReport full = await fixture.Reports.GetReportAsync(ReportKind.SalesRegister,
            filter, TestContext.Current.CancellationToken);
        ReportRow voidRow = full.Rows.Single(row => (string)row.Values["Status"]! == "VOID");
        voidRow.Values["Total"].ShouldBe(0L);
        voidRow.Values["OriginalTotal"].ShouldBe(200000L);
        full.Totals["Total"].ShouldBe(1000m);
        full.IsTruncated.ShouldBeFalse();

        TabularReport limited = await fixture.Reports.GetReportAsync(ReportKind.SalesRegister,
            filter with { Limit = 1 }, TestContext.Current.CancellationToken);
        limited.Rows.Count.ShouldBe(1);
        limited.IsTruncated.ShouldBeTrue();
        limited.Totals["Total"].ShouldBe(0m);
        string path = Path.Combine(fixture.Root, "partial.xlsx");
        fixture.Output.ExportExcel(limited, path);
        using var workbook = new XLWorkbook(path);
        workbook.Worksheet(1).Cell(4, 1).GetString().ShouldContain("Partial report");
    }

    [Fact]
    public async Task GstSummary_IncludesVendorCreditsWhenPurchaseIsOutsidePeriod()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO vendor(name,gstin) VALUES ('Supplier','24ABCDE1234F1Z5');
                INSERT INTO purchase(vendor_id,received_date,status) VALUES (1,'2026-09-10','DRAFT');
                INSERT INTO purchase_line(purchase_id,line_no,product_id,qty,unit_cost,
                    tax_rate_pct,tax_amount,line_total,landed_unit_cost,selling_price)
                VALUES (1,1,1,1000,100000,5000,5000,105000,100000,120000);
                UPDATE purchase SET status='POSTED',doc_no='P1',subtotal=100000,tax_total=5000,grand_total=105000 WHERE id=1;
                INSERT INTO product_lot(product_id,lot_code,purchase_line_id,vendor_id,received_date,
                    unit_cost,selling_price,qty_received)
                VALUES (1,'RETURN-LOT',1,1,'2026-09-10',100000,120000,1000);
                INSERT INTO vendor_return(doc_no,vendor_id,purchase_id,return_date,vendor_credit_no,reason,
                    tax_mode,credit_before_tax,tax_credit,credit_total,cost_total)
                VALUES ('VR1',1,1,'2026-10-02','CREDIT-1','Short dated','REGULAR',40000,2000,42000,40000);
                INSERT INTO vendor_return_line(vendor_return_id,product_id,lot_id,qty,credit_before_tax,
                    tax_credit,cost_value,purchase_line_id_snapshot)
                VALUES (1,1,2,400,40000,2000,40000,1);
                """);

        TabularReport report = await fixture.Reports.GetReportAsync(ReportKind.GstSummary,
            new(From: new(2026, 10, 1), To: new(2026, 10, 31)), TestContext.Current.CancellationToken);

        ReportRow inward = report.Rows.Single(row => (string)row.Values["Direction"]! == "INWARD");
        inward.Values["Hsn"].ShouldBe("24ABCDE1234F1Z5");
        inward.Values["Taxable"].ShouldBe(-40000L);
        inward.Values["Tax"].ShouldBe(-2000L);
        using (var connection = fixture.Factory.Open())
        {
            var balance = connection.QuerySingle<(long Returns, long BalanceDue)>("""
                SELECT t.returns,b.balance_due FROM vendor_balance_total t
                JOIN v_vendor_balance b ON b.vendor_id=t.vendor_id
                """);
            balance.Returns.ShouldBe(42000L);
            balance.BalanceDue.ShouldBe(63000L);
        }
    }

    [Fact]
    public async Task Reports_LocalizeHeadingsAndStatusWhileKeepingNumericValues()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        TranslationSource translations = TranslationSource.Instance;
        var original = translations.UiCulture;
        try
        {
            translations.UiCulture = System.Globalization.CultureInfo.GetCultureInfo("gu-IN");
            var service = new ReportService(fixture.Database, fixture.Settings,
                new FixedClock(new DateOnly(2026, 9, 27)), translations);
            TabularReport report = await service.GetReportAsync(ReportKind.SalesRegister,
                new(From: new(2026, 9, 1), To: new(2026, 9, 30)), TestContext.Current.CancellationToken);

            report.Title.ShouldBe(translations["Reports_Kind_SalesRegister"]);
            report.Columns[0].Heading.ShouldBe(translations["Reports_Column_Date"]);
            report.Rows.Single().Values["Status"].ShouldBe(translations["Reports_StatusCompleted"]);
            report.Rows.Single().Values["Total"].ShouldBe(1000000L);

            PnlStatement statement = await service.GetPnlAsync(new(2026, 9, 1), new(2026, 9, 30),
                TestContext.Current.CancellationToken);
            string path = Path.Combine(fixture.Root, "pnl-gu.xlsx");
            new ReportOutputService(fixture.Settings, translations).ExportPnlExcel(statement, path);
            using var workbook = new XLWorkbook(path);
            IXLWorksheet sheet = workbook.Worksheet(1);
            sheet.Cell(1, 1).GetString().ShouldBe(translations["Reports_Kind_ProfitAndLoss"]);
            sheet.RowsUsed().Select(static row => row.Cell(1).GetString())
                .ShouldContain(translations["Reports_Section_GrossProfit"]);
        }
        finally
        {
            translations.UiCulture = original;
        }
    }

    [Fact]
    public async Task FiscalYearPnl_WithTwentyThousandEntries_CompletesUnderFiveHundredMilliseconds()
    {
        using var fixture = new ReportFixture();
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                WITH RECURSIVE sequence(value) AS (
                  VALUES(1) UNION ALL SELECT value+1 FROM sequence WHERE value<20000
                )
                INSERT INTO sale(invoice_no,sale_date,status,line_count,taxable_total,tax_total,round_off,grand_total,cost_total)
                SELECT 'PERF-'||value,date('2025-04-01','+'||(value%365)||' days'),'COMPLETED',1,
                       100000,0,0,100000,70000 FROM sequence
                """);

        var stopwatch = Stopwatch.StartNew();
        PnlStatement statement = await fixture.Reports.GetPnlAsync(new(2025, 4, 1), new(2026, 3, 31), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        statement.NetSales.Value.ShouldBe(2_000_000m);
        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(500);
    }

    [Fact]
    public void ExcelExport_WritesMoneyAsNumericCellWithThreeDecimalFormat()
    {
        using var fixture = new ReportFixture();
        string path = Path.Combine(fixture.Root, "report.xlsx");
        var report = new TabularReport("Stock value", null,
            [new("Name", "Name", ReportValueKind.Text), new("Value", "Value", ReportValueKind.Money)],
            [new(new Dictionary<string, object?> { ["Name"] = "=2+2", ["Value"] = 123456L })],
            new Dictionary<string, decimal> { ["Value"] = 123.456m });

        fixture.Output.ExportExcel(report, path);

        using var workbook = new XLWorkbook(path);
        IXLWorksheet sheet = workbook.Worksheet(1);
        sheet.Cell(4, 1).DataType.ShouldBe(XLDataType.Text);
        sheet.Cell(4, 1).Value.GetText().ShouldBe("=2+2");
        sheet.Cell(4, 2).DataType.ShouldBe(XLDataType.Number);
        sheet.Cell(4, 2).GetDouble().ShouldBe(123.456);
        sheet.Cell(4, 2).Style.NumberFormat.Format.ShouldBe("#,##0.000");
    }

    [Fact]
    public void CancelledExcelExport_PreservesExistingOutputAndLeavesNoPartialFile()
    {
        using var fixture = new ReportFixture();
        string path = Path.Combine(fixture.Root, "existing.xlsx");
        File.WriteAllText(path, "existing output");
        var report = new TabularReport("Stock value", null,
            [new("Name", "Name", ReportValueKind.Text)],
            [new(new Dictionary<string, object?> { ["Name"] = "Rice" })],
            new Dictionary<string, decimal>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            fixture.Output.ExportExcel(report, path, null, cancellation.Token));

        File.ReadAllText(path).ShouldBe("existing output");
        Directory.EnumerateFiles(fixture.Root, "*.partial*").ShouldBeEmpty();
    }

    private sealed class ReportFixture : IDisposable
    {
        public ReportFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-reports-{Guid.NewGuid():N}");
            Paths = new AppPaths(new SettingsBootstrap(Root));
            Factory = new SqliteConnectionFactory(Paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, Paths, new AuditLog());
            var clock = new FixedClock(new DateOnly(2026, 9, 27));
            Reports = new ReportService(Database, Settings, clock);
            Output = new ReportOutputService(Settings);
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public ReportService Reports { get; }
        public ReportOutputService Output { get; }

        public void SeedGoldenScenario()
        {
            using var connection = Factory.Open();
            connection.Execute("""
                INSERT INTO sale(invoice_no,sale_date,status,line_count,taxable_total,tax_total,round_off,grand_total,cost_total)
                VALUES ('S1','2026-09-10','COMPLETED',1,1000000,0,0,1000000,600000);
                INSERT INTO sale_return(return_no,sale_id,return_date,refund_mode,taxable_total,tax_total,refund_total,cost_total)
                VALUES ('R1',1,'2026-09-12','CASH',100000,0,100000,60000);
                INSERT INTO expense(expense_date,category_id,amount,mode) VALUES ('2026-09-15',1,30000,'CASH');
                INSERT INTO employee(name,monthly_salary) VALUES ('Employee',40000);
                INSERT INTO salary_payment(employee_id,period_month,pay_date,amount,mode) VALUES (1,'2026-09','2026-09-30',40000,'CASH');
                INSERT INTO asset(name,purchase_date,in_use_from,cost,method,rate_pct,salvage_value)
                VALUES ('Freezer','2026-01-01','2026-01-01',1000000,'SLM',12000,0);
                INSERT INTO asset_depreciation(asset_id,period_month,opening_value,amount,closing_value)
                VALUES (1,'2026-09',1000000,10000,990000);
                """);
            long category = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Food') RETURNING id");
            long uom = connection.ExecuteScalar<long>("SELECT id FROM uom ORDER BY id LIMIT 1");
            long product = connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id) VALUES ('RICE','Rice',@category,@uom) RETURNING id
                """, new { category, uom });
            long lot = connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,selling_price,qty_received,qty_on_hand)
                VALUES (@product,'L1','2026-09-01',100000,120000,1000,1000) RETURNING id
                """, new { product });
            connection.Execute("""
                INSERT INTO damage(doc_no,damage_date,product_id,lot_id,qty,unit_cost,cost_value,recovered_amount,reason)
                VALUES ('D1','2026-09-20',@product,@lot,200,100000,20000,5000,'BROKEN')
                """, new { product, lot });
        }

        public void Dispose()
        {
            Settings.Dispose();
            Database.Dispose();
            Factory.ClearPool();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    // The v_expiry_alert rules as of a given business date; the view itself counts from the machine clock.
    private const string ExpiryAlertAsOfSql = """
        SELECT *,CASE WHEN days_left<0 THEN 'EXPIRED' WHEN days_left<=7 THEN 'CRITICAL' ELSE 'SOON' END AS bucket
        FROM (
          SELECT CAST(julianday(l.expiry_date)-julianday(@asOf) AS INTEGER) AS days_left,
                 COALESCE(p.expiry_alert_days,c.expiry_alert_days,pc.expiry_alert_days,
                          CAST((SELECT value FROM setting WHERE key='expiry.default_alert_days') AS INTEGER),30) AS alert_days
          FROM product_lot l JOIN product p ON p.id=l.product_id
          JOIN category c ON c.id=p.category_id LEFT JOIN category pc ON pc.id=c.parent_id
          WHERE l.qty_on_hand>0 AND l.is_active=1 AND l.expiry_date IS NOT NULL
        ) WHERE days_left<=alert_days
        """;

    private sealed class FixedClock(DateOnly date) : IClock
    {
        public DateTime Now => date.ToDateTime(new TimeOnly(12, 0));
        public DateOnly BusinessDate => date;
    }
}
