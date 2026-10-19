using Dapper;
using GroceryErp.Data.Infrastructure;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class PhaseOneSnapshotTests
{
    [Fact]
    public void Version28Upgrade_PreservesHistoryAndAllowsZeroLotAndProductRemoval()
    {
        using var fixture = new LegacyFixture();
        fixture.SeedHistory();
        new MigrationRunner(fixture.Factory, NullMigrationBackup.Instance).Run();
        using SqliteConnection db = fixture.Factory.Open();

        db.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(29);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM schema_migration_marker WHERE version=29")
            .ShouldBe(1);
        db.ExecuteScalar<string>("SELECT product_name FROM sale_line WHERE id=1")
            .ShouldBe("Original sale name");
        db.ExecuteScalar<string?>("SELECT batch_no_snapshot FROM sale_line WHERE id=1")
            .ShouldBeNull();
        db.ExecuteScalar<string>("SELECT snapshot_provenance FROM sale_line WHERE id=1")
            .ShouldBe("LEGACY_SALE_LINE");
        db.ExecuteScalar<string>("SELECT legacy_unknown_fields FROM sale_line WHERE id=1")
            .ShouldBe("SUCLBEVT");
        db.ExecuteScalar<string>("SELECT receipt_snapshot FROM sale WHERE id=1")
            .ShouldBe("{\"old\":true}");
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM direct_stock_origin WHERE lot_id=1")
            .ShouldBe(1);
        db.ExecuteScalar<long>("SELECT origin_ref_id FROM product_lot WHERE id=1")
            .ShouldBe(db.ExecuteScalar<long>("SELECT id FROM direct_stock_origin WHERE lot_id=1"));

        db.Execute("DELETE FROM product_lot WHERE id IN (1,2)");
        db.Execute("DELETE FROM product WHERE id=1");
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_foreign_key_check").ShouldBe(0);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM sale_line WHERE id=1").ShouldBe(1);
        db.ExecuteScalar<string>("SELECT product_name_snapshot FROM sale_return_line WHERE id=1")
            .ShouldBe("Original sale name");
        db.ExecuteScalar<string>("SELECT product_name_snapshot FROM damage WHERE id=1")
            .ShouldBe("Current master name");
        db.ExecuteScalar<string>("SELECT snapshot_provenance FROM damage WHERE id=1")
            .ShouldBe("LEGACY_MASTER_AT_MIGRATION");
        db.ExecuteScalar<string>("SELECT product_name_snapshot FROM vendor_return_line WHERE id=1")
            .ShouldBe("Current master name");
        db.ExecuteScalar<string>("SELECT lot_code_snapshot FROM purchase_line WHERE id=1")
            .ShouldBe("L-PURCHASE");
        db.ExecuteScalar<string>("SELECT product_name_snapshot FROM stock_count_line WHERE id=1")
            .ShouldBe("Current master name");
        db.ExecuteScalar<string>("SELECT lot_code_snapshot FROM stock_count_line WHERE id=1")
            .ShouldBe("L-OLD");

        long category = db.ExecuteScalar<long>("SELECT id FROM category LIMIT 1");
        long uom = db.ExecuteScalar<long>("SELECT id FROM uom LIMIT 1");
        long product = db.ExecuteScalar<long>("""
            INSERT INTO product(sku,name,category_id,uom_id)
            VALUES ('REPLACEMENT','Replacement',@category,@uom) RETURNING id
            """, new { category, uom });
        product.ShouldBeGreaterThan(1);
        long lot = db.ExecuteScalar<long>("""
            INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,selling_price)
            VALUES (@product,'L-NEW','2026-09-26',1000,2000) RETURNING id
            """, new { product });
        lot.ShouldBeGreaterThan(2);
        db.ExecuteScalar<long>("SELECT origin_ref_id FROM product_lot WHERE id=@lot", new { lot })
            .ShouldBeGreaterThan(0);
    }

    [Fact]
    public void InterruptedUpgrade_RollsBackAndCanRetry()
    {
        using var fixture = new LegacyFixture();
        fixture.SeedHistory();
        using (SqliteConnection db = fixture.Factory.Open())
            db.Execute("CREATE TABLE schema_migration_marker(id INTEGER PRIMARY KEY)");
        Should.Throw<SqliteException>(() =>
            new MigrationRunner(fixture.Factory, NullMigrationBackup.Instance).Run());
        using (SqliteConnection db = fixture.Factory.Open())
        {
            db.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(28);
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM sale_line").ShouldBe(1);
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('sale_line') WHERE name='batch_no_snapshot'")
                .ShouldBe(0);
            db.Execute("DROP TABLE schema_migration_marker");
        }
        new MigrationRunner(fixture.Factory, NullMigrationBackup.Instance).Run();
        using SqliteConnection upgraded = fixture.Factory.Open();
        upgraded.ExecuteScalar<long>("PRAGMA user_version").ShouldBe(29);
        upgraded.ExecuteScalar<long>("SELECT COUNT(*) FROM schema_migration_marker").ShouldBe(1);
    }

    private sealed class LegacyFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"grocery-phase1-{Guid.NewGuid():N}");

        internal LegacyFixture()
        {
            Factory = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(_root)));
            using SqliteConnection db = Factory.Open();
            string[] scripts = typeof(MigrationRunner).Assembly.GetManifestResourceNames()
                .Where(static name => name.Contains(".db.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Take(28).ToArray();
            scripts.Length.ShouldBe(28);
            foreach (string script in scripts)
            {
                using Stream stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(script)
                    ?? throw new InvalidOperationException(script);
                using var reader = new StreamReader(stream);
                db.Execute(reader.ReadToEnd());
            }
            db.Execute("PRAGMA user_version=28");
        }

        internal SqliteConnectionFactory Factory { get; }

        internal void SeedHistory()
        {
            using SqliteConnection db = Factory.Open();
            db.Execute("INSERT INTO category(name) VALUES ('Fixture')");
            long category = db.ExecuteScalar<long>("SELECT id FROM category LIMIT 1");
            long uom = db.ExecuteScalar<long>("SELECT id FROM uom LIMIT 1");
            db.Execute("""
                INSERT INTO product(id,sku,name,category_id,uom_id)
                VALUES (1,'ORIGINAL','Current master name',@category,@uom);
                INSERT INTO product_lot(id,product_id,lot_code,received_date,
                    unit_cost,selling_price,qty_received)
                VALUES (1,1,'L-OLD','2026-09-26',1000,2000,1000);
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-09-26',1,1,'OPENING',1000,1000);
                INSERT INTO sale(id,sale_date,status) VALUES (1,'2026-09-26','DRAFT');
                INSERT INTO sale_line(id,sale_id,line_no,product_id,lot_id,product_name,qty,
                    unit_price,taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
                VALUES (1,1,1,1,1,'Original sale name',1000,2000,2000,0,2000,1000,1000);
                UPDATE sale SET status='COMPLETED',invoice_no='S-OLD',
                    taxable_total=2000,grand_total=2000,cost_total=1000,
                    receipt_snapshot='{"old":true}' WHERE id=1;
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost,
                    ref_type,ref_id)
                VALUES ('2026-09-26',1,1,'SALE',-1000,1000,'sale_line',1);
                INSERT INTO sale_return(id,sale_id,return_date,refund_mode,return_no,
                    taxable_total,refund_total,cost_total)
                VALUES (1,1,'2026-09-26','CASH','R-OLD',2000,2000,1000);
                INSERT INTO sale_return_line(id,sale_return_id,sale_line_id,qty,
                    taxable_amount,tax_amount,refund_amount,cost_amount)
                VALUES (1,1,1,1000,2000,0,2000,1000);
                INSERT INTO damage(id,doc_no,damage_date,product_id,lot_id,qty,
                    unit_cost,cost_value,reason)
                VALUES (1,'D-OLD','2026-09-26',1,1,1000,1000,1000,'CUSTOMER_RETURN');
                INSERT INTO vendor(id,name) VALUES (1,'Original vendor');
                INSERT INTO purchase(id,vendor_id,received_date,status)
                VALUES (1,1,'2026-09-26','DRAFT');
                INSERT INTO purchase_line(id,purchase_id,line_no,product_id,qty,unit_cost,
                    line_total,landed_unit_cost,selling_price,product_name_snapshot,product_sku_snapshot)
                VALUES (1,1,1,1,1000,1000,1000,1000,2000,'Current master name','ORIGINAL');
                UPDATE purchase SET status='POSTED',doc_no='P-OLD',grand_total=1000,
                    vendor_name_snapshot='Original vendor' WHERE id=1;
                INSERT INTO product_lot(id,product_id,lot_code,purchase_line_id,vendor_id,
                    received_date,unit_cost,selling_price,qty_received)
                VALUES (2,1,'L-PURCHASE',1,1,'2026-09-26',1000,2000,1000);
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-09-26',1,2,'PURCHASE',1000,1000);
                INSERT INTO vendor_return(id,doc_no,vendor_id,purchase_id,return_date,
                    vendor_credit_no,reason,tax_mode,credit_before_tax,tax_credit,credit_total,cost_total)
                VALUES (1,'VR-OLD',1,1,'2026-09-26','CR-OLD','Old return','REGULAR',1000,0,1000,1000);
                INSERT INTO vendor_return_line(id,vendor_return_id,product_id,lot_id,qty,
                    credit_before_tax,tax_credit,cost_value)
                VALUES (1,1,1,2,1000,1000,0,1000);
                INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost)
                VALUES ('2026-09-26',1,2,'VENDOR_RETURN',-1000,1000);
                INSERT INTO stock_count(id,count_date,status,snapshot_movement_id)
                VALUES (1,'2026-09-26','DRAFT',(SELECT MAX(id) FROM stock_movement));
                INSERT INTO stock_count_line(id,stock_count_id,product_id,lot_id,expected_qty,unit_cost)
                VALUES (1,1,1,1,0,1000);
                UPDATE stock_count SET status='POSTED' WHERE id=1;
                """, new { category, uom });
            db.ExecuteScalar<long>("SELECT qty_on_hand FROM product_lot WHERE id=1").ShouldBe(0);
        }

        public void Dispose()
        {
            Factory.ClearPool();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
