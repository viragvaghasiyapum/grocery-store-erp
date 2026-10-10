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
using GroceryErp.Reports;
using GroceryErp.Sales;

if (args.Length < 1)
    throw new ArgumentException("Usage: GroceryErp.ScaleAudit <data-root> [read|catalog|index-trial|migrate|write|enrich|void|feature-write|concurrent|backup|restore-race|export|history-export|verify|data-shape|ui-setup] [output.json]");

string root = Path.GetFullPath(args[0]);
string mode = args.Length > 1 ? args[1] : "read";
string output = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.Combine(root, $"scale-audit-{mode}.json");
var paths = new AppPaths(new SettingsBootstrap(root));
if (!File.Exists(paths.DatabaseFile)) throw new FileNotFoundException("Benchmark database is missing.", paths.DatabaseFile);
DapperSetup.Register();
var factory = new SqliteConnectionFactory(paths);
using var database = new DbExecutor(factory);
var audit = new AuditLog();
using var settings = new SettingsService(database, paths, audit);
await settings.LoadAsync();
IClock clock = new AuditClock(new DateTime(2026, 9, 26, 12, 0, 0));
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
        results.Add(result);
        Console.WriteLine($"OK {name}: {result.Milliseconds:0.0} ms, rows={result.Rows}, working={result.WorkingSetAfter / 1048576.0:0} MiB");
    }
    catch (Exception exception)
    {
        timer.Stop();
        process.Refresh();
        var result = new Measurement(name, timer.Elapsed.TotalMilliseconds, 0, before,
            process.WorkingSet64, process.PeakWorkingSet64, exception.GetType().Name + ": " + exception.Message);
        results.Add(result);
        Console.WriteLine($"FAIL {name}: {result.Milliseconds:0.0} ms, {result.Error}");
    }
    Console.Out.Flush();
}

DateOnly end = new(2026, 9, 26);
DateOnly monthStart = new(2026, 9, 1);
DateOnly fiscalStart = new(2026, 4, 1);
switch (mode)
{
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
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULTS {output}");

internal sealed record Measurement(string Name, double Milliseconds, int Rows, long WorkingSetBefore,
    long WorkingSetAfter, long PeakWorkingSet, string? Error);

internal sealed class AuditClock(DateTime now) : IClock
{
    public DateTime Now => now;
    public DateOnly BusinessDate => DateOnly.FromDateTime(now);
}
