using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Reports;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

// Phase 6: independent calendar and fiscal locks, finalized annual snapshots and
// corrections dated by the locks of both years containing the original date.
public sealed class AnnualCloseTests
{
    [Fact]
    public async Task OctoberCorrection_OfFebruaryEntry_LeavesFiscalYearFrozenAndCountsToday()
    {
        using var fixture = new Fixture(new DateOnly(2026, 2, 20));
        CancellationToken ct = TestContext.Current.CancellationToken;
        var february = new DateOnly(2026, 2, 10);
        await fixture.RecordExpenseAsync(february, 1000, ct);

        fixture.Clock.BusinessDate = new DateOnly(2026, 10, 15);
        (await fixture.Closes.EnsureFinalizedAsync(ct)).ShouldBe(1);
        (await fixture.Closes.EnsureFinalizedAsync(ct)).ShouldBe(0);
        PnlReport frozen = await fixture.Reports.GetPnlReportAsync(fixture.Reports.FiscalYearPeriod(2025), ct);
        frozen.Status.ShouldBe("FINALIZED");
        frozen.Cutoff.ShouldBe(new DateOnly(2026, 4, 7));
        frozen.Statement.TotalExpenses.ShouldBe(new Money(1000));
        byte[] frozenCsv = await fixture.ExportAsync(frozen, ct);

        ExpenseItem original = (await fixture.MoneyOut.GetExpensesAsync(february, february, ct)).Single();
        CorrectionDating dating = await fixture.MoneyOut.PreviewCorrectionAsync(february, ct);
        dating.Treatment.ShouldBe(PeriodTreatment.CurrentDated);
        (dating.CalendarLocked, dating.FiscalLocked).ShouldBe((false, true));
        await fixture.MoneyOut.CorrectExpenseAsync(original,
            new(february, original.CategoryId, new Money(1200), "CASH", null, null, null),
            "Invoice was higher", OperationGuard.NewId(), ct);

        using var connection = fixture.Factory.Open();
        connection.QuerySingle<(string Treatment, string Reverse, string Apply)>("""
            SELECT treatment AS Treatment,reverse_date AS Reverse,apply_date AS Apply FROM expense_correction
            """).ShouldBe(("CURRENT_DATED", "2026-10-15", "2026-10-15"));
        PnlReport after = await fixture.Reports.GetPnlReportAsync(fixture.Reports.FiscalYearPeriod(2025), ct);
        after.Checksum.ShouldBe(frozen.Checksum);
        (await fixture.ExportAsync(after, ct)).ShouldBe(frozenCsv);
        PnlReport februaryReport = await fixture.Reports.GetPnlReportAsync(fixture.Reports.MonthPeriod(new YearMonth(2026, 2)), ct);
        februaryReport.Status.ShouldBe("FINALIZED");
        februaryReport.Statement.TotalExpenses.ShouldBe(new Money(1000));

        PnlReport calendar = await fixture.Reports.GetPnlReportAsync(fixture.Reports.CalendarYearPeriod(2026), ct);
        calendar.Status.ShouldBe("OPEN");
        calendar.Cutoff.ShouldBe(new DateOnly(2027, 1, 7));
        calendar.GeneratedAt.ShouldBe("2026-10-15");
        new ReportOutputService(fixture.Settings).PnlStatusText(calendar)
            .ShouldBe("Open · editable through 07-01-2027 · source records as of 15-10-2026");
        new ReportOutputService(fixture.Settings).PnlStatusText(frozen).ShouldStartWith("Finalized · FY 2025-26 · edits closed after 07-04-2026");
        calendar.Statement.TotalExpenses.ShouldBe(new Money(1200));
        PnlLayoutRow expenses = calendar.Rows.Single(static row => row.Kind == "HEAD" && row.Code == "EXPENSE_CATEGORY");
        expenses.Months[1].ShouldBe(new Money(1000));
        expenses.Months[9].ShouldBe(new Money(200));
        (await fixture.Reports.GetPnlReportAsync(fixture.Reports.FiscalYearPeriod(2026), ct)).Statement.TotalExpenses
            .ShouldBe(new Money(200));
        (await Should.ThrowAsync<DomainException>(() => fixture.RecordExpenseAsync(february.AddDays(1), 10, ct)))
            .Code.ShouldBe(ErrorCode.PeriodLocked);
    }

