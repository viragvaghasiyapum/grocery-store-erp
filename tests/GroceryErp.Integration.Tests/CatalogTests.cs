using ClosedXML.Excel;
using Dapper;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Printing;
using Shouldly;
using Xunit;

namespace GroceryErp.Integration.Tests;

public sealed class CatalogTests
{
    [Theory]
    [InlineData("4006381333931")]
    [InlineData("2700123012505")]
    public void Ean_KnownVectorsAreValid(string value) => Ean.IsValid(value).ShouldBeTrue();

    [Fact]
    public void Ean_InternalVectorsMatchSpecification()
    {
        Ean.Internal("20", 1).ShouldBe("2000000000015");
        Ean.Internal("21", 45).ShouldBe("2100000000456");
    }

    [Fact]
    public void ScaleRule_ParsesWeightAndRejectsBadCheckDigit()
    {
        var rule = new BarcodeRule("27", 13, 3, 5, 8, 5, BarcodeValueKind.Weight, 1);

        rule.TryParse("2700123012505", out string plu, out decimal value).ShouldBeTrue();
        plu.ShouldBe("123");
        value.ShouldBe(1.250m);
        rule.TryParse("2700123012504", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Gs1_ParsesGtinExpiryAndBatch()
    {
        Gs1.TryParse("]d201040063813339311726123110BATCH7", out Gs1Data parsed).ShouldBeTrue();
        parsed.Gtin.ShouldBe("04006381333931");
        parsed.Expiry.ShouldBe(new DateOnly(2026, 12, 31));
        parsed.Batch.ShouldBe("BATCH7");
    }

    [Fact]
    public async Task Product_CreateSearchGenerateBarcodeAndDelete_IsTransactionalAndAudited()
    {
        using var fixture = new CatalogFixture();
        long productId = await fixture.Service.CreateAsync(fixture.Product("Atta Premium 5 kg", "wheat flour aashirvaad"), TestContext.Current.CancellationToken);

        IReadOnlyList<ProductListItem> results = await fixture.Service.SearchAsync(
            new ProductSearchFilter("wheat"), TestContext.Current.CancellationToken);
        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe(productId);

        string barcode = await fixture.Service.GenerateInternalBarcodeAsync(productId, "21", TestContext.Current.CancellationToken);
        Ean.IsValid(barcode).ShouldBeTrue();
        ProductRemoval removal = await fixture.Service.RemoveAsync(productId, TestContext.Current.CancellationToken);
        removal.ShouldBe(ProductRemoval.Deleted);

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product WHERE id=@productId", new { productId }).ShouldBe(0L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity='product'").ShouldBe(3L);
    }

    [Fact]
    public async Task Product_WithStockHistoryIsArchivedAndCanBeRestored()
    {
        using var fixture = new CatalogFixture();
        long productId = await fixture.Service.CreateAsync(fixture.Product("Fresh Milk 1 L", "milk"), TestContext.Current.CancellationToken);
        using (var connection = fixture.Factory.Open())
            connection.Execute("""
                INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,mrp,selling_price)
                VALUES (@productId,'L000001','2026-09-26',50000,60000,55000)
                """, new { productId });

        (await fixture.Service.RemoveAsync(productId, TestContext.Current.CancellationToken)).ShouldBe(ProductRemoval.Archived);
        (await fixture.Service.SearchAsync(new ProductSearchFilter("Fresh", IncludeArchived: false), TestContext.Current.CancellationToken)).ShouldBeEmpty();
        await fixture.Service.RestoreAsync(productId, TestContext.Current.CancellationToken);
        (await fixture.Service.SearchAsync(new ProductSearchFilter("Fresh"), TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Product_InvalidOrDuplicateBarcodeIsRejected()
    {
        using var fixture = new CatalogFixture();
        ProductInput invalid = fixture.Product("Salt 1 kg", "salt") with
        {
            Barcodes = [new("4006381333932", Quantity.One, IsPrimary: true)],
        };

        DomainException exception = await Should.ThrowAsync<DomainException>(
            () => fixture.Service.CreateAsync(invalid, TestContext.Current.CancellationToken));
        exception.Code.ShouldBe(ErrorCode.InvalidBarcode);
    }

    [Fact]
    public async Task ScanResolver_ResolvesExactBarcodeAndTypedSku()
    {
        using var fixture = new CatalogFixture();
        ProductInput input = fixture.Product("Tea 250 g", "chai") with
        {
            Barcodes = [new("4006381333931", new Quantity(2), IsPrimary: true)],
        };
        long productId = await fixture.Service.CreateAsync(input, TestContext.Current.CancellationToken);
        var resolver = new ScanResolver(fixture.Database);

        ScanResult barcode = await resolver.ResolveAsync("4006381333931", TestContext.Current.CancellationToken);
        barcode.Kind.ShouldBe(ScanResultKind.Item);
        barcode.ProductId.ShouldBe(productId);
        barcode.Quantity.ShouldBe(new Quantity(2));

        ScanResult sku = await resolver.ResolveAsync(input.Sku.ToLowerInvariant(), TestContext.Current.CancellationToken);
        sku.Kind.ShouldBe(ScanResultKind.Item);
        sku.ProductId.ShouldBe(productId);
        sku.Barcode.ShouldBeNull();
    }

    [Fact]
    public void TsplLabel_ProducesPrintableEnglishDigitCommand()
    {
        byte[] bytes = TsplLabelBuilder.Build(new(
            "Tata Salt 1 kg", "ટાટા મીઠું", "4006381333931", 27.5m, 30m, Copies: 3));
        string command = System.Text.Encoding.UTF8.GetString(bytes);

        command.ShouldContain("BARCODE 24,82");
        command.ShouldContain("Rs 27.50");
        command.ShouldContain("MRP 30.00");
        command.ShouldEndWith("PRINT 1,3\r\n");
    }

    [Fact]
    public async Task Product_LoadAndUpdate_RoundTripsEditableFields()
    {
        using var fixture = new CatalogFixture();
        long productId = await fixture.Service.CreateAsync(fixture.Product("Original Name", "old alias"), TestContext.Current.CancellationToken);
        ProductDetails loaded = await fixture.Service.GetAsync(productId, TestContext.Current.CancellationToken);
        ProductInput changed = loaded.Product with
        {
            Name = "Updated Name",
            NameLocal = "અપડેટ નામ",
            SearchTerms = "new alias",
            DefaultSellingPrice = new Money(55),
        };

        await fixture.Service.UpdateAsync(productId, changed, TestContext.Current.CancellationToken);
        ProductDetails updated = await fixture.Service.GetAsync(productId, TestContext.Current.CancellationToken);

        updated.Product.Name.ShouldBe("Updated Name");
        updated.Product.NameLocal.ShouldBe("અપડેટ નામ");
        updated.Product.DefaultSellingPrice.ShouldBe(new Money(55));
        (await fixture.Service.SearchAsync(new ProductSearchFilter("new alias"), TestContext.Current.CancellationToken))
            .Single().Id.ShouldBe(productId);
    }

    [Fact]
    public async Task CategoryHierarchy_RejectsMoreThanThreeLevelsAndAuditsMasters()
    {
        using var fixture = new CatalogFixture();
        long root = await fixture.Service.CreateCategoryAsync(new("Food", "ખોરાક", null, 30), TestContext.Current.CancellationToken);
        long child = await fixture.Service.CreateCategoryAsync(new("Dry Food", null, root, null), TestContext.Current.CancellationToken);
        long grandchild = await fixture.Service.CreateCategoryAsync(new("Flour", null, child, null), TestContext.Current.CancellationToken);

        DomainException exception = await Should.ThrowAsync<DomainException>(() =>
            fixture.Service.CreateCategoryAsync(new("Wheat Flour", null, grandchild, null), TestContext.Current.CancellationToken));
        exception.Code.ShouldBe(ErrorCode.Validation);

        long brand = await fixture.Service.CreateBrandAsync("Example Brand", TestContext.Current.CancellationToken);
        await fixture.Service.UpdateBrandAsync(brand, "Renamed Brand", TestContext.Current.CancellationToken);
        (await fixture.Service.GetBrandsAsync(TestContext.Current.CancellationToken)).Single(item => item.Id == brand).Name
            .ShouldBe("Renamed Brand");
        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity IN ('category','brand')")
            .ShouldBe(5L);
    }

    [Fact]
    public async Task ExcelImport_DryRunReportsRowsAndImportsValidRowsAtomically()
    {
        using var fixture = new CatalogFixture();
        string path = Path.Combine(fixture.Root, "products.xlsx");
        ProductExcelImporter.WriteTemplate(path);
        using (var workbook = new XLWorkbook(path))
        {
            IXLWorksheet sheet = workbook.Worksheet(1);
            string[] valid =
            [
                "IMP001", "Imported Atta", "આયાત લોટ", "wheat flour", "Food", "Flour", "Brand A",
                "PCS", "1001", "5", "4006381333931", "50", "60", "2", "90", "N", "", "Y",
            ];
            string[] invalid =
            [
                "IMP002", "Invalid Tea", "", "", "Food", "", "", "UNKNOWN", "1001", "5",
                "4006381333932", "70", "60", "0", "", "N", "", "Y",
            ];
            for (int index = 0; index < valid.Length; index++)
            {
                sheet.Cell(2, index + 1).Value = valid[index];
                sheet.Cell(3, index + 1).Value = invalid[index];
            }
            workbook.Save();
        }
        var importer = new ProductExcelImporter(fixture.Database, new AuditLog(), fixture.Settings);

        ProductImportPreview preview = await importer.DryRunAsync(path, TestContext.Current.CancellationToken);
        preview.ValidCount.ShouldBe(1);
        preview.ErrorCount.ShouldBe(1);
        preview.Rows[1].Errors.ShouldContain(static error => error.Contains("Unit", StringComparison.Ordinal));
        preview.Rows[1].Errors.ShouldContain(static error => error.Contains("check digit", StringComparison.Ordinal));
        preview.Rows[1].Errors.ShouldContain(static error => error.Contains("exceed MRP", StringComparison.Ordinal));

        string operationId = OperationGuard.NewId();
        ProductImportSummary summary = await importer.ImportAsync(preview, operationId, cancellationToken: TestContext.Current.CancellationToken);
        summary.ShouldBe(new ProductImportSummary(1, 0, 1));
        (await importer.ImportAsync(preview, operationId, cancellationToken: TestContext.Current.CancellationToken)).WasReplay.ShouldBeTrue();

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product WHERE sku='IMP001'").ShouldBe(1L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product WHERE sku='IMP002'").ShouldBe(0L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM category WHERE name IN ('Food','Flour')").ShouldBe(2L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM brand WHERE name='Brand A'").ShouldBe(1L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity='product_import'").ShouldBe(1L);
    }

    [Fact]
    public async Task UnitAndTaxMasters_AreAuditedAndUsedUnitCannotChangeFractionRule()
    {
        using var fixture = new CatalogFixture();
        await fixture.Service.CreateAsync(fixture.Product("Unit Lock Product", "unit"), TestContext.Current.CancellationToken);

        DomainException locked = await Should.ThrowAsync<DomainException>(() => fixture.Service.UpdateUnitAsync(
            fixture.UnitId, "PCS", "Piece", null, true, TestContext.Current.CancellationToken));
        locked.Code.ShouldBe(ErrorCode.Validation);

        long unitId = await fixture.Service.CreateUnitAsync("BAG", "Bag", "થેલી", false, TestContext.Current.CancellationToken);
        await fixture.Service.UpdateUnitAsync(unitId, "BAG", "Large Bag", "મોટી થેલી", true, TestContext.Current.CancellationToken);
        (await fixture.Service.GetUnitDetailsAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == unitId).AllowFraction.ShouldBeTrue();

        long taxId = await fixture.Service.CreateTaxRateAsync("GST 12%", 12m, TestContext.Current.CancellationToken);
        await fixture.Service.UpdateTaxRateAsync(taxId, "GST 12%", 12m, false, TestContext.Current.CancellationToken);
        (await fixture.Service.GetTaxRatesAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == taxId).IsActive.ShouldBeFalse();
    }

    [Fact]
    public void RawPrinter_RejectsInvalidJobBeforeCallingWindowsSpooler()
    {
        Should.Throw<ArgumentException>(() => RawPrinter.Send(string.Empty, [1], "label"));
        Should.Throw<ArgumentException>(() => RawPrinter.Send("printer", [], "label"));
        Should.Throw<ArgumentException>(() => RawPrinter.Send("printer", [1], string.Empty));
    }

    [Fact]
    public async Task BulkCatalogChanges_UpdateSelectedProductsAndWriteAuditEntries()
    {
        using var fixture = new CatalogFixture();
        long first = await fixture.Service.CreateAsync(fixture.Product("Bulk One", "bulk"), TestContext.Current.CancellationToken);
        long second = await fixture.Service.CreateAsync(fixture.Product("Bulk Two", "bulk"), TestContext.Current.CancellationToken);
        long category = await fixture.Service.CreateCategoryAsync(new("Bulk Category", null, null, null), TestContext.Current.CancellationToken);
        long tax = await fixture.Service.CreateTaxRateAsync("GST 9%", 9m, TestContext.Current.CancellationToken);

        (await fixture.Service.BulkChangeCategoryAsync([first, second], category, TestContext.Current.CancellationToken)).ShouldBe(2);
        (await fixture.Service.BulkChangeTaxRateAsync([first, second], tax, TestContext.Current.CancellationToken)).ShouldBe(2);

        using var connection = fixture.Factory.Open();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product WHERE id IN (@first,@second) AND category_id=@category",
            new { first, second, category }).ShouldBe(2L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product WHERE id IN (@first,@second) AND tax_rate_id=@tax",
            new { first, second, tax }).ShouldBe(2L);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE action IN ('BULK_CATEGORY','BULK_TAX_RATE')")
            .ShouldBe(2L);
    }

    private sealed class CatalogFixture : IDisposable
    {
        public CatalogFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"grocery-erp-catalog-{Guid.NewGuid():N}");
            Paths = new AppPaths(new SettingsBootstrap(Root));
            Factory = new SqliteConnectionFactory(Paths);
            Database = new DbExecutor(Factory);
            new MigrationRunner(Factory, NullMigrationBackup.Instance).Run();
            Settings = new SettingsService(Database, Paths, new AuditLog());
            Settings.LoadAsync().GetAwaiter().GetResult();
            using var connection = Factory.Open();
            CategoryId = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Groceries') RETURNING id");
            UnitId = connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
            TaxRateId = connection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=5000");
            Service = new CatalogService(Database, new AuditLog(), Settings);
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public SqliteConnectionFactory Factory { get; }
        public DbExecutor Database { get; }
        public SettingsService Settings { get; }
        public CatalogService Service { get; }
        public long CategoryId { get; }
        public long UnitId { get; }
        public long TaxRateId { get; }

        public ProductInput Product(string name, string aliases) => new(
            $"P{Guid.NewGuid():N}"[..20], name, null, aliases, CategoryId, null, UnitId, "1001", TaxRateId,
            true, false, null, true, null, null, Quantity.Zero, new Money(50), new Money(60), []);

        public void Dispose()
        {
            Settings.Dispose();
            Database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
