using Dapper;
using GroceryErp.Assets;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
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
    public async Task DamageAndCorrection_KeepTheLotReconciledWithoutMovements()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long damageId = await fixture.MoneyOut.RecordDamageAsync(
            new(fixture.LotId, fixture.Today, new Quantity(2), "BROKEN", Money.Zero, "Handling"),
            OperationGuard.NewId(), ct);
        DamageItem original = (await fixture.MoneyOut.GetDamagesAsync(fixture.Today,
            fixture.Today, ct)).Single();
        await fixture.MoneyOut.CorrectDamageAsync(original,
            new(Quantity.One, Money.Zero, "BROKEN", null), "Recount", OperationGuard.NewId(), ct);
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id",
                new { id = fixture.LotId }).ShouldBe(9000L);
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM damage WHERE id=@damageId",
                new { damageId }).ShouldBe(1L);
            db.ExecuteScalar<string>("SELECT source_kind FROM stock_correction WHERE lot_id=@id",
                new { id = fixture.LotId }).ShouldBe("DAMAGE_CORRECTION");
        }
        await fixture.Database.WriteAsync(tx => LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction)
            .ToArray().ShouldBeEmpty(), ct);
    }

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
    public async Task OpenPeriodDamageCorrection_RestatesOriginalDateAndRestoresStock()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long damageId = await fixture.MoneyOut.RecordDamageAsync(
            new(fixture.LotId, fixture.Today, new Quantity(2), "BROKEN", new Money(10), "Original"),
            OperationGuard.NewId(), ct);
        DamageItem original = (await fixture.MoneyOut.GetDamagesAsync(fixture.Today, fixture.Today, ct)).Single();
        (await fixture.MoneyOut.PreviewCorrectionAsync(original.Date, ct)).Treatment.ShouldBe(PeriodTreatment.OpenPeriod);
        string operation = OperationGuard.NewId();
        long correctionId = await fixture.MoneyOut.CorrectDamageAsync(original,
            new(new Quantity(1), new Money(5), "SPOILED", null), "Counted one twice", operation, ct);
        (await fixture.MoneyOut.CorrectDamageAsync(original,
            new(new Quantity(1), new Money(5), "SPOILED", null), "Counted one twice", operation, ct))
            .ShouldBe(correctionId);

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT qty FROM damage WHERE id=@damageId", new { damageId }).ShouldBe(2000L);
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId }).ShouldBe(9000L);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='DAMAGES' AND entry_date=@date",
            new { date = Iso(fixture.Today) }).ShouldBe(45000L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM v_pnl_entry WHERE head_code='DAMAGES' AND entry_date<>@date",
            new { date = Iso(fixture.Today) }).ShouldBe(0L);
        connection.ExecuteScalar<string>("SELECT treatment FROM damage_correction WHERE id=@correctionId",
            new { correctionId }).ShouldBe("OPEN_PERIOD");
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM v_pnl_entry WHERE head_code='STOCK_ADJUSTMENT'").ShouldBe(0L);
        var reports = new ReportService(fixture.Database, fixture.Settings, fixture.Clock);
        (await reports.GetPnlAsync(fixture.Today, fixture.Today, ct)).TotalExpenses.ShouldBe(new Money(45));
        DamageItem corrected = (await fixture.MoneyOut.GetDamagesAsync(fixture.Today, fixture.Today, ct)).Single();
        corrected.Quantity.ShouldBe(new Quantity(1));
        corrected.Notes.ShouldBeNull();
        corrected.Status.ShouldBe("CORRECTED");
        (await fixture.MoneyOut.GetDamageHistoryAsync(damageId, ct)).Select(static row => row.Kind)
            .ShouldBe(["ORIGINAL", "EDIT"]);
        (await Should.ThrowAsync<DomainException>(() => fixture.MoneyOut.CorrectDamageAsync(original,
            new(new Quantity(3), Money.Zero, "BROKEN", null), "Stale", OperationGuard.NewId(), ct)))
            .Code.ShouldBe(ErrorCode.EntryChanged);

        await fixture.MoneyOut.VoidDamageAsync(corrected, "Not damaged", OperationGuard.NewId(), ct);
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId }).ShouldBe(10000L);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='DAMAGES'").ShouldBe(0L);
        (await fixture.MoneyOut.GetDamagesAsync(fixture.Today, fixture.Today, ct)).Single().Status.ShouldBe("VOID");
        await fixture.Database.WriteAsync(tx => LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction).ToArray().ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task DamageCorrectionAfterFiscalLock_IsDatedTodayAndLeavesFinalizedYear()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var damageDate = new DateOnly(2026, 3, 15);
        fixture.Clock.BusinessDate = new DateOnly(2026, 3, 20);
        long damageId = await fixture.MoneyOut.RecordDamageAsync(
            new(fixture.LotId, damageDate, new Quantity(2), "BROKEN", Money.Zero, null), OperationGuard.NewId(), ct);

        fixture.Clock.BusinessDate = fixture.Today;
        var closes = new AnnualCloseService(fixture.Database, fixture.Periods, fixture.Clock);
        (await closes.EnsureFinalizedAsync(ct)).ShouldBe(1);
        var reports = new ReportService(fixture.Database, fixture.Settings, fixture.Clock);
        PnlReport frozen = await reports.GetPnlReportAsync(reports.FiscalYearPeriod(2025), ct);
        frozen.Status.ShouldBe("FINALIZED");
        frozen.Statement.TotalExpenses.ShouldBe(new Money(100));

        DamageItem original = (await fixture.MoneyOut.GetDamagesAsync(damageDate, damageDate, ct)).Single();
        CorrectionDating dating = await fixture.MoneyOut.PreviewCorrectionAsync(damageDate, ct);
        dating.Treatment.ShouldBe(PeriodTreatment.CurrentDated);
        dating.FiscalLocked.ShouldBeTrue();
        dating.CalendarLocked.ShouldBeFalse();
        dating.EffectiveDate.ShouldBe(fixture.Today);
        await fixture.MoneyOut.CorrectDamageAsync(original, new(Quantity.One, Money.Zero, "BROKEN", null),
            "Recount", OperationGuard.NewId(), ct);

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId }).ShouldBe(9000L);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='DAMAGES' AND entry_date=@date",
            new { date = Iso(damageDate) }).ShouldBe(100_000L);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='DAMAGES' AND entry_date=@date",
            new { date = Iso(fixture.Today) }).ShouldBe(-50_000L);
        connection.ExecuteScalar<string>("SELECT treatment FROM damage_correction WHERE damage_id=@damageId",
            new { damageId }).ShouldBe("CURRENT_DATED");
        PnlReport after = await reports.GetPnlReportAsync(reports.FiscalYearPeriod(2025), ct);
        after.Checksum.ShouldBe(frozen.Checksum);
        after.Statement.TotalExpenses.ShouldBe(new Money(100));
        PnlReport calendar = await reports.GetPnlReportAsync(reports.CalendarYearPeriod(2026), ct);
        calendar.Status.ShouldBe("OPEN");
        calendar.Statement.TotalExpenses.ShouldBe(new Money(50));
        (await reports.GetPnlReportAsync(reports.FiscalYearPeriod(2026), ct)).Statement.TotalExpenses
            .ShouldBe(new Money(-50));
    }

    [Fact]
    public async Task DamageCorrection_RecreatesDeletedZeroLotFromSnapshot()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.MoneyOut.RecordDamageAsync(
            new(fixture.LotId, fixture.Today, new Quantity(10), "SPOILED", Money.Zero, null), OperationGuard.NewId(), ct);
        using var connection = fixture.Factory.Open();
        long product = connection.ExecuteScalar<long>("SELECT product_id FROM product_lot WHERE id=@id", new { id = fixture.LotId });
        connection.Execute("DELETE FROM product_lot WHERE id=@id", new { id = fixture.LotId });
        DamageItem original = (await fixture.MoneyOut.GetDamagesAsync(fixture.Today, fixture.Today, ct)).Single();

        await fixture.MoneyOut.CorrectDamageAsync(original, new(new Quantity(6), Money.Zero, "SPOILED", null),
            "Four were fine", OperationGuard.NewId(), ct);

        long restored = connection.ExecuteScalar<long>("SELECT restored_lot_id FROM damage_correction");
        restored.ShouldNotBe(fixture.LotId);
        connection.QuerySingle<(long ProductId, long OnHand, long Cost, string Origin)>("""
            SELECT product_id AS ProductId,qty_on_hand AS OnHand,unit_cost AS Cost,origin_kind AS Origin
            FROM product_lot WHERE id=@restored
            """, new { restored }).ShouldBe((product, 4000L, 50000L, "DIRECT"));
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='DAMAGES'").ShouldBe(300_000L);
        await fixture.Database.WriteAsync(tx => LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction).ToArray().ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task DamageRestore_OfADeletedProduct_GoesIntoTheChosenProduct_AndLaterCorrectionsFollowIt()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.MoneyOut.RecordDamageAsync(
            new(fixture.LotId, fixture.Today, new Quantity(10), "SPOILED", Money.Zero, null), OperationGuard.NewId(), ct);
        using var connection = fixture.Factory.Open();
        long original = connection.ExecuteScalar<long>("SELECT product_id FROM product_lot WHERE id=@id", new { id = fixture.LotId });
        connection.Execute("DELETE FROM product_lot WHERE id=@id", new { id = fixture.LotId });
        connection.Execute("DELETE FROM product WHERE id=@original", new { original });
        long Product(string sku, int active, string productClass) => connection.ExecuteScalar<long>("""
            INSERT INTO product(sku,name,category_id,uom_id,tax_rate_id,track_expiry,default_selling_price,default_mrp,
                is_active,product_class)
            SELECT @sku,'Test product',(SELECT MIN(id) FROM category),(SELECT id FROM uom WHERE code='PCS'),
                (SELECT MIN(id) FROM tax_rate),1,160000,170000,@active,@productClass
            RETURNING id
            """, new { sku, active, productClass });
        long readded = Product("MONEY-2", 1, "SALEABLE");
        long archived = Product("MONEY-OLD", 0, "SALEABLE");
        long internalUse = Product("MONEY-RAW", 1, "INTERNAL");
        DamageItem damage = (await fixture.MoneyOut.GetDamagesAsync(fixture.Today, fixture.Today, ct)).Single();

        RestoreDraft draft = (await fixture.MoneyOut.GetDamageRestoreDraftAsync(damage.Id, new Quantity(4), ct))!;
        draft.DocumentNumber.ShouldBe("D2627-000001");
        draft.ProductName.ShouldBe("Test product");
        draft.SuggestedSku.ShouldBe("MONEY-1");
        draft.UnitCode.ShouldBe("PCS");
        draft.CategoryId.ShouldBeNull();
        draft.ExpiryDate.ShouldBe(new DateOnly(2027, 9, 1));
        draft.Quantity.ShouldBe(new Quantity(4));
        draft.UnitCost.ShouldBe(new Money(50));
        Task<long> Correct(decimal quantity, RestoreProductChoice? restore) => fixture.MoneyOut.CorrectDamageAsync(damage,
            new(new Quantity(quantity), Money.Zero, "SPOILED", null) { Restore = restore }, "Some were fine",
            OperationGuard.NewId(), ct);
        RestoreProductChoice Into(long productId) => new(productId, null, new Money(155), new Money(170));
        (await Should.ThrowAsync<DomainException>(() => Correct(6, null))).Code.ShouldBe(ErrorCode.DamageProductDeleted);
        (await Should.ThrowAsync<DomainException>(() => Correct(6, Into(archived)))).Code
            .ShouldBe(ErrorCode.RestoreProductUnavailable);
        (await Should.ThrowAsync<DomainException>(() => Correct(6, Into(internalUse)))).Code
            .ShouldBe(ErrorCode.RestoreProductTypeMismatch);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM damage_correction").ShouldBe(0L);

        await Correct(6, Into(readded));

        long restored = connection.ExecuteScalar<long>("SELECT restored_lot_id FROM damage_correction");
        connection.QuerySingle<(long Product, long OnHand, long Cost, long Price, long Mrp, string Expiry, string Origin)>("""
            SELECT product_id,qty_on_hand,unit_cost,selling_price,mrp,expiry_date,origin_kind FROM product_lot WHERE id=@restored
            """, new { restored }).ShouldBe((readded, 4000L, 50000L, 155000L, 170000L, "2027-09-01", "DIRECT"));
        connection.ExecuteScalar<string>("SELECT reason FROM direct_stock_origin WHERE lot_id=@restored", new { restored })
            .ShouldBe("Damage D2627-000001 correction restore: Some were fine");
        connection.ExecuteScalar<string>("SELECT changes FROM audit_log WHERE entity='damage' AND action='CORRECT'")!
            .ShouldContain($"\"restoredProduct\":{readded}");
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='DAMAGES'").ShouldBe(300_000L);
        (await fixture.MoneyOut.GetDamageRestoreDraftAsync(damage.Id, Quantity.One, ct)).ShouldBeNull();

        // The next correction finds the units in the restored lot of the chosen product.
        damage = (await fixture.MoneyOut.GetDamagesAsync(fixture.Today, fixture.Today, ct)).Single();
        await Correct(4, null);
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@restored", new { restored })
            .ShouldBe(6000L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot").ShouldBe(1L);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='DAMAGES'").ShouldBe(200_000L);
        await fixture.Database.WriteAsync(tx => LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction).ToArray().ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task InternalUseDamage_HasNoPnlHead()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var connection = fixture.Factory.Open();
        connection.Execute("UPDATE product SET product_class='INTERNAL'");
        await fixture.MoneyOut.RecordDamageAsync(
            new(fixture.LotId, fixture.Today, new Quantity(2), "SPOILED", Money.Zero, null), OperationGuard.NewId(), ct);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM damage").ShouldBe(1L);
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId }).ShouldBe(8000L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM v_pnl_entry WHERE head_code='DAMAGES'").ShouldBe(0L);
    }

    [Fact]
    public async Task BulkExpiryWriteOff_PreviewsByProductAndReplaysBatches()
    {
        using var fixture = new Fixture(expiry: new DateOnly(2026, 8, 1));
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var connection = fixture.Factory.Open();
        long product = connection.ExecuteScalar<long>("SELECT product_id FROM product_lot WHERE id=@id", new { id = fixture.LotId });

        IReadOnlyList<ExpiredWriteOffLot> preview = await fixture.MoneyOut.PreviewExpiredWriteOffAsync([product], fixture.Today, ct);
        preview.Select(static lot => lot.LotId).ShouldBe([fixture.LotId]);
        preview[0].Quantity.ShouldBe(new Quantity(10));
        string batch = BulkBatch.OperationId(OperationGuard.NewId(), 0);
        IReadOnlyList<BulkItemResult> first = await fixture.MoneyOut.WriteOffExpiredBatchAsync([fixture.LotId], fixture.Today, batch, ct);
        IReadOnlyList<BulkItemResult> replay = await fixture.MoneyOut.WriteOffExpiredBatchAsync([fixture.LotId], fixture.Today, batch, ct);
        IReadOnlyList<BulkItemResult> again = await fixture.MoneyOut.WriteOffExpiredBatchAsync([fixture.LotId], fixture.Today,
            BulkBatch.OperationId(OperationGuard.NewId(), 0), ct);

        first.Single().Outcome.ShouldBe(BulkItemOutcome.Changed);
        replay.Single().Outcome.ShouldBe(BulkItemOutcome.AlreadyDone);
        again.Single().Outcome.ShouldBe(BulkItemOutcome.NotEligible);
        (await fixture.MoneyOut.PreviewExpiredWriteOffAsync([product], fixture.Today, ct)).ShouldBeEmpty();
        connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.LotId }).ShouldBe(0L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM damage WHERE reason='EXPIRED'").ShouldBe(1L);
    }

    [Fact]
    public async Task SelectedLotDamage_RechecksEveryQuantityAndCommitsAtomically()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var connection = fixture.Factory.Open();
        var first = connection.QuerySingle<(long Quantity, long Version)>("""
            SELECT qty_on_hand AS Quantity,detail_version AS Version FROM product_lot WHERE id=@id
            """, new { id = fixture.LotId });
        long product = connection.ExecuteScalar<long>("SELECT product_id FROM product_lot WHERE id=@id",
            new { id = fixture.LotId });
        long secondId = connection.ExecuteScalar<long>("""
            INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,unit_cost,selling_price,qty_received)
            VALUES (@product,'L-MONEY-2','2026-04-01','2027-09-01',50000,150000,5000) RETURNING id
            """, new { product });
        connection.Execute("UPDATE product_lot SET qty_on_hand=5000 WHERE id=@secondId", new { secondId });
        long secondVersion = connection.ExecuteScalar<long>(
            "SELECT detail_version FROM product_lot WHERE id=@secondId", new { secondId });
        BulkDamageInput[] lines =
        [
            new(fixture.LotId, first.Quantity, first.Version, new Quantity(2)),
            new(secondId, 5_000, secondVersion, new Quantity(1)),
        ];
        BulkDamageInput[] invalid = [lines[0], lines[1] with { ExpectedOnHandScaled = 4_000 }];
        await Should.ThrowAsync<DomainException>(() => fixture.MoneyOut.RecordBulkDamageAsync(
            invalid, fixture.Today, "BROKEN", OperationGuard.NewId(), ct));
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM damage").ShouldBe(0);
        string operation = OperationGuard.NewId();
        IReadOnlyList<long> posted = await fixture.MoneyOut.RecordBulkDamageAsync(
            lines, fixture.Today, "BROKEN", operation, ct);
        posted.Count.ShouldBe(2);
        (await fixture.MoneyOut.RecordBulkDamageAsync(lines, fixture.Today, "BROKEN", operation, ct))
            .ShouldBe(posted);
        connection.ExecuteScalar<long>("SELECT SUM(qty_on_hand) FROM product_lot WHERE product_id=@product",
            new { product }).ShouldBe(12_000);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM damage WHERE bulk_operation_id=@operation",
            new { operation }).ShouldBe(2);
    }

    [Fact]
    public void ExpiryAlertView_IncludesLocalNameAndExcludesBlockedLots()
    {
        using var fixture = new Fixture(expiry: new DateOnly(2026, 8, 1));
        using var connection = fixture.Factory.Open();
        // The Expiry report and the Products badge read this view.
        connection.ExecuteScalar<string>("SELECT name_local FROM v_expiry_alert WHERE bucket='EXPIRED'")
            .ShouldBe("પરીક્ષણ ઉત્પાદન");
        connection.Execute("UPDATE product_lot SET is_active=0 WHERE id=@id", new { id = fixture.LotId });
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM v_expiry_alert WHERE bucket='EXPIRED'").ShouldBe(0L);
    }

    [Fact]
    public async Task ActionableExpiryCount_MatchesAlertViewAcrossThresholdChanges()
    {
        using var fixture = new Fixture();
        string soon = Iso(fixture.Today.AddDays(3));
        using var connection = fixture.Factory.Open();
        connection.Execute("UPDATE product_lot SET expiry_date=@soon WHERE id=@id", new { soon, id = fixture.LotId });
        long productId = connection.ExecuteScalar<long>("SELECT product_id FROM product_lot WHERE id=@id", new { id = fixture.LotId });
        async Task CheckAsync() => (await fixture.MoneyOut.GetActionableExpiryCountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM ({ExpiryAlertAsOfSql}) WHERE bucket IN ('EXPIRED','CRITICAL')",
                new { asOf = Iso(fixture.Today) }));
        await CheckAsync();
        connection.Execute("UPDATE product SET expiry_alert_days=2 WHERE id=@productId", new { productId });
        await CheckAsync();
        connection.Execute("UPDATE product SET expiry_alert_days=7 WHERE id=@productId", new { productId });
        await CheckAsync();
        connection.Execute("UPDATE product_lot SET is_active=0 WHERE id=@id", new { id = fixture.LotId });
        await CheckAsync();
        // Blocked, the lot leaves Critical; once expired it counts again, since it still has to be
        // written off. An archived product's expiring lot does not count; its expired one does.
        connection.Execute("UPDATE product_lot SET expiry_date=@past WHERE id=@id", new { past = Iso(fixture.Today.AddDays(-1)), id = fixture.LotId });
        await CheckAsync();
        (await fixture.MoneyOut.GetActionableExpiryCountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        connection.Execute("UPDATE product_lot SET is_active=1,expiry_date=@soon WHERE id=@id", new { soon, id = fixture.LotId });
        connection.Execute("UPDATE product SET is_active=0 WHERE id=@productId", new { productId });
        await CheckAsync();
        (await fixture.MoneyOut.GetActionableExpiryCountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        connection.Execute("UPDATE product_lot SET expiry_date=@past WHERE id=@id", new { past = Iso(fixture.Today.AddDays(-1)), id = fixture.LotId });
        await CheckAsync();
        (await fixture.MoneyOut.GetActionableExpiryCountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
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
    public async Task PostedSalaryCorrection_PreservesOriginalPostingAndRestatesOpenMonth()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long employee = await fixture.MoneyOut.SaveEmployeeAsync(null,
            new("Asha", null, null, null, null, new Money(1000), true, null), ct);
        await fixture.MoneyOut.RecordOtherSalaryPaymentAsync(employee, fixture.Today, new Money(1000),
            "BANK", "Advance", OperationGuard.NewId(), ct);
        YearMonth month = YearMonth.From(fixture.Today);
        SalaryPaymentItem original = (await fixture.MoneyOut.GetSalaryRegisterAsync(month, month, ct)).Single();
        long correctionId = await fixture.MoneyOut.CorrectSalaryPaymentAsync(original,
            new(fixture.Today.AddDays(2), new Money(900), "CASH", null),
            "Bank transfer was returned", OperationGuard.NewId(), ct);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT amount FROM salary_payment WHERE id=@id", new { id = original.Id }).ShouldBe(1_000_000L);
        connection.QuerySingle<(long Delta, string Treatment, string Effective)>("""
            SELECT amount_delta AS Delta,treatment AS Treatment,effective_date AS Effective
            FROM salary_correction WHERE id=@correctionId
            """, new { correctionId }).ShouldBe((-100_000L, "OPEN_PERIOD", Iso(month.LastDay)));
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='SALARIES' AND entry_date=@date",
            new { date = Iso(month.LastDay) }).ShouldBe(900_000L);
        var reports = new ReportService(fixture.Database, fixture.Settings, fixture.Clock);
        (await reports.GetPnlAsync(month.FirstDay, month.LastDay, ct)).TotalExpenses.ShouldBe(new Money(900));
        SalaryPaymentItem corrected = (await fixture.MoneyOut.GetSalaryRegisterAsync(month, month, ct)).Single();
        corrected.Amount.ShouldBe(new Money(900));
        corrected.Mode.ShouldBe("CASH");
        corrected.PayDate.ShouldBe(fixture.Today.AddDays(2));
        corrected.Notes.ShouldBeNull();
        corrected.Status.ShouldBe("CORRECTED");
        await Should.ThrowAsync<DomainException>(() => fixture.MoneyOut.CorrectSalaryPaymentAsync(original,
            new(fixture.Today, new Money(800), "BANK", null), "Stale", OperationGuard.NewId(), ct));

        await fixture.MoneyOut.VoidSalaryPaymentAsync(corrected, "Paid by mistake", OperationGuard.NewId(), ct);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='SALARIES'").ShouldBe(0L);
        SalaryPaymentItem voided = (await fixture.MoneyOut.GetSalaryRegisterAsync(month, month, ct)).Single();
        voided.Status.ShouldBe("VOID");
        (await Should.ThrowAsync<DomainException>(() => fixture.MoneyOut.VoidSalaryPaymentAsync(voided,
            "Again", OperationGuard.NewId(), ct))).Code.ShouldBe(ErrorCode.EntryVoided);
        (await fixture.MoneyOut.GetSalaryHistoryAsync(original.Id, ct)).Select(static row => row.Kind)
            .ShouldBe(["ORIGINAL", "EDIT", "VOID"]);
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
        string operation = OperationGuard.NewId();
        var replacement = new ExpenseInput(fixture.Today, category, new Money(450), "BANK", "Power Co", "FIXED",
            "Invoice correction");
        long correctionId = await fixture.MoneyOut.CorrectExpenseAsync(original, replacement,
            "Wrong amount and payment mode", operation, ct);
        (await fixture.MoneyOut.CorrectExpenseAsync(original, replacement, "Wrong amount and payment mode",
            operation, ct)).ShouldBe(correctionId);
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT amount FROM expense WHERE id=@id", new { id = original.Id }).ShouldBe(500_000L);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='EXPENSE_CATEGORY' AND entry_date=@date",
            new { date = Iso(fixture.Today) }).ShouldBe(450_000L);
        connection.ExecuteScalar<string>("SELECT changes FROM audit_log WHERE entity='expense' AND entity_id=@id AND action='CORRECT'",
            new { id = original.Id })!.ShouldContain("Wrong amount and payment mode");
        await Should.ThrowAsync<DomainException>(() => fixture.MoneyOut.CorrectExpenseAsync(original,
            new(fixture.Today, category, new Money(400), "BANK", null, null, null), "Stale", OperationGuard.NewId(), ct));
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() =>
            connection.Execute("DELETE FROM expense WHERE id=@id", new { id = original.Id }));

        ExpenseItem corrected = (await fixture.MoneyOut.GetExpensesAsync(fixture.Today, fixture.Today, ct)).Single();
        corrected.Status.ShouldBe("CORRECTED");
        DateOnly moved = fixture.Today.AddDays(-3);
        await fixture.MoneyOut.CorrectExpenseAsync(corrected, replacement with { Date = moved },
            "Bill was dated earlier", OperationGuard.NewId(), ct);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='EXPENSE_CATEGORY' AND entry_date=@date",
            new { date = Iso(fixture.Today) }).ShouldBe(0L);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='EXPENSE_CATEGORY' AND entry_date=@date",
            new { date = Iso(moved) }).ShouldBe(450_000L);
        (await fixture.MoneyOut.GetExpensesAsync(fixture.Today, fixture.Today, ct)).ShouldBeEmpty();
        ExpenseItem movedItem = (await fixture.MoneyOut.GetExpensesAsync(moved, moved, ct)).Single();

        await fixture.MoneyOut.VoidExpenseAsync(movedItem, "Duplicate entry", OperationGuard.NewId(), ct);
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM v_pnl_entry WHERE head_code='EXPENSE_CATEGORY'").ShouldBe(0L);
        ExpenseItem voided = (await fixture.MoneyOut.GetExpensesAsync(moved, moved, ct)).Single();
        voided.Status.ShouldBe("VOID");
        (await Should.ThrowAsync<DomainException>(() => fixture.MoneyOut.VoidExpenseAsync(voided, "Again",
            OperationGuard.NewId(), ct))).Code.ShouldBe(ErrorCode.EntryVoided);
        (await fixture.MoneyOut.GetExpenseHistoryAsync(original.Id, ct)).Select(static row => row.Kind)
            .ShouldBe(["ORIGINAL", "EDIT", "EDIT", "VOID"]);
    }

    [Fact]
    public async Task SalaryTerms_AreEffectiveDatedAndLeavePostedPaymentsAlone()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long employee = await fixture.MoneyOut.SaveEmployeeAsync(null,
            new("Asha", null, null, new DateOnly(2026, 1, 1), null, new Money(1000), true, null), ct);
        var august = new YearMonth(2026, 8);
        await fixture.MoneyOut.PostSalaryRunAsync(new(august, fixture.Today, "BANK",
            [new(employee, new Money(1000), Money.Zero, Money.Zero, Money.Zero, null)]), OperationGuard.NewId(), ct);
        EmployeeItem saved = (await fixture.MoneyOut.GetEmployeesAsync(cancellationToken: ct)).Single();
        EmployeeInput raise = new(saved.Name, saved.Phone, saved.Role, saved.JoinDate, saved.LeaveDate,
            new Money(1200), true, saved.Notes);

        SalaryTermPreview preview = await fixture.MoneyOut.PreviewSalaryTermAsync(employee, new YearMonth(2026, 7), ct);
        preview.FirstOpenMonth.ShouldBe(new YearMonth(2026, 4));
        preview.PostedMonths.ShouldBe(1);
        (await Should.ThrowAsync<DomainException>(() => fixture.MoneyOut.SaveEmployeeAsync(employee, raise,
            new YearMonth(2026, 3), ct))).Code.ShouldBe(ErrorCode.EffectiveMonthClosed);
        await fixture.MoneyOut.SaveEmployeeAsync(employee, raise, new YearMonth(2026, 7), ct);

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT SUM(amount) FROM salary_payment").ShouldBe(1_000_000L);
        (await fixture.MoneyOut.PrepareSalaryRunAsync(new YearMonth(2026, 6), ct)).Single().BaseSalary.ShouldBe(new Money(1000));
        (await fixture.MoneyOut.PrepareSalaryRunAsync(new YearMonth(2026, 9), ct)).Single().BaseSalary.ShouldBe(new Money(1200));
        (await fixture.MoneyOut.PrepareSalaryRunAsync(august, ct)).ShouldBeEmpty();
        (await fixture.MoneyOut.GetSalaryTermsAsync(employee, ct)).Select(static term => term.EffectiveMonth)
            .ShouldBe([new YearMonth(2026, 7), new YearMonth(2026, 1)]);
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() => connection.Execute("DELETE FROM employee_salary_term"));
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
        long bookValue = connection.ExecuteScalar<long>(
            "SELECT closing_value FROM asset_depreciation WHERE asset_id=@asset ORDER BY period_month DESC LIMIT 1", new { asset });
        connection.ExecuteScalar<long>("SELECT disposal_book_value FROM asset WHERE id=@asset", new { asset }).ShouldBe(bookValue);
        connection.QuerySingle<(string Date, long Amount)>(
            "SELECT entry_date AS Date,amount AS Amount FROM v_pnl_entry WHERE head_code='ASSET_DISPOSAL'")
            .ShouldBe(("2027-04-15", 40_000_000L - bookValue));
    }

    [Fact]
    public async Task OpenAssetTerms_RecalculateWithoutChoiceAndFinalizedMonthsStayFixed()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var original = new AssetInput("Freezer", "Equipment", new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 1), new Money(45000), null,
            DepreciationMethod.Slm, new Percent(12), Money.Zero, null);
        fixture.Clock.BusinessDate = new DateOnly(2026, 2, 10);
        long id = await fixture.Assets.SaveAssetAsync(null, original, ct);
        (await fixture.Assets.PostDueDepreciationAsync(ct)).PostedCount.ShouldBe(1);

        // While every posted month is open, a cost correction rebuilds the schedule.
        await fixture.Assets.SaveAssetAsync(id, original with { Cost = new Money(48000) }, ct);
        (await fixture.Assets.GetScheduleAsync(id, ct)).Single().Amount.ShouldBe(new Money(48000m * 0.12m * 31 / 365));

        // Nothing posted in February or March before FY 2025-26 locked: those months are
        // still charged, in the open period of the posting date.
        fixture.Clock.BusinessDate = fixture.Today;
        DepreciationPostResult catchUp = await fixture.Assets.PostDueDepreciationAsync(ct);
        catchUp.PostedCount.ShouldBe(7);
        catchUp.CurrentDatedPeriods.ShouldBe([new YearMonth(2026, 2), new YearMonth(2026, 3)]);
        using var connection = fixture.Factory.Open();
        connection.Query<string?>("SELECT pnl_date FROM asset_depreciation ORDER BY period_month").ToArray()
            .ShouldBe([null, Iso(fixture.Today), Iso(fixture.Today), null, null, null, null, null]);

        var revised = original with { Cost = new Money(48000), Name = "Main freezer", Notes = "Back room" };
        await fixture.Assets.SaveAssetAsync(id, revised, ct);
        (await fixture.Assets.GetAssetsAsync(cancellationToken: ct)).Single().Name.ShouldBe("Main freezer");
        AssetChangePreview blocked = await fixture.Assets.PreviewChangeAsync(id, revised with { Cost = new Money(50000) }, null, ct);
        blocked.Treatment.ShouldBe("BLOCKED");
        (await Should.ThrowAsync<DomainException>(() => fixture.Assets.SaveAssetAsync(id,
            revised with { Cost = new Money(50000) }, ct))).Code.ShouldBe(ErrorCode.AssetTermsFinalized);
        (await Should.ThrowAsync<DomainException>(() => fixture.Assets.SaveAssetAsync(id,
            revised with { Rate = new Percent(24) }, new YearMonth(2026, 3), ct))).Code.ShouldBe(ErrorCode.EffectiveMonthClosed);

        Money[] before = (await fixture.Assets.GetScheduleAsync(id, ct)).Select(static row => row.Amount).ToArray();
        AssetChangePreview preview = await fixture.Assets.PreviewChangeAsync(id, revised with { Rate = new Percent(24) }, null, ct);
        preview.Treatment.ShouldBe("TERMS");
        preview.EffectiveMonth.ShouldBe(new YearMonth(2026, 4));
        preview.MonthsRecalculated.ShouldBe(5);
        await fixture.Assets.SaveAssetAsync(id, revised with { Rate = new Percent(24) }, ct);
        IReadOnlyList<DepreciationEntry> after = await fixture.Assets.GetScheduleAsync(id, ct);
        after.Take(3).Select(static row => row.Amount).ShouldBe(before.Take(3));
        after.Skip(3).Select(static row => row.Amount).ShouldAllBe(amount => amount > new Money(900));
        after.Skip(3).Sum(static row => row.Amount.Scaled).ShouldBe(preview.DepreciationAfter.Scaled);
        after[^1].ClosingValue.ShouldBe(preview.BookValueAfter);
        (await fixture.Assets.GetTermsAsync(id, ct)).Select(static term => term.Reason)
            .ShouldBe(["CHANGE", "RECALCULATION", "INITIAL"]);
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() =>
            connection.Execute("DELETE FROM asset_depreciation WHERE period_month='2026-02'"));
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

        (await Should.ThrowAsync<GroceryErp.Core.Errors.DomainException>(() => fixture.Assets.DisposeAsync(
            asset, new DateOnly(2026, 8, 15), new Money(30000), OperationGuard.NewId(),
            TestContext.Current.CancellationToken))).Code.ShouldBe(GroceryErp.Core.Errors.ErrorCode.AssetDepreciationPosted);

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
        GroceryErp.Core.Errors.DomainException posted = await Should.ThrowAsync<GroceryErp.Core.Errors.DomainException>(() =>
            fixture.MoneyOut.PostSalaryRunAsync(
                new(period, fixture.Today, "BANK", [new(employee, new Money(1000), Money.Zero, Money.Zero, Money.Zero, null)]),
                OperationGuard.NewId(), TestContext.Current.CancellationToken));
        posted.Code.ShouldBe(GroceryErp.Core.Errors.ErrorCode.SalaryAlreadyPosted);
        posted.Arguments.ShouldBe(["Asha"]);
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
            Clock = new MutableClock(Today);
            Periods = new PeriodGuard(Settings, Clock);
            var ledger = new LotQuantities(Settings);
            MoneyOut = new MoneyOutService(Database, ledger, new DocNumberService(Settings), Periods, new AuditLog());
            Assets = new AssetService(Database, Settings, Periods, new AuditLog(), Clock);

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
            using var transaction = connection.BeginTransaction();
            ledger.Change(new(connection, transaction), new(new DateOnly(2026, 4, 1), product, LotId,
                LotChangeKind.Opening, new Quantity(10)));
            transaction.Commit();
        }

        public string Root { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public MoneyOutService MoneyOut { get; }
        public AssetService Assets { get; }
        public MutableClock Clock { get; }
        public PeriodGuard Periods { get; }
        public DateOnly Today { get; }
        public long LotId { get; }

        public void Dispose()
        {
            Database.Dispose();
            SqliteConnectionFactory.ClearPoolForDataRoot(Root);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // The dashboard's expiry rule as of a given business date, restated without the app's SQL: every
    // expired lot with stock, blocked or not and of any product; critical and soon only for active lots
    // of active products inside their alert window.
    private const string ExpiryAlertAsOfSql = """
        SELECT *,CASE WHEN days_left<0 THEN 'EXPIRED' WHEN days_left<=7 THEN 'CRITICAL' ELSE 'SOON' END AS bucket
        FROM (
          SELECT CAST(julianday(l.expiry_date)-julianday(@asOf) AS INTEGER) AS days_left,
                 COALESCE(p.expiry_alert_days,c.expiry_alert_days,pc.expiry_alert_days,
                          CAST((SELECT value FROM setting WHERE key='expiry.default_alert_days') AS INTEGER),30) AS alert_days,
                 l.is_active=1 AND p.is_active=1 AS in_use
          FROM product_lot l JOIN product p ON p.id=l.product_id
          JOIN category c ON c.id=p.category_id LEFT JOIN category pc ON pc.id=c.parent_id
          WHERE l.qty_on_hand>0 AND l.expiry_date IS NOT NULL
        ) WHERE days_left<0 OR (in_use AND days_left<=alert_days)
        """;

    private sealed class FixedClock(DateOnly date) : IClock
    {
        public DateTime Now => date.ToDateTime(new TimeOnly(12, 0));
        public DateOnly BusinessDate => date;
    }

    private sealed class MutableClock(DateOnly date) : IClock
    {
        public DateOnly BusinessDate { get; set; } = date;
        public DateTime Now => BusinessDate.ToDateTime(new TimeOnly(12, 0));
    }
}