    [Fact]
    public async Task FebruaryCorrection_OfOctoberEntry_LeavesCalendarYearFrozenAndCountsToday()
    {
        using var fixture = new Fixture(new DateOnly(2026, 10, 20));
        CancellationToken ct = TestContext.Current.CancellationToken;
        var october = new DateOnly(2026, 10, 10);
        await fixture.RecordExpenseAsync(october, 500, ct);

        fixture.Clock.BusinessDate = new DateOnly(2027, 2, 15);
        (await fixture.Closes.EnsureFinalizedAsync(ct)).ShouldBe(1);
        PnlReport frozen = await fixture.Reports.GetPnlReportAsync(fixture.Reports.CalendarYearPeriod(2026), ct);
        frozen.Status.ShouldBe("FINALIZED");
        ExpenseItem original = (await fixture.MoneyOut.GetExpensesAsync(october, october, ct)).Single();
        CorrectionDating dating = await fixture.MoneyOut.PreviewCorrectionAsync(october, ct);
        (dating.Treatment, dating.CalendarLocked, dating.FiscalLocked)
            .ShouldBe((PeriodTreatment.CurrentDated, true, false));
        await fixture.MoneyOut.CorrectExpenseAsync(original,
            new(october, original.CategoryId, new Money(300), "CASH", null, null, null),
            "Part refund from supplier", OperationGuard.NewId(), ct);

        PnlReport after = await fixture.Reports.GetPnlReportAsync(fixture.Reports.CalendarYearPeriod(2026), ct);
        after.Checksum.ShouldBe(frozen.Checksum);
        after.Statement.TotalExpenses.ShouldBe(new Money(500));
        (await fixture.Reports.GetPnlReportAsync(fixture.Reports.CalendarYearPeriod(2027), ct)).Statement.TotalExpenses
            .ShouldBe(new Money(-200));
        PnlReport fiscal = await fixture.Reports.GetPnlReportAsync(fixture.Reports.FiscalYearPeriod(2026), ct);
        fiscal.Status.ShouldBe("OPEN");
        fiscal.Statement.TotalExpenses.ShouldBe(new Money(300));
    }

    [Fact]
    public async Task SevenDayBuffer_KeepsYearEndCorrectionsOnTheirOriginalDate()
    {
        using var fixture = new Fixture(new DateOnly(2026, 12, 31));
        CancellationToken ct = TestContext.Current.CancellationToken;
        var yearEnd = new DateOnly(2026, 12, 31);
        await fixture.RecordExpenseAsync(yearEnd, 100, ct);

        fixture.Clock.BusinessDate = new DateOnly(2027, 1, 7);
        await fixture.CorrectSingleAsync(yearEnd, 150, ct);
        fixture.Clock.BusinessDate = new DateOnly(2027, 1, 8);
        (await fixture.MoneyOut.PreviewCorrectionAsync(yearEnd, ct)).CalendarLocked.ShouldBeTrue();
        await fixture.CorrectSingleAsync(yearEnd, 120, ct);

        var fiscalYearEnd = new DateOnly(2027, 3, 31);
        fixture.Clock.BusinessDate = fiscalYearEnd;
        await fixture.RecordExpenseAsync(fiscalYearEnd, 80, ct);
        fixture.Clock.BusinessDate = new DateOnly(2027, 4, 7);
        await fixture.CorrectSingleAsync(fiscalYearEnd, 90, ct);
        fixture.Clock.BusinessDate = new DateOnly(2027, 4, 8);
        CorrectionDating locked = await fixture.MoneyOut.PreviewCorrectionAsync(fiscalYearEnd, ct);
        (locked.CalendarLocked, locked.FiscalLocked).ShouldBe((false, true));
        await fixture.CorrectSingleAsync(fiscalYearEnd, 70, ct);

        using var connection = fixture.Factory.Open();
        connection.Query<(string Treatment, string Reverse)>("""
            SELECT treatment AS Treatment,reverse_date AS Reverse FROM expense_correction ORDER BY id
            """).ToArray().ShouldBe(
        [
            ("OPEN_PERIOD", "2026-12-31"), ("CURRENT_DATED", "2027-01-08"),
            ("OPEN_PERIOD", "2027-03-31"), ("CURRENT_DATED", "2027-04-08"),
        ]);
        (await fixture.Closes.EnsureFinalizedAsync(ct)).ShouldBe(2);
        (await fixture.Reports.GetPnlReportAsync(fixture.Reports.CalendarYearPeriod(2026), ct)).Statement.TotalExpenses
            .ShouldBe(new Money(150));
        (await fixture.Reports.GetPnlReportAsync(fixture.Reports.FiscalYearPeriod(2026), ct)).Statement.TotalExpenses
            .ShouldBe(new Money(150 - 30 + 90));
        (await fixture.Reports.GetPnlReportAsync(fixture.Reports.FiscalYearPeriod(2027), ct)).Statement.TotalExpenses
            .ShouldBe(new Money(-20));
    }

