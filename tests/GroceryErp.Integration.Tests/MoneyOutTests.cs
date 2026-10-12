using Dapper;
using GroceryErp.Assets;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
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
