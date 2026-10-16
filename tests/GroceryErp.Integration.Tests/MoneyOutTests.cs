using Dapper;
using GroceryErp.Assets;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Reports;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class MoneyOutTests
{
    [Fact]
    public async Task Damage_IsIdempotentReducesStockAndFlowsToPnl()
    {
        using var fixture = new Fixture();
        string operation = OperationGuard.NewId();
        var input = new DamageInput(fixture.LotId, fixture.Today, new Quantity(2), "BROKEN", new Money(10), "Handling");

        long id = await fixture.MoneyOut.RecordDamageAsync(input, operation, TestContext.Current.CancellationToken);
        long replay = await fixture.MoneyOut.RecordDamageAsync(input, operation, TestContext.Current.CancellationToken);

        replay.ShouldBe(id);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId }).ShouldBe(8000L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM damage").ShouldBe(1L);
        connection.ExecuteScalar<long>("SELECT amount FROM v_pnl_entry WHERE head_code='DAMAGES'").ShouldBe(90000L);
    }

    [Fact]
    public async Task PostedDamageCorrection_RestoresStockAndBooksOnlyDatedDifference()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long damageId = await fixture.MoneyOut.RecordDamageAsync(
            new(fixture.LotId, fixture.Today, new Quantity(2), "BROKEN", new Money(10), "Original"),
            OperationGuard.NewId(), ct);
        DamageItem original = (await fixture.MoneyOut.GetDamagesAsync(fixture.Today, fixture.Today, ct)).Single();
        DateOnly correctionDate = fixture.Today.AddDays(1);
        string operation = OperationGuard.NewId();
        long correctionId = await fixture.MoneyOut.CorrectDamageAsync(original,
            new(new Quantity(1), new Money(5), "SPOILED", null), correctionDate,
            "Counted one twice", operation, ct);
        (await fixture.MoneyOut.CorrectDamageAsync(original,
            new(new Quantity(1), new Money(5), "SPOILED", null), correctionDate,
            "Counted one twice", operation, ct)).ShouldBe(correctionId);

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT qty FROM damage WHERE id=@damageId", new { damageId }).ShouldBe(2000L);
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId }).ShouldBe(9000L);
        connection.ExecuteScalar<long>("SELECT amount FROM v_pnl_entry WHERE head_code='DAMAGES' AND entry_date=@date",
            new { date = correctionDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }).ShouldBe(-45000L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM v_pnl_entry WHERE head_code='STOCK_ADJUSTMENT'").ShouldBe(0L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM damage_correction WHERE damage_id=@damageId", new { damageId }).ShouldBe(1L);
        var reports = new ReportService(fixture.Database, fixture.Settings, new FixedClock(fixture.Today));
        (await reports.GetPnlAsync(correctionDate, correctionDate, ct)).TotalExpenses.ShouldBe(new Money(-45));
        DamageItem corrected = (await fixture.MoneyOut.GetDamagesAsync(fixture.Today, fixture.Today, ct)).Single();
        corrected.Quantity.ShouldBe(new Quantity(1));
        corrected.Notes.ShouldBeNull();
        await Should.ThrowAsync<DomainException>(() => fixture.MoneyOut.CorrectDamageAsync(original,
            new(new Quantity(3), Money.Zero, "BROKEN", null), correctionDate,
            "Stale", OperationGuard.NewId(), ct));
    }

    [Fact]
    public async Task BulkExpiryWriteOff_IsAtomicAndReplaySafe()
    {
        using var fixture = new Fixture(expiry: new DateOnly(2026, 8, 1));
        string operation = OperationGuard.NewId();

        IReadOnlyList<long> first = await fixture.MoneyOut.WriteOffExpiredAsync(
            [fixture.LotId], fixture.Today, operation, TestContext.Current.CancellationToken);
        IReadOnlyList<long> replay = await fixture.MoneyOut.WriteOffExpiredAsync(
            [fixture.LotId], fixture.Today, operation, TestContext.Current.CancellationToken);

        replay.ShouldBe(first);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId }).ShouldBe(0L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM damage WHERE reason='EXPIRED'").ShouldBe(1L);
    }

    [Fact]
    public async Task ExpiryArea_IncludesLocalNameAndExcludesBlockedLots()
    {
        using var fixture = new Fixture(expiry: new DateOnly(2026, 8, 1));

        IReadOnlyList<ExpiryLot> visible = await fixture.MoneyOut.GetExpiryAsync("EXPIRED", TestContext.Current.CancellationToken);
        visible.Single().ProductNameLocal.ShouldBe("પરીક્ષણ ઉત્પાદન");

        using (var connection = fixture.Factory.Open())
            connection.Execute("UPDATE product_lot SET is_active=0 WHERE id=@id", new { id = fixture.LotId });

        (await fixture.MoneyOut.GetExpiryAsync("EXPIRED", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ActionableExpiryCount_MatchesAlertViewAcrossThresholdChanges()
    {
        using var fixture = new Fixture();
        string soon = DateOnly.FromDateTime(DateTime.Now).AddDays(3)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        using var connection = fixture.Factory.Open();
        connection.Execute("UPDATE product_lot SET expiry_date=@soon WHERE id=@id", new { soon, id = fixture.LotId });
        long productId = connection.ExecuteScalar<long>("SELECT product_id FROM product_lot WHERE id=@id", new { id = fixture.LotId });
        async Task CheckAsync() => (await fixture.MoneyOut.GetActionableExpiryCountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM v_expiry_alert WHERE bucket IN ('EXPIRED','CRITICAL')"));
        await CheckAsync();
        connection.Execute("UPDATE product SET expiry_alert_days=2 WHERE id=@productId", new { productId });
        await CheckAsync();
        connection.Execute("UPDATE product SET expiry_alert_days=7 WHERE id=@productId", new { productId });
        await CheckAsync();
        connection.Execute("UPDATE product_lot SET is_active=0 WHERE id=@id", new { id = fixture.LotId });
        await CheckAsync();
    }

    [Fact]
    public async Task ExpenseAndSalary_AreChargedToCorrectPnlDates()
    {
        using var fixture = new Fixture();
        long category = (await fixture.MoneyOut.GetExpenseCategoriesAsync(cancellationToken: TestContext.Current.CancellationToken))[0].Id;
        await fixture.MoneyOut.RecordExpenseAsync(
            new(fixture.Today, category, new Money(500), "UPI", "Power Co", "TX-1", null),
            OperationGuard.NewId(), TestContext.Current.CancellationToken);
        long employee = await fixture.MoneyOut.SaveEmployeeAsync(null,
            new("Asha", "9999999999", "Cashier", new DateOnly(2026, 1, 1), null, new Money(12000), true, null),
            TestContext.Current.CancellationToken);
        await fixture.MoneyOut.PostSalaryRunAsync(new(
            new YearMonth(2026, 8), fixture.Today, "BANK",
            [new(employee, new Money(12000), new Money(1000), new Money(500), new Money(250), "August")]),
            OperationGuard.NewId(), TestContext.Current.CancellationToken);
        await fixture.MoneyOut.RecordOtherSalaryPaymentAsync(employee, fixture.Today, new Money(100), "CASH", "Advance",
            OperationGuard.NewId(), TestContext.Current.CancellationToken);

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM salary_payment").ShouldBe(11850000L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM salary_payment WHERE kind='OTHER'").ShouldBe(1L);
        connection.ExecuteScalar<string>("SELECT entry_date FROM v_pnl_entry WHERE head_code='SALARIES' ORDER BY amount DESC LIMIT 1")
            .ShouldBe("2026-08-31");
        connection.ExecuteScalar<long>("SELECT amount FROM v_pnl_entry WHERE head_code='EXPENSE_CATEGORY'").ShouldBe(500000L);
    }

    [Fact]
    public async Task PostedSalaryCorrection_PreservesOriginalPostingAndAdjustsPnl()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long employee = await fixture.MoneyOut.SaveEmployeeAsync(null,
            new("Asha", null, null, null, null, new Money(1000), true, null), ct);
        await fixture.MoneyOut.RecordOtherSalaryPaymentAsync(employee, fixture.Today, new Money(1000),
            "BANK", "Advance", OperationGuard.NewId(), ct);
        YearMonth month = YearMonth.From(fixture.Today);
        SalaryPaymentItem original = (await fixture.MoneyOut.GetSalaryRegisterAsync(month, month, ct)).Single();
        DateOnly correctionDate = fixture.Today.AddDays(1);
        long correctionId = await fixture.MoneyOut.CorrectSalaryPaymentAsync(original,
            new(fixture.Today.AddDays(2), new Money(900), "CASH", null), correctionDate,
            "Bank transfer was returned", OperationGuard.NewId(), ct);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT amount FROM salary_payment WHERE id=@id", new { id = original.Id }).ShouldBe(1_000_000L);
        connection.ExecuteScalar<long>("SELECT amount_delta FROM salary_correction WHERE id=@correctionId", new { correctionId }).ShouldBe(-100_000L);
        connection.ExecuteScalar<long>("SELECT amount FROM v_pnl_entry WHERE head_code='SALARIES' AND entry_date=@date",
            new { date = correctionDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }).ShouldBe(-100_000L);
        var reports = new ReportService(fixture.Database, fixture.Settings, new FixedClock(fixture.Today));
        (await reports.GetPnlAsync(correctionDate, correctionDate, ct)).TotalExpenses.ShouldBe(new Money(-100));
        SalaryPaymentItem corrected = (await fixture.MoneyOut.GetSalaryRegisterAsync(month, month, ct)).Single();
        corrected.Amount.ShouldBe(new Money(900));
        corrected.Mode.ShouldBe("CASH");
        corrected.PayDate.ShouldBe(fixture.Today.AddDays(2));
        corrected.Notes.ShouldBeNull();
        await Should.ThrowAsync<DomainException>(() => fixture.MoneyOut.CorrectSalaryPaymentAsync(original,
            new(fixture.Today, new Money(800), "BANK", null), correctionDate,
            "Stale", OperationGuard.NewId(), ct));
    }

    [Fact]
    public async Task PostedExpenseCorrection_UpdatesReportsAndRecordsBeforeAfterAudit()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long category = (await fixture.MoneyOut.GetExpenseCategoriesAsync(cancellationToken: ct))[0].Id;
        await fixture.MoneyOut.RecordExpenseAsync(
            new(fixture.Today, category, new Money(500), "UPI", "Power Co", "OLD", null),
            OperationGuard.NewId(), ct);
        ExpenseItem original = (await fixture.MoneyOut.GetExpensesAsync(fixture.Today, fixture.Today, ct)).Single();
        await fixture.MoneyOut.CorrectExpenseAsync(original,
            new(fixture.Today, category, new Money(450), "BANK", "Power Co", "FIXED", "Invoice correction"),
            "Wrong amount and payment mode", ct);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT amount FROM v_pnl_entry WHERE head_code='EXPENSE_CATEGORY'").ShouldBe(450_000L);
        connection.ExecuteScalar<string>("SELECT changes FROM audit_log WHERE entity='expense' AND entity_id=@id AND action='CORRECT'",
            new { id = original.Id })!.ShouldContain("Wrong amount and payment mode");
        await Should.ThrowAsync<DomainException>(() => fixture.MoneyOut.CorrectExpenseAsync(original,
            new(fixture.Today, category, new Money(400), "BANK", null, null, null), "Stale", ct));
    }

    [Fact]
    public async Task DepreciationPostingAndDisposal_AreExactAndIdempotent()
    {
        using var fixture = new Fixture();
        long asset = await fixture.Assets.SaveAssetAsync(null, new(
            "Visi-cooler", "Equipment", new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1),
            new Money(45000), null, DepreciationMethod.Wdv, new Percent(15), Money.Zero, null),
            TestContext.Current.CancellationToken);

        DepreciationPostResult posted = await fixture.Assets.PostThroughAsync(new YearMonth(2027, 3), TestContext.Current.CancellationToken);
        DepreciationPostResult replay = await fixture.Assets.PostThroughAsync(new YearMonth(2027, 3), TestContext.Current.CancellationToken);

        posted.PostedCount.ShouldBe(12);
        replay.PostedCount.ShouldBe(0);
        IReadOnlyList<DepreciationEntry> schedule = await fixture.Assets.GetScheduleAsync(asset, TestContext.Current.CancellationToken);
        schedule.Sum(static entry => entry.Amount.Scaled).ShouldBe(6750000L);
        schedule[^1].ClosingValue.ShouldBe(new Money(38250));

        await fixture.Assets.DisposeAsync(asset, new DateOnly(2027, 4, 15), new Money(40000),
            OperationGuard.NewId(), TestContext.Current.CancellationToken);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM asset_depreciation WHERE asset_id=@asset", new { asset }).ShouldBe(13L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM v_pnl_entry WHERE head_code='ASSET_DISPOSAL' AND ref_id=@asset", new { asset }).ShouldBe(1L);
    }

    [Fact]
    public async Task PostedAsset_AllowsDescriptionEditsButKeepsFinancialHistoryLocked()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var original = new AssetInput("Freezer", "Equipment", new DateOnly(2026, 4, 1),
            new DateOnly(2026, 4, 1), new Money(45000), null,
            DepreciationMethod.Wdv, new Percent(15), Money.Zero, null);
        long id = await fixture.Assets.SaveAssetAsync(null, original, ct);
        await fixture.Assets.PostThroughAsync(new YearMonth(2026, 4), ct);
        var revised = original with { Name = "Main freezer", Category = "Electronics", Notes = "Back room" };

        await fixture.Assets.SaveAssetAsync(id, revised, ct);
        AssetItem asset = (await fixture.Assets.GetAssetsAsync(cancellationToken: ct)).Single(item => item.Id == id);
        asset.Name.ShouldBe("Main freezer");
        asset.Category.ShouldBe("Electronics");
        asset.Notes.ShouldBe("Back room");
        (await fixture.Assets.GetScheduleAsync(id, ct)).Count.ShouldBe(1);

        await Should.ThrowAsync<DomainException>(() => fixture.Assets.SaveAssetAsync(id,
            revised with { Cost = new Money(46000) }, ct));
        (await fixture.Assets.GetAssetsAsync(cancellationToken: ct)).Single(item => item.Id == id)
            .Cost.ShouldBe(new Money(45000));
    }

    [Fact]
    public async Task Disposal_RejectsMonthWithAlreadyPostedDepreciation()
    {
        using var fixture = new Fixture();
        long asset = await fixture.Assets.SaveAssetAsync(null, new(
            "Freezer", "Equipment", new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1),
            new Money(45000), null, DepreciationMethod.Wdv, new Percent(15), Money.Zero, null),
            TestContext.Current.CancellationToken);
        await fixture.Assets.PostThroughAsync(new YearMonth(2026, 8), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<GroceryErp.Core.Errors.DomainException>(() => fixture.Assets.DisposeAsync(
            asset, new DateOnly(2026, 8, 15), new Money(30000), OperationGuard.NewId(),
            TestContext.Current.CancellationToken));

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<string?>("SELECT disposed_date FROM asset WHERE id=@asset", new { asset }).ShouldBeNull();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM asset_depreciation WHERE asset_id=@asset", new { asset }).ShouldBe(5L);
    }

    [Fact]
    public async Task BonusOnlySalaryRun_IsNotPreparedAgain()
    {
        using var fixture = new Fixture();
        long employee = await fixture.MoneyOut.SaveEmployeeAsync(null,
            new("Asha", null, null, new DateOnly(2026, 1, 1), null, new Money(1000), true, null),
            TestContext.Current.CancellationToken);
        YearMonth period = new(2026, 8);
        await fixture.MoneyOut.PostSalaryRunAsync(new(period, fixture.Today, "BANK",
            [new(employee, new Money(1000), new Money(1000), Money.Zero, new Money(100), null)]),
            OperationGuard.NewId(), TestContext.Current.CancellationToken);

        (await fixture.MoneyOut.PrepareSalaryRunAsync(period, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        await Should.ThrowAsync<GroceryErp.Core.Errors.DomainException>(() => fixture.MoneyOut.PostSalaryRunAsync(
            new(period, fixture.Today, "BANK", [new(employee, new Money(1000), Money.Zero, Money.Zero, Money.Zero, null)]),
            OperationGuard.NewId(), TestContext.Current.CancellationToken));
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM salary_payment WHERE employee_id=@employee", new { employee }).ShouldBe(1L);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(DateOnly? expiry = null)
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-money-out-{Guid.NewGuid():N}");
            var paths = new AppPaths(new SettingsBootstrap(Root));
            Factory = new SqliteConnectionFactory(paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            Today = new DateOnly(2026, 9, 27);
            var ledger = new StockLedger(Settings);
            MoneyOut = new MoneyOutService(Database, ledger, new DocNumberService(Settings), new PeriodGuard(Settings), new AuditLog());
            Assets = new AssetService(Database, Settings, new PeriodGuard(Settings), new AuditLog(), new FixedClock(Today));

            using var connection = Factory.Open();
            long category = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Food') RETURNING id");
            long unit = connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
            long tax = connection.ExecuteScalar<long>("SELECT id FROM tax_rate LIMIT 1");
            long product = connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,name_local,category_id,uom_id,tax_rate_id,track_expiry,default_selling_price)
                VALUES ('MONEY-1','Test product','પરીક્ષણ ઉત્પાદન',@category,@unit,@tax,1,150000) RETURNING id
                """, new { category, unit, tax });
            LotId = connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,unit_cost,selling_price,qty_received)
                VALUES (@product,'L-MONEY','2026-04-01',@expiry,50000,150000,10000) RETURNING id
                """, new { product, expiry = (expiry ?? new DateOnly(2027, 9, 1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
            connection.Execute("""
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-04-01',@product,@lot,'OPENING',10000,50000)
                """, new { product, lot = LotId });
        }

        public string Root { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public MoneyOutService MoneyOut { get; }
        public AssetService Assets { get; }
        public DateOnly Today { get; }
        public long LotId { get; }

        public void Dispose()
        {
            Database.Dispose();
            SqliteConnectionFactory.ClearPoolForDataRoot(Root);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class FixedClock(DateOnly date) : IClock
    {
        public DateTime Now => date.ToDateTime(new TimeOnly(12, 0));
        public DateOnly BusinessDate => date;
    }
}