    [Fact]
    public async Task FinalizedYear_IsChecksummedImmutableAndKeepsItsLockIfTheClockMovesBack()
    {
        using var fixture = new Fixture(new DateOnly(2026, 10, 23));
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var connection = fixture.Factory.Open();
        SeedSaleAndFullReturn(connection, "2025-06-10", "2025-06-12");
        connection.Execute("""
            INSERT INTO expense(expense_date,category_id,amount,mode) VALUES ('2025-07-01',1,30000,'CASH');
            """);
        AnnualCloseService.Missing(connection, null, fixture.Clock.BusinessDate, 4).Select(static year => year.Label)
            .ShouldBe(["2025", "2025-26"]);
        (await fixture.Closes.EnsureFinalizedAsync(ct)).ShouldBe(2);
        AnnualCloseService.Missing(connection, null, fixture.Clock.BusinessDate, 4).ShouldBeEmpty();
        AnnualCloseSnapshot snapshot = AnnualCloseService.Load(connection, null, AnnualPeriod.Calendar(new DateOnly(2025, 1, 1)))!;
        snapshot.Info.NetProfit.ShouldBe(-30000);
        snapshot.Lines.Sum(static line => line.SourceCount).ShouldBeGreaterThan(0);
        snapshot.Lines.Where(static line => line.Section == "INCOME").Sum(static line => line.Amount).ShouldBe(0);
        snapshot.Lines.Single(static line => line.HeadCode == "EXPENSE_CATEGORY").CategoryName.ShouldNotBeNullOrEmpty();

        Should.Throw<SqliteException>(() => connection.Execute("UPDATE annual_pnl_line SET amount=amount+1"));
        Should.Throw<SqliteException>(() => connection.Execute("DELETE FROM annual_pnl_close"));
        connection.Execute("UPDATE expense_category SET name='Renamed later' WHERE id=1");
        AnnualCloseService.Load(connection, null, AnnualPeriod.Calendar(new DateOnly(2025, 1, 1)))!
            .Lines.Single(static line => line.HeadCode == "EXPENSE_CATEGORY").CategoryName.ShouldNotBe("Renamed later");

        fixture.Clock.BusinessDate = new DateOnly(2025, 12, 20);
        (await Should.ThrowAsync<DomainException>(() => fixture.RecordExpenseAsync(new DateOnly(2025, 12, 1), 10, ct)))
            .Code.ShouldBe(ErrorCode.PeriodLocked);
        (await fixture.MoneyOut.PreviewCorrectionAsync(new DateOnly(2025, 7, 1), ct)).Treatment
            .ShouldBe(PeriodTreatment.CurrentDated);

        connection.Execute("DROP TRIGGER trg_annual_line_no_update");
        connection.Execute("UPDATE annual_pnl_line SET amount=amount+1 WHERE period_kind='CALENDAR'");
        (await Should.ThrowAsync<DomainException>(() => fixture.Reports.GetPnlReportAsync(
            fixture.Reports.CalendarYearPeriod(2025), ct))).Code.ShouldBe(ErrorCode.AnnualReportInvalid);
        AnnualCloseService.VerifyAll(connection).ShouldBe(["CALENDAR 2025"]);
    }

    [Fact]
    public async Task FullReturn_ReversesSalesCostAndRoundOff_AndOpenDaysReconcile()
    {
        using var fixture = new Fixture(new DateOnly(2026, 10, 23));
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var connection = fixture.Factory.Open();
        SeedSaleAndFullReturn(connection, "2026-09-10", "2026-09-12");

        PnlReport month = await fixture.Reports.GetPnlReportAsync(fixture.Reports.MonthPeriod(new YearMonth(2026, 9)), ct);
        month.Status.ShouldBe("OPEN");
        month.Statement.NetSales.ShouldBe(Money.Zero);
        month.Statement.GrossProfit.ShouldBe(Money.Zero);
        month.Statement.Income.Single(static line => line.HeadCode == "SALES").Amount.ShouldBe(new Money(1000));
        PnlReconciliation.CheckOpenDays(connection, fixture.Clock.BusinessDate, ct).ShouldBeEmpty();

        connection.Execute("UPDATE pnl_day SET return_rounding=return_rounding+1 WHERE day='2026-09-12'");
        PnlReconciliation.CheckOpenDays(connection, fixture.Clock.BusinessDate, ct).ShouldBe(["2026-09-12"]);
    }

