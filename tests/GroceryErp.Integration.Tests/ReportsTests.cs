using System.Diagnostics;
using ClosedXML.Excel;
using Dapper;
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
    public async Task GoldenScenario_PnlAndMonthlyMatrixMatchExpectedAmounts()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();

        PnlStatement statement = await fixture.Reports.GetPnlAsync(new(2026, 9, 1), new(2026, 9, 30), TestContext.Current.CancellationToken);
        PnlMatrix matrix = await fixture.Reports.GetPnlMatrixAsync(new(2026, 9, 1), new(2026, 9, 30), TestContext.Current.CancellationToken);
        TabularReport sales = await fixture.Reports.GetReportAsync(ReportKind.SalesRegister,
            new(From: new(2026, 9, 1), To: new(2026, 9, 30)), TestContext.Current.CancellationToken);
        TabularReport salaries = await fixture.Reports.GetReportAsync(ReportKind.SalaryRegister,
            new(From: new(2026, 9, 1), To: new(2026, 9, 30)), TestContext.Current.CancellationToken);
        TabularReport assetSchedule = await fixture.Reports.GetReportAsync(ReportKind.AssetSchedule,
            new(From: new(2026, 9, 1), To: new(2026, 12, 31), AssetId: 1), TestContext.Current.CancellationToken);

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
        salaries.Columns.ShouldContain(static column => column.Key == "2026-09");
        salaries.Rows.Count.ShouldBe(1);
        assetSchedule.Rows.Count.ShouldBe(4);
        assetSchedule.Rows[^1].Values["Status"].ShouldBe("PROJECTED");
        string pnlPath = Path.Combine(fixture.Root, "pnl.xlsx");
        fixture.Output.ExportPnlExcel(statement, pnlPath);
        using var pnlWorkbook = new XLWorkbook(pnlPath);
        IXLWorksheet pnlSheet = pnlWorkbook.Worksheet(1);
        int grossRow = pnlSheet.RowsUsed().Single(row => row.Cell(1).GetString() == "Gross profit").RowNumber();
        pnlSheet.Cell(grossRow, 3).GetDouble().ShouldBe(40d);
        pnlSheet.Cell(grossRow, 3).Style.NumberFormat.Format.ShouldBe("#,##0.000");
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
        }
    }

    [Fact]
    public async Task HourlySales_GroupsCompletedBillsAndLocalizesWeekdays()
    {
        using var fixture = new ReportFixture();
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO sale(sale_date,status) VALUES ('2026-09-14','DRAFT'),('2026-09-14','DRAFT');
                UPDATE sale SET status='COMPLETED',invoice_no='H-'||id,
                    completed_at='2026-09-14 09:30:00',taxable_total=45000,grand_total=45000 WHERE id IN (1,2);
                """);
        TranslationSource translations = TranslationSource.Instance;
        var original = translations.UiCulture;
        try
        {
            translations.UiCulture = System.Globalization.CultureInfo.GetCultureInfo("gu-IN");
            var service = new ReportService(fixture.Database, fixture.Settings,
                new FixedClock(new DateOnly(2026, 9, 14)), translations);
            TabularReport report = await service.GetReportAsync(ReportKind.HourlySales,
                new(From: new(2026, 9, 14), To: new(2026, 9, 14)), TestContext.Current.CancellationToken);

            ReportRow row = report.Rows.Single();
            row.Values["Weekday"].ShouldBe(translations["Reports_Weekday_Mon"]);
            row.Values["Hour"].ShouldBe(9L);
            row.Values["Bills"].ShouldBe(2L);
            row.Values["Revenue"].ShouldBe(90000L);
        }
        finally { translations.UiCulture = original; }
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
        TabularReport categories = await fixture.Reports.GetReportAsync(ReportKind.CategorySales,
            new(From: new(2026, 10, 1), To: new(2026, 10, 31)), TestContext.Current.CancellationToken);
        categories.Rows.Single().Values["Revenue"].ShouldBe(-100000L);
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
                    tax_credit,cost_value)
                VALUES (1,1,2,400,40000,2000,40000);
                """);

        TabularReport report = await fixture.Reports.GetReportAsync(ReportKind.GstSummary,
            new(From: new(2026, 10, 1), To: new(2026, 10, 31)), TestContext.Current.CancellationToken);

        ReportRow inward = report.Rows.Single(row => (string)row.Values["Direction"]! == "INWARD");
        inward.Values["Hsn"].ShouldBe("24ABCDE1234F1Z5");
        inward.Values["Taxable"].ShouldBe(-40000L);
        inward.Values["Tax"].ShouldBe(-2000L);
        TabularReport payables = await fixture.Reports.GetReportAsync(ReportKind.VendorPayables,
            new(To: new(2026, 10, 31)), TestContext.Current.CancellationToken);
        payables.Rows.Single().Values["Returns"].ShouldBe(42000L);
        payables.Rows.Single().Values["BalanceDue"].ShouldBe(63000L);
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
        }
        finally
        {
            translations.UiCulture = original;
        }
    }

    [Fact]
    public async Task CategoryComparison_IncludesCategoriesWithOnlyPreviousPeriodSales()
    {
        using var fixture = new ReportFixture();
        fixture.SeedGoldenScenario();
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO category(name) VALUES ('Dairy');
                INSERT INTO product(sku,name,category_id,uom_id)
                VALUES ('MILK','Milk',2,(SELECT id FROM uom ORDER BY id LIMIT 1));
                INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,selling_price,qty_received)
                VALUES (2,'MILK-LOT','2026-09-01',60000,100000,1000);
                INSERT INTO sale(sale_date,status) VALUES ('2026-09-15','DRAFT');
                INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,
                    taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
                VALUES (2,1,2,2,'Milk',1000,100000,100000,0,100000,60000,60000);
                UPDATE sale SET invoice_no='S2',status='COMPLETED',line_count=1,
                    taxable_total=100000,grand_total=100000,cost_total=60000 WHERE id=2;
                """);

        TabularReport report = await fixture.Reports.GetReportAsync(ReportKind.CategorySales,
            new(From: new(2026, 10, 1), To: new(2026, 10, 31)), TestContext.Current.CancellationToken);

        ReportRow dairy = report.Rows.Single(row => (string)row.Values["Category"]! == "Dairy");
        dairy.Values["Revenue"].ShouldBe(0L);
        dairy.Values["PreviousRevenue"].ShouldBe(100000L);
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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FixedClock(DateOnly date) : IClock
    {
        public DateTime Now => date.ToDateTime(new TimeOnly(12, 0));
        public DateOnly BusinessDate => date;
    }
}
