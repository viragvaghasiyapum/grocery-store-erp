using System.Diagnostics;
using ClosedXML.Excel;
using Dapper;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
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
        sales.Rows.Count.ShouldBe(1);
        salaries.Columns.ShouldContain(static column => column.Key == "2026-09");
        salaries.Rows.Count.ShouldBe(1);
        assetSchedule.Rows.Count.ShouldBe(4);
        assetSchedule.Rows[^1].Values["Status"].ShouldBe("PROJECTED");
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
