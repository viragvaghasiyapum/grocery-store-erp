using System.Diagnostics;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using GroceryErp.Assets;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Purchasing;
using GroceryErp.Printing;
using GroceryErp.Reports;
using GroceryErp.Sales;

if (args.Length < 1)
    throw new ArgumentException("Usage: GroceryErp.ScaleAudit <data-root> [read|gap-read|gap-scan|gap-projection|gap-seed|gap-payroll-seed|whole-count|weekly-maintenance|weekly-maintenance-overlap|opening-preview|product-preview|product-import|product-import-overlap|data-move|a4-render|print-render|catalog|index-trial|migrate|write|enrich|void|feature-write|concurrent|backup|restore-race|export|history-export|verify|data-shape|ui-setup] [output.json]");

string root = Path.GetFullPath(args[0]);
string mode = args.Length > 1 ? args[1] : "read";
string output = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.Combine(root, $"scale-audit-{mode}.json");
if (mode == "data-move")
{
    string target = root + "-moved";
    string bootstrap = Path.Combine(root, "audit-data-move-bootstrap.json");
    SettingsBootstrap.Save(root, bootstrap);
    if (!DataRootRelocator.HasPending(root)) DataRootRelocator.Schedule(root, target);
    long sourceBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Sum(static file => new FileInfo(file).Length);
    var moveTimer = Stopwatch.StartNew();
    bool moved = DataRootRelocator.ApplyPending(bootstrap);
    moveTimer.Stop();
    if (!moved || !File.Exists(Path.Combine(target, "db", "grocery.db")))
        throw new InvalidDataException("The data-root move did not complete.");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, JsonSerializer.Serialize(new { mode, root, target, sourceBytes,
        milliseconds = moveTimer.Elapsed.TotalMilliseconds, moved }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"OK data-root-move-and-verify: {moveTimer.Elapsed.TotalMilliseconds:0.0} ms, source bytes={sourceBytes}");
    Console.WriteLine($"RESULTS {output}");
    return;
}
var paths = new AppPaths(new SettingsBootstrap(root));
if (!File.Exists(paths.DatabaseFile)) throw new FileNotFoundException("Benchmark database is missing.", paths.DatabaseFile);
DapperSetup.Register();
var factory = new SqliteConnectionFactory(paths);
using var database = new DbExecutor(factory);
var audit = new AuditLog();
using var settings = new SettingsService(database, paths, audit);
await settings.LoadAsync();
var clock = new AuditClock(new DateTime(2026, 9, 26, 12, 0, 0));
var numbers = new DocNumberService(settings);
var periods = new PeriodGuard(settings);
var ledger = new StockLedger(settings);
var catalog = new CatalogService(database, audit, settings);
var inventory = new InventoryService(database, ledger, numbers, periods, audit);
var purchasing = new PurchasingService(database, ledger, numbers, periods, settings, audit);
var sales = new SalesService(database, ledger, numbers, periods, settings, audit, clock);
var moneyOut = new MoneyOutService(database, ledger, numbers, periods, audit);
var assets = new AssetService(database, settings, periods, audit, clock);
var reports = new ReportService(database, settings, clock);
var scans = new ScanResolver(database);
var backups = new BackupService(paths, factory, clock, settings);
var maintenance = new MaintenanceService(paths, factory, database, backups, clock, audit);
var results = new List<Measurement>();
var queryPlans = new List<QueryPlan>();
using var process = Process.GetCurrentProcess();
long databaseBytes = new FileInfo(paths.DatabaseFile).Length;

using (var connection = factory.Open())
{
    foreach (string table in new[] { "product", "product_lot", "purchase", "sale", "sale_line", "sale_payment", "stock_movement", "vendor", "audit_log" })
    {
        long count = connection.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}");
        Console.WriteLine($"ROWS {table} {count}");
    }
    Console.WriteLine($"DATABASE_BYTES {databaseBytes}");
}

async Task Measure<T>(string name, Func<Task<T>> action, Func<T, int> count)
{
    process.Refresh();
    long before = process.WorkingSet64;
    var timer = Stopwatch.StartNew();
    try
    {
        T value = await action();
        timer.Stop();
        process.Refresh();
        var result = new Measurement(name, timer.Elapsed.TotalMilliseconds, count(value), before,
            process.WorkingSet64, process.PeakWorkingSet64, null);
        lock (results) results.Add(result);
        Console.WriteLine($"OK {name}: {result.Milliseconds:0.0} ms, rows={result.Rows}, working={result.WorkingSetAfter / 1048576.0:0} MiB");
    }
    catch (Exception exception)
    {
        timer.Stop();
        process.Refresh();
        var result = new Measurement(name, timer.Elapsed.TotalMilliseconds, 0, before,
            process.WorkingSet64, process.PeakWorkingSet64, exception.GetType().Name + ": " + exception.Message);
        lock (results) results.Add(result);
        Console.WriteLine($"FAIL {name}: {result.Milliseconds:0.0} ms, {result.Error}");
    }
    Console.Out.Flush();
}

void CapturePlan(string name, string sql, object? parameters = null)
{
    using var connection = factory.Open();
    string[] details = connection.Query("EXPLAIN QUERY PLAN " + sql, parameters)
        .Select(static row => Convert.ToString(((IDictionary<string, object>)row)["detail"], System.Globalization.CultureInfo.InvariantCulture) ?? "")
        .ToArray();
    queryPlans.Add(new QueryPlan(name, details));
    Console.WriteLine("PLAN " + name + ": " + string.Join(" | ", details));
}

