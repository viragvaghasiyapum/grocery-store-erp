using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Sales;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class DirectStockEngineTests
{
    private static readonly DirectStockEngine Engine = new();

    [Fact]
    public async Task CashierCompletionAndSelectedProductCount_UseDirectSourceDocuments()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(ct);
        var periods = new PeriodGuard(settings);
        var countService = new DirectStockCountService(fixture.Database, Engine, periods);
        (await countService.IsAvailableAsync(ct)).ShouldBeFalse();
        await fixture.Database.WriteAsync(tx =>
        {
            tx.Connection.Execute("""
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-10-18',@product,@lot,'OPENING',10000,1000)
                """, new { product = fixture.Product, lot = fixture.Lot }, tx.Transaction);
            DirectStockSandbox.Activate(tx);
        }, ct);
        (await countService.IsAvailableAsync(ct)).ShouldBeTrue();
        var clock = new FixedClock(new DateTime(2026, 10, 18, 12, 0, 0));
        var sales = new SalesService(fixture.Database, new StockLedger(settings),
            new DocNumberService(settings), periods, settings, new AuditLog(), clock, Engine);
        long saleId = await sales.AddProductAsync(null, fixture.Product, Quantity.One,
            cancellationToken: ct);
        await sales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(2))],
            OperationGuard.NewId()), ct);
        DirectCountLotPage page = await countService.GetPageAsync([fixture.Product], cancellationToken: ct);
        page.Items.Count.ShouldBe(1);
        page.Items[0].OnHand.ShouldBe(9000);
        await countService.PostReviewedAsync([
            new(page.Items[0].LotId, page.Items[0].Version, 9000, 8000,
                clock.BusinessDate, "Physical shelf count", OperationGuard.NewId(), "LOST")
        ], ct);
        using var db = fixture.Factory.Open();
        db.ExecuteScalar<string>("SELECT reason FROM damage WHERE lot_id=@lot",
            new { lot = fixture.Lot }).ShouldBe("LOST");
        db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@lot",
            new { lot = fixture.Lot }).ShouldBe(8000);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_movement WHERE lot_id=@lot",
            new { lot = fixture.Lot }).ShouldBe(1);
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime Now => now;
        public DateOnly BusinessDate => DateOnly.FromDateTime(now);
    }

    [Fact]
    public async Task SelectedProductCount_PagesStayBounded_WhenSurplusCreatesANewLot()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Database.WriteAsync(tx =>
        {
            tx.Connection.Execute("""
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-10-18',@product,@lot,'OPENING',10000,1000)
                """, new { product = fixture.Product, lot = fixture.Lot }, tx.Transaction);
            DirectStockSandbox.Activate(tx);
            for (int index = 0; index < 51; index++)
            {
                long lot = tx.Connection.ExecuteScalar<long>("""
                    INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,
                      selling_price,qty_received)
                    VALUES (@product,@code,'2026-10-18',1000,2000,1000) RETURNING id
                    """, new { product = fixture.Product, code = "COUNT-" + index }, tx.Transaction);
                Engine.CreateDirectLot(tx, lot, 1, OperationGuard.NewId());
            }
        }, ct);
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(ct);
        var counts = new DirectStockCountService(fixture.Database, Engine, new PeriodGuard(settings));
        DirectCountLotPage first = await counts.GetPageAsync([fixture.Product], limit: 50, cancellationToken: ct);
        first.Items.Count.ShouldBe(50);
        first.HasMore.ShouldBeTrue();
        await counts.PostReviewedAsync(first.Items.Select((lot, index) => new DirectCountReview(
            lot.LotId, lot.Version, lot.OnHand, lot.OnHand + (index == 0 ? 1000 : 0),
            new DateOnly(2026, 10, 18), "Shelf count", OperationGuard.NewId())).ToArray(), ct);
        DirectCountLotPage second = await counts.GetPageAsync([fixture.Product], first.NextLotId,
            first.MaxLotId, limit: 50, cancellationToken: ct);
        second.Items.Count.ShouldBe(2);
        second.HasMore.ShouldBeFalse();
        second.Items.All(lot => lot.LotId <= first.MaxLotId).ShouldBeTrue();
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task DisposableConversion_KeepsLegacyParity_AndDirectWritesAreAtomicAndIdempotent()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Database.WriteAsync(tx =>
        {
            tx.Connection.Execute("""
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-10-18',@product,@lot,'OPENING',10000,1000)
                """, new { product = fixture.Product, lot = fixture.Lot }, tx.Transaction);
            DirectStockSandbox.Activate(tx);
            Engine.CheckAll(tx).ShouldBeEmpty();
        }, ct);
        Should.Throw<DomainException>(() =>
            new MigrationRunner(fixture.Factory, NullMigrationBackup.Instance).Run()).Code
            .ShouldBe(ErrorCode.DatabaseNewerThanApp);
        using (var db = fixture.Factory.Open())
            Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() => db.Execute("""
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-10-18',@product,@lot,'OPENING',1000,1000)
                """, new { product = fixture.Product, lot = fixture.Lot }));

        string saleOp = OperationGuard.NewId();
        long saleId = await fixture.Database.WriteAsync(tx =>
        {
            long id = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO sale(invoice_no,sale_date) VALUES ('S-1','2026-10-18') RETURNING id
                """, transaction: tx.Transaction);
            tx.Connection.Execute("""
                INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,
                  unit_price,taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
                VALUES (@id,1,@product,@lot,'Original',2000,2000,4000,0,4000,1000,2000)
                """, new { id, product = fixture.Product, lot = fixture.Lot }, tx.Transaction);
            tx.Connection.Execute("UPDATE sale SET status='COMPLETED' WHERE id=@id", new { id }, tx.Transaction);
            Engine.CompleteSale(tx, id, new Dictionary<long, long> { [fixture.Lot] = 1 }, saleOp);
            return id;
        }, ct);
        await fixture.Database.WriteAsync(tx =>
            Engine.CompleteSale(tx, saleId, new Dictionary<long, long>(), saleOp), ct);
        using (var db = fixture.Factory.Open())
            db.Execute("DELETE FROM operation WHERE op_id=@saleOp", new { saleOp });
        await fixture.Database.WriteAsync(tx =>
            Engine.CompleteSale(tx, saleId, new Dictionary<long, long>(), saleOp), ct);
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.Lot })
                .ShouldBe(8000);
            db.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@id",
                new { id = fixture.Product }).ShouldBe(8000);
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_movement WHERE lot_id=@id",
                new { id = fixture.Lot }).ShouldBe(1);
        }

        string countOp = OperationGuard.NewId();
        await Should.ThrowAsync<DomainException>(async () => await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, 1, 8000, 7000,
                new DateOnly(2026, 10, 18), "Counted shelf", countOp), ct));
        long damageId = await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, 2, 8000, 7000,
                new DateOnly(2026, 10, 18), "Counted shelf", countOp), ct);
        damageId.ShouldBeGreaterThan(0);
        (await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, 2, 8000, 7000,
                new DateOnly(2026, 10, 18), "Counted shelf", countOp), ct)).ShouldBe(damageId);
        await Should.ThrowAsync<DomainException>(() => fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, 2, 8000, 6000,
                new DateOnly(2026, 10, 18), "Counted shelf", countOp), ct));
        await Should.ThrowAsync<DomainException>(() => fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, 2, 8000, 7000,
                new DateOnly(2026, 10, 18), "Counted shelf", countOp, "LOST"), ct));
        long surplusLot = await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, 3, 7000, 9000,
                new DateOnly(2026, 10, 18), "Back room count", OperationGuard.NewId()), ct);
        surplusLot.ShouldNotBe(fixture.Lot);
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.Lot })
                .ShouldBe(7000);
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = surplusLot })
                .ShouldBe(2000);
            db.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@id",
                new { id = fixture.Product }).ShouldBe(9000);
        }
        await Should.ThrowAsync<DomainException>(async () => await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCountBatch(tx,
            [
                new(fixture.Lot, 3, 7000, 6000, new DateOnly(2026, 10, 18),
                    "Batch count", OperationGuard.NewId()),
                new(surplusLot, 99, 2000, 1000, new DateOnly(2026, 10, 18),
                    "Stale batch count", OperationGuard.NewId()),
            ]), ct));
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@id", new { id = fixture.Lot })
                .ShouldBe(7000);
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM damage WHERE lot_id=@id", new { id = fixture.Lot })
                .ShouldBe(1);
        }
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task PurchaseVendorReturnAndInternalUse_Reconcile_AndTouchedPurchaseCannotVoid()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Database.WriteAsync(tx =>
        {
            tx.Connection.Execute("""
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-10-18',@product,@lot,'OPENING',10000,1000)
                """, new { product = fixture.Product, lot = fixture.Lot }, tx.Transaction);
            tx.Connection.Execute("UPDATE product SET product_class='INTERNAL' WHERE id=@id",
                new { id = fixture.Product }, tx.Transaction);
            DirectStockSandbox.Activate(tx);
        }, ct);
        long purchaseLot = await fixture.Database.WriteAsync(tx =>
        {
            long vendor = tx.Connection.ExecuteScalar<long>(
                "INSERT INTO vendor(name) VALUES ('Supplier') RETURNING id", transaction: tx.Transaction);
            long purchase = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO purchase(vendor_id,received_date,doc_no)
                VALUES (@vendor,'2026-10-18','P-1') RETURNING id
                """, new { vendor }, tx.Transaction);
            long line = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO purchase_line(purchase_id,line_no,product_id,qty,free_qty,
                  unit_cost,line_total,landed_unit_cost,selling_price)
                VALUES (@purchase,1,@product,5000,0,1000,5000,1000,2000) RETURNING id
                """, new { purchase, product = fixture.Product }, tx.Transaction);
            tx.Connection.Execute("UPDATE purchase SET status='POSTED' WHERE id=@purchase",
                new { purchase }, tx.Transaction);
            long lot = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,purchase_line_id,vendor_id,
                  received_date,unit_cost,selling_price,qty_received)
                VALUES (@product,'PUR-1',@line,@vendor,'2026-10-18',1000,2000,5000)
                RETURNING id
                """, new { product = fixture.Product, line, vendor }, tx.Transaction);
            Engine.ReceivePurchaseLot(tx, lot, 1, OperationGuard.NewId());
            long returned = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO vendor_return(doc_no,vendor_id,purchase_id,return_date,
                  vendor_credit_no,reason,tax_mode,credit_before_tax,tax_credit,credit_total,cost_total)
                VALUES ('VR-1',@vendor,@purchase,'2026-10-18','C-1','Damaged pack',
                  'NONE',1000,0,1000,1000) RETURNING id
                """, new { vendor, purchase }, tx.Transaction);
            tx.Connection.Execute("""
                INSERT INTO vendor_return_line(vendor_return_id,product_id,lot_id,qty,
                  credit_before_tax,tax_credit,cost_value)
                VALUES (@returned,@product,@lot,1000,1000,0,1000)
                """, new { returned, product = fixture.Product, lot }, tx.Transaction);
            Engine.ReturnToVendor(tx, returned, new Dictionary<long, long> { [lot] = 2 }, OperationGuard.NewId());
            Engine.CancelVendorReturn(tx, returned, new Dictionary<long, long> { [lot] = 3 },
                "Vendor rejected credit", OperationGuard.NewId());
            long internalId = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO internal_use_record(product_id_snapshot,lot_id_snapshot,
                  product_name_snapshot,quantity,unit_cost,used_date,reason)
                VALUES (@product,@lot,'Direct stock',1000,1000,'2026-10-18','Packing') RETURNING id
                """, new { product = fixture.Product, lot }, tx.Transaction);
            Engine.ConsumeInternal(tx, internalId, 4, OperationGuard.NewId());
            Engine.CheckLot(tx, lot).ShouldBeNull();

            long untouchedPurchase = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO purchase(vendor_id,received_date,doc_no)
                VALUES (@vendor,'2026-10-18','P-2') RETURNING id
                """, new { vendor }, tx.Transaction);
            long untouchedLine = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO purchase_line(purchase_id,line_no,product_id,qty,
                  unit_cost,line_total,landed_unit_cost,selling_price)
                VALUES (@untouchedPurchase,1,@product,3000,1000,3000,1000,2000) RETURNING id
                """, new { untouchedPurchase, product = fixture.Product }, tx.Transaction);
            tx.Connection.Execute("UPDATE purchase SET status='POSTED' WHERE id=@untouchedPurchase",
                new { untouchedPurchase }, tx.Transaction);
            long untouchedLot = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,purchase_line_id,vendor_id,
                  received_date,unit_cost,selling_price,qty_received)
                VALUES (@product,'PUR-2',@untouchedLine,@vendor,'2026-10-18',1000,2000,3000)
                RETURNING id
                """, new { product = fixture.Product, untouchedLine, vendor }, tx.Transaction);
            Engine.ReceivePurchaseLot(tx, untouchedLot, 1, OperationGuard.NewId());
            tx.Connection.Execute("UPDATE purchase SET status='CANCELLED' WHERE id=@untouchedPurchase",
                new { untouchedPurchase }, tx.Transaction);
            Engine.VoidPurchase(tx, untouchedPurchase, new Dictionary<long, long> { [untouchedLot] = 2 },
                OperationGuard.NewId());
            Engine.CheckLot(tx, untouchedLot).ShouldBeNull();
            return lot;
        }, ct);
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@purchaseLot",
                new { purchaseLot }).ShouldBe(4000);
            db.ExecuteScalar<long>("SELECT quantity FROM product_stock_total WHERE product_id=@product",
                new { product = fixture.Product }).ShouldBe(14000);
        }
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task FefoFailureRollsBackDocument_ReturnRestocks_AndReturnedSaleCannotVoid()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        long laterLot = await fixture.Database.WriteAsync(tx =>
        {
            tx.Connection.Execute("""
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-10-18',@product,@lot,'OPENING',10000,1000)
                """, new { product = fixture.Product, lot = fixture.Lot }, tx.Transaction);
            DirectStockSandbox.Activate(tx);
            long lot = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,
                  unit_cost,selling_price,qty_received)
                VALUES (@product,'DS-LATER','2026-10-18','2028-10-18',1000,2000,5000)
                RETURNING id
                """, new { product = fixture.Product }, tx.Transaction);
            Engine.CreateDirectLot(tx, lot, 1, OperationGuard.NewId());
            return lot;
        }, ct);
        await Should.ThrowAsync<DomainException>(async () => await fixture.Database.WriteAsync(tx =>
        {
            long sale = InsertSale(tx, fixture.Product, laterLot, "FEFO-BAD");
            Engine.CompleteSale(tx, sale, new Dictionary<long, long> { [laterLot] = 2 }, OperationGuard.NewId());
        }, ct));
        using (var db = fixture.Factory.Open())
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM sale WHERE invoice_no='FEFO-BAD'").ShouldBe(0);

        long saleId = await fixture.Database.WriteAsync(tx =>
        {
            long sale = InsertSale(tx, fixture.Product, fixture.Lot, "FEFO-GOOD");
            Engine.CompleteSale(tx, sale, new Dictionary<long, long> { [fixture.Lot] = 1 }, OperationGuard.NewId());
            return sale;
        }, ct);
        await fixture.Database.WriteAsync(tx =>
        {
            long returned = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO sale_return(sale_id,return_date,refund_mode)
                VALUES (@saleId,'2026-10-18','CASH') RETURNING id
                """, new { saleId }, tx.Transaction);
            long line = tx.Connection.ExecuteScalar<long>(
                "SELECT id FROM sale_line WHERE sale_id=@saleId", new { saleId }, tx.Transaction);
            tx.Connection.Execute("""
                INSERT INTO sale_return_line(sale_return_id,sale_line_id,qty,
                  taxable_amount,tax_amount,refund_amount,cost_amount)
                VALUES (@returned,@line,1000,2000,0,2000,1000)
                """, new { returned, line }, tx.Transaction);
            Engine.CustomerReturn(tx, returned, new Dictionary<long, long> { [fixture.Lot] = 2 },
                OperationGuard.NewId());
        }, ct);
        await Should.ThrowAsync<DomainException>(async () => await fixture.Database.WriteAsync(tx =>
        {
            tx.Connection.Execute("UPDATE sale SET status='VOID',void_reason='Mistake' WHERE id=@saleId",
                new { saleId }, tx.Transaction);
            Engine.VoidSale(tx, saleId, new Dictionary<long, long> { [fixture.Lot] = 3 },
                OperationGuard.NewId());
        }, ct));
        using (var db = fixture.Factory.Open())
        {
            db.ExecuteScalar<string>("SELECT status FROM sale WHERE id=@saleId", new { saleId })
                .ShouldBe("COMPLETED");
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@lot",
                new { lot = fixture.Lot }).ShouldBe(9000);
        }
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    private static long InsertSale(DbTx tx, long product, long lot, string invoice)
    {
        long sale = tx.Connection.ExecuteScalar<long>("""
            INSERT INTO sale(invoice_no,sale_date) VALUES (@invoice,'2026-10-18') RETURNING id
            """, new { invoice }, tx.Transaction);
        tx.Connection.Execute("""
            INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,
              unit_price,taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
            VALUES (@sale,1,@product,@lot,'Direct stock',2000,2000,4000,0,4000,1000,2000)
            """, new { sale, product, lot }, tx.Transaction);
        tx.Connection.Execute("UPDATE sale SET status='COMPLETED' WHERE id=@sale",
            new { sale }, tx.Transaction);
        return sale;
    }

    [Fact]
    public async Task BackupDuringDirectWrite_VerifiesAndLeavesSourceBalanced()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Database.WriteAsync(tx =>
        {
            tx.Connection.Execute("""
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-10-18',@product,@lot,'OPENING',10000,1000)
                """, new { product = fixture.Product, lot = fixture.Lot }, tx.Transaction);
            DirectStockSandbox.Activate(tx);
        }, ct);
        using var settings = new SettingsService(fixture.Database, fixture.Paths, new AuditLog());
        await settings.LoadAsync(ct);
        var backup = new BackupService(fixture.Paths, fixture.Factory,
            new GroceryErp.Core.Time.SystemClock(settings), settings);
        Task<BackupInfo> backupTask = Task.Run(() => backup.Create("manual", ct), ct);
        await fixture.Database.WriteAsync(tx =>
            Engine.ReviewCount(tx, fixture.Lot, 1, 10000, 9000,
                new DateOnly(2026, 10, 18), "Concurrent backup count", OperationGuard.NewId()), ct);
        BackupInfo created = await backupTask;
        BackupService.Verify(created.Path, ct).SchemaVersion.ShouldBe(32);
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    [Fact]
    public async Task DiskFullAfterDirectStockUpdate_RollsBackSaleAndQuantity()
    {
        using var fixture = new Fixture();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Database.WriteAsync(tx =>
        {
            tx.Connection.Execute("""
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-10-18',@product,@lot,'OPENING',10000,1000)
                """, new { product = fixture.Product, lot = fixture.Lot }, tx.Transaction);
            DirectStockSandbox.Activate(tx);
        }, ct);
        using (var connection = fixture.Factory.Open())
        {
            long pages = connection.ExecuteScalar<long>("PRAGMA page_count");
            connection.Execute($"PRAGMA max_page_count = {pages + 16}");
        }
        DomainException failure = await Should.ThrowAsync<DomainException>(() =>
            fixture.Database.WriteAsync(tx =>
            {
                long sale = InsertSale(tx, fixture.Product, fixture.Lot, "FULL-DIRECT");
                Engine.CompleteSale(tx, sale, new Dictionary<long, long> { [fixture.Lot] = 1 },
                    OperationGuard.NewId());
                tx.Connection.Execute("""
                    INSERT INTO audit_log(entity,entity_id,action,changes)
                    VALUES ('fault',0,'FULL',@changes)
                    """, new { changes = new string('x', 2_000_000) }, tx.Transaction);
            }, ct));
        failure.Code.ShouldBe(ErrorCode.DiskFull);
        using (var connection = fixture.Factory.Open())
        {
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale WHERE invoice_no='FULL-DIRECT'")
                .ShouldBe(0);
            connection.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=@lot",
                new { lot = fixture.Lot }).ShouldBe(10000);
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM stock_movement WHERE lot_id=@lot",
                new { lot = fixture.Lot }).ShouldBe(1);
        }
        await fixture.Database.WriteAsync(tx => Engine.CheckAll(tx).ShouldBeEmpty(), ct);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "direct-stock-" + Guid.NewGuid().ToString("N"));
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public AppPaths Paths { get; }
        public long Product { get; }
        public long Lot { get; }

        public Fixture()
        {
            Paths = new AppPaths(new SettingsBootstrap(_root));
            Factory = new SqliteConnectionFactory(Paths);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Database = new DbExecutor(Factory);
            using var db = Factory.Open();
            long category = db.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Test') RETURNING id");
            long uom = db.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
            Product = db.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id)
                VALUES ('DS-1','Direct stock',@category,@uom) RETURNING id
                """, new { category, uom });
            Lot = db.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,received_date,expiry_date,
                  unit_cost,selling_price,qty_received)
                VALUES (@product,'DS-L1','2026-10-18','2027-10-18',1000,2000,10000)
                RETURNING id
                """, new { product = Product });
        }

        public void Dispose()
        {
            Database.Dispose();
            Factory.ClearPool();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