    [Fact]
    public async Task StockReports_StateExcludedExpiredStockAndShowSourceDifferences()
    {
        using var fixture = new Fixture(new DateOnly(2026, 10, 23));
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var connection = fixture.Factory.Open();
        long product = SeedProduct(connection);
        long expired = connection.ExecuteScalar<long>("""
            INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,unit_cost,selling_price,qty_received)
            VALUES (@product,'L-OLD','2026-08-01','2026-10-01',100000,120000,2000) RETURNING id
            """, new { product });
        long fresh = connection.ExecuteScalar<long>("""
            INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,unit_cost,selling_price,qty_received)
            VALUES (@product,'L-NEW','2026-10-01','2027-01-01',100000,120000,3000) RETURNING id
            """, new { product });
        connection.Execute("UPDATE product_lot SET qty_on_hand=qty_received WHERE id IN (@expired,@fresh)", new { expired, fresh });

        TabularReport excluding = await fixture.Reports.GetReportAsync(ReportKind.StockValue, new(IncludeExpired: false), ct);
        ((long)excluding.Rows.Single().Values["Quantity"]!).ShouldBe(3000);
        excluding.Summary.ShouldBe("Excludes expired stock: 1 lots, quantity 2.000, cost value 200.000.");
        TabularReport including = await fixture.Reports.GetReportAsync(ReportKind.StockValue, new(IncludeExpired: true), ct);
        ((long)including.Rows.Single().Values["Quantity"]!).ShouldBe(5000);
        including.Summary.ShouldBe("Includes expired stock: 1 lots, quantity 2.000, cost value 200.000.");

        TabularReport reconciled = await fixture.Reports.GetReportAsync(ReportKind.StockReconciliation, new(), ct);
        reconciled.Rows.Count.ShouldBe(2);
        reconciled.Rows.ShouldAllBe(static row => (long)row.Values["Difference"]! == 0);
        ((long)reconciled.Rows[0].Values["ReceivedQty"]!).ShouldBe(3000);
        (await fixture.Reports.GetReportAsync(ReportKind.StockReconciliation, new(MismatchesOnly: true), ct))
            .Rows.ShouldBeEmpty();

        connection.Execute("UPDATE product_lot SET qty_on_hand=qty_on_hand+1000 WHERE id=@expired", new { expired });
        TabularReport beforeCheck = await fixture.Reports.GetReportAsync(ReportKind.StockReconciliation,
            new(MismatchesOnly: true), ct);
        beforeCheck.Rows.ShouldBeEmpty();
        beforeCheck.Summary.ShouldBe("No full lot check has run yet; weekly maintenance runs it.");
        TabularReport newest = await fixture.Reports.GetReportAsync(ReportKind.StockReconciliation, new(), ct);
        ((long)newest.Rows[1].Values["Difference"]!).ShouldBe(1000);
        newest.Summary.ShouldBe("2 lots shown, newest first; 1 differ from their documents.");

        // Weekly maintenance checks every lot, records the difference and still fails.
        var backups = new GroceryErp.Data.Backup.BackupService(fixture.Paths, fixture.Factory, fixture.Clock, fixture.Settings);
        var maintenance = new MaintenanceService(fixture.Paths, fixture.Factory, fixture.Database, backups, fixture.Clock,
            new AuditLog());
        (await Should.ThrowAsync<DomainException>(() => maintenance.RunAfterShellAsync(ct))).Code
            .ShouldBe(ErrorCode.BackupInvalid);
        connection.ExecuteScalar<long>("SELECT lot_id FROM lot_source_issue").ShouldBe(expired);
        TabularReport mismatches = await fixture.Reports.GetReportAsync(ReportKind.StockReconciliation,
            new(MismatchesOnly: true), ct);
        mismatches.Rows.Single().Values["Lot"].ShouldBe("L-OLD");
        ((long)mismatches.Rows.Single().Values["Difference"]!).ShouldBe(1000);
        mismatches.Summary.ShouldBe("Full check on 23-10-2026 found 1 lots that differ; 1 still differ.");
        (await fixture.Reports.GetReportAsync(ReportKind.StockReconciliation,
            new(ProductId: product + 1, MismatchesOnly: true), ct)).Rows.ShouldBeEmpty();
        string csv = Path.Combine(fixture.Root, "reconciliation.csv");
        (await fixture.Reports.ExportCsvAsync(ReportKind.StockReconciliation, new(MismatchesOnly: true), csv,
            cancellationToken: ct)).ShouldBe(1);
        string[] reconciliation = File.ReadAllLines(csv);
        reconciliation.Length.ShouldBe(3);
        reconciliation[0].ShouldContain("Differences only");

        // Once the lot agrees again, the recorded difference is no longer listed.
        connection.Execute("UPDATE product_lot SET qty_on_hand=qty_on_hand-1000 WHERE id=@expired", new { expired });
        (await fixture.Reports.GetReportAsync(ReportKind.StockReconciliation, new(MismatchesOnly: true), ct))
            .Summary.ShouldBe("Full check on 23-10-2026 found 1 lots that differ; 0 still differ.");
    }