DateOnly end = new(2026, 9, 26);
DateOnly monthStart = new(2026, 9, 1);
DateOnly fiscalStart = new(2026, 4, 1);
switch (mode)
{
    case "gap-scan":
    {
        string weightCode;
        string gs1Code;
        long expectedWeightProduct;
        long expectedGs1Lot;
        using (var connection = factory.Open())
        {
            expectedWeightProduct = connection.ExecuteScalar<long>("SELECT id FROM product WHERE is_loose=1 ORDER BY id LIMIT 1");
            string plu = expectedWeightProduct.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (plu.Length > 5) throw new InvalidDataException("The scale audit PLU exceeds five digits.");
            connection.Execute("UPDATE product SET plu_code=@plu WHERE id=@id", new { plu, id = expectedWeightProduct });
            connection.Execute("""
                INSERT INTO barcode_rule(name,prefix,total_length,item_start,item_length,value_start,value_length,value_kind,value_scale)
                SELECT 'Scale audit weight','22',13,3,5,8,5,'WEIGHT',1
                WHERE NOT EXISTS (SELECT 1 FROM barcode_rule WHERE name='Scale audit weight')
                """);
            string weightBody = "22" + expectedWeightProduct.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "00123";
            weightCode = weightBody + Ean.CheckDigit(weightBody);
            var gs1 = connection.QueryFirst<(long LotId, string Barcode, string Batch, string Expiry)>("""
                SELECT l.id AS LotId,pb.barcode AS Barcode,l.batch_no AS Batch,l.expiry_date AS Expiry
                FROM product_lot l JOIN product_barcode pb ON pb.product_id=l.product_id
                WHERE l.batch_no IS NOT NULL AND l.expiry_date IS NOT NULL
                ORDER BY l.id LIMIT 1
                """);
            expectedGs1Lot = gs1.LotId;
            string expiry = DateOnly.ParseExact(gs1.Expiry, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                .ToString("yyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            gs1Code = "(01)0" + gs1.Barcode + "(17)" + expiry + "(10)" + gs1.Batch;
        }
        await Measure("scan-valid-weight-rule", async () =>
        {
            ScanResult result = await scans.ResolveAsync(weightCode);
            if (result.Kind != ScanResultKind.Weight || result.ProductId != expectedWeightProduct)
                throw new InvalidDataException("The weighted scale code did not resolve the expected product.");
            return result;
        }, static _ => 1);
        await Measure("scan-valid-gs1-batch-expiry", async () =>
        {
            ScanResult result = await scans.ResolveAsync(gs1Code);
            if (result.Kind != ScanResultKind.Item || result.LotId != expectedGs1Lot)
                throw new InvalidDataException("The GS1 code did not resolve the expected lot.");
            return result;
        }, static _ => 1);
        break;
    }
    case "gap-projection":
        await Measure("salary-matrix-five-year-limit-100", () => reports.GetReportAsync(
            ReportKind.SalaryRegister, new ReportFilter(From: new DateOnly(2024, 1, 1), To: new DateOnly(2028, 12, 31), Limit: 100)),
            static x => x.Rows.Count * x.Columns.Count);
        await Measure("asset-schedule-through-2040-limit-100", () => reports.GetReportAsync(
            ReportKind.AssetSchedule, new ReportFilter(From: new DateOnly(2025, 1, 1), To: new DateOnly(2040, 12, 31),
                AssetId: 1, Limit: 100)), static x => x.Rows.Count);
        break;
    case "product-preview":
    case "product-import":
    case "product-import-overlap":
    {
        string previewPath = Path.Combine(paths.TempDir, "audit-product-preview-20000.xlsx");
        string unit;
        decimal gst;
        using (var connection = factory.Open())
        {
            unit = connection.ExecuteScalar<string>("SELECT code FROM uom ORDER BY id LIMIT 1")!;
            gst = connection.ExecuteScalar<long>("SELECT rate_pct FROM tax_rate WHERE is_active=1 ORDER BY rate_pct LIMIT 1") / 1000m;
        }
        await Measure("product-preview-workbook-build-20000", () => Task.Run(() =>
        {
            using var workbook = new XLWorkbook();
            IXLWorksheet sheet = workbook.AddWorksheet("Products");
            for (int column = 0; column < ProductExcelImporter.Headers.Length; column++)
                sheet.Cell(1, column + 1).Value = ProductExcelImporter.Headers[column];
            for (int index = 1; index <= 20000; index++)
            {
                int row = index + 1;
                sheet.Cell(row, 1).Value = $"SCAL{index:000000}";
                sheet.Cell(row, 2).Value = $"Scale Preview Product {index}";
                sheet.Cell(row, 5).Value = "Scale Category";
                sheet.Cell(row, 8).Value = unit;
                sheet.Cell(row, 9).Value = "1000";
                sheet.Cell(row, 10).Value = gst;
                sheet.Cell(row, 12).Value = 10;
                sheet.Cell(row, 13).Value = 20;
                sheet.Cell(row, 16).Value = "N";
                sheet.Cell(row, 18).Value = "Y";
            }
            workbook.SaveAs(previewPath);
            return 20000;
        }), static x => x);
        var productImporter = new ProductExcelImporter(database, audit, settings);
        ProductImportPreview? productPreview = null;
        await Measure("product-preview-valid-20000", async () =>
        {
            productPreview = await productImporter.DryRunAsync(previewPath);
            return productPreview;
        }, static x => x.ValidCount);
        if (productPreview is not null && productPreview.ValidCount == 20000)
        {
            if (mode == "product-import")
                await Measure("product-import-commit-20000", () =>
                    productImporter.ImportAsync(productPreview, OperationGuard.NewId()),
                    static x => x.Created + x.Updated);
            else if (mode == "product-import-overlap")
            {
                long productId;
                using (var connection = factory.Open())
                    productId = connection.ExecuteScalar<long>("""
                        SELECT product_id FROM product_lot WHERE qty_on_hand>1000 AND is_active=1
                        AND expiry_date>='2026-09-26' ORDER BY qty_on_hand DESC LIMIT 1
                        """);
                Task importTask = Measure("product-import-commit-20000", () =>
                    productImporter.ImportAsync(productPreview, OperationGuard.NewId()),
                    static x => x.Created + x.Updated);
                await Task.Delay(100);
                long saleId = 0;
                await Measure("sale-add-during-product-import", async () =>
                    saleId = await sales.AddProductAsync(null, productId, Quantity.One), static _ => 1);
                if (saleId != 0)
                {
                    SaleDetails details = await sales.GetAsync(saleId);
                    await Measure("sale-commit-after-product-import", () =>
                        sales.CompleteAsync(new CompleteSaleCommand(saleId,
                            [new PaymentInput(PaymentMode.Cash, details.Totals.GrandTotal)], OperationGuard.NewId())),
                        static _ => 1);
                }
                await importTask;
            }
        }
        break;
    }
    case "opening-preview":
    {
        string previewPath = Path.Combine(paths.TempDir, "audit-opening-preview-20000.xlsx");
        using (var connection = factory.Open())
        {
            string[] skus = connection.Query<string>("SELECT sku FROM product ORDER BY id LIMIT 2000").ToArray();
            await Measure("opening-preview-workbook-build-20000", () => Task.Run(() =>
            {
                using var workbook = new XLWorkbook();
                IXLWorksheet sheet = workbook.AddWorksheet("Opening Stock");
                for (int index = 0; index < 20000; index++)
                {
                    int row = index + 2;
                    sheet.Cell(row, 1).Value = skus[index % skus.Length];
                    sheet.Cell(row, 2).Value = 1;
                    sheet.Cell(row, 3).Value = 10;
                    sheet.Cell(row, 4).Value = 15;
                    sheet.Cell(row, 6).Value = new DateTime(2027, 9, 26);
                    sheet.Cell(row, 8).Value = new DateTime(2026, 9, 26);
                }
                workbook.SaveAs(previewPath);
                return 20000;
            }), static x => x);
        }
        await Measure("opening-preview-valid-20000", () =>
            new OpeningStockExcelImporter(database, inventory, clock).DryRunAsync(previewPath), static x => x.ValidCount);
        break;
    }
    case "weekly-maintenance":
        await Measure("after-shell-backup-and-weekly-check", async () =>
        {
            await maintenance.RunAfterShellAsync();
            return 1;
        }, static x => x);
        break;
    case "weekly-maintenance-overlap":
    {
        Task maintenanceTask = Measure("after-shell-backup-and-weekly-check", async () =>
        {
            await maintenance.RunAfterShellAsync();
            return 1;
        }, static x => x);
        await Task.Delay(1000);
        long productId;
        using (var connection = factory.Open())
            productId = connection.ExecuteScalar<long>("""
                SELECT product_id FROM product_lot WHERE qty_on_hand>1000 AND is_active=1
                AND expiry_date>='2026-09-26' ORDER BY qty_on_hand DESC LIMIT 1
                """);
        for (int index = 0; index < 20; index++)
        {
            long saleId = 0;
            await Measure("maintenance-sale-add-" + index, async () =>
                saleId = await sales.AddProductAsync(null, productId, Quantity.One), static _ => 1);
            if (saleId == 0) continue;
            SaleDetails details = await sales.GetAsync(saleId);
            await Measure("maintenance-sale-commit-" + index,
                () => sales.CompleteAsync(new CompleteSaleCommand(saleId,
                    [new PaymentInput(PaymentMode.Cash, details.Totals.GrandTotal)], OperationGuard.NewId())),
                static _ => 1);
        }
        await maintenanceTask;
        break;
    }
    case "whole-count":
    {
        long countId = 0;
        await Measure("whole-count-create", async () => countId = await inventory.CreateStockCountAsync(end, null, "Scale whole count"), static _ => 1);
        if (countId == 0) break;
        StockCountDetails? count = null;
        await Measure("whole-count-load", async () => count = await inventory.GetStockCountAsync(countId), static x => x?.Lines.Count ?? 0);
        if (count is null) break;
        await Measure("whole-count-search-client", () => Task.Run(() =>
        {
            List<StockCountLine> lines = count.Lines.ToList();
            return lines.FindIndex(line => line.LotCode == count.Lines[^1].LotCode);
        }), static x => x < 0 ? 0 : 1);
        await Measure("whole-count-drafts-summary", () => inventory.GetDraftCountsAsync(), static x => x.Count);
        StockCountLine first = count.Lines[0];
        await Measure("whole-count-set-one", async () =>
        {
            await inventory.SetCountedQuantityAsync(countId, first.LotId, first.Expected);
            return 1;
        }, static x => x);
        var quantities = count.Lines.ToDictionary(static line => line.LotId, static line => line.Expected);
        await Measure("whole-count-enter-all", async () =>
        {
            await inventory.SetCountedQuantitiesAsync(countId, quantities);
            return quantities.Count;
        }, static x => x);
        await Measure("whole-count-post", async () =>
        {
            await inventory.PostStockCountAsync(countId, OperationGuard.NewId());
            return 1;
        }, static x => x);
        break;
    }
    case "a4-render":
    {
        TabularReport printReport = await reports.GetReportAsync(ReportKind.SalesRegister,
            new ReportFilter(From: new DateOnly(2021, 9, 27), To: end, Limit: 5000));
        await Measure("a4-build-5000-rows", async () =>
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    int pages = new ReportOutputService(settings).CreateA4(printReport, clock.Now).Pages.Count;
                    completion.TrySetResult(pages);
                }
                catch (Exception exception) { completion.TrySetException(exception); }
            }) { IsBackground = true, Name = "Scale A4 render" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task;
        }, static x => x);
        break;
    }
    case "print-render":
    {
        TsplLabel[] labels = Enumerable.Range(1, 50000)
            .Select(static number => new TsplLabel("Scale Item", null, $"SCALE{number:00000}", 99m, null))
            .ToArray();
        await Measure("label-batch-json-50000", () => Task.Run(() => JsonSerializer.Serialize(labels).Length), static x => x);
        await Measure("label-batch-bytes-50000", () => Task.Run(() => TsplLabelBuilder.BuildBatch(labels).Length), static x => x);
        break;
    }
    case "gap-payroll-seed":
    {
        await Measure("seed-employees-and-salaries", () => Task.Run(() =>
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            connection.Execute("""
                WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<5000)
                INSERT INTO employee(name,role,join_date,monthly_salary)
                SELECT 'Scale Employee ' || printf('%05d',x),'Fixture','2025-01-01',1000000 FROM seq;
                """, transaction: transaction);
            connection.Execute("""
                WITH RECURSIVE months(m) AS (VALUES(1) UNION ALL SELECT m+1 FROM months WHERE m<12)
                INSERT INTO salary_payment(employee_id,period_month,pay_date,kind,amount,mode)
                SELECT e.id,printf('2025-%02d',m),printf('2025-%02d-28',m),'SALARY',1000000,'BANK'
                FROM employee e CROSS JOIN months WHERE e.name LIKE 'Scale Employee %';
                """, transaction: transaction);
            transaction.Commit();
            return 65000;
        }), static x => x);
        break;
    }
    case "gap-seed":
    {
        await Measure("seed-customer-draft-asset-purchase", () => Task.Run(() =>
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            connection.Execute("""
                WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<50000)
                INSERT INTO customer(name,phone)
                SELECT 'Scale Customer ' || printf('%05d',x), printf('9%09d',x) FROM seq;
                """, transaction: transaction);
            connection.Execute("""
                INSERT INTO sale(sale_date,customer_id,status,notes)
                SELECT '2026-09-26',id,CASE WHEN id%2=0 THEN 'HELD' ELSE 'DRAFT' END,'Scale picker fixture'
                FROM customer WHERE name LIKE 'Scale Customer %' ORDER BY id LIMIT 10000;
                """, transaction: transaction);
            connection.Execute("""
                INSERT INTO asset(name,category,purchase_date,in_use_from,cost,method,rate_pct)
                SELECT 'Scale Asset ' || printf('%05d',id),'Fixture','2025-01-01','2025-01-01',100000,'SLM',10000
                FROM customer WHERE name LIKE 'Scale Customer %' ORDER BY id LIMIT 5000;
                """, transaction: transaction);
            connection.Execute("""
                WITH RECURSIVE months(m) AS (VALUES(1) UNION ALL SELECT m+1 FROM months WHERE m<12)
                INSERT INTO asset_depreciation(asset_id,period_month,opening_value,amount,closing_value)
                SELECT a.id,printf('2025-%02d',m),100000-(m-1)*1000,1000,100000-m*1000
                FROM asset a CROSS JOIN months WHERE a.name LIKE 'Scale Asset %';
                """, transaction: transaction);
            connection.Execute("""
                WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<600)
                INSERT INTO purchase(doc_no,vendor_id,received_date,status,grand_total)
                SELECT 'SCALE-GRN-' || printf('%05d',x),1,'2026-09-26','POSTED',0 FROM seq;
                """, transaction: transaction);
            transaction.Commit();
            return 50000 + 10000 + 5000 + 600;
        }), static x => x);
        await Measure("seed-whole-store-draft-count", () => inventory.CreateStockCountAsync(end, null, "Scale count fixture"), static _ => 1);
        break;
    }
    case "gap-read":
    {
        await Measure("search-empty-30", () => catalog.SearchAsync(new ProductSearchFilter(Limit: 30)), static x => x.Count);
        await Measure("search-short-g", () => catalog.SearchAsync(new ProductSearchFilter("g", Limit: 30)), static x => x.Count);
        await Measure("search-short-gr", () => catalog.SearchAsync(new ProductSearchFilter("gr", Limit: 30)), static x => x.Count);
        await Measure("search-fts", () => catalog.SearchAsync(new ProductSearchFilter("grocery item", Limit: 30)), static x => x.Count);
        await Measure("search-mixed-term", () => catalog.SearchAsync(new ProductSearchFilter("grocery i", Limit: 30)), static x => x.Count);
        await Measure("search-category", () => catalog.SearchAsync(new ProductSearchFilter(CategoryId: 1, Limit: 30)), static x => x.Count);
        await Measure("search-brand", () => catalog.SearchAsync(new ProductSearchFilter(BrandId: 1, Limit: 30)), static x => x.Count);
        await Measure("search-no-barcode", () => catalog.SearchAsync(new ProductSearchFilter(NoBarcodeOnly: true, Limit: 30)), static x => x.Count);
        await Measure("search-missing-local", () => catalog.SearchAsync(new ProductSearchFilter(MissingLocalNameOnly: true, Limit: 30)), static x => x.Count);
        await Measure("search-loose", () => catalog.SearchAsync(new ProductSearchFilter(LooseOnly: true, Limit: 30)), static x => x.Count);
        await Measure("search-combined", () => catalog.SearchAsync(new ProductSearchFilter("grocery", CategoryId: 1, LowStockOnly: true, Limit: 30)), static x => x.Count);

        await Measure("vendors-all", () => purchasing.GetVendorsAsync(includeInactive: true), static x => x.Count);
        await Measure("vendor-purchase-choices", () => purchasing.GetVendorPurchaseChoicesAsync(1), static x => x.Count);
        await Measure("purchases-5000", () => purchasing.GetPurchasesAsync(5000), static x => x.Count);
        await Measure("inventory-valuation", () => inventory.GetValuationAsync(), static x => x.Count);
        await Measure("draft-stock-counts", () => inventory.GetDraftCountsAsync(), static x => x.Count);
        await Measure("customer-empty", () => sales.SearchCustomersAsync(), static x => x.Count);
        await Measure("customer-contains-miss", () => sales.SearchCustomersAsync("son"), static x => x.Count);
        await Measure("customer-contains-match", () => sales.SearchCustomersAsync("Scale Customer 4"), static x => x.Count);
        await Measure("customer-phone", () => sales.SearchCustomersAsync("9000040000"), static x => x.Count);
        await Measure("held-bills", () => sales.GetHeldAsync(), static x => x.Count);
        await Measure("saved-drafts", () => sales.GetDraftsAsync(), static x => x.Count);
        await Measure("assets-all", () => assets.GetAssetsAsync(), static x => x.Count);
        await Measure("asset-schedule-posted", () => assets.GetScheduleAsync(41), static x => x.Count);

        await Measure("history-date", () => sales.GetHistoryAsync(new SaleHistoryFilter(From: monthStart, To: end)), static x => x.Count);
        await Measure("history-status", () => sales.GetHistoryAsync(new SaleHistoryFilter(Status: SaleStatus.Completed)), static x => x.Count);
        await Measure("history-payment", () => sales.GetHistoryAsync(new SaleHistoryFilter(PaymentMode: PaymentMode.Upi)), static x => x.Count);
        await Measure("history-amount", () => sales.GetHistoryAsync(new SaleHistoryFilter(MinimumAmount: new Money(100), MaximumAmount: new Money(500))), static x => x.Count);
        await Measure("history-invoice", () => sales.GetHistoryAsync(new SaleHistoryFilter(InvoiceNumber: "2026")), static x => x.Count);
        await Measure("history-combined", () => sales.GetHistoryAsync(new SaleHistoryFilter(From: fiscalStart, To: end, Status: SaleStatus.Completed, PaymentMode: PaymentMode.Cash, MinimumAmount: new Money(100), Limit: 100)), static x => x.Count);

        await Measure("expiry-expired", () => moneyOut.GetExpiryAsync("EXPIRED"), static x => x.Count);
        await Measure("damages-year", () => moneyOut.GetDamagesAsync(end.AddYears(-1), end), static x => x.Count);
        await Measure("expenses-year", () => moneyOut.GetExpensesAsync(end.AddYears(-1), end), static x => x.Count);
        await Measure("employees-all", () => moneyOut.GetEmployeesAsync(includeInactive: true), static x => x.Count);
        await Measure("salary-register-year", () => moneyOut.GetSalaryRegisterAsync(new YearMonth(2025, 10), new YearMonth(2026, 9)), static x => x.Count);
        await Measure("salary-matrix-year", () => reports.GetReportAsync(ReportKind.SalaryRegister, new ReportFilter(From: end.AddYears(-1), To: end, Limit: 100)), static x => x.Rows.Count);
        await Measure("asset-schedule-year", () => reports.GetReportAsync(ReportKind.AssetSchedule, new ReportFilter(From: end.AddYears(-1), To: end, AssetId: 1, Limit: 100)), static x => x.Rows.Count);
        await Measure("report-asset-register", () => reports.GetReportAsync(ReportKind.AssetRegister, new ReportFilter(To: end, Limit: 5000)), static x => x.Rows.Count);
        await Measure("report-salary-matrix-uncapped", () => reports.GetReportAsync(ReportKind.SalaryRegister, new ReportFilter(From: new DateOnly(2025, 1, 1), To: new DateOnly(2025, 12, 31), Limit: 100)), static x => x.Rows.Count);

        await Measure("report-sales-status-payment", () => reports.GetReportAsync(ReportKind.SalesRegister, new ReportFilter(From: fiscalStart, To: end, Status: "COMPLETED", PaymentMode: "UPI", Limit: 100)), static x => x.Rows.Count);
        await Measure("report-item-product", () => reports.GetReportAsync(ReportKind.ItemSales, new ReportFilter(From: fiscalStart, To: end, ProductId: 1, Limit: 100)), static x => x.Rows.Count);
        await Measure("report-ledger-lot", () => reports.GetReportAsync(ReportKind.StockLedger, new ReportFilter(From: fiscalStart, To: end, ProductId: 1, LotId: 1, Limit: 100)), static x => x.Rows.Count);
        await Measure("report-expiry-bucket", () => reports.GetReportAsync(ReportKind.Expiry, new ReportFilter(Status: "EXPIRED", Limit: 100)), static x => x.Rows.Count);
        await Measure("report-payables-vendor", () => reports.GetReportAsync(ReportKind.VendorPayables, new ReportFilter(VendorId: 1, Limit: 100)), static x => x.Rows.Count);
        await Measure("report-purchases-vendor", () => reports.GetReportAsync(ReportKind.PurchaseRegister, new ReportFilter(From: fiscalStart, To: end, VendorId: 1, Limit: 100)), static x => x.Rows.Count);
        await Measure("report-audit-filter", () => reports.GetReportAsync(ReportKind.AuditLog, new ReportFilter(From: fiscalStart, To: end, Entity: "product", Action: "IMPORT", Limit: 100)), static x => x.Rows.Count);

        using (var connection = factory.Open())
        {
            long barcodeProduct = connection.ExecuteScalar<long>("SELECT product_id FROM product_barcode ORDER BY id LIMIT 1");
            string barcode = connection.ExecuteScalar<string>("SELECT barcode FROM product_barcode ORDER BY id LIMIT 1")!;
            string sku = connection.ExecuteScalar<string>("SELECT sku FROM product WHERE id=@barcodeProduct", new { barcodeProduct })!;
            await Measure("scan-exact-barcode", () => scans.ResolveAsync(barcode), static x => x.ProductId is null ? 0 : 1);
            await Measure("scan-sku", () => scans.ResolveAsync(sku), static x => x.ProductId is null ? 0 : 1);
            long? pluProduct = connection.ExecuteScalar<long?>("SELECT id FROM product WHERE plu_code IS NOT NULL ORDER BY id LIMIT 1");
            if (pluProduct is not null)
            {
                string plu = connection.ExecuteScalar<string>("SELECT plu_code FROM product WHERE id=@pluProduct", new { pluProduct })!;
                await Measure("scan-plu-as-text", () => scans.ResolveAsync(plu), static x => x.ProductId is null ? 0 : 1);
            }
        }
        CapturePlan("customer-contains", "SELECT id FROM customer WHERE is_active=1 AND (name LIKE @pattern OR phone LIKE @pattern) ORDER BY name,id LIMIT 100", new { pattern = "%Scale%" });
        CapturePlan("vendor-list-balance", "SELECT v.id,b.balance_due FROM vendor v JOIN v_vendor_balance b ON b.vendor_id=v.id WHERE v.is_active=1 ORDER BY v.name COLLATE NOCASE");
        CapturePlan("vendor-purchase-choices", "SELECT id FROM purchase WHERE vendor_id=@vendorId AND status='POSTED' ORDER BY received_date DESC,id DESC LIMIT 500", new { vendorId = 1 });
        CapturePlan("purchase-history", "SELECT id FROM purchase ORDER BY received_date DESC,id DESC LIMIT 5000");
        CapturePlan("history-invoice", "SELECT id FROM sale WHERE invoice_no LIKE @invoice || '%' ORDER BY sale_date DESC,id DESC LIMIT 100", new { invoice = "2026" });
        CapturePlan("salary-register", "SELECT id FROM salary_payment WHERE period_month BETWEEN @from AND @to ORDER BY period_month DESC LIMIT 100", new { from = "2025-10", to = "2026-09" });
        CapturePlan("draft-count-summary", "SELECT sc.id,COUNT(scl.id) FROM stock_count sc JOIN stock_count_line scl ON scl.stock_count_id=sc.id WHERE sc.status='DRAFT' GROUP BY sc.id ORDER BY sc.id DESC");
        break;
    }
    case "catalog":
        await Measure("catalog-exact", () => catalog.SearchAsync(new ProductSearchFilter("grocery item 1", Limit: 30)), static x => x.Count);
        await Measure("catalog-broad-30", () => catalog.SearchAsync(new ProductSearchFilter(Limit: 30)), static x => x.Count);
        await Measure("catalog-broad-20000", () => catalog.SearchAsync(new ProductSearchFilter(Limit: 20_000)), static x => x.Count);
        await Measure("catalog-low-stock", () => catalog.SearchAsync(new ProductSearchFilter(LowStockOnly: true, Limit: 20_000)), static x => x.Count);
        break;
    case "index-trial":
        using (var connection = factory.Open())
        {
            await Measure("create-index-product-lot-product", () => Task.Run(() =>
            {
                connection.Execute("CREATE INDEX IF NOT EXISTS ix_audit_lot_product ON product_lot(product_id)");
                connection.Execute("ANALYZE product_lot");
                return 1;
            }), static _ => 1);
        }
        break;
    case "migrate":
        await Measure("migration-to-latest", () => Task.Run(() =>
        {
            new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
            using var connection = factory.Open();
            int version = connection.ExecuteScalar<int>("PRAGMA user_version");
            if (version != MigrationRunner.LatestVersion)
                throw new InvalidDataException($"Expected schema {MigrationRunner.LatestVersion}, found {version}.");
            return version;
        }), static x => x);
        break;
    case "read":
        await Measure("startup-health", () => maintenance.CheckBeforeShellAsync(), static _ => 1);
        await Measure("barcode-scan", () => scans.ResolveAsync(Ean.Internal("21", 1)), static _ => 1);
        await Measure("catalog-exact", () => catalog.SearchAsync(new ProductSearchFilter("grocery item 1", Limit: 30)), static x => x.Count);
        await Measure("catalog-broad-30", () => catalog.SearchAsync(new ProductSearchFilter(Limit: 30)), static x => x.Count);
        await Measure("catalog-broad-20000", () => catalog.SearchAsync(new ProductSearchFilter(Limit: 20_000)), static x => x.Count);
        await Measure("catalog-low-stock", () => catalog.SearchAsync(new ProductSearchFilter(LowStockOnly: true, Limit: 20_000)), static x => x.Count);
        await Measure("inventory-overview", () => inventory.GetOverviewAsync(), static x => x.Count);
        await Measure("inventory-lots-all", () => inventory.GetLotsAsync(), static x => x.Count);
        await Measure("inventory-ledger-hot", () => inventory.GetLedgerAsync(1, DateOnly.MinValue, DateOnly.MaxValue), static x => x.Count);
        await Measure("expiry-all", () => moneyOut.GetExpiryAsync(), static x => x.Count);
        await Measure("purchases-first", () => purchasing.GetPurchasesAsync(), static x => x.Count);
        await Measure("vendor-ledger", () => purchasing.GetVendorLedgerAsync(1), static x => x.Count);
        await Measure("vendor-lots", () => purchasing.GetVendorLotsAsync(1), static x => x.Count);
        await Measure("sales-history-first", () => sales.GetHistoryAsync(new SaleHistoryFilter(Limit: 100)), static x => x.Count);
        await Measure("sale-receipt", () => sales.GetReceiptAsync(1), static _ => 1);
        await Measure("assets-list", () => assets.GetAssetsAsync(), static x => x.Count);
        await Measure("pnl-month", () => reports.GetPnlAsync(monthStart, end), static x => x.Income.Count + x.CostOfGoodsSold.Count + x.Expenses.Count);
        await Measure("pnl-fiscal", () => reports.GetPnlAsync(fiscalStart, end), static x => x.Income.Count + x.CostOfGoodsSold.Count + x.Expenses.Count);
        await Measure("pnl-matrix", () => reports.GetPnlMatrixAsync(fiscalStart, end), static x => x.Rows.Count);
        await Measure("dashboard", () => reports.GetDashboardAsync(), static _ => 1);
        foreach (ReportKind kind in Enum.GetValues<ReportKind>())
        {
            var filter = new ReportFilter(From: monthStart, To: end, ProductId: kind == ReportKind.StockLedger ? 1 : null,
                AssetId: kind == ReportKind.AssetSchedule ? 1 : null, Limit: 100);
            await Measure("report-" + kind, () => reports.GetReportAsync(kind, filter), static x => x.Rows.Count);
        }
        break;
    case "write":
        using (var connection = factory.Open())
        {
            long productId = connection.ExecuteScalar<long>("""
                SELECT product_id FROM product_lot WHERE qty_on_hand>1000 AND is_active=1
                AND expiry_date>=@today ORDER BY qty_on_hand DESC LIMIT 1
                """, new { today = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
            for (int index = 0; index < 20; index++)
            {
                long saleId = 0;
                await Measure("sale-add-" + index, async () => saleId = await sales.AddProductAsync(null, productId, Quantity.One), static _ => 1);
                if (saleId == 0) continue;
                SaleDetails details = await sales.GetAsync(saleId);
                await Measure("sale-commit-" + index,
                    () => sales.CompleteAsync(new CompleteSaleCommand(saleId,
                        [new PaymentInput(PaymentMode.Cash, details.Totals.GrandTotal)], OperationGuard.NewId())), static _ => 1);
            }
        }
        break;
    case "enrich":
        // Exercise historical document paths against a disposable copy of the generated store.
        using (var connection = factory.Open())
        {
            var returnSources = connection.Query<(long SaleId, long LineId)>("""
                SELECT s.id AS SaleId,sl.id AS LineId FROM sale s JOIN sale_line sl ON sl.sale_id=s.id
                WHERE s.status='COMPLETED' AND s.sale_date<='2026-09-26'
                AND sl.line_no=1 AND s.id BETWEEN 1 AND 100 ORDER BY s.id
                """).ToArray();
            foreach (var source in returnSources)
                await Measure("sale-return-" + source.SaleId,
                    () => sales.ReturnAsync(new(source.SaleId, end, "Scale audit return", RefundMode.Cash,
                        [new(source.LineId, Quantity.One, source.SaleId % 2 == 0)], OperationGuard.NewId())), static _ => 1);

            long[] lots = connection.Query<long>("""
                SELECT id FROM product_lot WHERE qty_on_hand>10000 AND is_active=1
                ORDER BY id DESC LIMIT 100
                """).ToArray();
            foreach (long lotId in lots)
                await Measure("damage-" + lotId,
                    () => moneyOut.RecordDamageAsync(new(lotId, end, Quantity.One, "BROKEN", Money.Zero,
                        "Scale audit damage"), OperationGuard.NewId()), static _ => 1);

            var paymentSources = connection.Query<(long PurchaseId, long VendorId)>("""
                SELECT id AS PurchaseId,vendor_id AS VendorId FROM purchase
                WHERE status='POSTED' ORDER BY id LIMIT 50
                """).ToArray();
            foreach (var source in paymentSources)
                await Measure("vendor-payment-" + source.PurchaseId,
                    () => purchasing.RecordPaymentAsync(new(source.VendorId, source.PurchaseId, end,
                        new Money(1), "CASH", "AUDIT-" + source.PurchaseId, null), OperationGuard.NewId()), static _ => 1);

            for (int purchaseId = 101; purchaseId <= 150; purchaseId++)
            {
                VendorReturnLot? lot = (await purchasing.GetVendorReturnLotsAsync(purchaseId)).FirstOrDefault(
                    static item => item.Returnable >= Quantity.One);
                if (lot is null) continue;
                await Measure("vendor-return-" + purchaseId,
                    () => purchasing.PostVendorReturnAsync(new(purchaseId, end,
                        "AUDIT-CN-" + purchaseId, "Scale audit vendor return",
                        [new(lot.LotId, Quantity.One, new Money(1), Money.Zero)]), OperationGuard.NewId()), static _ => 1);
            }
        }
        break;
    case "void":
        using (var connection = factory.Open())
        {
            long[] saleIds = connection.Query<long>("""
                SELECT s.id FROM sale s WHERE s.id>640000 AND s.status='COMPLETED'
                AND s.sale_date='2026-09-26' AND NOT EXISTS
                (SELECT 1 FROM sale_return r WHERE r.sale_id=s.id)
                ORDER BY s.id LIMIT 10
                """).ToArray();
            foreach (long saleId in saleIds)
                await Measure("sale-void-" + saleId,
                    async () => { await sales.VoidAsync(saleId, "Scale audit void", OperationGuard.NewId()); return 1; },
                    static x => x);
        }
        break;
    case "feature-write":
    {
        long purchaseId = 0;
        await Measure("purchase-save-draft", async () => purchaseId = await purchasing.SaveDraftAsync(null,
            new PurchaseDraftInput(1, "AUDIT-FULL-PURCHASE-001", end, end, null, Money.Zero,
                "Scale audit purchase", [new PurchaseLineInput(1, "AUDIT-BATCH-001", end.AddMonths(-1),
                    end.AddMonths(9), new Quantity(10), Quantity.Zero, new Money(250), Money.Zero,
                    new Percent(5), new Money(350), new Money(300))])), static _ => 1);
        if (purchaseId != 0)
            await Measure("purchase-post", () => purchasing.PostAsync(purchaseId, OperationGuard.NewId()), static _ => 1);

        long adjustmentLot = 0;
        using (var connection = factory.Open())
            adjustmentLot = connection.ExecuteScalar<long>("SELECT id FROM product_lot WHERE qty_on_hand>10000 ORDER BY id DESC LIMIT 1");
        await Measure("stock-adjustment", async () =>
        {
            await inventory.AdjustAsync(adjustmentLot, end, Quantity.One, "Scale audit adjustment", OperationGuard.NewId());
            return 1;
        }, static x => x);

        long countId = 0;
        await Measure("stock-count-create-category", async () =>
            countId = await inventory.CreateStockCountAsync(end, [1], "Scale audit category count"), static _ => 1);
        if (countId != 0)
        {
            StockCountDetails? count = null;
            await Measure("stock-count-load", async () => count = await inventory.GetStockCountAsync(countId),
                static x => x?.Lines.Count ?? 0);
            if (count is not null)
            {
                var quantities = count.Lines.ToDictionary(static line => line.LotId, static line => line.Expected);
                StockCountLine first = count.Lines[0];
                quantities[first.LotId] = new Quantity(first.Expected.Value + 1);
                await Measure("stock-count-enter-all", async () =>
                {
                    await inventory.SetCountedQuantitiesAsync(countId, quantities);
                    return quantities.Count;
                }, static x => x);
                await Measure("stock-count-post", async () =>
                {
                    await inventory.PostStockCountAsync(countId, OperationGuard.NewId());
                    return 1;
                }, static x => x);
            }
        }

        await Measure("expense-record", () => moneyOut.RecordExpenseAsync(
            new(end, 1, new Money(100), "CASH", "Scale audit", null, null), OperationGuard.NewId()), static _ => 1);
        long employeeId = 0;
        await Measure("employee-create", async () => employeeId = await moneyOut.SaveEmployeeAsync(null,
            new("Scale Audit Employee", null, "Auditor", end.AddMonths(-1), null,
                new Money(1000), true, null)), static _ => 1);
        if (employeeId != 0)
            await Measure("salary-post", () => moneyOut.PostSalaryRunAsync(
                new(new YearMonth(2026, 9), end, "BANK",
                    [new SalaryRunLine(employeeId, new Money(1000), Money.Zero, Money.Zero, Money.Zero, null)]),
                OperationGuard.NewId()), static _ => 1);

        long assetId = 0;
        await Measure("asset-create", async () => assetId = await assets.SaveAssetAsync(null,
            new("Scale Audit Asset", "Equipment", end.AddMonths(-1), end.AddMonths(-1),
                new Money(10000), null, DepreciationMethod.Wdv, new Percent(15), Money.Zero, null)), static _ => 1);
        await Measure("asset-depreciation-through-month", () => assets.PostThroughAsync(new YearMonth(2026, 9)),
            static x => x.PostedCount);
        if (assetId != 0)
            await Measure("asset-schedule", () => assets.GetScheduleAsync(assetId), static x => x.Count);
        break;
    }
    case "concurrent":
        using (var connection = factory.Open())
        {
            long productId = connection.ExecuteScalar<long>("""
                SELECT product_id FROM product_lot WHERE qty_on_hand>1000 AND is_active=1
                AND expiry_date>=@today ORDER BY qty_on_hand DESC LIMIT 1
                """, new { today = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
            await Measure("four-catalog-reads-with-twenty-sales", async () =>
            {
                Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
                {
                    for (int iteration = 0; iteration < 5; iteration++)
                        await catalog.SearchAsync(new ProductSearchFilter(Limit: 20_000));
                })).ToArray();
                Task writer = Task.Run(async () =>
                {
                    for (int iteration = 0; iteration < 20; iteration++)
                    {
                        long saleId = await sales.AddProductAsync(null, productId, Quantity.One);
                        SaleDetails details = await sales.GetAsync(saleId);
                        await sales.CompleteAsync(new(saleId,
                            [new PaymentInput(PaymentMode.Cash, details.Totals.GrandTotal)], OperationGuard.NewId()));
                    }
                });
                await Task.WhenAll(readers.Append(writer));
                return 40;
            }, static x => x);
        }
        break;
    case "backup":
        await Measure("backup-create", () => Task.Run(() => backups.Create("scale-audit")), static _ => 1);
        break;
    case "restore-race":
    {
        BackupInfo backup = backups.Create("restore-race");
        RestoreCandidate candidate = backups.InspectRestore(backup.Path);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task reader = database.ReadAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale", transaction: transaction);
            entered.Set();
            release.Wait(TimeSpan.FromMinutes(2));
            transaction.Rollback();
            return 1;
        });
        if (!entered.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The reader did not start.");
        try
        {
            await Measure("restore-with-active-reader-rejected", () => Task.Run(() =>
            {
                try
                {
                    backups.ApplyRestore(candidate);
                    throw new InvalidDataException("Restore unexpectedly succeeded with an active reader.");
                }
                catch (Exception exception) when (exception is not InvalidDataException)
                {
                    Console.WriteLine("RESTORE_REJECTION " + exception.GetType().Name + ": " + exception.Message);
                    if (backups.HasPendingRestore)
                        throw new InvalidDataException("A failed restore left a pending marker.");
                    return 1;
                }
            }), static x => x);
        }
        finally
        {
            release.Set();
            await reader;
            backups.DiscardRestoreCandidate(candidate);
            if (backups.HasPendingRestore) backups.UndoPendingRestore();
        }
        break;
    }
    case "export":
        TabularReport report = await reports.GetReportAsync(ReportKind.SalesRegister,
            new ReportFilter(From: new DateOnly(2021, 9, 27), To: end, Limit: 50_000));
        string exportPath = Path.Combine(paths.ExportsDir, "audit-sales-register.xlsx");
        await Measure("sales-register-xlsx-50000", () => Task.Run(() =>
        {
            new ReportOutputService(settings).ExportExcel(report, exportPath);
            return report;
        }), static x => x.Rows.Count);
        await Measure("xlsx-reopen-and-validate", () => Task.Run(() =>
        {
            using var workbook = new XLWorkbook(exportPath);
            IXLWorksheet sheet = workbook.Worksheet(1);
            int rows = sheet.LastRowUsed()?.RowNumber() ?? 0;
            int headerRow = (report.Period is null ? 3 : 4) + (report.IsTruncated ? 1 : 0);
            int expected = headerRow + report.Rows.Count + (report.Totals.Count > 0 ? 1 : 0);
            if (rows != expected)
                throw new InvalidDataException($"Expected {expected} worksheet rows; found {rows}.");
            if (sheet.Cell(headerRow, 1).GetString() != report.Columns[0].Heading)
                throw new InvalidDataException("The first export header is incorrect.");
            for (int row = 0; row < report.Rows.Count; row++)
            for (int column = 0; column < report.Columns.Count; column++)
            {
                ReportColumn definition = report.Columns[column];
                if (!report.Rows[row].Values.TryGetValue(definition.Key, out object? value) || value is null)
                    continue;
                XLDataType expectedType = definition.Kind switch
                {
                    ReportValueKind.Money or ReportValueKind.Quantity or ReportValueKind.Percent or ReportValueKind.Count
                        => XLDataType.Number,
                    ReportValueKind.Date => XLDataType.DateTime,
                    _ => XLDataType.Text,
                };
                XLDataType actualType = sheet.Cell(headerRow + row + 1, column + 1).DataType;
                if (actualType != expectedType)
                    throw new InvalidDataException($"Export cell {row + 1},{column + 1} is {actualType}, expected {expectedType}.");
            }
            return rows;
        }), static x => x);
        break;
    case "history-export":
    {
        var all = new List<SaleHistoryItem>();
        await Measure("history-fetch-all", async () =>
        {
            DateOnly? beforeDate = null;
            long? beforeId = null;
            while (true)
            {
                IReadOnlyList<SaleHistoryItem> page = await sales.GetHistoryAsync(
                    new SaleHistoryFilter(BeforeDate: beforeDate, BeforeId: beforeId, Limit: 500));
                all.AddRange(page);
                if (all.Count % 50_000 == 0) Console.WriteLine($"HISTORY_ROWS {all.Count}");
                if (page.Count < 500) break;
                beforeDate = page[^1].SaleDate;
                beforeId = page[^1].Id;
            }
            return all.Count;
        }, static x => x);
        string historyPath = Path.Combine(paths.ExportsDir, "audit-sales-history-full.xlsx");
        await Measure("history-export-xlsx", () => Task.Run(() =>
        {
            var report = new TabularReport("Sales history", null,
                [new("Date", "Date", ReportValueKind.Date),
                 new("Invoice", "Invoice", ReportValueKind.Text),
                 new("Status", "Status", ReportValueKind.Text),
                 new("Items", "Items", ReportValueKind.Count),
                 new("Payment", "Payment", ReportValueKind.Text),
                 new("Amount", "Amount", ReportValueKind.Money)],
                all.Select(item => new ReportRow(new Dictionary<string, object?>
                {
                    ["Date"] = item.SaleDate,
                    ["Invoice"] = item.InvoiceNumber,
                    ["Status"] = item.Status.ToString(),
                    ["Items"] = item.LineCount,
                    ["Payment"] = item.PaymentModes,
                    ["Amount"] = item.GrandTotal.Scaled,
                })).ToArray(), new Dictionary<string, decimal>());
            new ReportOutputService(settings).ExportExcel(report, historyPath);
            return report.Rows.Count;
        }), static x => x);
        if (File.Exists(historyPath))
            Console.WriteLine($"HISTORY_EXPORT_BYTES {new FileInfo(historyPath).Length}");
        break;
    }
    case "ui-setup":
        using (var connection = factory.Open())
        {
            if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM app_user") == 0)
                connection.Execute("INSERT INTO app_user(id,username,password_hash) VALUES (1,'owner',@hash)",
                    new { hash = new PasswordHasher().Hash("scale-audit-2026") });
            connection.Execute("""
                INSERT INTO setting(key,value) VALUES ('expiry.popup_on_login','false')
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """);
        }
        break;
    case "data-shape":
        using (var connection = factory.Open())
        {
            await Measure("sale-lines-after-lot-expiry", () => Task.Run(() =>
                connection.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM sale_line sl
                    JOIN sale s ON s.id=sl.sale_id
                    JOIN product_lot pl ON pl.id=sl.lot_id
                    WHERE s.sale_date>pl.expiry_date
                    """)), static x => x);
            await Measure("max-lines-per-sale", () => Task.Run(() =>
                connection.ExecuteScalar<int>("SELECT MAX(line_count) FROM sale")), static x => x);
            await Measure("category-grandchildren", () => Task.Run(() =>
                connection.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM category child
                    JOIN category parent ON parent.id=child.parent_id
                    WHERE parent.parent_id IS NOT NULL
                    """)), static x => x);
        }
        break;
    case "verify":
        using (var connection = factory.Open())
        {
            await Measure("integrity-check", () => Task.Run(() =>
            {
                string value = connection.ExecuteScalar<string>("PRAGMA integrity_check") ?? "";
                if (value != "ok") throw new InvalidDataException(value);
                return 1;
            }), static x => x);
            await Measure("foreign-key-check", () => Task.Run(() =>
            {
                int failures = connection.Query("PRAGMA foreign_key_check").Count();
                if (failures != 0) throw new InvalidDataException($"{failures} foreign key failures.");
                return failures;
            }), static x => x);
            await Measure("lot-cache-mismatch", () => Task.Run(() =>
            {
                long mismatches = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM v_lot_cache_mismatch");
                if (mismatches != 0) throw new InvalidDataException($"{mismatches} lot cache mismatches.");
                return (int)mismatches;
            }), static x => x);
        }
        break;
    default:
        throw new ArgumentException($"Unknown mode '{mode}'.");
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    mode,
    root,
    databaseBytes,
    machine = Environment.MachineName,
    logicalProcessors = Environment.ProcessorCount,
    results,
    queryPlans,
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULTS {output}");

internal sealed record Measurement(string Name, double Milliseconds, int Rows, long WorkingSetBefore,
    long WorkingSetAfter, long PeakWorkingSet, string? Error);

internal sealed record QueryPlan(string Name, IReadOnlyList<string> Details);

internal sealed class AuditClock(DateTime now) : IClock
{
    public DateTime Now => now;
    public DateOnly BusinessDate => DateOnly.FromDateTime(now);
}