    private static void SeedSaleAndFullReturn(SqliteConnection connection, string saleDate, string returnDate) =>
        connection.Execute("""
            INSERT INTO sale(invoice_no,sale_date,status,line_count,taxable_total,tax_total,round_off,grand_total,cost_total)
            VALUES ('S-' || @saleDate,@saleDate,'COMPLETED',1,1000000,50000,400,1050400,600000);
            INSERT INTO sale_return(return_no,sale_id,return_date,refund_mode,taxable_total,tax_total,refund_total,cost_total)
            VALUES ('R-' || @saleDate,last_insert_rowid(),@returnDate,'CASH',1000000,50000,1050400,600000);
            """, new { saleDate, returnDate });

    private static long SeedProduct(SqliteConnection connection)
    {
        long category = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Food') RETURNING id");
        long uom = connection.ExecuteScalar<long>("SELECT id FROM uom ORDER BY id LIMIT 1");
        return connection.ExecuteScalar<long>("""
            INSERT INTO product(sku,name,category_id,uom_id) VALUES ('RICE','Rice',@category,@uom) RETURNING id
            """, new { category, uom });
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(DateOnly today)
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-annual-{Guid.NewGuid():N}");
            Paths = new AppPaths(new SettingsBootstrap(Root));
            Factory = new SqliteConnectionFactory(Paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, Paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            Clock = new MutableClock(today);
            Periods = new PeriodGuard(Settings, Clock);
            MoneyOut = new MoneyOutService(Database, new LotQuantities(Settings), new DocNumberService(Settings), Periods,
                new AuditLog());
            Reports = new ReportService(Database, Settings, Clock);
            Closes = new AnnualCloseService(Database, Periods, Clock);
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public MutableClock Clock { get; }
        public PeriodGuard Periods { get; }
        public MoneyOutService MoneyOut { get; }
        public ReportService Reports { get; }
        public AnnualCloseService Closes { get; }

        public async Task RecordExpenseAsync(DateOnly date, decimal amount, CancellationToken cancellationToken)
        {
            long category = (await MoneyOut.GetExpenseCategoriesAsync(cancellationToken: cancellationToken))[0].Id;
            await MoneyOut.RecordExpenseAsync(new(date, category, new Money(amount), "CASH", null, null, null),
                OperationGuard.NewId(), cancellationToken);
        }

        public async Task CorrectSingleAsync(DateOnly date, decimal amount, CancellationToken cancellationToken)
        {
            ExpenseItem current = (await MoneyOut.GetExpensesAsync(date, date, cancellationToken)).Single();
            await MoneyOut.CorrectExpenseAsync(current, new(date, current.CategoryId, new Money(amount), "CASH", null, null, null),
                "Corrected amount", OperationGuard.NewId(), cancellationToken);
        }

        public async Task<byte[]> ExportAsync(PnlReport report, CancellationToken cancellationToken)
        {
            string path = Path.Combine(Root, $"pnl-{Guid.NewGuid():N}.csv");
            await Reports.ExportPnlCsvAsync(report, byMonth: true, path, cancellationToken);
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }

        public void Dispose()
        {
            Settings.Dispose();
            Database.Dispose();
            SqliteConnectionFactory.ClearPoolForDataRoot(Root);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class MutableClock(DateOnly date) : IClock
    {
        public DateOnly BusinessDate { get; set; } = date;
        public DateTime Now => BusinessDate.ToDateTime(new TimeOnly(12, 0));
    }
}
