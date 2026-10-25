using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.Sqlite;
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
    throw new ArgumentException("Usage: GroceryErp.ScaleAudit <data-root> [phase9-profile|phase9-export|phase3-purchase-read|phase7-filters|phase1-schema-audit|phase1-rollback|refresh-storm|attribution-profile|history-profile|phase1-read|phase1-export|phase1-low-seed|phase1-count|phase1-history-seed|phase1-history-hot|profile|sustained-profile|shape-profile|report-profile|phase6-reports|phase6-matrix|phase6-category-index|hot-plans|cold-warm|customer-seed|realism-seed|fault-transaction|print-queue-metric|print-journal-profile|phase5-label-failure|phase5-live-write|phase5-return-reconcile|phase5-sales-read|phase5-count-read|phase5-fixture-align|phase5-direct-audit|phase5-direct-crash|phase5-direct-crash-check|phase6-annual|rebuild-pnl-days|optimized-read|hot-index-trial|read|gap-read|gap-scan|gap-projection|gap-seed|gap-payroll-seed|whole-count|weekly-maintenance|weekly-maintenance-overlap|opening-preview|product-preview|product-import|product-import-overlap|data-move|a4-render|print-render|catalog|index-trial|migrate|write|enrich|void|feature-write|concurrent|backup|backup-cancel|phase3-read|phase3-resource|restore-race|restore-crash|restore-recover|export|history-export|dashboard-reconcile|verify|data-shape|ui-setup] [output.json] [duration-seconds or restore-crash-boundary]");

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
if (mode is not ("restore-recover" or "restore-crash" or "phase8-restore") && !File.Exists(paths.DatabaseFile))
    throw new FileNotFoundException("Benchmark database is missing.", paths.DatabaseFile);
DapperSetup.Register();
if (mode is "restore-crash" or "phase8-restore" && !File.Exists(paths.DatabaseFile))
    new MigrationRunner(new SqliteConnectionFactory(paths), NullMigrationBackup.Instance).Run();
var metricSamples = new System.Collections.Concurrent.ConcurrentBag<MetricSample>();
using var meterListener = new MeterListener();
meterListener.InstrumentPublished = (instrument, listener) =>
{
    if (instrument.Meter.Name.StartsWith("GroceryErp.", StringComparison.Ordinal))
        listener.EnableMeasurementEvents(instrument);
};
meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
    metricSamples.Add(new(MetricName(instrument.Name, tags), value)));
meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
    metricSamples.Add(new(MetricName(instrument.Name, tags), value)));
meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
    metricSamples.Add(new(MetricName(instrument.Name, tags), value)));
meterListener.Start();
static string MetricName(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
{
    foreach (KeyValuePair<string, object?> tag in tags)
        if (tag.Key is "operation" or "page" or "phase" && tag.Value is string label)
            name += "." + label;
    return name;
}
var factory = new SqliteConnectionFactory(paths);
var tracedQueries = new System.Collections.Concurrent.ConcurrentDictionary<(string Operation, string Shape), string>();
// The trace hook also fires for every internal FTS5 statement, so timings taken under it run
// slower than the app; "untraced" in a Phase 7 run's fourth argument measures without it.
bool untraced = mode == "phase7-filters" && args.Length > 3 && args[3].Contains("untraced", StringComparison.Ordinal);
if (!untraced && mode is "attribution-profile" or "history-profile" or "shape-profile" or "report-profile" or "phase6-reports" or "gap-read" or "feature-write" or "weekly-maintenance-overlap" or "print-journal-profile" or "phase7-filters")
    factory.SqlTrace = (operation, sql) =>
    {
        string statement = sql.Trim();
        // A write may execute the same statement tens of thousands of times with
        // different synthetic values. Retain one executable example per SQL shape.
        string shape = mode == "feature-write"
            ? System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(statement, "'(?:''|[^'])*'", "'?'") ,
                @"\b\d+(?:\.\d+)?\b", "?")
            : statement;
        if (tracedQueries.Count < 1024 &&
            (statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
             statement.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)))
            tracedQueries.TryAdd((operation, shape), statement);
    };
int readConcurrency = mode == "sustained-profile" && args.Length > 4
    ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 4;
if (readConcurrency is < 2 or > 8)
    throw new ArgumentException("Use a reader limit from 2 to 8.", nameof(args));
using var database = new DbExecutor(factory, readConcurrency);
var audit = new AuditLog();
using var settings = new SettingsService(database, paths, audit);
if (mode != "restore-recover") await settings.LoadAsync();
var clock = new AuditClock(new DateTime(2026, 9, 26, 12, 0, 0));
var numbers = new DocNumberService(settings);
var periods = new PeriodGuard(settings, clock);
var ledger = new LotQuantities(settings);
var catalog = new CatalogService(database, audit, settings, clock);
var inventory = new InventoryService(database, ledger, numbers, periods, audit);
var purchasing = new PurchasingService(database, ledger, numbers, periods, settings, audit);
var sales = new SalesService(database, ledger, numbers, periods, settings, audit, clock);
var moneyOut = new MoneyOutService(database, ledger, numbers, periods, audit);
var assets = new AssetService(database, settings, periods, audit, clock);
var reports = new ReportService(database, settings, clock);
var scans = new ScanResolver(database);
var backups = new BackupService(paths, factory, clock, settings);
var maintenance = new MaintenanceService(paths, factory, database, backups, clock, audit);
if (mode == "restore-crash")
{
    if (args.Length < 4) throw new ArgumentException("restore-crash needs a boundary name as the fourth argument.");
    string target = args[3];
    bool duringUndo = target is "marker-undoing" or "candidate-discarded" or "candidate-discarded-wal" or
        "candidate-discarded-shm" or "original-restored" or "original-restored-wal" or "original-restored-shm";
    await database.WriteAsync(tx => tx.Connection.Execute(
        "UPDATE setting SET value = 'phase2-candidate' WHERE key = 'store.name'", transaction: tx.Transaction));
    BackupInfo saved = backups.Create("restore-crash");
    RestoreCandidate candidate = backups.InspectRestore(saved.Path);
    await database.WriteAsync(tx => tx.Connection.Execute(
        "UPDATE setting SET value = 'phase2-original' WHERE key = 'store.name'", transaction: tx.Transaction));
    using (var before = factory.Open())
    {
        string[] tables = ["sale", "sale_line", "sale_payment", "stock_correction", "operation", "print_job"];
        File.WriteAllText(Path.Combine(paths.TempDir, "restore-crash-expected.json"),
            JsonSerializer.Serialize(tables.ToDictionary(table => table,
                table => before.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}"))));
    }
    void StopAtBoundary(string step)
    {
        if (step != target) return;
        Console.WriteLine($"KILL_AT {step}");
        Console.Out.Flush();
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }
    if (duringUndo)
    {
        backups.ApplyRestore(candidate);
        backups.RestoreBoundary = StopAtBoundary;
        backups.UndoPendingRestore();
    }
    else
    {
        backups.RestoreBoundary = StopAtBoundary;
        backups.ApplyRestore(candidate);
    }
    throw new InvalidOperationException($"Restore boundary '{target}' was not reached.");
}
if (mode == "restore-recover")
{
    backups.RecoverInterruptedRestoreBeforeStartup();
    if (backups.HasPendingRestore) backups.UndoPendingRestore();
    using var recovered = factory.Open();
    if (recovered.ExecuteScalar<string>("PRAGMA quick_check") != "ok" ||
        recovered.Query("PRAGMA foreign_key_check").Any() ||
        recovered.ExecuteScalar<string>("SELECT value FROM setting WHERE key = 'store.name'") != "phase2-original")
        throw new InvalidDataException("The pre-restore store was not recovered intact.");
    var expected = JsonSerializer.Deserialize<Dictionary<string, long>>(
        File.ReadAllText(Path.Combine(paths.TempDir, "restore-crash-expected.json")))
        ?? throw new InvalidDataException("The pre-crash row counts are missing.");
    foreach ((string table, long count) in expected)
        if (recovered.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}") != count)
            throw new InvalidDataException($"Recovered {table} row count changed.");
    Console.WriteLine("OK restore-recover: original store, quick check, foreign keys and document counts");
    return;
}
var results = new List<Measurement>();
var queryPlans = new List<QueryPlan>();
var profileSamples = new System.Collections.Concurrent.ConcurrentBag<ProfileSample>();
var resourceSamples = new System.Collections.Concurrent.ConcurrentQueue<ResourceSample>();
var phase3ResourceSamples = new System.Collections.Concurrent.ConcurrentQueue<Phase3ResourceSample>();
SustainedSalesCheck? sustainedSalesCheck = null;
Phase3ResourceCheck? phase3ResourceCheck = null;
using var process = Process.GetCurrentProcess();
var auditElapsed = Stopwatch.StartNew();
long databaseBytes = new FileInfo(paths.DatabaseFile).Length;
long walBytesBefore = File.Exists(paths.DatabaseFile + "-wal") ? new FileInfo(paths.DatabaseFile + "-wal").Length : 0;
var rowCounts = new Dictionary<string, long>();
List<PhaseOneStorageRow>? phaseOneStorage = null;
Dictionary<string, long>? phaseOneChecks = null;
Dictionary<string, long>? phaseTwoChecks = null;
object? phaseNine = null;

using (var connection = factory.Open())
{
    bool ledgerPresent = connection.ExecuteScalar<long>(
        "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='stock_movement'") != 0;
    string[] countedTables = ["product", "product_lot", "purchase", "sale", "sale_line", "sale_payment", "stock_movement", "vendor", "audit_log"];
    foreach (string table in countedTables.Where(table => ledgerPresent || table != "stock_movement"))
    {
        long count = connection.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}");
        rowCounts.Add(table, count);
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
        if (mode == "refresh-storm") Console.WriteLine(exception);
    }
    Console.Out.Flush();
}

void CapturePlan(string name, string sql, object? parameters = null)
{
    using var connection = factory.Open();
    string[] details = connection.Query("EXPLAIN QUERY PLAN " + sql, parameters)
        .Select(static row => Convert.ToString(((IDictionary<string, object>)row)["detail"], System.Globalization.CultureInfo.InvariantCulture) ?? "")
        .ToArray();
    queryPlans.Add(new QueryPlan(name, details, sql));
    Console.WriteLine("PLAN " + name + ": " + string.Join(" | ", details));
}

DateOnly end = new(2026, 9, 26);
DateOnly monthStart = new(2026, 9, 1);
DateOnly fiscalStart = new(2026, 4, 1);
switch (mode)
{
    case "phase5-direct-crash":
    {
        if (!Path.GetFileName(root).Contains("rehearsal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Crash rehearsal runs only in a disposable rehearsal root.");
        using var connection = factory.Open();
        long product = connection.ExecuteScalar<long>("""
            SELECT product_id FROM product_lot WHERE qty_on_hand>0 AND is_active=1
            ORDER BY id DESC LIMIT 1
            """);
        using var transaction = connection.BeginTransaction(deferred: false);
        long lotId = connection.ExecuteScalar<long>("""
            SELECT id FROM product_lot WHERE product_id=@product AND qty_on_hand>0 AND is_active=1
            ORDER BY expiry_date IS NULL,expiry_date,received_date,id LIMIT 1
            """, new { product }, transaction);
        long sale = connection.ExecuteScalar<long>("""
            INSERT INTO sale(invoice_no,sale_date,line_count,gross_total,taxable_total,
              grand_total,cost_total)
            VALUES ('PH5-CRASH-ROLLBACK','2026-10-18',1,2000,2000,2000,1000) RETURNING id
            """, transaction: transaction);
        connection.Execute("""
            INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,
              qty,unit_price,taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
            VALUES (@sale,1,@product,@lotId,'Phase 5 crash product',1000,2000,2000,0,2000,1000,1000)
            """, new { sale, product, lotId }, transaction);
        ledger.Change(new(connection, transaction), new(new DateOnly(2026, 10, 18), product, lotId,
            GroceryErp.Inventory.Contracts.LotChangeKind.Sale, Quantity.FromScaled(-1000)));
        connection.Execute("UPDATE sale SET status='COMPLETED' WHERE id=@sale", new { sale }, transaction);
        Console.WriteLine($"INTENTIONAL_CRASH_BEFORE_COMMIT lot={lotId} sale={sale}");
        Console.Out.Flush();
        Environment.Exit(137);
        break;
    }
    case "phase5-direct-crash-check":
    {
        using var connection = factory.Open();
        phaseTwoChecks = new Dictionary<string, long>
        {
            ["uncommittedSaleRows"] = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sale WHERE invoice_no='PH5-CRASH-ROLLBACK'"),
            ["foreignKeyViolations"] = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM pragma_foreign_key_check"),
            ["quickCheckOk"] = connection.ExecuteScalar<string>("PRAGMA quick_check") == "ok" ? 1 : 0,
        };
        int mismatches = LotSourceReconciliation.CheckAll(connection, null).Count();
        phaseTwoChecks["sourceMismatches"] = mismatches;
        if (phaseTwoChecks["uncommittedSaleRows"] != 0 || phaseTwoChecks["foreignKeyViolations"] != 0 ||
            phaseTwoChecks["quickCheckOk"] != 1 || mismatches != 0)
            throw new InvalidDataException("Crash recovery left a document, balance or integrity mismatch.");
        break;
    }
    case "phase5-count-read":
    {
        using var check = factory.Open();
        long product = check.ExecuteScalar<long>("""
            SELECT product_id FROM product_lot GROUP BY product_id ORDER BY COUNT(*) DESC LIMIT 1
            """);
        var counts = new DirectStockCountService(database, new DirectStockEngine(), periods);
        DirectCountLotPage? page = null;
        for (int index = 0; index < 100; index++)
        {
            long started = Stopwatch.GetTimestamp();
            page = await counts.GetPageAsync([product]);
            profileSamples.Add(new("phase5-selected-product-count-page",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, page.Items.Count, null));
        }
        phaseTwoChecks = new Dictionary<string, long>
        {
            ["productId"] = product,
            ["firstPageRows"] = page!.Items.Count,
            ["hasMore"] = page.HasMore ? 1 : 0,
        };
        if (page.Items.Count == 0) throw new InvalidDataException("Count page is empty.");
        break;
    }
    // Phase 9 query and write review: isolated, mixed and per-statement profiles of every hot path.
    case "phase9-profile":
        if (!Path.GetFileName(root).Contains("rehearsal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Phase 9 profile writes documents; use a disposable rehearsal root.");
        phaseNine = await new PhaseNineProfile(paths, factory, database, settings, audit, backups).RunAsync();
        break;
    // The full-history CSV gate: one report over every date through the streaming export, with the
    // process peak, the managed allocation, and how fast a second export stops when it is cancelled.
    // The fourth argument names the report: SalesRegister (one row per bill, the default) or an
    // aggregate such as ItemSales, which groups every sale line. Run each in a fresh process.
    case "phase9-export":
    {
        ReportKind exportKind = args.Length > 3 ? Enum.Parse<ReportKind>(args[3]) : ReportKind.SalesRegister;
        var exportReports = new ReportService(database, settings, new AuditClock(new DateTime(2026, 10, 18, 12, 0, 0)));
        var filter = new ReportFilter(From: new DateOnly(2019, 1, 1), To: new DateOnly(2026, 10, 18), Limit: 5000);
        string path = Path.Combine(paths.ExportsDir, "phase9-full-history.csv");
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        process.Refresh();
        long workingBefore = process.WorkingSet64;
        var exportTimer = Stopwatch.StartNew();
        long exported = await exportReports.ExportCsvAsync(exportKind, filter, path);
        exportTimer.Stop();
        process.Refresh();
        long fileBytes = new FileInfo(path).Length;
        File.Delete(path);
        using var cancellation = new CancellationTokenSource();
        var cancelTimer = new Stopwatch();
        bool cancelled = false;
        Task cancelLater = Task.Run(async () => { await Task.Delay(3000); cancelTimer.Start(); await cancellation.CancelAsync(); });
        try { await exportReports.ExportCsvAsync(exportKind, filter, path, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; cancelTimer.Stop(); }
        await cancelLater;
        // A report that finished before the cancel leaves its finished file; only a partial file is a leak.
        if (!cancelled) File.Delete(path);
        bool partialLeft = Directory.EnumerateFiles(paths.ExportsDir, "phase9-full-history.csv*").Any();
        phaseNine = new
        {
            report = exportKind.ToString(),
            rows = exported,
            fileBytes,
            milliseconds = exportTimer.Elapsed.TotalMilliseconds,
            workingSetBeforeBytes = workingBefore,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            gcPauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds,
            cancelled,
            cancelStopMilliseconds = cancelTimer.Elapsed.TotalMilliseconds,
            partialLeft,
        };
        Console.WriteLine($"OK phase9-export {exportKind}: {exported} rows in {exportTimer.Elapsed.TotalSeconds:0.0} s, peak {process.PeakWorkingSet64 / 1048576.0:0} MiB, cancel stop {cancelTimer.Elapsed.TotalMilliseconds:0} ms");
        // A report that finishes within the delay is not cancelled; it must still leave no partial file.
        if (partialLeft || (!cancelled && exportTimer.Elapsed > TimeSpan.FromSeconds(3)))
            throw new InvalidDataException("The cancelled export did not stop cleanly.");
        break;
    }
    // A cashier bill flow on the real write path while two readers page sales history.
    case "phase5-live-write":
    {
        if (!Path.GetFileName(root).Contains("rehearsal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cashier rehearsal needs a disposable rehearsal root.");
        using (var check = factory.Open())
            if (check.ExecuteScalar<long>("PRAGMA user_version") != MigrationRunner.LatestVersion)
                throw new InvalidDataException("Migrate the rehearsal copy first.");
        var cashierClock = new AuditClock(new DateTime(2026, 10, 18, 12, 0, 0));
        var cashierSales = new SalesService(database, ledger, numbers, periods, settings, audit, cashierClock);
        string suffix = Guid.NewGuid().ToString("N");
        long product = await database.WriteAsync(tx =>
        {
            long category = tx.Connection.ExecuteScalar<long>("SELECT id FROM category LIMIT 1", transaction: tx.Transaction);
            long uom = tx.Connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'", transaction: tx.Transaction);
            long id = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id)
                VALUES (@sku,'Phase 5 cashier product',@category,@uom) RETURNING id
                """, new { sku = "PH5-C-" + suffix, category, uom }, tx.Transaction);
            for (int index = 0; index < 128; index++)
            {
                long lotId = tx.Connection.ExecuteScalar<long>("""
                    INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,
                      unit_cost,selling_price,qty_received)
                    VALUES (@id,@code,'CASHIER-BATCH','2026-10-18','2027-10-18',1000,2000,1000)
                    RETURNING id
                    """, new { id, code = "PH5-C-L-" + suffix + "-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) },
                    tx.Transaction);
                ledger.Change(tx, new(cashierClock.BusinessDate, id, lotId,
                    GroceryErp.Inventory.Contracts.LotChangeKind.DirectEntry, Quantity.One));
            }
            return id;
        });
        using var readersStop = new CancellationTokenSource();
        Task[] readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            while (!readersStop.IsCancellationRequested)
                await cashierSales.GetHistoryAsync(new SaleHistoryFilter(Limit: 50), readersStop.Token);
        })).ToArray();
        try
        {
            for (int index = 0; index < 100; index++)
            {
                long scanStarted = Stopwatch.GetTimestamp();
                long saleId = await cashierSales.AddProductAsync(null, product, Quantity.One);
                profileSamples.Add(new("phase5-live-scan-to-line",
                    Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds, 1, null));
                long started = Stopwatch.GetTimestamp();
                try
                {
                    await cashierSales.CompleteAsync(new(saleId,
                        [new(PaymentMode.Cash, new Money(2))], OperationGuard.NewId()));
                    profileSamples.Add(new("phase5-live-bill-commit",
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new("phase5-live-bill-commit",
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, exception.GetType().Name));
                    throw;
                }
                if (index % 10 == 0)
                {
                    process.Refresh();
                    long wal = File.Exists(paths.DatabaseFile + "-wal") ? new FileInfo(paths.DatabaseFile + "-wal").Length : 0;
                    resourceSamples.Enqueue(new(auditElapsed.Elapsed.TotalSeconds, wal, process.WorkingSet64, 0, factory.ActiveConnections));
                }
            }
        }
        finally
        {
            readersStop.Cancel();
            try { await Task.WhenAll(readers); }
            catch (OperationCanceledException) { }
        }
        using (var check = factory.Open())
            phaseTwoChecks = new Dictionary<string, long>
            {
                ["cashierSales"] = check.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM sale_line WHERE product_id=@product", new { product }),
                ["cashierRemaining"] = check.ExecuteScalar<long>(
                    "SELECT quantity FROM product_stock_total WHERE product_id=@product", new { product }),
                ["sourceMismatches"] = check.Query<long>("SELECT id FROM product_lot WHERE product_id=@product",
                    new { product }).Count(lot => LotSourceReconciliation.CheckLot(check, null, lot) is not null),
            };
        if (phaseTwoChecks["cashierSales"] != 100 || phaseTwoChecks["cashierRemaining"] != 28000 ||
            phaseTwoChecks["sourceMismatches"] != 0)
            throw new InvalidDataException("Cashier-path stock or source parity failed.");
        break;
    }
    // The full-size schema-31 fixture was built with drafts of 025 and 031. Give a
    // disposable copy the committed shape: 031's sale and return snapshot columns,
    // which it adds without a backfill, and 025's idempotent last-sale triggers.
    case "phase5-fixture-align":
    {
        if (!Path.GetFileName(root).Contains("rehearsal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture alignment runs only in a disposable rehearsal root.");
        string repository = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repository, "025_sale_report_day.sql")))
            repository = Path.GetDirectoryName(repository) ?? throw new FileNotFoundException("025_sale_report_day.sql");
        string[] triggers = System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(Path.Combine(repository, "025_sale_report_day.sql")),
                @"CREATE TRIGGER IF NOT EXISTS trg_sale_product_last_\w+.*?\bEND;",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(static match => match.Value).ToArray();
        string[] columns =
        [
            "category_id_snapshot INTEGER", "category_name_snapshot TEXT", "uom_id_snapshot INTEGER",
            "tax_rate_id_snapshot INTEGER", "track_expiry_snapshot INTEGER", "is_loose_snapshot INTEGER",
        ];
        using var connection = factory.Open();
        if (connection.ExecuteScalar<long>("PRAGMA user_version") != 31)
            throw new InvalidDataException("Align only a schema-31 fixture before migrating it.");
        using var transaction = connection.BeginTransaction(deferred: false);
        long added = 0;
        foreach (string table in new[] { "sale_line", "sale_return_line" })
            foreach (string column in columns)
                if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name=@name",
                        new { table, name = column.Split(' ')[0] }, transaction) == 0)
                {
                    connection.Execute($"ALTER TABLE {table} ADD COLUMN {column};", transaction: transaction);
                    added++;
                }
        long before = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'trg_sale_product_last_%'",
            transaction: transaction);
        foreach (string trigger in triggers) connection.Execute(trigger, transaction: transaction);
        long after = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'trg_sale_product_last_%'",
            transaction: transaction);
        transaction.Commit();
        phaseTwoChecks = new Dictionary<string, long>
        {
            ["snapshotColumnsAdded"] = added,
            ["lastSaleTriggersFound"] = triggers.Length,
            ["lastSaleTriggersAdded"] = after - before,
        };
        if (triggers.Length != 7 || after != 7)
            throw new InvalidDataException("The last-sale triggers did not align with 025.");
        break;
    }
    case "phase5-direct-audit":
    {
        await Measure("phase5-source-reconciliation", () => Task.Run(() =>
        {
            using var connection = factory.Open();
            int mismatches = 0;
            foreach (LotSourceMismatch mismatch in LotSourceReconciliation.CheckAll(connection, null))
            {
                if (mismatches < 10)
                    Console.WriteLine($"MISMATCH lot={mismatch.LotId} actual={mismatch.Actual} expected={mismatch.Expected}");
                mismatches++;
            }
            phaseTwoChecks = new Dictionary<string, long> { ["sourceMismatches"] = mismatches };
            return mismatches;
        }), static value => value);
        break;
    }
    case "phase1-rollback":
    {
        if (args.Length < 4) throw new ArgumentException("Pass the verified pre-migration backup zip as the fourth argument.");
        using (var check = factory.Open())
            if (check.ExecuteScalar<long>("PRAGMA user_version") != 29)
                throw new InvalidDataException("The rollback rehearsal must start from schema 29.");
        RestoreCandidate candidate = backups.InspectRestore(Path.GetFullPath(args[3]));
        if (candidate.Manifest.SchemaVersion != 28)
            throw new InvalidDataException("The rollback archive must contain schema 28.");
        await Measure("phase1-restored-backup-rollback", () => Task.Run(() =>
        {
            backups.ApplyRestore(candidate);
            backups.FinishPendingRestore();
            using var check = factory.Open();
            if (check.ExecuteScalar<long>("PRAGMA user_version") != 28 ||
                check.ExecuteScalar<string>("PRAGMA quick_check") != "ok" ||
                check.QueryFirstOrDefault("PRAGMA foreign_key_check") is not null)
                throw new InvalidDataException("Restored schema-28 database failed validation.");
            foreach ((string table, long expected) in rowCounts)
            {
                long actual = check.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}");
                // Creating the verified source archive records one backup audit event.
                if (actual != expected && (table != "audit_log" || actual != expected + 1))
                    throw new InvalidDataException($"Rollback changed the {table} row count.");
            }
            return 28;
        }), static version => version);
        break;
    }
    case "phase1-schema-audit":
        using (var connection = factory.Open())
        {
            bool dbstatAvailable = connection.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM pragma_compile_options WHERE compile_options='ENABLE_DBSTAT_VTAB'
                """) != 0;
            phaseOneStorage = dbstatAvailable
                ? connection.Query<PhaseOneStorageRow>("""
                    SELECT name AS Name,SUM(pgsize) AS Bytes FROM dbstat
                    GROUP BY name ORDER BY Bytes DESC
                    """).ToList()
                : new List<PhaseOneStorageRow>
                {
                    new()
                    {
                        Name = "database-total (dbstat unavailable)",
                        Bytes = connection.ExecuteScalar<long>("PRAGMA page_count") *
                            connection.ExecuteScalar<long>("PRAGMA page_size")
                    }
                };
            if (connection.ExecuteScalar<long>("PRAGMA user_version") >= 29)
            {
                phaseOneChecks = new Dictionary<string, long>
                {
                    ["marker"] = connection.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM schema_migration_marker WHERE version=29"),
                    ["missingLotOrigins"] = connection.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM product_lot WHERE origin_ref_id IS NULL"),
                    ["saleLegacy"] = connection.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM sale_line WHERE snapshot_provenance='LEGACY_SALE_LINE'"),
                    ["saleNew"] = connection.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM sale_line WHERE snapshot_provenance='AT_COMPLETION'"),
                    ["returnSnapshotMissing"] = connection.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM sale_return_line WHERE product_name_snapshot IS NULL"),
                };
                CapturePlan("phase1-source-purchase-lot", """
                    SELECT id FROM purchase_line WHERE lot_id_snapshot=@lotId LIMIT 1
                    """, new { lotId = 1 });
            }
        }
        CapturePlan("phase1-fefo", """
            SELECT id FROM product_lot WHERE product_id=@productId AND qty_on_hand>0 AND is_active=1
            ORDER BY expiry_date IS NULL,expiry_date,received_date,id LIMIT 8
            """, new { productId = 1 });
        CapturePlan("phase1-sale-by-lot", "SELECT id FROM sale_line WHERE lot_id=@lotId LIMIT 8",
            new { lotId = 1 });
        CapturePlan("phase1-return-by-line", "SELECT id FROM sale_return_line WHERE sale_line_id=@lineId LIMIT 8",
            new { lineId = 1 });
        break;
    case "phase6-low-stock-day-verify":
    {
        using var connection = factory.Open();
        long started = Stopwatch.GetTimestamp();
        long mismatches = connection.ExecuteScalar<long>("""
            WITH expected AS (
                SELECT sl.product_id,s.sale_date AS day,COUNT(*) AS entries,SUM(sl.qty) AS qty
                FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                WHERE s.status='COMPLETED' GROUP BY sl.product_id,s.sale_date
            )
            SELECT COUNT(*) FROM (
                SELECT * FROM (SELECT * FROM expected EXCEPT SELECT * FROM sale_product_day)
                UNION ALL
                SELECT * FROM (SELECT * FROM sale_product_day EXCEPT SELECT * FROM expected)
            )
            """, commandTimeout: 300);
        Console.WriteLine($"LOW-STOCK daily-parity: mismatches={mismatches}, rows={connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale_product_day")}, milliseconds={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0}");
        if (mismatches != 0) throw new InvalidDataException("The Low Stock daily projection differs from completed sale lines.");
        break;
    }
    case "phase6-low-stock-cold":
    {
        await Measure("report-LowStock-broad", () => reports.GetReportAsync(ReportKind.LowStock,
            new ReportFilter(To: end, Limit: 100)), static report => report.Rows.Count);
        CapturePlan("low-stock-report", ReportDefinitions.All[ReportKind.LowStock].Sql,
            new { to = "2026-09-26", Limit = 101 });
        break;
    }
    case "phase6-low-stock-trial":
    case "phase6-low-stock-cover-trial":
    case "phase6-low-stock-day-trial":
    {
        if (mode == "phase6-low-stock-cover-trial")
        {
            using var setup = factory.Open();
            long started = Stopwatch.GetTimestamp();
            setup.Execute("CREATE INDEX IF NOT EXISTS ix_sale_line_product_cover ON sale_line(product_id,sale_id,qty)", commandTimeout: 300);
            Console.WriteLine($"LOW-STOCK covering-index-build: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms");
        }
        if (mode == "phase6-low-stock-day-trial")
        {
            using var setup = factory.Open();
            long started = Stopwatch.GetTimestamp();
            setup.Execute("""
                CREATE TABLE IF NOT EXISTS sale_product_day (
                    product_id INTEGER NOT NULL, day TEXT NOT NULL,
                    entries INTEGER NOT NULL, qty INTEGER NOT NULL,
                    PRIMARY KEY(product_id,day)
                ) WITHOUT ROWID, STRICT;
                INSERT INTO sale_product_day(product_id,day,entries,qty)
                SELECT sl.product_id,s.sale_date,COUNT(*),SUM(sl.qty)
                FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                WHERE s.status='COMPLETED'
                GROUP BY sl.product_id,s.sale_date;
                """, commandTimeout: 300);
            Console.WriteLine($"LOW-STOCK daily-build: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms, rows={setup.ExecuteScalar<long>("SELECT COUNT(*) FROM sale_product_day")}");
        }
        const string page = """
            WITH page AS MATERIALIZED (
                SELECT p.id,p.name FROM product_stock_total t JOIN product p ON p.id=t.product_id
                WHERE p.is_active=1 AND t.is_low=1 ORDER BY p.name LIMIT 101
            )
            """;
        var variants = new List<(string Name, string Sql)>
        {
            ("current", page + """
                SELECT page.id,COALESCE((SELECT SUM(sl.qty)/30 FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                    WHERE sl.product_id=page.id AND s.status='COMPLETED'
                    AND s.sale_date BETWEEN date(@to,'-29 day') AND @to),0) AS average
                FROM page ORDER BY page.name
                """),
            ("date-first", page + """
                , recent AS MATERIALIZED (
                    SELECT sl.product_id,SUM(sl.qty)/30 AS average
                    FROM sale s INDEXED BY ix_sale_date_id
                    CROSS JOIN sale_line sl ON sl.sale_id=s.id
                    JOIN page ON page.id=sl.product_id
                    WHERE s.status='COMPLETED' AND s.sale_date BETWEEN date(@to,'-29 day') AND @to
                    GROUP BY sl.product_id
                )
                SELECT page.id,COALESCE(recent.average,0) AS average FROM page
                LEFT JOIN recent ON recent.product_id=page.id ORDER BY page.name
                """),
            ("date-all", page + """
                , recent AS MATERIALIZED (
                    SELECT sl.product_id,SUM(sl.qty)/30 AS average
                    FROM sale s INDEXED BY ix_sale_date_id
                    CROSS JOIN sale_line sl ON sl.sale_id=s.id
                    WHERE s.status='COMPLETED' AND s.sale_date BETWEEN date(@to,'-29 day') AND @to
                    GROUP BY sl.product_id
                )
                SELECT page.id,COALESCE(recent.average,0) AS average FROM page
                LEFT JOIN recent ON recent.product_id=page.id ORDER BY page.name
                """),
        };
        using var connection = factory.Open();
        if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_sale_line_product_cover'") != 0)
            variants.Insert(1, ("cover", page + """
                SELECT page.id,COALESCE((SELECT SUM(sl.qty)/30 FROM sale_line sl INDEXED BY ix_sale_line_product_cover JOIN sale s ON s.id=sl.sale_id
                    WHERE sl.product_id=page.id AND s.status='COMPLETED'
                    AND s.sale_date BETWEEN date(@to,'-29 day') AND @to),0) AS average
                FROM page ORDER BY page.name
                """));
        if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sale_product_day'") != 0)
            variants.Insert(1, ("day", page + """
                SELECT page.id,COALESCE((SELECT SUM(d.qty)/30 FROM sale_product_day d
                    WHERE d.product_id=page.id AND d.day BETWEEN date(@to,'-29 day') AND @to),0) AS average
                FROM page ORDER BY page.name
                """));
        List<(long id,long average)>? expected = null;
        foreach (var variant in variants)
        {
            for (int i = 0; i < 3; i++)
            {
                long started = Stopwatch.GetTimestamp();
                var rows = connection.Query<(long id,long average)>(variant.Sql,new { to = "2026-09-26" }).ToList();
                if (expected is null) expected = rows;
                else if (!expected.SequenceEqual(rows)) throw new InvalidDataException(variant.Name + " changed the Low Stock rows.");
                Console.WriteLine($"LOW-STOCK {variant.Name} {i}: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms, rows={rows.Count}");
            }
            CapturePlan("low-stock-" + variant.Name, variant.Sql,new { to = "2026-09-26" });
        }
        for (int i = 0; i < 3; i++)
        {
            long started = Stopwatch.GetTimestamp();
            var rows = connection.Query(ReportDefinitions.All[ReportKind.LowStock].Sql,
                new { to = "2026-09-26", Limit = 101 }).ToList();
            Console.WriteLine($"LOW-STOCK exact-sql {i}: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms, rows={rows.Count}");
        }
        for (int i = 0; i < 3; i++)
        {
            long started = Stopwatch.GetTimestamp();
            var stockReport = await reports.GetReportAsync(ReportKind.LowStock,
                new ReportFilter(To: end, Limit: 100));
            Console.WriteLine($"LOW-STOCK service {i}: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms, rows={stockReport.Rows.Count}");
        }
        break;
    }
    case "phase6-last-parity":
    {
        await Measure("last-sale-source-parity", () => Task.Run(() =>
        {
            using var connection = factory.Open();
            long mismatches = connection.ExecuteScalar<long>("""
                WITH expected AS (
                  SELECT sl.product_id,MAX(s.sale_date) AS last_day
                  FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                  WHERE s.status='COMPLETED' GROUP BY sl.product_id
                )
                SELECT COUNT(*) FROM (
                  SELECT * FROM (SELECT * FROM expected EXCEPT SELECT * FROM sale_product_last)
                  UNION ALL
                  SELECT * FROM (SELECT * FROM sale_product_last EXCEPT SELECT * FROM expected)
                )
                """);
            if (mismatches != 0) throw new InvalidDataException($"{mismatches} last-sale products differ from source.");
            return 0;
        }), static value => value);
        for (int i = 0; i < 8; i++)
            await Measure("slow-movers", () => reports.GetReportAsync(ReportKind.SlowMovers,
                new ReportFilter(To: end, SlowMoverDays: 90, Limit: 100)), static report => report.Rows.Count);
        break;
    }
    case "phase6-summary-parity":
    {
        foreach ((string label, DateOnly from, DateOnly to) in new[]
                 { ("month", monthStart, end), ("fiscal", fiscalStart, end),
                   ("partial", new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 17)) })
        {
            int days = to.DayNumber - from.DayNumber + 1;
            DateOnly priorTo = from.AddDays(-1);
            var parameters = new {
                from = from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                to = to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                previousFrom = priorTo.AddDays(1 - days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                previousTo = priorTo.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Limit = 1001
            };
            foreach (ReportKind kind in new[] { ReportKind.CategorySales, ReportKind.GstSummary, ReportKind.HourlySales })
            {
                List<Dictionary<string, string>> source = [];
                await Measure($"source-{kind}-{label}", () => Task.Run(() =>
                {
                    using var connection = factory.Open();
                    source = connection.Query(ReportDefinitions.All[kind].Sql, parameters)
                        .Select(static row => ((IDictionary<string, object?>)row).ToDictionary(
                            static pair => pair.Key,
                            static pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""))
                        .ToList();
                    return source.Count;
                }), static count => count);
                async Task<TabularReport> Projected() => await reports.GetReportAsync(kind,
                    new ReportFilter(From: from, To: to, Limit: 1000));
                TabularReport projected = await Projected();
                var actual = projected.Rows.Select(static row => row.Values.ToDictionary(
                    static pair => pair.Key,
                    static pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""))
                    .ToList();
                static string Key(Dictionary<string, string> row, ReportKind kind) => kind switch
                {
                    ReportKind.CategorySales => row["Category"],
                    ReportKind.HourlySales => row["Weekday"] + "|" + row["Hour"],
                    _ => row["Direction"] + "|" + row["Hsn"] + "|" + row["Rate"]
                };
                var expectedByKey = source.ToDictionary(row => Key(row, kind));
                var actualByKey = actual.ToDictionary(row => Key(row, kind));
                if (expectedByKey.Count != actualByKey.Count || expectedByKey.Any(pair =>
                    !actualByKey.TryGetValue(pair.Key, out Dictionary<string, string>? found) ||
                    pair.Value.Any(cell => !found.TryGetValue(cell.Key, out string? value) || value != cell.Value)))
                    throw new InvalidDataException($"{kind} {label} projected rows differ from source.");
                Console.WriteLine($"OK parity-{kind}-{label}: {source.Count} rows");
                for (int i = 0; i < 5; i++)
                    await Measure($"projected-{kind}-{label}", Projected, static report => report.Rows.Count);
            }
        }
        break;
    }
    case "phase6-search-trial":
    {
        const string match = "\"grocery\" \"item\"";
        const string ftsFirst = """
            SELECT p.id FROM product_fts f JOIN product p ON p.id=f.rowid
            WHERE product_fts MATCH @match AND p.is_active=1
            ORDER BY p.name COLLATE NOCASE,p.id LIMIT 31
            """;
        const string nameFirst = """
            SELECT p.id FROM product p INDEXED BY ix_product_name
            WHERE p.is_active=1 AND p.id IN
                (SELECT rowid FROM product_fts WHERE product_fts MATCH @match)
            ORDER BY p.name COLLATE NOCASE,p.id LIMIT 31
            """;
        const string nameProbe = """
            SELECT p.id FROM product p INDEXED BY ix_product_name
            CROSS JOIN product_fts f ON f.rowid=p.id
            WHERE p.is_active=1 AND product_fts MATCH @match
            ORDER BY p.name COLLATE NOCASE,p.id LIMIT 31
            """;
        for (int i = 0; i < 5; i++)
            await Measure("fts-hit-threshold", () => Task.Run(() =>
            {
                using var connection = factory.Open();
                return connection.ExecuteScalar<long>("""
                    SELECT COUNT(*) FROM (SELECT rowid FROM product_fts
                    WHERE product_fts MATCH @match LIMIT 23500)
                    """, new { match });
            }), static count => (int)count);
        foreach ((string name, string sql) in new[] { ("fts-first", ftsFirst), ("name-first", nameFirst),
                 ("name-probe", nameProbe) })
        {
            CapturePlan(name, sql, new { match });
            for (int i = 0; i < 5; i++)
                await Measure(name, () => Task.Run(() =>
                {
                    using var connection = factory.Open();
                    return connection.Query<long>(sql, new { match }).ToArray();
                }), static rows => rows.Length);
        }
        for (int i = 0; i < 10; i++)
            await Measure("catalog-page", () => catalog.SearchPageAsync(
                new ProductSearchFilter("grocery item", Limit: 30)), static page => page.Items.Count);
        break;
    }
    case "refresh-storm":
    {
        bool cancelObsolete = args.Length > 3 && bool.Parse(args[3]);
        await Measure(cancelObsolete ? "refresh-storm-cancel" : "refresh-storm-all", async () =>
        {
            var cancellations = new List<CancellationTokenSource>();
            var requests = new List<Task<int>>();
            try
            {
                for (int index = 0; index < 12; index++)
                {
                    if (cancelObsolete && cancellations.Count > 0) cancellations[^1].Cancel();
                    var cancellation = new CancellationTokenSource();
                    cancellations.Add(cancellation);
                    requests.Add(RunRefresh(cancellation));
                    await Task.Delay(35);
                }
                return (await Task.WhenAll(requests)).Sum();
            }
            finally
            {
                foreach (var cancellation in cancellations) cancellation.Dispose();
            }
        }, static completed => completed);
        break;

        async Task<int> RunRefresh(CancellationTokenSource cancellation)
        {
            try { await reports.GetDashboardAsync(cancellation.Token); return 1; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        }
    }
    case "history-profile":
    {
        string invoicePrefix;
        string oldestInvoice;
        DateOnly latestDate;
        long latestFirstId;
        DateOnly middleDate;
        long middleId;
        DateOnly oldestDate;
        long oldestId;
        using (var connection = factory.Open())
        {
            string invoice = connection.ExecuteScalar<string>(
                "SELECT invoice_no FROM sale WHERE invoice_no IS NOT NULL ORDER BY id DESC LIMIT 1")
                ?? throw new InvalidDataException("The history fixture has no invoice numbers.");
            invoicePrefix = invoice[..Math.Min(5, invoice.Length)];
            oldestInvoice = connection.ExecuteScalar<string>(
                "SELECT invoice_no FROM sale WHERE invoice_no IS NOT NULL ORDER BY sale_date,id LIMIT 1")
                ?? throw new InvalidDataException("The history fixture has no old invoice number.");
            var latest = connection.QuerySingle<(string SaleDate, long FirstId)>(
                "SELECT sale_date,MIN(id) FROM sale WHERE sale_date=(SELECT MAX(sale_date) FROM sale)");
            latestFirstId = latest.FirstId;
            latestDate = DateOnly.ParseExact(latest.SaleDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var middle = connection.QuerySingle<(string SaleDate, long Id)>(
                "SELECT sale_date,id FROM sale ORDER BY sale_date DESC,id DESC LIMIT 1 OFFSET 400000");
            middleId = middle.Id;
            middleDate = DateOnly.ParseExact(middle.SaleDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var oldest = connection.QuerySingle<(string SaleDate, long Id)>(
                "SELECT sale_date,id FROM sale ORDER BY sale_date,id LIMIT 1");
            oldestId = oldest.Id;
            oldestDate = DateOnly.ParseExact(oldest.SaleDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
        var shapes = new (string Name, SaleHistoryFilter Filter)[]
        {
            ("history-first", new(Limit: 50)),
            ("history-first-500", new(Limit: 500)),
            ("history-current", new(From: monthStart, To: end, Limit: 50)),
            ("history-old", new(From: new(2025, 4, 1), To: new(2025, 4, 30), Limit: 50)),
            ("history-status", new(Status: SaleStatus.Completed, Limit: 50)),
            ("history-payment", new(PaymentMode: PaymentMode.Cash, Limit: 50)),
            ("history-invoice-match", new(InvoiceNumber: invoicePrefix, Limit: 50)),
            ("history-invoice-old-exact", new(InvoiceNumber: oldestInvoice, Limit: 50)),
            ("history-invoice-miss", new(InvoiceNumber: "ZZZZ-NO-MATCH", Limit: 50)),
            ("history-invoice-broad", new(InvoiceNumber: "S", Limit: 50)),
            ("history-amount", new(MinimumAmount: new Money(100), MaximumAmount: new Money(500), Limit: 50)),
            ("history-combined", new(From: monthStart, To: end, Status: SaleStatus.Completed,
                PaymentMode: PaymentMode.Cash, MinimumAmount: new Money(100), Limit: 50)),
            ("history-middle", new(BeforeDate: middleDate, BeforeId: middleId, Limit: 50)),
            ("history-oldest", new(BeforeDate: oldestDate, BeforeId: oldestId, Limit: 50)),
            ("history-date-transition", new(BeforeDate: latestDate, BeforeId: latestFirstId, Limit: 50)),
        };
        foreach (var shape in shapes)
            for (int iteration = 0; iteration < 9; iteration++)
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    int rows = (await sales.GetHistoryAsync(shape.Filter)).Count;
                    profileSamples.Add(new(shape.Name + (iteration == 0 ? "-first" : "-warm"),
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, rows, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new(shape.Name + (iteration == 0 ? "-first" : "-warm"),
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0,
                        exception.GetType().Name + ": " + exception.Message));
                }
            }
        break;
    }
    case "attribution-profile":
    {
        // This process starts with an open database for settings and fixture metadata;
        // the first measured service call is process-cold, not disk-cache-cold.
        var emptyDayReports = new ReportService(database, settings,
            new AuditClock(new DateTime(2030, 1, 1, 12, 0, 0)));
        string invoicePrefix;
        using (var connection = factory.Open())
        {
            string invoice = connection.ExecuteScalar<string>(
                "SELECT invoice_no FROM sale WHERE invoice_no IS NOT NULL ORDER BY id DESC LIMIT 1")
                ?? throw new InvalidDataException("The attribution fixture has no invoice numbers.");
            invoicePrefix = invoice[..Math.Min(5, invoice.Length)];
        }
        var operations = new (string Name, Func<Task<int>> Run)[]
        {
            ("dashboard-populated", async () => { await reports.GetDashboardAsync(); return 1; }),
            ("pnl-month", async () => (await reports.GetPnlAsync(monthStart, end)).Income.Count),
            ("pnl-fiscal", async () => (await reports.GetPnlAsync(fiscalStart, end)).Income.Count),
            ("history-first", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(Limit: 50))).Count),
            ("history-current", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(From: monthStart, To: end, Limit: 50))).Count),
            ("history-old", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(From: new(2025, 4, 1), To: new(2025, 4, 30), Limit: 50))).Count),
            ("history-status", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(Status: SaleStatus.Completed, Limit: 50))).Count),
            ("history-payment", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(PaymentMode: PaymentMode.Cash, Limit: 50))).Count),
            ("history-invoice-match", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(InvoiceNumber: invoicePrefix, Limit: 50))).Count),
            ("history-invoice-miss", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(InvoiceNumber: "ZZZZ-NO-MATCH", Limit: 50))).Count),
            ("history-amount", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(MinimumAmount: new Money(100), MaximumAmount: new Money(500), Limit: 50))).Count),
            ("history-combined", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(From: monthStart, To: end, Status: SaleStatus.Completed, PaymentMode: PaymentMode.Cash, MinimumAmount: new Money(100), Limit: 50))).Count),
            ("dashboard-empty-day", async () => { await emptyDayReports.GetDashboardAsync(); return 1; }),
        };
        foreach (var operation in operations)
            for (int iteration = 0; iteration < 11; iteration++)
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    int rows = await operation.Run();
                    profileSamples.Add(new(operation.Name + (iteration == 0 ? "-first" : "-warm"),
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, rows, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new(operation.Name + (iteration == 0 ? "-first" : "-warm"),
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0,
                        exception.GetType().Name + ": " + exception.Message));
                }
            }
        foreach (var period in new[] { ("month", "2026-09-01", "2026-09-26"), ("fiscal", "2026-04-01", "2026-09-26") })
        {
            // One representative execution of each source branch, alongside the
            // exact composite service statement and plan captured by SQL tracing.
            var branches = new (string Name, string Sql)[]
            {
                ("sales", "SELECT COUNT(*) AS Rows,SUM(taxable_total) AS Amount FROM sale WHERE status='COMPLETED' AND sale_date BETWEEN @from AND @to"),
                ("returns", "SELECT COUNT(*) AS Rows,SUM(taxable_total) AS Amount FROM sale_return WHERE return_date BETWEEN @from AND @to"),
                ("damage", "SELECT COUNT(*) AS Rows,SUM(cost_value-recovered_amount) AS Amount FROM damage WHERE damage_date BETWEEN @from AND @to"),
                ("adjustment", "SELECT COUNT(*) AS Rows,SUM(quantity_delta*unit_cost) AS Amount FROM stock_correction WHERE correction_date BETWEEN @from AND @to"),
                ("expenses", "SELECT COUNT(*) AS Rows,SUM(amount) AS Amount FROM expense WHERE expense_date BETWEEN @from AND @to"),
                ("salary", "SELECT COUNT(*) AS Rows,SUM(amount) AS Amount FROM salary_payment WHERE period_month BETWEEN substr(@from,1,7) AND @lastCompleteMonth"),
                ("depreciation", "SELECT COUNT(*) AS Rows,SUM(amount) AS Amount FROM asset_depreciation WHERE period_month BETWEEN substr(@from,1,7) AND @lastCompleteMonth"),
                ("disposal", "SELECT COUNT(*) AS Rows,SUM(disposal_value) AS Amount FROM asset WHERE disposed_date BETWEEN @from AND @to"),
                ("vendor-variance", "SELECT COUNT(*) AS Rows,SUM(credit_before_tax-cost_total) AS Amount FROM vendor_return WHERE return_date BETWEEN @from AND @to"),
            };
            foreach (var branch in branches)
            {
                string name = "pnl-branch-" + period.Item1 + "-" + branch.Name;
                await Measure(name, () => Task.Run(() =>
                {
                    using var connection = factory.Open();
                    return connection.QuerySingle<(long Rows, long? Amount)>(branch.Sql,
                        new { from = period.Item2, to = period.Item3, lastCompleteMonth = "2026-08" });
                }), static row => checked((int)row.Rows));
                CapturePlan(name, branch.Sql, new { from = period.Item2, to = period.Item3, lastCompleteMonth = "2026-08" });
            }
        }
        break;
    }
    case "hot-plans":
    {
        CapturePlan("startup-last-sale-actual",
            "SELECT MAX(sale_date) FROM sale WHERE status = 'COMPLETED'");
        CapturePlan("expiry-badge-actual",
            "SELECT COUNT(*) FROM v_expiry_alert WHERE bucket IN ('EXPIRED','CRITICAL')");
        CapturePlan("dashboard-core-actual", """
            WITH today_sales AS MATERIALIZED (
              SELECT sales,bills,taxable,taxable-cost AS gross_profit
              FROM pnl_day WHERE day=@today
            ), expiry_bounds AS MATERIALIZED (
              SELECT date('now','localtime') AS day,MAX(alert_days) AS max_days FROM (
                SELECT COALESCE(MAX(expiry_alert_days),0) AS alert_days FROM product
                UNION ALL SELECT COALESCE(MAX(expiry_alert_days),0) FROM category
                UNION ALL SELECT COALESCE(CAST(value AS INTEGER),30)
                  FROM setting WHERE key='expiry.default_alert_days'
                UNION ALL SELECT 30
              )
            ), expiry_near AS MATERIALIZED (
              SELECT COUNT(*) FILTER (WHERE l.expiry_date<=date(b.day,'+7 days')) AS critical,
                     COUNT(*) FILTER (WHERE l.expiry_date>date(b.day,'+7 days')) AS soon
              FROM expiry_bounds b JOIN product_lot l
                ON l.qty_on_hand>0 AND l.expiry_date IS NOT NULL
               AND l.expiry_date BETWEEN b.day
                   AND COALESCE(date(b.day,'+'||b.max_days||' days'),'9999-12-31')
              JOIN product p ON p.id=l.product_id
              JOIN category c ON c.id=p.category_id
              LEFT JOIN category pc ON pc.id=c.parent_id
              WHERE l.expiry_date<=COALESCE(date(b.day,'+'||COALESCE(
                  p.expiry_alert_days,c.expiry_alert_days,pc.expiry_alert_days,
                  CAST((SELECT value FROM setting WHERE key='expiry.default_alert_days') AS INTEGER),30
                )||' days'),'9999-12-31')
            ), expiry AS MATERIALIZED (
              SELECT (SELECT COUNT(*) FROM product_lot l
                      WHERE l.qty_on_hand>0 AND l.expiry_date IS NOT NULL
                        AND l.expiry_date<b.day) AS expired,
                     n.critical,n.soon
              FROM expiry_bounds b CROSS JOIN expiry_near n
            ), stock AS MATERIALIZED (
              SELECT COUNT(*) FILTER (WHERE t.is_low=1) AS low_products,
                     COALESCE(SUM(t.cost_value),0) AS stock_value
              FROM product_stock_total t JOIN product p ON p.id=t.product_id
              WHERE p.is_active=1
            )
            SELECT
              COALESCE(today_sales.sales,0) AS TodaySales,
              COALESCE(today_sales.taxable,0) AS TodayTaxableSales,
              COALESCE(today_sales.bills,0) AS TodayBills,
              COALESCE(today_sales.gross_profit,0) AS TodayGrossProfit,
              COALESCE((SELECT SUM(sales) FROM pnl_day WHERE day BETWEEN @monthStart AND @today),0) AS MonthSales,
              COALESCE((SELECT SUM(sales) FROM pnl_day WHERE day BETWEEN @previousStart AND @previousThrough),0) AS PreviousMonthSales,
              expiry.expired AS ExpiredLots,
              expiry.critical AS CriticalLots,
              expiry.soon AS SoonLots,
              stock.low_products AS LowStockProducts,
              stock.stock_value AS StockValue,
              COALESCE((SELECT SUM(balance_due) FROM v_vendor_balance),0) AS Payables
            FROM (SELECT 1) AS anchor
            LEFT JOIN today_sales ON 1=1
            CROSS JOIN expiry
            CROSS JOIN stock
            """, new { today = "2026-09-26", monthStart = "2026-09-01",
                previousStart = "2026-08-01", previousThrough = "2026-08-26" });
        CapturePlan("dashboard-payments-actual", """
            SELECT sp.mode AS Mode,SUM(sp.amount) AS Amount FROM sale_payment sp
            JOIN sale s ON s.id=sp.sale_id WHERE s.status='COMPLETED' AND s.sale_date=@today GROUP BY sp.mode ORDER BY sp.mode
            """, new { today = "2026-09-26" });
        CapturePlan("dashboard-trend-actual", """
            SELECT day AS Date,sales AS Sales,bills AS Bills,taxable-cost AS GrossProfit
            FROM pnl_day WHERE day BETWEEN @from AND @today AND bills>0 ORDER BY day
            """, new { from = "2026-08-28", today = "2026-09-26" });
        CapturePlan("dashboard-top-products-actual", """
            WITH totals AS (
              SELECT product_id AS ProductId,MAX(name) AS Name,
                     MAX(CASE WHEN local_is_null=0 THEN local_name END) AS LocalName,
                     SUM(qty) AS Quantity,SUM(revenue) AS Revenue,SUM(cost) AS Cost,
                     SUM(revenue-cost) AS Margin
              FROM dashboard_top_summary WHERE movements>0 GROUP BY product_id
            ), ranked AS (
              SELECT *,ROW_NUMBER() OVER (ORDER BY Revenue DESC,ProductId) AS RevenueRank,
                       ROW_NUMBER() OVER (ORDER BY Margin DESC,ProductId) AS MarginRank
              FROM totals
            )
            SELECT * FROM ranked WHERE RevenueRank<=10 OR MarginRank<=10
            """);
        break;
    }
    case "cold-warm":
    {
        // Fresh-process first call versus repeats. The OS file cache is deliberately
        // reported as uncontrolled; this does not claim a physical-disk cold start.
        var operations = new (string Name, Func<Task<int>> Run)[]
        {
            ("startup-health", async () => { await maintenance.CheckBeforeShellAsync(); return 1; }),
            ("catalog-first-page", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(Limit: 100))).Items.Count),
            ("catalog-fts", async () => (await catalog.SearchPageAsync(new ProductSearchFilter("grocery item", Limit: 100))).Items.Count),
            ("catalog-fts-ui8", async () => (await catalog.SearchPageAsync(new ProductSearchFilter("grocery item", Limit: 8))).Items.Count),
            ("history-recent", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(From: monthStart, To: end))).Count),
            ("expiry-badge", () => moneyOut.GetActionableExpiryCountAsync()),
            ("pnl-month", async () => (await reports.GetPnlAsync(monthStart, end)).Income.Count),
            ("pnl-fiscal", async () => (await reports.GetPnlAsync(fiscalStart, end)).Income.Count),
            ("dashboard", async () => { await reports.GetDashboardAsync(); return 1; }),
        };
        foreach (var operation in operations)
        {
            for (int iteration = 0; iteration < 21; iteration++)
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    int rows = await operation.Run();
                    profileSamples.Add(new(operation.Name + (iteration == 0 ? "-first" : "-warm"),
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, rows, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new(operation.Name + (iteration == 0 ? "-first" : "-warm"),
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0,
                        exception.GetType().Name + ": " + exception.Message));
                }
            }
        }
        break;
    }
    case "fault-transaction":
    {
        // The parent process waits for the marker, then kills this process while an
        // uncommitted write is held. Only use a disposable acceptance-store copy.
        string marker = Path.Combine(root, "phase0-fault-ready.txt");
        if (File.Exists(marker)) File.Delete(marker);
        await database.WriteAsync(transaction =>
        {
            transaction.Connection.Execute("""
                INSERT INTO expense(expense_date,category_id,amount,mode,paid_to)
                VALUES ('2026-09-26',1,12345,'CASH','phase0-interrupted-write')
                """, transaction: transaction.Transaction);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            Thread.Sleep(TimeSpan.FromMinutes(1));
            throw new InvalidOperationException("Fault transaction was not interrupted by the parent process.");
        });
        break;
    }
    case "print-queue-metric":
    {
        var printer = new ReceiptPrinter(settings);
        try
        {
            await printer.PrintLabelsAsync($"missing-printer-{Guid.NewGuid():N}",
                [new TsplLabel("Metric test", null, "METRIC-1", 1m, null)], useDriver: false);
        }
        catch (Exception) { /* The isolated test intentionally has no printer. */ }
        if (!metricSamples.Any(static sample => sample.Name == "print.queue.age"))
            throw new InvalidDataException("The printer queue did not emit its age metric.");
        break;
    }
    case "print-journal-profile":
    {
        var journal = new PrintJobJournal(factory);
        var saved = new SavedPrintJob("RECEIPT", "Audit printer", "Phase 0 print journal", "{}");
        for (int iteration = 0; iteration < 20; iteration++)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                long id = await journal.AddAsync(saved);
                journal.MarkSending(id);
                if (journal.RecoverInterrupted() != 1) throw new InvalidDataException("Print recovery count differed.");
                if (journal.OldestReview()?.Id != id) throw new InvalidDataException("Print review lookup differed.");
                if (journal.ClaimOldestReview(id)?.Id != id) throw new InvalidDataException("Print claim differed.");
                journal.MarkFailed(id);
                if (journal.ReviewCount() != 1) throw new InvalidDataException("Print failed count differed.");
                journal.Remove(id);
                profileSamples.Add(new("print-journal-cycle", Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1, null));
            }
            catch (Exception exception)
            {
                profileSamples.Add(new("print-journal-cycle", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    0, exception.GetType().Name + ": " + exception.Message));
            }
        }
        break;
    }
    case "realism-seed":
    {
        List<RealismSale> candidates;
        using (var connection = factory.Open())
            candidates = connection.Query<RealismSale>("""
                SELECT s.id AS SaleId,sl.id AS LineId,sl.qty AS Quantity
                FROM sale s JOIN sale_line sl ON sl.sale_id=s.id AND sl.line_no=1
                WHERE s.sale_date=@date AND s.status='COMPLETED'
                  AND NOT EXISTS (SELECT 1 FROM sale_return sr WHERE sr.sale_id=s.id)
                ORDER BY s.id LIMIT 200
                """, new { date = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }).ToList();
        if (candidates.Count < 200) throw new InvalidDataException("The fixture needs at least 200 same-day completed bills.");
        await Measure("seed-100-voids", async () =>
        {
            foreach (RealismSale item in candidates.Take(100))
                await sales.VoidAsync(item.SaleId, "Scale fixture void", OperationGuard.NewId());
            return 100;
        }, static x => x);
        await Measure("seed-100-returns", async () =>
        {
            foreach (RealismSale item in candidates.Skip(100).Take(100))
                await sales.ReturnAsync(new(item.SaleId, end, "Scale fixture return", RefundMode.Cash,
                    [new ReturnLineInput(item.LineId, Quantity.FromScaled(item.Quantity), true)], OperationGuard.NewId()));
            return 100;
        }, static x => x);
        await Measure("seed-30-backdated-expenses", () => Task.Run(() =>
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            for (int index = 0; index < 30; index++)
                connection.Execute("""
                    INSERT INTO expense(expense_date,category_id,amount,mode,paid_to)
                    VALUES (@date,1,@amount,'CASH','Scale fixture')
                    """, new { date = $"2026-08-{index % 28 + 1:D2}", amount = 1000L + index }, transaction);
            transaction.Commit();
            return 30;
        }), static x => x);
        using (var connection = factory.Open())
        {
            long voids = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale WHERE status='VOID'");
            long returns = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale_return");
            long backdated = connection.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM expense WHERE paid_to='Scale fixture' AND expense_date<'2026-09-26'
                """);
            if (voids < 100 || returns < 100 || backdated < 30)
                throw new InvalidDataException($"Realism seeding was incomplete: voids={voids}, returns={returns}, backdated={backdated}.");
        }
        break;
    }
    case "profile":
    case "sustained-profile":
    case "phase4-combined":
    {
        bool sustained = mode is "sustained-profile" or "phase4-combined";
        bool combined = mode == "phase4-combined";
        int durationSeconds = sustained && args.Length > 3
            ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 600;
        if (sustained && durationSeconds is < 120 or > 3600)
            throw new ArgumentException("Use 120 to 3600 duration seconds.", nameof(args));
        ProductImportPreview? combinedProductPreview = null;
        OpeningStockImportPreview? combinedOpeningPreview = null;
        string? combinedLotCode = null;
        var combinedProductImporter = new ProductExcelImporter(database, audit, settings);
        var combinedOpeningImporter = new OpeningStockExcelImporter(database, inventory, clock);
        if (combined)
        {
            string productPath = Path.Combine(paths.TempDir, "phase4-combined-products.xlsx");
            string openingPath = Path.Combine(paths.TempDir, "phase4-combined-opening.xlsx");
            using (var connection = factory.Open())
            {
                string unit = connection.ExecuteScalar<string>("SELECT code FROM uom ORDER BY id LIMIT 1")!;
                decimal gst = connection.ExecuteScalar<long>("SELECT rate_pct FROM tax_rate WHERE is_active=1 ORDER BY rate_pct LIMIT 1") / 1000m;
                string[] skus = connection.Query<string>("SELECT sku FROM product ORDER BY id LIMIT 2000").ToArray();
                combinedLotCode = connection.ExecuteScalar<string>("SELECT lot_code FROM product_lot ORDER BY id DESC LIMIT 1");
                await Measure("combined-build-workbooks", () => Task.Run(() =>
                {
                    using (var workbook = new XLWorkbook())
                    {
                        IXLWorksheet sheet = workbook.AddWorksheet("Products");
                        for (int column = 0; column < ProductExcelImporter.Headers.Length; column++)
                            sheet.Cell(1, column + 1).Value = ProductExcelImporter.Headers[column];
                        for (int index = 1; index <= 20000; index++)
                        {
                            int row = index + 1;
                            sheet.Cell(row, 1).Value = $"COMB{index:000000}";
                            sheet.Cell(row, 2).Value = $"Combined Product {index}";
                            sheet.Cell(row, 5).Value = "Scale Category";
                            sheet.Cell(row, 8).Value = unit;
                            sheet.Cell(row, 9).Value = "1000";
                            sheet.Cell(row, 10).Value = gst;
                            sheet.Cell(row, 12).Value = 10;
                            sheet.Cell(row, 13).Value = 20;
                            sheet.Cell(row, 16).Value = "N";
                            sheet.Cell(row, 18).Value = "Y";
                        }
                        workbook.SaveAs(productPath);
                    }
                    using (var workbook = new XLWorkbook())
                    {
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
                        workbook.SaveAs(openingPath);
                    }
                    return 40000;
                }), static x => x);
            }
            combinedProductPreview = await combinedProductImporter.DryRunAsync(productPath);
            combinedOpeningPreview = await combinedOpeningImporter.DryRunAsync(openingPath);
            if (combinedProductPreview.ValidCount != 20000 || combinedOpeningPreview.ValidCount != 20000)
                throw new InvalidDataException("The combined-run import previews did not validate 20,000 rows each.");
        }
        long startedAt = Stopwatch.GetTimestamp();
        using var sampleStop = new CancellationTokenSource();
        Task sampler = sustained ? Task.Run(async () =>
        {
            TimeSpan priorCpu = process.TotalProcessorTime;
            long priorSample = Stopwatch.GetTimestamp();
            while (!sampleStop.IsCancellationRequested)
            {
                process.Refresh();
                long now = Stopwatch.GetTimestamp();
                TimeSpan cpu = process.TotalProcessorTime;
                double elapsedSeconds = Stopwatch.GetElapsedTime(priorSample, now).TotalSeconds;
                double cpuPercent = elapsedSeconds > 0
                    ? (cpu - priorCpu).TotalSeconds / elapsedSeconds / Environment.ProcessorCount * 100 : 0;
                priorCpu = cpu;
                priorSample = now;
                string walPath = paths.DatabaseFile + "-wal";
                resourceSamples.Enqueue(new(Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                    File.Exists(walPath) ? new FileInfo(walPath).Length : 0, process.WorkingSet64,
                    cpuPercent, factory.ActiveConnections));
                try { await Task.Delay(1000, sampleStop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }) : Task.CompletedTask;
        Task backup = sustained ? Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            await Measure("sustained-backup", () => Task.Run(() => backups.Create("sustained-profile")), static _ => 1);
        }) : Task.CompletedTask;
        Task importAndCount = combined ? Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            Task productImport = Measure("combined-product-import-20000", () =>
                combinedProductImporter.ImportAsync(combinedProductPreview!, OperationGuard.NewId()),
                static result => result.Created + result.Updated);
            Task openingImport = Measure("combined-opening-import-20000", () =>
                combinedOpeningImporter.ImportAsync(combinedOpeningPreview!, OperationGuard.NewId()),
                static _ => 20000);
            await Task.WhenAll(productImport, openingImport);
        }) : Task.CompletedTask;
        using var secondDatabase = new DbExecutor(new SqliteConnectionFactory(paths), readConcurrency);
        var secondSales = new SalesService(secondDatabase, ledger, numbers, periods, settings, audit, clock);
        long productId;
        using (var connection = factory.Open())
            productId = connection.ExecuteScalar<long>("""
                SELECT product_id FROM product_lot WHERE qty_on_hand>1000 AND is_active=1
                AND expiry_date>=@today ORDER BY qty_on_hand DESC LIMIT 1
                """, new { today = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        var operations = new (string Name, Func<Task<int>> Run)[]
        {
            ("catalog-broad", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(Limit: 100))).Items.Count),
            ("catalog-fts", async () => (await catalog.SearchPageAsync(new ProductSearchFilter("grocery item", Limit: 100))).Items.Count),
            ("catalog-fts-ui8", async () => (await catalog.SearchPageAsync(new ProductSearchFilter("grocery item", Limit: 8))).Items.Count),
            ("catalog-low-stock", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(LowStockOnly: true, Limit: 100))).Items.Count),
            ("history-recent", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(From: monthStart, To: end))).Count),
            ("expiry-badge", () => moneyOut.GetActionableExpiryCountAsync()),
            ("pnl-month", async () => (await reports.GetPnlAsync(monthStart, end)).Income.Count),
            ("dashboard", async () => { await reports.GetDashboardAsync(); return 1; }),
        };
        if (combined)
            operations = [.. operations,
                ("report-stock-value", async () => (await reports.GetReportAsync(ReportKind.StockValue,
                    new ReportFilter(Limit: 100))).Rows.Count),
                ("report-sales", async () => (await reports.GetReportAsync(ReportKind.SalesRegister,
                    new ReportFilter(From: monthStart, To: end, Limit: 100))).Rows.Count)];
        async Task Reader(int reader)
        {
            for (int iteration = 0; !sustained ? iteration < 80 :
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds < durationSeconds; iteration++)
            {
                var operation = operations[(iteration + reader * 2) % operations.Length];
                long started = Stopwatch.GetTimestamp();
                try
                {
                    int rows = await operation.Run();
                    profileSamples.Add(new(operation.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, rows, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new(operation.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0,
                        exception.GetType().Name + ": " + exception.Message));
                    if (sustained)
                    {
                        Console.WriteLine($"SUSTAINED reader failed: {operation.Name}: {exception.GetType().Name}: {exception.Message}");
                        break;
                    }
                }
            }
        }
        async Task Writer(SalesService terminal)
        {
            for (int iteration = 0; !sustained ? iteration < 10 :
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds < durationSeconds; iteration++)
            {
                if (sustained) await Task.Delay(1000);
                long started = Stopwatch.GetTimestamp();
                try
                {
                    long addStarted = Stopwatch.GetTimestamp();
                    long saleId = await terminal.AddProductAsync(null, productId, Quantity.One);
                    profileSamples.Add(new("sale-add", Stopwatch.GetElapsedTime(addStarted).TotalMilliseconds, 1, null));
                    long getStarted = Stopwatch.GetTimestamp();
                    SaleDetails details = await terminal.GetAsync(saleId);
                    profileSamples.Add(new("sale-get", Stopwatch.GetElapsedTime(getStarted).TotalMilliseconds, 1, null));
                    long commitStarted = Stopwatch.GetTimestamp();
                    await terminal.CompleteAsync(new(saleId,
                        [new PaymentInput(PaymentMode.Cash, details.Totals.GrandTotal)], OperationGuard.NewId()));
                    profileSamples.Add(new("sale-commit", Stopwatch.GetElapsedTime(commitStarted).TotalMilliseconds, 1, null));
                    profileSamples.Add(new("sale-add-complete", Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new("sale-add-complete", Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0,
                        exception.GetType().Name + ": " + exception.Message));
                    if (sustained)
                    {
                        Console.WriteLine($"SUSTAINED writer failed: {exception.GetType().Name}: {exception.Message}");
                        break;
                    }
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(Reader)
            .Append(Writer(sales)).Append(Writer(secondSales)));
        await backup;
        await importAndCount;
        sampleStop.Cancel();
        await sampler;
        if (sustained)
        {
            using var connection = factory.Open();
            long afterSales = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale");
            long successfulSales = profileSamples.Count(static sample =>
                sample.Name == "sale-add-complete" && sample.Error is null);
            long failedSales = profileSamples.Count(static sample =>
                sample.Name == "sale-add-complete" && sample.Error is not null);
            sustainedSalesCheck = new(rowCounts["sale"], successfulSales, failedSales, afterSales);
            Console.WriteLine($"SUSTAINED sales before={rowCounts["sale"]}, after={afterSales}, " +
                $"successful={successfulSales}, WAL peak={resourceSamples.Max(static sample => sample.WalBytes)}, " +
                $"working peak={resourceSamples.Max(static sample => sample.WorkingSetBytes)}");
        }
        foreach (var group in profileSamples.GroupBy(static sample => sample.Name).OrderBy(static group => group.Key))
        {
            double[] times = group.Select(static sample => sample.Milliseconds).Order().ToArray();
            Console.WriteLine($"PROFILE {group.Key}: n={times.Length}, p50={Percentile(times, .5):0.0}, " +
                $"p95={Percentile(times, .95):0.0}, p99={Percentile(times, .99):0.0} ms, " +
                $"errors={group.Count(static sample => sample.Error is not null)}");
        }
        CapturePlan("catalog-broad", "SELECT id FROM product WHERE is_active=1 ORDER BY name COLLATE NOCASE,id LIMIT 100");
        CapturePlan("catalog-low-stock", """
            SELECT p.id FROM product p LEFT JOIN product_stock_total stock ON stock.product_id=p.id
            WHERE p.is_active=1 AND p.reorder_level>0 AND COALESCE(stock.quantity,0)<=p.reorder_level
            ORDER BY p.name COLLATE NOCASE,p.id LIMIT 100
            """);
        CapturePlan("catalog-fts", """
            SELECT p.id FROM product p JOIN product_fts f ON f.rowid=p.id
            WHERE p.is_active=1 AND product_fts MATCH @match
            ORDER BY p.name COLLATE NOCASE,p.id LIMIT 100
            """, new { match = "\"grocery\" \"item\"" });
        CapturePlan("history-recent", "SELECT id FROM sale WHERE sale_date BETWEEN @from AND @to ORDER BY sale_date DESC,id DESC LIMIT 50", new { from = "2026-09-01", to = "2026-09-26" });
        CapturePlan("pnl-day-range", "SELECT SUM(bills),SUM(taxable),SUM(rounding),SUM(cost),SUM(returns),SUM(return_taxable),SUM(return_cost) FROM pnl_day WHERE day BETWEEN @from AND @to", new { from = "2026-09-01", to = "2026-09-26" });
        CapturePlan("expiry-first-page", "SELECT lot_id FROM v_expiry_alert WHERE bucket='EXPIRED' ORDER BY expiry_date,lot_id LIMIT 100");
        break;
    }
    case "shape-profile":
    {
        using var secondDatabase = new DbExecutor(new SqliteConnectionFactory(paths));
        var secondSales = new SalesService(secondDatabase, ledger, numbers, periods, settings, audit, clock);
        long productId;
        string barcode;
        string sku;
        using (var connection = factory.Open())
        {
            productId = connection.ExecuteScalar<long>("""
                SELECT product_id FROM product_lot WHERE qty_on_hand>1000 AND is_active=1
                AND expiry_date>=@today ORDER BY qty_on_hand DESC LIMIT 1
                """, new { today = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
            barcode = connection.ExecuteScalar<string>("SELECT barcode FROM product_barcode ORDER BY id LIMIT 1")!;
            sku = connection.ExecuteScalar<string>("""
                SELECT p.sku FROM product p JOIN product_barcode pb ON pb.product_id=p.id
                ORDER BY pb.id LIMIT 1
                """)!;
        }
        var operations = new (string Name, Func<Task<int>> Run)[]
        {
            ("catalog-empty", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(Limit: 100))).Items.Count),
            ("catalog-short", async () => (await catalog.SearchPageAsync(new ProductSearchFilter("gr", Limit: 100))).Items.Count),
            ("catalog-fts", async () => (await catalog.SearchPageAsync(new ProductSearchFilter("grocery item", Limit: 100))).Items.Count),
            ("catalog-category", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(CategoryId: 1, Limit: 100))).Items.Count),
            ("catalog-brand", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(BrandId: 1, Limit: 100))).Items.Count),
            ("catalog-low-stock", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(LowStockOnly: true, Limit: 100))).Items.Count),
            ("catalog-combined", async () => (await catalog.SearchPageAsync(new ProductSearchFilter("grocery", CategoryId: 1, LowStockOnly: true, Limit: 100))).Items.Count),
            ("catalog-no-barcode", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(NoBarcodeOnly: true, Limit: 100))).Items.Count),
            ("catalog-missing-local", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(MissingLocalNameOnly: true, Limit: 100))).Items.Count),
            ("catalog-loose", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(LooseOnly: true, Limit: 100))).Items.Count),
            ("catalog-archived", async () => (await catalog.SearchPageAsync(new ProductSearchFilter(IncludeArchived: true, Limit: 100))).Items.Count),
            ("catalog-mixed-term", async () => (await catalog.SearchPageAsync(new ProductSearchFilter("grocery i", Limit: 100))).Items.Count),
            ("scan-barcode", async () => (await scans.ResolveAsync(barcode)).ProductId is null ? 0 : 1),
            ("scan-sku", async () => (await scans.ResolveAsync(sku)).ProductId is null ? 0 : 1),
            ("customer-empty", async () => (await sales.SearchCustomersAsync()).Count),
            ("customer-contains", async () => (await sales.SearchCustomersAsync("Scale Customer 4")).Count),
            ("customer-phone", async () => (await sales.SearchCustomersAsync("9000040000")).Count),
            ("customer-prefix", async () => (await sales.SearchCustomersAsync("Scale Customer 000")).Count),
            ("customer-no-match", async () => (await sales.SearchCustomersAsync("zzzz-missing")).Count),
            ("vendor-list", async () => (await purchasing.GetVendorsAsync()).Count),
            ("vendor-all", async () => (await purchasing.GetVendorsAsync(includeInactive: true)).Count),
            ("vendor-ledger", async () => (await purchasing.GetVendorLedgerAsync(1)).Count),
            ("vendor-lots", async () => (await purchasing.GetVendorLotsAsync(1)).Count),
            ("vendor-purchase-choices", async () => (await purchasing.GetVendorPurchaseChoicesAsync(1)).Count),
            ("purchase-page", async () => (await purchasing.GetPurchasesPageAsync()).Items.Count),
            ("purchase-filtered", async () => (await purchasing.GetPurchasesPageAsync(from: monthStart, to: end, vendorId: 1)).Items.Count),
            ("purchase-cursor", async () => { PurchasePage first = await purchasing.GetPurchasesPageAsync(limit: 20); return first.NextDate is null ? 0 : (await purchasing.GetPurchasesPageAsync(beforeDate: first.NextDate, beforeId: first.NextId, limit: 20)).Items.Count; }),
            ("purchase-legacy-cap", async () => (await purchasing.GetPurchasesAsync(5000)).Count),
            ("lot-page", async () => (await inventory.GetLotPageAsync()).Items.Count),
            ("lot-product", async () => (await inventory.GetLotPageAsync(productId: productId)).Items.Count),
            ("lot-available", async () => (await inventory.GetLotPageAsync(availableOnly: true)).Items.Count),
            ("lot-search", async () => (await inventory.GetLotPageAsync(query: "grocery")).Items.Count),
            ("lot-cursor", async () => { LotPage first = await inventory.GetLotPageAsync(limit: 20); return first.NextId is null ? 0 : (await inventory.GetLotPageAsync(beforeId: first.NextId, limit: 20)).Items.Count; }),
            ("history-recent", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(From: monthStart, To: end))).Count),
            ("history-invoice", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(InvoiceNumber: "2026"))).Count),
            ("history-amount", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(MinimumAmount: new Money(100), MaximumAmount: new Money(500)))).Count),
            ("history-status-payment", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(Status: SaleStatus.Completed, PaymentMode: PaymentMode.Cash))).Count),
            ("history-combined", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(From: fiscalStart, To: end, Status: SaleStatus.Completed, PaymentMode: PaymentMode.Cash, MinimumAmount: new Money(100)))).Count),
            ("history-cursor", async () => { IReadOnlyList<SaleHistoryItem> first = await sales.GetHistoryAsync(new SaleHistoryFilter(Limit: 20)); SaleHistoryItem? last = first.Count == 0 ? null : first[^1]; return last is null ? 0 : (await sales.GetHistoryAsync(new SaleHistoryFilter(BeforeDate: last.SaleDate, BeforeId: last.Id, Limit: 20))).Count; }),
            ("saved-bills", async () => (await sales.GetSavedBillPageAsync(includeDrafts: true)).Items.Count),
            ("saved-bills-search", async () => (await sales.GetSavedBillPageAsync(includeDrafts: true, query: "Scale")).Items.Count),
            ("saved-bills-cursor", async () => { SavedBillPage first = await sales.GetSavedBillPageAsync(includeDrafts: true, limit: 20); return first.NextCreatedAt is null ? 0 : (await sales.GetSavedBillPageAsync(includeDrafts: true, beforeCreatedAt: first.NextCreatedAt, beforeId: first.NextId, limit: 20)).Items.Count; }),
            ("held-bills", async () => (await sales.GetHeldAsync()).Count),
            ("held-recent", async () => (await sales.GetRecentHeldAsync()).Count),
            ("draft-bills", async () => (await sales.GetDraftsAsync()).Count),
            ("expiry-badge", () => moneyOut.GetActionableExpiryCountAsync()),
            ("damage-page", async () => (await moneyOut.GetDamagePageAsync(monthStart, end)).Items.Count),
            ("expense-page", async () => (await moneyOut.GetExpensePageAsync(monthStart, end)).Items.Count),
            ("asset-page", async () => (await assets.GetAssetPageAsync()).Items.Count),
            ("asset-search", async () => (await assets.GetAssetPageAsync(query: "Scale")).Items.Count),
            ("asset-active", async () => (await assets.GetAssetPageAsync(includeDisposed: false)).Items.Count),
            ("asset-cursor", async () => { AssetPage first = await assets.GetAssetPageAsync(limit: 20); return first.NextName is null ? 0 : (await assets.GetAssetPageAsync(afterDisposed: first.NextDisposed, afterName: first.NextName, afterId: first.NextId, limit: 20)).Items.Count; }),
            ("asset-unbounded", async () => (await assets.GetAssetsAsync()).Count),
            ("asset-schedule", async () => (await assets.GetScheduleAsync(41)).Count),
            ("employee-list", async () => (await moneyOut.GetEmployeesAsync(includeInactive: true)).Count),
            ("salary-register-month", async () => (await moneyOut.GetSalaryRegisterAsync(new YearMonth(2025, 10), new YearMonth(2025, 10))).Count),
            ("startup-health", async () => { await maintenance.CheckBeforeShellAsync(); return 1; }),
            ("pnl-month", async () => (await reports.GetPnlAsync(monthStart, end)).Income.Count),
            ("pnl-fiscal", async () => (await reports.GetPnlAsync(fiscalStart, end)).Income.Count),
            ("dashboard", async () => { await reports.GetDashboardAsync(); return 1; }),
            ("report-sales", async () => (await reports.GetReportAsync(ReportKind.SalesRegister, new ReportFilter(From: monthStart, To: end, Limit: 100))).Rows.Count),
            ("report-item", async () => (await reports.GetReportAsync(ReportKind.ItemSales, new ReportFilter(From: monthStart, To: end, ProductId: productId, Limit: 100))).Rows.Count),
            ("report-gst", async () => (await reports.GetReportAsync(ReportKind.GstSummary, new ReportFilter(From: monthStart, To: end, Limit: 100))).Rows.Count),
        };
        async Task Reader(int reader)
        {
            for (int iteration = 0; iteration < operations.Length * 5; iteration++)
            {
                var operation = operations[(iteration + reader * 7) % operations.Length];
                long started = Stopwatch.GetTimestamp();
                try
                {
                    int rows = await operation.Run();
                    profileSamples.Add(new(operation.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, rows, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new(operation.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0,
                        exception.GetType().Name + ": " + exception.Message));
                }
            }
        }
        async Task Writer(SalesService terminal)
        {
            for (int iteration = 0; iteration < 10; iteration++)
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    long saleId = await terminal.AddProductAsync(null, productId, Quantity.One);
                    SaleDetails details = await terminal.GetAsync(saleId);
                    await terminal.CompleteAsync(new(saleId,
                        [new PaymentInput(PaymentMode.Cash, details.Totals.GrandTotal)], OperationGuard.NewId()));
                    profileSamples.Add(new("sale-add-complete", Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new("sale-add-complete", Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0,
                        exception.GetType().Name + ": " + exception.Message));
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(Reader)
            .Append(Writer(sales)).Append(Writer(secondSales)));
        foreach (var group in profileSamples.GroupBy(static sample => sample.Name).OrderBy(static group => group.Key))
        {
            double[] times = group.Select(static sample => sample.Milliseconds).Order().ToArray();
            Console.WriteLine($"PROFILE {group.Key}: n={times.Length}, p50={Percentile(times, .5):0.0}, " +
                $"p95={Percentile(times, .95):0.0}, p99={Percentile(times, .99):0.0} ms, " +
                $"errors={group.Count(static sample => sample.Error is not null)}");
        }
        const string historySql = """
            SELECT s.id AS Id,s.invoice_no AS InvoiceNumber,s.sale_date AS SaleDate,s.status AS Status,
                   s.line_count AS LineCount,s.grand_total AS GrandTotal,s.completed_at AS CompletedAt,
                   s.void_reason AS VoidReason,
                   COALESCE((SELECT group_concat(DISTINCT p.mode) FROM sale_payment p WHERE p.sale_id=s.id),'') AS PaymentModes
            FROM sale s
            WHERE (@fromDate IS NULL OR s.sale_date>=@fromDate) AND (@toDate IS NULL OR s.sale_date<=@toDate)
              AND (@status IS NULL OR s.status=@status)
              AND (@payment IS NULL OR EXISTS(SELECT 1 FROM sale_payment p WHERE p.sale_id=s.id AND p.mode=@payment))
              AND (@minimum IS NULL OR s.grand_total>=@minimum) AND (@maximum IS NULL OR s.grand_total<=@maximum)
              AND (@invoice IS NULL OR s.invoice_no LIKE @invoice || '%')
              AND (@beforeDate IS NULL OR (s.sale_date,s.id)<(@beforeDate,@beforeId))
            ORDER BY s.sale_date DESC,s.id DESC LIMIT @limit
            """;
        foreach (string shape in new[] { "recent", "invoice", "amount" })
            CapturePlan("history-actual-" + shape, historySql, new
            {
                fromDate = shape == "recent" ? "2026-09-01" : null,
                toDate = shape == "recent" ? "2026-09-26" : null,
                status = (string?)null,
                payment = (string?)null,
                minimum = shape == "amount" ? 100000L : (long?)null,
                maximum = shape == "amount" ? 500000L : (long?)null,
                invoice = shape == "invoice" ? "2026" : null,
                beforeDate = (string?)null,
                beforeId = (long?)null,
                limit = 100,
            });
        const string customerSql = """
            SELECT id AS Id,name AS Name,phone AS Phone,gstin AS Gstin FROM customer
            WHERE is_active=1 AND (@search='' OR name LIKE @pattern OR phone LIKE @pattern)
            ORDER BY name,id LIMIT 100
            """;
        CapturePlan("customer-actual-empty", customerSql, new { search = "", pattern = "%%" });
        CapturePlan("customer-actual-contains", customerSql,
            new { search = "Scale Customer 4", pattern = "%Scale Customer 4%" });
        const string purchaseSql = """
            SELECT p.id AS Id,p.doc_no AS DocumentNumber,v.name AS VendorName,
                   p.vendor_invoice_no AS VendorInvoiceNumber,p.received_date AS ReceivedDate,
                   p.status AS Status,p.grand_total AS GrandTotal,p.vendor_bill_total AS VendorBillTotal
            FROM purchase p JOIN vendor v ON v.id=p.vendor_id
            WHERE (@from IS NULL OR p.received_date>=@from)
              AND (@to IS NULL OR p.received_date<=@to)
              AND (@vendorId IS NULL OR p.vendor_id=@vendorId)
              AND (@beforeDate IS NULL OR p.received_date<@beforeDate
                   OR (p.received_date=@beforeDate AND p.id<@beforeId))
            ORDER BY p.received_date DESC,p.id DESC LIMIT @take
            """;
        CapturePlan("purchase-actual-broad", purchaseSql, new
        { from = (string?)null, to = (string?)null, vendorId = (long?)null, beforeDate = (string?)null, beforeId = (long?)null, take = 101 });
        CapturePlan("purchase-actual-vendor", purchaseSql, new
        { from = "2026-09-01", to = "2026-09-26", vendorId = 1L, beforeDate = (string?)null, beforeId = (long?)null, take = 101 });
        foreach (string shape in new[] { "empty", "short", "fts", "category", "low-stock", "combined" })
        {
            bool fts = shape is "fts" or "combined";
            bool lowStock = shape is "low-stock" or "combined";
            string join = fts ? "JOIN product_fts f ON f.rowid=p.id" : "";
            string stockJoin = lowStock ? "LEFT JOIN product_stock_total stock ON stock.product_id=p.id" : "";
            string stockFilter = lowStock
                ? "AND p.reorder_level>0 AND COALESCE(stock.quantity,0)<=p.reorder_level" : "";
            string search = fts ? "AND product_fts MATCH @match"
                : shape == "short"
                    ? "AND (p.sku LIKE @prefix ESCAPE '\\' OR p.name LIKE @prefix ESCAPE '\\' OR p.name_local LIKE @prefix ESCAPE '\\' OR p.search_terms LIKE @prefix ESCAPE '\\')"
                    : "";
            string sql = $$"""
                WITH visible AS MATERIALIZED (
                    SELECT p.id,p.name FROM product p {{join}} {{stockJoin}}
                    WHERE (@IncludeArchived=1 OR p.is_active=1)
                      AND (@CategoryId IS NULL OR p.category_id=@CategoryId)
                      AND (@BrandId IS NULL OR p.brand_id=@BrandId)
                      {{stockFilter}}
                      AND (@NoBarcodeOnly=0 OR NOT EXISTS(SELECT 1 FROM product_barcode pb WHERE pb.product_id=p.id))
                      AND (@MissingLocalNameOnly=0 OR p.name_local IS NULL OR trim(p.name_local)='')
                      AND (@LooseOnly=0 OR p.is_loose=1)
                      AND (@afterName IS NULL OR p.name COLLATE NOCASE > @afterName COLLATE NOCASE
                           OR (p.name COLLATE NOCASE = @afterName COLLATE NOCASE AND p.id>@afterId))
                      {{search}}
                    ORDER BY p.name COLLATE NOCASE,p.id LIMIT @limit
                )
                SELECT p.id AS Id, p.sku AS Sku, p.name AS Name, p.name_local AS NameLocal,
                       c.name AS Category, b.name AS Brand, u.code AS Uom,
                       COALESCE(total.quantity,0) AS StockScaled,
                       COALESCE(l.selling_price,p.default_selling_price) AS SellingPriceScaled,
                       COALESCE(l.mrp,p.default_mrp) AS MrpScaled,
                       p.is_loose AS IsLoose, p.is_active AS IsActive,
                       EXISTS(SELECT 1 FROM product_barcode pb WHERE pb.product_id=p.id) AS HasBarcode
                FROM visible page JOIN product p ON p.id=page.id
                LEFT JOIN product_stock_total total ON total.product_id=p.id
                JOIN category c ON c.id=p.category_id
                JOIN uom u ON u.id=p.uom_id
                LEFT JOIN brand b ON b.id=p.brand_id
                LEFT JOIN product_lot l ON l.id=(
                    SELECT pl.id FROM product_lot pl WHERE pl.product_id=p.id AND pl.qty_on_hand>0 AND pl.is_active=1
                    ORDER BY CASE WHEN pl.expiry_date IS NULL THEN 1 ELSE 0 END, pl.expiry_date, pl.id LIMIT 1)
                ORDER BY page.name COLLATE NOCASE,page.id
                """;
            CapturePlan("catalog-actual-" + shape, sql, new
            {
                IncludeArchived = 0,
                CategoryId = shape is "category" or "combined" ? 1L : (long?)null,
                BrandId = (long?)null,
                NoBarcodeOnly = 0,
                MissingLocalNameOnly = 0,
                LooseOnly = 0,
                afterName = (string?)null,
                afterId = (long?)null,
                prefix = "gr%",
                match = shape == "combined" ? "\"grocery\"" : "\"grocery\" \"item\"",
                limit = 101,
            });
        }
        // These are the actual SQL definitions used by ReportService, with the same
        // parameter set. Capture both broad and selective filters to expose OR plans.
        foreach ((ReportKind kind, ReportDefinition definition) in ReportDefinitions.All)
        {
            foreach (bool selective in new[] { false, true })
                CapturePlan($"report-{kind}-{(selective ? "selective" : "broad")}", definition.Sql,
                    new
                    {
                        from = selective ? "2026-09-01" : "2024-01-01",
                        to = "2026-09-26",
                        ProductId = selective ? productId : (long?)null,
                        LotId = selective ? 1L : (long?)null,
                        VendorId = selective ? 1L : (long?)null,
                        Status = selective ? "COMPLETED" : null,
                        PaymentMode = selective ? "CASH" : null,
                        Entity = selective ? "product" : null,
                        Action = selective ? "IMPORT" : null,
                        previousFrom = "2026-08-01",
                        previousTo = "2026-08-31",
                        slowFrom = "2026-08-27",
                        Limit = 101,
                    });
        }
        break;
    }
    case "phase6-category-index":
    {
        async Task<TabularReport> FiscalCategory() => await reports.GetReportAsync(
            ReportKind.CategorySales, new ReportFilter(From: fiscalStart, To: end, Limit: 100));
        for (int repeat = 0; repeat < 3; repeat++)
            await Measure("category-before", FiscalCategory, static report => report.Rows.Count);
        long bytesBefore = new FileInfo(paths.DatabaseFile).Length;
        await Measure("create-category-covering-index", () => Task.Run(() =>
        {
            using var connection = factory.Open();
            connection.Execute("""
                CREATE INDEX ix_sale_line_report_cover
                ON sale_line(sale_id,product_id,qty,taxable_amount,cost_amount)
                """);
            connection.Execute("PRAGMA optimize");
            connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            return 1;
        }), static value => value);
        Console.WriteLine($"CATEGORY_INDEX_MAIN_GROWTH_BYTES {new FileInfo(paths.DatabaseFile).Length - bytesBefore}");
        for (int repeat = 0; repeat < 3; repeat++)
            await Measure("category-after", FiscalCategory, static report => report.Rows.Count);
        break;
    }
    case "phase6-matrix":
    {
        const string sourceSql = """
            SELECT section AS Section,head_code AS HeadCode,NULLIF(category_id,0) AS ReferenceId,
                   substr(entry_date,1,7) AS Month,SUM(amount) AS Amount
            FROM v_pnl_entry WHERE entry_date BETWEEN @from AND @to
            GROUP BY section,head_code,category_id,substr(entry_date,1,7)
            """;
        foreach ((string name, DateOnly from, DateOnly to) in new[]
                 { ("five-year", new DateOnly(2022, 1, 1), end),
                   ("partial-month", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 15)) })
        {
            string fromIso = from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            string toIso = to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            List<MatrixAuditRow> source = [];
            PnlMatrix? matrix = null;
            await Measure("matrix-source-" + name, () => Task.Run(() =>
            {
                using var connection = factory.Open();
                return source = connection.Query<MatrixAuditRow>(sourceSql,
                    new { from = fromIso, to = toIso }).ToList();
            }), static rows => rows.Count);
            CapturePlan("matrix-source-" + name, sourceSql, new { from = fromIso, to = toIso });
            await Measure("matrix-projected-" + name, async () =>
                matrix = await reports.GetPnlMatrixAsync(from, to), static value => value.Rows.Count);
            var expected = source.ToDictionary(row => (row.Section, row.HeadCode, row.ReferenceId, row.Month),
                row => row.Amount);
            var allCells = matrix!.Rows
                .Where(row => row.HeadCode is not ("NET_SALES" or "GROSS_PROFIT" or "TOTAL_EXPENSES" or "NET_PROFIT"))
                .SelectMany(row => matrix.MonthKeys.Select((month, index) => new
                    { Key = (row.Section, row.HeadCode, row.ReferenceId, month), Amount = row.Months[index].Scaled }))
                .ToList();
            if (allCells.Any(cell => !expected.ContainsKey(cell.Key) && cell.Amount != 0))
                throw new InvalidDataException("The projected monthly P&L matrix contains an unexpected nonzero cell.");
            var observed = allCells
                .Where(cell => expected.ContainsKey(cell.Key))
                .ToDictionary(cell => cell.Key, cell => cell.Amount);
            if (observed.Count != expected.Count || expected.Any(pair => !observed.TryGetValue(pair.Key, out long amount) || amount != pair.Value))
                throw new InvalidDataException("The projected monthly P&L matrix differs from the source view.");
            Console.WriteLine($"OK matrix-parity-{name}: {expected.Count} populated cells");
        }
        break;
    }
    case "phase6-reports":
    case "report-profile":
    {
        long productId;
        using (var connection = factory.Open())
            productId = connection.ExecuteScalar<long>("SELECT product_id FROM product_lot WHERE qty_on_hand>0 ORDER BY qty_on_hand DESC LIMIT 1");
        var operations = (from kind in Enum.GetValues<ReportKind>()
            from selective in Enumerable.Range(0, 2).Select(static index => index == 1)
            select (Name: $"report-{kind}-{(selective ? "selected" : "broad")}",
                Run: (Func<Task<int>>)(async () =>
                {
                    var filter = new ReportFilter(
                        From: selective ? fiscalStart : monthStart,
                        To: end,
                        ProductId: selective ? productId : null,
                        VendorId: selective ? 1 : null,
                        AssetId: kind == ReportKind.AssetSchedule ? 1 : null,
                        Status: selective ? "COMPLETED" : null,
                        PaymentMode: selective ? "CASH" : null,
                        Entity: selective ? "product" : null,
                        Action: selective ? "IMPORT" : null,
                        Limit: 100);
                    return (await reports.GetReportAsync(kind, filter)).Rows.Count;
                }))).ToArray();
        async Task Reader(int reader)
        {
            for (int iteration = 0; iteration < operations.Length * 2; iteration++)
            {
                var operation = operations[(iteration + reader * 9) % operations.Length];
                long started = Stopwatch.GetTimestamp();
                try
                {
                    int rows = await operation.Run();
                    profileSamples.Add(new(operation.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, rows, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new(operation.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0,
                        exception.GetType().Name + ": " + exception.Message));
                }
            }
        }
        async Task Writer()
        {
            for (int iteration = 0; iteration < 10; iteration++)
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    long saleId = await sales.AddProductAsync(null, productId, Quantity.One);
                    SaleDetails details = await sales.GetAsync(saleId);
                    await sales.CompleteAsync(new(saleId,
                        [new PaymentInput(PaymentMode.Cash, details.Totals.GrandTotal)], OperationGuard.NewId()));
                    profileSamples.Add(new("sale-add-complete", Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new("sale-add-complete", Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0,
                        exception.GetType().Name + ": " + exception.Message));
                }
            }
        }
        if (mode == "phase6-reports")
        {
            foreach (var operation in operations)
            {
                await Measure(operation.Name, operation.Run, static rows => rows);
                Console.Out.Flush();
            }
        }
        else
            await Task.WhenAll(Enumerable.Range(0, 4).Select(Reader).Append(Writer()));
        foreach (var group in profileSamples.GroupBy(static sample => sample.Name).OrderBy(static group => group.Key))
        {
            double[] times = group.Select(static sample => sample.Milliseconds).Order().ToArray();
            Console.WriteLine($"PROFILE {group.Key}: n={times.Length}, p50={Percentile(times, .5):0.0}, " +
                $"p95={Percentile(times, .95):0.0}, p99={Percentile(times, .99):0.0} ms, " +
                $"errors={group.Count(static sample => sample.Error is not null)}");
        }
        break;
    }
    case "hot-index-trial":
        foreach ((string name, string sql) in new[]
        {
            ("saved-bills", "CREATE INDEX IF NOT EXISTS ix_sale_saved ON sale(created_at DESC,id DESC) WHERE status IN ('HELD','DRAFT')"),
            ("sale-date-id", "CREATE INDEX IF NOT EXISTS ix_sale_date_id ON sale(sale_date DESC,id DESC)"),
            ("return-line-return", "CREATE INDEX IF NOT EXISTS ix_return_line_return ON sale_return_line(sale_return_id)"),
            ("lot-vendor-date", "CREATE INDEX IF NOT EXISTS ix_lot_vendor_date ON product_lot(vendor_id,received_date DESC,id DESC)"),
            ("audit-at-id", "CREATE INDEX IF NOT EXISTS ix_audit_at_id ON audit_log(at,id DESC)"),
        })
            await Measure("create-index-" + name, () => Task.Run(() =>
            {
                using var connection = factory.Open();
                connection.Execute(sql);
                return 1;
            }), static x => x);
        using (var connection = factory.Open()) connection.Execute("PRAGMA optimize");
        break;
    case "phase4-valuation":
    {
        for (int run = 0; run < 5; run++)
            await Measure("stock-value-report-projected", () => reports.GetReportAsync(
                ReportKind.StockValue, new ReportFilter(Limit: 100)), static report => report.Rows.Count);
        using var connection = factory.Open();
        long drift = connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM product_stock_total t
            WHERE t.quantity<>COALESCE((SELECT SUM(l.qty_on_hand) FROM product_lot l
                                        WHERE l.product_id=t.product_id),0)
               OR t.cost_value<>COALESCE((SELECT SUM(CAST(ROUND(l.qty_on_hand*l.unit_cost/1000.0) AS INTEGER))
                                          FROM product_lot l WHERE l.product_id=t.product_id),0)
               OR t.selling_value<>COALESCE((SELECT SUM(CAST(ROUND(l.qty_on_hand*l.selling_price/1000.0) AS INTEGER))
                                             FROM product_lot l WHERE l.product_id=t.product_id),0)
               OR t.mrp_value<>COALESCE((SELECT SUM(CAST(ROUND(l.qty_on_hand*COALESCE(l.mrp,l.selling_price)/1000.0) AS INTEGER))
                                         FROM product_lot l WHERE l.product_id=t.product_id),0)
            """);
        Console.WriteLine($"VALUATION_DRIFT {drift}");
        if (drift != 0) throw new InvalidDataException($"{drift} product valuations differ from their lots.");
        break;
    }
    case "phase1-low-seed":
    {
        await Measure("seed-low-stock", () => database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute("""
                UPDATE product SET reorder_level=1000000000 WHERE id%10=0
                """, transaction: transaction.Transaction);
            long low = transaction.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM product_stock_total WHERE is_low=1", transaction: transaction.Transaction);
            if (low == 0) throw new InvalidDataException("The low stock seed had no matches.");
            return changed;
        }), static x => x);
        break;
    }
    case "phase1-export":
    {
        string csv = Path.Combine(paths.ExportsDir, "phase1-full-sales.csv");
        await Measure("full-sales-csv", async () =>
        {
            long count = await reports.ExportCsvAsync(ReportKind.SalesRegister,
                new ReportFilter(From: new DateOnly(2020, 1, 1), To: end, Limit: 100), csv);
            using var connection = factory.Open();
            long expected = connection.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM sale WHERE sale_date BETWEEN '2020-01-01' AND '2026-09-26'
                  AND status IN ('COMPLETED','VOID')
                """);
            if (count != expected || File.ReadLines(csv).LongCount() != expected + 1)
                throw new InvalidDataException($"CSV rows {count} do not match expected {expected}.");
            if (Directory.GetFiles(paths.ExportsDir, "*.partial").Length != 0)
                throw new InvalidDataException("Partial export files remain.");
            return checked((int)count);
        }, static x => x);
        break;
    }
    case "phase1-history-hot":
    {
        using var historyConnection = factory.Open();
        DateOnly today = DateOnly.ParseExact(historyConnection.ExecuteScalar<string>(
            "SELECT MAX(sale_date) FROM sale")!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await Measure("history-500-today", () => sales.GetHistoryAsync(
            new SaleHistoryFilter(From: today, To: today, Limit: 500)), static x => x.Count);
        await Measure("history-500-range", () => sales.GetHistoryAsync(
            new SaleHistoryFilter(From: new DateOnly(1900, 1, 1), To: today, Limit: 500)), static x => x.Count);
        await Measure("history-500-middle", () => sales.GetHistoryAsync(
            new SaleHistoryFilter(From: today, To: today, BeforeDate: today,
                BeforeId: 2_500_000, Limit: 500)), static x => x.Count);
        await Measure("history-500-oldest", () => sales.GetHistoryAsync(
            new SaleHistoryFilter(From: today, To: today, BeforeDate: today,
                BeforeId: 100_000, Limit: 500)), static x => x.Count);
        await Measure("history-500-middle-unbounded", () => sales.GetHistoryAsync(
            new SaleHistoryFilter(BeforeDate: today, BeforeId: 2_500_000, Limit: 500)), static x => x.Count);
        await Measure("history-500-middle-range", () => sales.GetHistoryAsync(
            new SaleHistoryFilter(From: new DateOnly(1900, 1, 1), To: today,
                BeforeDate: today, BeforeId: 2_500_000, Limit: 500)), static x => x.Count);
        await Measure("history-500-transition-range", () => sales.GetHistoryAsync(
            new SaleHistoryFilter(From: new DateOnly(1900, 1, 1), To: today,
                BeforeDate: today, BeforeId: 64_001, Limit: 500)), static x => x.Count);
        await Measure("history-20-pages-today", async () =>
        {
            DateOnly? beforeDate = null;
            long? beforeId = null;
            int count = 0;
            for (int pageNumber = 0; pageNumber < 20; pageNumber++)
            {
                IReadOnlyList<SaleHistoryItem> page = await sales.GetHistoryAsync(
                    new SaleHistoryFilter(From: today, To: today, BeforeDate: beforeDate,
                        BeforeId: beforeId, Limit: 500));
                count += page.Count;
                if (page.Count < 500) break;
                beforeDate = page[^1].SaleDate;
                beforeId = page[^1].Id;
            }
            return count;
        }, static x => x);
        CapturePlan("history-500-today", $"""
            SELECT s.id FROM sale s
            WHERE s.sale_date='{today:yyyy-MM-dd}'
            ORDER BY s.sale_date DESC,s.id DESC LIMIT 500
            """);
        CapturePlan("history-500-middle", $"""
            SELECT s.id FROM sale s
            WHERE s.sale_date='{today:yyyy-MM-dd}' AND s.id<2500000
            ORDER BY s.sale_date DESC,s.id DESC LIMIT 500
            """);
        break;
    }
    case "phase1-history-seed":
    {
        using var connection = factory.Open();
        long existing = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale");
        if (existing > 5_000_000) throw new InvalidDataException("The sale fixture exceeds five million rows.");
        string saleDate = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        const int batchSize = 50_000;
        for (long offset = existing; offset < 5_000_000; offset += batchSize)
        {
            int take = (int)Math.Min(batchSize, 5_000_000 - offset);
            using var transaction = connection.BeginTransaction();
            connection.Execute("""
                WITH RECURSIVE n(value) AS (
                    SELECT 1 UNION ALL SELECT value+1 FROM n WHERE value<@take
                )
                INSERT INTO sale(invoice_no,sale_date,status,line_count,taxable_total,grand_total)
                SELECT 'PHASE1-'||(value+@offset),@saleDate,'COMPLETED',0,1000,1000 FROM n
                """, new { take, offset, saleDate }, transaction, commandTimeout: 300);
            transaction.Commit();
            if ((offset / batchSize) % 10 == 0) Console.WriteLine($"SEEDED {offset + take} / 5000000");
        }
        long actual = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale");
        if (actual != 5_000_000) throw new InvalidDataException($"Seeded {actual} sales, expected 5000000.");
        Console.WriteLine($"HISTORY_ROWS {actual} DATE {saleDate}");
        break;
    }
    case "phase1-read":
    {
        await Measure("catalog-visible-page", async () =>
        {
            ProductSearchPage page = await catalog.SearchPageAsync(new ProductSearchFilter(Limit: 100));
            if (page.Items.Count > 100) throw new InvalidDataException("Catalog page exceeded its bound.");
            return page.Items.Count;
        }, static x => x);
        await Measure("catalog-visible-page-warm", () =>
            catalog.SearchPageAsync(new ProductSearchFilter(Limit: 100)), static x => x.Items.Count);
        await Measure("catalog-low-stock-page", () =>
            catalog.SearchPageAsync(new ProductSearchFilter(LowStockOnly: true, Limit: 100)),
            static x => x.Items.Count);
        await Measure("catalog-low-stock-traversal", async () =>
        {
            string? afterName = null;
            long? afterId = null;
            var seen = new HashSet<long>();
            while (true)
            {
                ProductSearchPage page = await catalog.SearchPageAsync(
                    new ProductSearchFilter(LowStockOnly: true, Limit: 100), afterName, afterId);
                foreach (ProductListItem item in page.Items)
                    if (!seen.Add(item.Id)) throw new InvalidDataException("Duplicate low stock page row.");
                if (!page.HasMore) break;
                afterName = page.NextName;
                afterId = page.NextId;
            }
            using var connection = factory.Open();
            int expected = connection.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM product p JOIN product_stock_total t ON t.product_id=p.id
                WHERE p.is_active=1 AND t.is_low=1
                """);
            if (seen.Count != expected) throw new InvalidDataException($"Reached {seen.Count} of {expected} low products.");
            return seen.Count;
        }, static x => x);
        await Measure("catalog-short-page", () =>
            catalog.SearchPageAsync(new ProductSearchFilter("gr", Limit: 100)),
            static x => x.Items.Count);
        await Measure("catalog-fts-page", () =>
            catalog.SearchPageAsync(new ProductSearchFilter("grocery item", Limit: 100)),
            static x => x.Items.Count);
        await Measure("lot-visible-page", async () =>
        {
            LotPage page = await inventory.GetLotPageAsync(limit: 100);
            if (page.Items.Count > 100) throw new InvalidDataException("Lot page exceeded its bound.");
            return page.Items.Count;
        }, static x => x);
        await Measure("vendor-ledger-page", async () =>
        {
            VendorLedgerPage page = await purchasing.GetVendorLedgerPageAsync(1);
            if (page.Items.Count > 100) throw new InvalidDataException("Vendor ledger page exceeded its bound.");
            return page.Items.Count;
        }, static x => x);
        await Measure("vendor-lot-page", async () =>
        {
            VendorLotPage page = await purchasing.GetVendorLotPageAsync(1);
            if (page.Items.Count > 100) throw new InvalidDataException("Vendor lot page exceeded its bound.");
            return page.Items.Count;
        }, static x => x);
        await Measure("vendor-purchase-search", async () =>
        {
            VendorPurchasePage page = await purchasing.GetVendorPurchasePageAsync(1, "SCALE-GRN-");
            if (page.Items.Count > 100) throw new InvalidDataException("Vendor purchase page exceeded its bound.");
            return page.Items.Count;
        }, static x => x);
        await Measure("vendor-purchase-traversal", async () =>
        {
            DateOnly? beforeDate = null;
            long? beforeId = null;
            var seen = new HashSet<long>();
            while (true)
            {
                VendorPurchasePage page = await purchasing.GetVendorPurchasePageAsync(1,
                    beforeDate: beforeDate, beforeId: beforeId);
                foreach (VendorPurchaseChoice choice in page.Items)
                    if (!seen.Add(choice.Id)) throw new InvalidDataException("Duplicate vendor purchase page row.");
                if (!page.HasMore) break;
                beforeDate = page.NextDate;
                beforeId = page.NextId;
            }
            using var connection = factory.Open();
            int expected = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM purchase WHERE vendor_id=1 AND status='POSTED'");
            if (seen.Count != expected) throw new InvalidDataException($"Reached {seen.Count} of {expected} purchases.");
            return seen.Count;
        }, static x => x);
        using (var connection = factory.Open())
        {
            long lowMismatch = connection.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM product p LEFT JOIN product_stock_total t ON t.product_id=p.id
                WHERE t.product_id IS NULL OR t.is_low != CASE WHEN p.reorder_level>0
                    AND t.quantity<=p.reorder_level THEN 1 ELSE 0 END
                """);
            if (lowMismatch != 0) throw new InvalidDataException($"Low-stock projection mismatches: {lowMismatch}.");
            CapturePlan("vendor-purchase-page", "SELECT id FROM purchase WHERE vendor_id=1 AND status='POSTED' ORDER BY received_date DESC,id DESC LIMIT 100");
            CapturePlan("customer-name-page", "SELECT id FROM customer WHERE is_active=1 ORDER BY name COLLATE NOCASE,id LIMIT 100");
            CapturePlan("customer-phone-prefix", "SELECT id FROM customer WHERE is_active=1 AND phone>='90000' AND phone<'90000:' ORDER BY phone,id LIMIT 100");
            CapturePlan("catalog-low-stock", "SELECT p.id FROM product_stock_total t JOIN product p ON p.id=t.product_id WHERE t.is_low=1 AND p.is_active=1 ORDER BY p.name COLLATE NOCASE,p.id LIMIT 100");
            CapturePlan("catalog-low-stock-name-order", "SELECT p.id FROM product p INDEXED BY ix_product_name CROSS JOIN product_stock_total t ON t.product_id=p.id AND t.is_low=1 WHERE p.is_active=1 ORDER BY p.name COLLATE NOCASE,p.id LIMIT 100");
        }
        break;
    }
    case "phase4-catalog-read":
    {
        DateOnly today = new(2026, 10, 19);
        var stockReports = new ReportService(database, settings,
            new AuditClock(new DateTime(2026, 10, 19, 12, 0, 0)));
        long productId;
        using (var connection = factory.Open())
            productId = connection.ExecuteScalar<long>("""
                SELECT product_id FROM product_lot GROUP BY product_id
                ORDER BY COUNT(*) DESC LIMIT 1
                """);
        if (productId <= 0) throw new InvalidDataException("Catalog fixture has no lots.");
        var shapes = new (string Name, Func<Task<int>> Read)[]
        {
            ("products-expired", async () => (await catalog.SearchPageAsync(
                new ProductSearchFilter(Limit: 50, ExpiryFilter: "EXPIRED", ExpiryAsOf: today))).Items.Count),
            ("products-expiring", async () => (await catalog.SearchPageAsync(
                new ProductSearchFilter(Limit: 50, ExpiryFilter: "EXPIRING", ExpiryAsOf: today))).Items.Count),
            ("lots-product-all", async () => (await inventory.GetLotPageAsync(productId,
                limit: 50, asOfDate: today)).Items.Count),
            ("lots-product-expired", async () => (await inventory.GetLotPageAsync(productId,
                limit: 50, expiryFilter: "EXPIRED", asOfDate: today)).Items.Count),
            ("lots-product-expiring", async () => (await inventory.GetLotPageAsync(productId,
                limit: 50, expiryFilter: "EXPIRING", asOfDate: today)).Items.Count),
            ("current-stock-exclude-expired", async () => (await stockReports.GetReportAsync(
                ReportKind.StockValue, new ReportFilter(IncludeExpired: false, Limit: 50))).Rows.Count),
        };
        foreach (var shape in shapes)
        {
            await Measure(shape.Name + "-first", shape.Read, static count => count);
            for (int index = 0; index < 30; index++)
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    int rows = await shape.Read();
                    profileSamples.Add(new(shape.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        rows, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new(shape.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        0, exception.GetType().Name + ": " + exception.Message));
                    throw;
                }
            }
        }
        CapturePlan("phase4-products-expired", """
            SELECT p.id FROM product p INDEXED BY ix_product_name
            WHERE p.is_active=1 AND EXISTS(SELECT 1 FROM product_lot l
              WHERE l.product_id=p.id AND l.qty_on_hand>0 AND l.expiry_date<'2026-10-19')
            ORDER BY p.name COLLATE NOCASE,p.id LIMIT 50
            """);
        CapturePlan("phase4-product-lots-expired", """
            SELECT l.id FROM product_lot l WHERE l.product_id=@productId
              AND l.qty_on_hand>0 AND l.expiry_date<'2026-10-19'
            ORDER BY l.id DESC LIMIT 50
            """, new { productId });
        break;
    }
    case "phase7-sql":
    {
        // Times one SQL file through the app's connection factory, raw and through Dapper, to
        // separate engine time from provider time. Arguments: <root> phase7-sql <out> <file.sql>.
        string sql = File.ReadAllText(args[3]);
        using var connection = factory.Open();
        for (int pass = 0; pass < 3; pass++)
        {
            var raw = new List<double>();
            var dapper = new List<double>();
            for (int run = 0; run < 15; run++)
            {
                long started = Stopwatch.GetTimestamp();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) { }
                }
                raw.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                started = Stopwatch.GetTimestamp();
                _ = connection.Query(sql).AsList();
                dapper.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            raw.Sort();
            dapper.Sort();
            Console.WriteLine($"PASS {pass} reader p50 {raw[7]:0.00} ms p95 {raw[14]:0.00}; dapper p50 {dapper[7]:0.00} p95 {dapper[14]:0.00}");
        }
        Console.WriteLine("SQLITE " + connection.ExecuteScalar<string>("SELECT sqlite_version()") + " cache " +
            connection.ExecuteScalar<long>("PRAGMA cache_size") + " mmap " + connection.ExecuteScalar<long>("PRAGMA mmap_size"));
        break;
    }
    case "phase7-filters":
    {
        // Every Phase 7 list, filter, count, ID resolution and bulk preview on the full store:
        // empty, broad, rare, no-match, Gujarati and high-cardinality shapes. The SQL trace
        // records each executed statement for EXPLAIN QUERY PLAN after the run.
        if (!Path.GetFileName(root).Contains("rehearsal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Phase 7 bulk batches may run only in a disposable rehearsal root.");
        DateOnly today = clock.BusinessDate;
        long busyProduct, largeCategory, rareCategory, busyVendor, largeBrand, busyExpenseCategory, lastPurchase;
        string commonWord, rareName, localWord, sku, barcode, documentNumber, invoicePrefix, purchaseProduct;
        long[] firstProducts, expiredProducts, newestPosted, busyLots, vendorIds;
        using (var connection = factory.Open())
        {
            busyProduct = connection.ExecuteScalar<long>(
                "SELECT product_id FROM product_lot GROUP BY product_id ORDER BY COUNT(*) DESC LIMIT 1");
            largeCategory = connection.ExecuteScalar<long>(
                "SELECT category_id FROM product GROUP BY category_id ORDER BY COUNT(*) DESC LIMIT 1");
            rareCategory = connection.ExecuteScalar<long>(
                "SELECT category_id FROM product GROUP BY category_id ORDER BY COUNT(*),category_id LIMIT 1");
            busyVendor = connection.ExecuteScalar<long>("""
                SELECT vendor_id FROM product_lot WHERE vendor_id IS NOT NULL
                GROUP BY vendor_id ORDER BY COUNT(*) DESC LIMIT 1
                """);
            largeBrand = connection.ExecuteScalar<long?>("""
                SELECT brand_id FROM product WHERE brand_id IS NOT NULL
                GROUP BY brand_id ORDER BY COUNT(*) DESC LIMIT 1
                """) ?? 0;
            busyExpenseCategory = connection.ExecuteScalar<long?>(
                "SELECT category_id FROM expense GROUP BY category_id ORDER BY COUNT(*) DESC LIMIT 1") ?? 0;
            commonWord = connection.ExecuteScalar<string>("""
                SELECT lower(substr(name,1,instr(name||' ',' ')-1)) AS word FROM product
                GROUP BY word HAVING length(word)>=3 ORDER BY COUNT(*) DESC LIMIT 1
                """) ?? "";
            rareName = connection.ExecuteScalar<string>("SELECT name FROM product ORDER BY id DESC LIMIT 1") ?? "";
            localWord = connection.ExecuteScalar<string>("""
                SELECT substr(name_local,1,instr(name_local||' ',' ')-1) FROM product
                WHERE name_local IS NOT NULL AND trim(name_local)<>'' ORDER BY id LIMIT 1
                """) ?? "";
            sku = connection.ExecuteScalar<string>("SELECT sku FROM product ORDER BY id DESC LIMIT 1") ?? "";
            barcode = connection.ExecuteScalar<string>(
                "SELECT barcode FROM product_barcode WHERE lot_id IS NULL ORDER BY id DESC LIMIT 1") ?? "";
            lastPurchase = connection.ExecuteScalar<long>("SELECT id FROM purchase ORDER BY id DESC LIMIT 1");
            documentNumber = connection.ExecuteScalar<string>(
                "SELECT doc_no FROM purchase WHERE doc_no IS NOT NULL ORDER BY id DESC LIMIT 1") ?? "";
            invoicePrefix = connection.ExecuteScalar<string>("""
                SELECT substr(vendor_invoice_no,1,4) FROM purchase
                WHERE vendor_invoice_no IS NOT NULL AND length(vendor_invoice_no)>=4 ORDER BY id DESC LIMIT 1
                """) ?? "";
            purchaseProduct = connection.ExecuteScalar<string>("""
                SELECT substr(COALESCE(product_name_snapshot,''),1,12) FROM purchase_line
                WHERE line_kind='PRODUCT' AND product_name_snapshot IS NOT NULL ORDER BY id DESC LIMIT 1
                """) ?? "";
            firstProducts = connection.Query<long>(
                "SELECT id FROM product WHERE is_active=1 ORDER BY name COLLATE NOCASE,id LIMIT 5000").ToArray();
            expiredProducts = connection.Query<long>("""
                SELECT DISTINCT product_id FROM product_lot
                WHERE qty_on_hand>0 AND expiry_date<@today ORDER BY product_id DESC LIMIT 5000
                """, new { today = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }).ToArray();
            newestPosted = connection.Query<long>(
                "SELECT id FROM purchase WHERE status='POSTED' ORDER BY received_date DESC,id DESC LIMIT 2000").ToArray();
            busyLots = connection.Query<long>(
                "SELECT id FROM product_lot WHERE product_id=@busyProduct ORDER BY id DESC LIMIT 5000", new { busyProduct }).ToArray();
            vendorIds = connection.Query<long>("SELECT id FROM vendor ORDER BY id").ToArray();
        }
        if (busyProduct <= 0 || commonWord.Length == 0 || sku.Length == 0 || firstProducts.Length == 0)
            throw new InvalidDataException("The Phase 7 fixture needs products, lots and purchases.");
        Console.WriteLine($"SHAPES common='{commonWord}' rare='{rareName}' local='{localWord}' sku={sku} barcode={barcode} " +
            $"category={largeCategory}/{rareCategory} vendor={busyVendor} product={busyProduct} lots={busyLots.Length}");

        // Runs a read once cold, then repeatedly warm; the first run is a separate measurement.
        async Task Profile(string name, Func<Task<int>> read, int runs = 20)
        {
            await Measure(name + "-first", read, static count => count);
            for (int run = 0; run < runs; run++)
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    int rows = await read();
                    profileSamples.Add(new(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, rows, null));
                }
                catch (Exception exception)
                {
                    profileSamples.Add(new(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0,
                        exception.GetType().Name + ": " + exception.Message));
                }
            }
        }
        async Task<int> Rejected(Func<Task> read)
        {
            try { await read(); return 0; }
            catch (GroceryErp.Core.Errors.DomainException) { return -1; }
        }
        ProductSearchFilter Products(string? query = null, int limit = 30) =>
            new(query, Limit: limit, ExpiryAsOf: today) { ExactFirst = true };

        // Products: the default browse page, its next page, every search shape and every filter.
        ProductSearchPage browse = await catalog.SearchPageAsync(Products());
        await Profile("products-browse", async () => (await catalog.SearchPageAsync(Products())).Items.Count);
        await Profile("products-browse-next", async () => (await catalog.SearchPageAsync(Products(),
            browse.NextName, browse.NextId)).Items.Count);
        await Profile("products-search-broad", async () => (await catalog.SearchPageAsync(Products(commonWord, 8))).Items.Count);
        await Profile("products-search-rare", async () => (await catalog.SearchPageAsync(Products(rareName, 8))).Items.Count);
        await Profile("products-search-nomatch", async () => (await catalog.SearchPageAsync(Products("zqxjv", 8))).Items.Count);
        await Profile("products-search-short", async () => (await catalog.SearchPageAsync(Products(commonWord[..2], 8))).Items.Count);
        await Profile("products-search-gujarati", async () => (await catalog.SearchPageAsync(Products(localWord, 8))).Items.Count);
        await Profile("products-search-sku", async () => (await catalog.SearchPageAsync(Products(sku, 8))).Items.Count);
        await Profile("products-search-barcode", async () => (await catalog.SearchPageAsync(Products(barcode, 8))).Items.Count);
        var productFilters = new (string Name, ProductSearchFilter Filter)[]
        {
            ("internal", Products() with { ProductClass = "INTERNAL" }),
            ("saleable", Products() with { ProductClass = "SALEABLE" }),
            ("archived", Products() with { ArchivedOnly = true }),
            ("any-status", Products() with { IncludeArchived = true }),
            ("category-large", Products() with { CategoryId = largeCategory }),
            ("category-rare", Products() with { CategoryId = rareCategory }),
            ("brand", Products() with { BrandId = largeBrand > 0 ? largeBrand : null }),
            ("has-stock", Products() with { HasStockOnly = true }),
            ("low-stock", Products() with { LowStockOnly = true }),
            ("expired", Products() with { ExpiryFilter = "EXPIRED" }),
            ("expiring", Products() with { ExpiryFilter = "EXPIRING" }),
            ("vendor", Products() with { VendorId = busyVendor }),
            ("vendor-category", Products() with { VendorId = busyVendor, CategoryId = largeCategory }),
            ("category-has-stock-expiring", Products() with { CategoryId = largeCategory, HasStockOnly = true, ExpiryFilter = "EXPIRING" }),
            ("no-barcode", Products() with { NoBarcodeOnly = true }),
            ("missing-local", Products() with { MissingLocalNameOnly = true }),
            ("loose", Products() with { LooseOnly = true }),
            ("search-broad-has-stock", Products(commonWord, 8) with { HasStockOnly = true }),
        };
        foreach ((string name, ProductSearchFilter filter) in productFilters)
            await Profile("products-" + name, async () => (await catalog.SearchPageAsync(filter)).Items.Count);
        await Profile("products-count-all", () => catalog.CountAsync(Products()), 10);
        await Profile("products-count-has-stock", () => catalog.CountAsync(Products() with { HasStockOnly = true }), 10);
        await Profile("products-count-category", () => catalog.CountAsync(Products() with { CategoryId = largeCategory }), 10);
        await Profile("products-count-search-broad", () => catalog.CountAsync(Products(commonWord)), 10);
        await Profile("products-resolve-category", async () =>
        {
            try { return (await catalog.ResolveIdsAsync(Products() with { CategoryId = largeCategory })).Count; }
            catch (GroceryErp.Core.Errors.DomainException) { return -1; }
        }, 10);
        await Profile("products-resolve-all-rejected", () => Rejected(() => catalog.ResolveIdsAsync(Products())), 5);
        await Profile("products-preview-category-5000", async () => (await catalog.PreviewBulkAsync(firstProducts,
            new CatalogBulkCommand(CatalogBulkAction.Category, largeCategory))).Rows.Count, 5);
        await Profile("products-preview-archive-5000", async () => (await catalog.PreviewBulkAsync(firstProducts,
            new CatalogBulkCommand(CatalogBulkAction.Archive))).Rows.Count, 5);
        await Profile("products-preview-expired-writeoff-200", async () =>
            (await moneyOut.PreviewExpiredWriteOffAsync(expiredProducts.Take(200).ToArray(), today)).Count, 5);
        await Profile("products-preview-expired-writeoff-over-cap", () =>
            Rejected(() => moneyOut.PreviewExpiredWriteOffAsync(expiredProducts, today)), 3);

        // Lots of the product with the most lots, under every lot filter.
        LotFilter Lots() => new() { ProductId = busyProduct, AsOf = today };
        var lotFilters = new (string Name, LotFilter Filter)[]
        {
            ("all", Lots()),
            ("available", Lots() with { Status = "AVAILABLE" }),
            ("zero", Lots() with { Status = "ZERO" }),
            ("blocked", Lots() with { Status = "BLOCKED" }),
            ("expired", Lots() with { Expiry = "EXPIRED" }),
            ("expiring", Lots() with { Expiry = "EXPIRING" }),
            ("no-expiry", Lots() with { Expiry = "NONE" }),
            ("source-purchase", Lots() with { Source = "PURCHASE_LINE" }),
            ("source-direct", Lots() with { Source = "DIRECT" }),
            ("expiry-range", Lots() with { ExpiryFrom = today, ExpiryTo = today.AddDays(365) }),
            ("price-range", Lots() with { MinPrice = new Money(1), MaxPrice = new Money(100) }),
            ("batch-search", Lots() with { Query = "B" }),
            ("batch-nomatch", Lots() with { Query = "zqxjv" }),
            ("all-products-expired", new LotFilter { Expiry = "EXPIRED", AsOf = today }),
        };
        foreach ((string name, LotFilter filter) in lotFilters)
            await Profile("lots-" + name, async () => (await inventory.GetLotPageAsync(filter, null, 30)).Items.Count);
        LotPage firstLots = await inventory.GetLotPageAsync(Lots(), null, 30);
        await Profile("lots-next", async () => (await inventory.GetLotPageAsync(Lots(), firstLots.NextId, 30)).Items.Count);
        await Profile("lots-count", () => inventory.CountLotsAsync(Lots()), 10);
        await Profile("lots-resolve", async () => (await inventory.ResolveLotIdsAsync(Lots())).Count, 10);
        await Profile("lots-preview-delete-zero", async () =>
            (await inventory.PreviewLotBulkAsync(busyLots, LotBulkAction.DeleteZero)).Count, 5);

        // Purchases and vendors.
        var purchaseFilters = new (string Name, PurchaseFilter Filter)[]
        {
            ("all", new PurchaseFilter()),
            ("vendor", new PurchaseFilter { VendorId = busyVendor }),
            ("posted", new PurchaseFilter { Status = "POSTED" }),
            ("void", new PurchaseFilter { Status = "CANCELLED" }),
            ("draft", new PurchaseFilter { Status = "DRAFT" }),
            ("type-expense", new PurchaseFilter { LineKind = "EXPENSE" }),
            ("type-internal", new PurchaseFilter { LineKind = "INTERNAL" }),
            ("document-exact", new PurchaseFilter { DocumentQuery = documentNumber }),
            ("invoice-prefix", new PurchaseFilter { DocumentQuery = invoicePrefix }),
            ("document-nomatch", new PurchaseFilter { DocumentQuery = "NO-SUCH-BILL" }),
            ("product", new PurchaseFilter { ProductQuery = purchaseProduct }),
            ("returnable", new PurchaseFilter { ReturnableOnly = true }),
            ("date-month", new PurchaseFilter { From = today.AddDays(-30), To = today }),
            ("vendor-posted-month", new PurchaseFilter { VendorId = busyVendor, Status = "POSTED", From = today.AddDays(-90), To = today }),
        };
        foreach ((string name, PurchaseFilter filter) in purchaseFilters)
            await Profile("purchases-" + name, async () => (await purchasing.GetPurchasePageAsync(filter, null, null, 30)).Items.Count);
        await Profile("purchases-count-posted", () => purchasing.CountPurchasesAsync(new PurchaseFilter { Status = "POSTED" }), 10);
        await Profile("purchases-count-vendor", () => purchasing.CountPurchasesAsync(new PurchaseFilter { VendorId = busyVendor }), 10);
        await Profile("purchases-resolve-vendor-month", async () => (await purchasing.ResolvePurchaseIdsAsync(
            new PurchaseFilter { VendorId = busyVendor, From = today.AddDays(-90), To = today })).Count, 10);
        await Profile("purchases-preview-void-2000", async () =>
            (await purchasing.PreviewVoidAsync(newestPosted)).Count, 3);
        await Profile("vendors-page", async () => (await purchasing.GetVendorPageAsync(new VendorFilter(), null, null, 30)).Items.Count);
        await Profile("vendors-search", async () => (await purchasing.GetVendorPageAsync(
            new VendorFilter { Query = "a", Status = "ALL" }, null, null, 30)).Items.Count);
        await Profile("vendors-balance", async () => (await purchasing.GetVendorPageAsync(
            new VendorFilter { WithBalanceOnly = true }, null, null, 30)).Items.Count);
        await Profile("vendors-count", () => purchasing.CountVendorsAsync(new VendorFilter { Status = "ALL" }), 10);
        await Profile("vendors-preview", async () => (await purchasing.PreviewVendorBulkAsync(vendorIds, false)).Count, 5);

        // Money out tabs.
        var history = new DateOnly(2021, 1, 1);
        await Profile("damages-quarter", async () => (await moneyOut.GetDamagePageAsync(
            new DamageFilter(today.AddMonths(-3), today), null, null, 30)).Items.Count);
        await Profile("damages-history", async () => (await moneyOut.GetDamagePageAsync(
            new DamageFilter(history, today), null, null, 30)).Items.Count);
        await Profile("damages-reason", async () => (await moneyOut.GetDamagePageAsync(
            new DamageFilter(history, today) { Reason = "EXPIRED" }, null, null, 30)).Items.Count);
        await Profile("damages-search", async () => (await moneyOut.GetDamagePageAsync(
            new DamageFilter(history, today) { Query = commonWord }, null, null, 30)).Items.Count);
        await Profile("damages-void", async () => (await moneyOut.GetDamagePageAsync(
            new DamageFilter(history, today) { Status = "VOID" }, null, null, 30)).Items.Count);
        await Profile("expenses-quarter", async () => (await moneyOut.GetExpensePageAsync(
            new ExpenseFilter(today.AddMonths(-3), today), null, null, 30)).Items.Count);
        await Profile("expenses-history-category", async () => (await moneyOut.GetExpensePageAsync(
            new ExpenseFilter(history, today) { CategoryId = busyExpenseCategory > 0 ? busyExpenseCategory : null }, null, null, 30)).Items.Count);
        await Profile("expenses-history-purchase", async () => (await moneyOut.GetExpensePageAsync(
            new ExpenseFilter(history, today) { Status = "PURCHASE" }, null, null, 30)).Items.Count);
        await Profile("expenses-search", async () => (await moneyOut.GetExpensePageAsync(
            new ExpenseFilter(history, today) { Query = "a" }, null, null, 30)).Items.Count);
        await Profile("employees-all", async () => (await moneyOut.GetEmployeesAsync(new EmployeeFilter { Status = "ALL" })).Count);
        await Profile("salary-register-year", async () => (await moneyOut.GetSalaryRegisterAsync(
            YearMonth.From(today).AddMonths(-11), YearMonth.From(today), null, null)).Count);

        // "reads" as the fourth argument stops here, for repeated read-only comparisons.
        if (args.Length > 3 && args[3].StartsWith("reads", StringComparison.Ordinal)) break;
        // Bounded bulk batches on this disposable copy at the UI's batch sizes: each holds the
        // single writer, so its duration is what a cashier's bill can wait behind.
        string root7 = OperationGuard.NewId();
        int batchNumber = 0;
        long[] taxTargets = firstProducts.Take(2000).ToArray();
        long originalTax, otherTax;
        using (var connection = factory.Open())
        {
            originalTax = connection.ExecuteScalar<long>("SELECT tax_rate_id FROM product WHERE id=@id", new { id = taxTargets[0] });
            otherTax = connection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE id<>@originalTax ORDER BY id LIMIT 1", new { originalTax });
        }
        async Task<int> TaxBatch(long[] ids, long tax) => (await catalog.RunBulkBatchAsync(
            new CatalogBulkCommand(CatalogBulkAction.Tax, tax), ids, BulkBatch.OperationId(root7, ++batchNumber)))
            .Count(static result => result.Outcome == BulkItemOutcome.Changed);
        async Task<int> LotBatch(long[] ids, LotBulkAction action) => (await inventory.RunLotBulkBatchAsync(
            action, ids, BulkBatch.OperationId(root7, ++batchNumber))).Count(static result => result.Outcome == BulkItemOutcome.Changed);
        long[] firstTax = taxTargets.Take(50).ToArray();
        await Measure("bulk-tax-batch-50", () => TaxBatch(firstTax, otherTax), static rows => rows);
        await Measure("bulk-tax-batch-50-replay", () => catalog.RunBulkBatchAsync(new CatalogBulkCommand(CatalogBulkAction.Tax, otherTax),
            firstTax, BulkBatch.OperationId(root7, batchNumber)), static results => results.Count);
        await Measure("bulk-tax-restore-50", () => TaxBatch(firstTax, originalTax), static rows => rows);
        long[] blockTargets = busyLots.Take(50).ToArray();
        await Measure("bulk-lot-block-50", () => LotBatch(blockTargets, LotBulkAction.Block), static rows => rows);
        await Measure("bulk-lot-unblock-50", () => LotBatch(blockTargets, LotBulkAction.Unblock), static rows => rows);
        IReadOnlyList<ExpiredWriteOffLot> expiredLots = await moneyOut.PreviewExpiredWriteOffAsync(expiredProducts.Take(200).ToArray(), today);
        long[] writeOff = expiredLots.Take(50).Select(static lot => lot.LotId).ToArray();
        if (writeOff.Length > 0)
            await Measure("bulk-writeoff-batch-50", async () => (await moneyOut.WriteOffExpiredBatchAsync(writeOff, today,
                BulkBatch.OperationId(root7, ++batchNumber))).Count(static result => result.Outcome == BulkItemOutcome.Changed),
                static rows => rows);
        IReadOnlyList<PurchasingBulkRow> voidable = await purchasing.PreviewVoidAsync(newestPosted);
        long[] voidTargets = voidable.Where(static row => row.Eligible).Select(static row => row.Id).ToArray();
        Console.WriteLine($"VOIDABLE {voidTargets.Length} of {voidable.Count}");
        if (voidTargets.Length >= 5)
            await Measure("bulk-void-batch-5", async () => (await purchasing.RunVoidBatchAsync(voidTargets[..5], today,
                BulkBatch.OperationId(root7, ++batchNumber))).Count(static result => result.Outcome == BulkItemOutcome.Changed),
                static rows => rows);

        // Cashier bills alone, then with bulk batches committing back to back: the bill commit is
        // the gate (p95 under 100 ms), so a batch must stay short enough to wait behind.
        var cashierSales = new SalesService(database, ledger, numbers, periods, settings, audit, clock);
        string suffix = Guid.NewGuid().ToString("N");
        long cashierProduct = await database.WriteAsync(tx =>
        {
            long category = tx.Connection.ExecuteScalar<long>("SELECT id FROM category LIMIT 1", transaction: tx.Transaction);
            long uom = tx.Connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'", transaction: tx.Transaction);
            long id = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id)
                VALUES (@sku,'Phase 7 cashier product',@category,@uom) RETURNING id
                """, new { sku = "PH7-C-" + suffix, category, uom }, tx.Transaction);
            long lotId = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,unit_cost,selling_price,qty_received)
                VALUES (@id,@code,'CASHIER','2026-09-01','2027-09-01',1000,2000,1000000) RETURNING id
                """, new { id, code = "PH7-C-L-" + suffix }, tx.Transaction);
            ledger.Change(tx, new(clock.BusinessDate, id, lotId,
                GroceryErp.Inventory.Contracts.LotChangeKind.DirectEntry, new Quantity(1000)));
            return id;
        });
        async Task Bills(string name, int count)
        {
            for (int index = 0; index < count; index++)
            {
                long saleId = await cashierSales.AddProductAsync(null, cashierProduct, Quantity.One);
                long started = Stopwatch.GetTimestamp();
                await cashierSales.CompleteAsync(new(saleId, [new(PaymentMode.Cash, new Money(2))], OperationGuard.NewId()));
                profileSamples.Add(new(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1, null));
            }
        }
        await Bills("cashier-bill-commit-alone", 100);
        using var bulkStop = new CancellationTokenSource();
        Task bulkLoad = Task.Run(async () =>
        {
            int round = 0;
            while (!bulkStop.IsCancellationRequested)
            {
                long[] batch = taxTargets.Skip(round % 40 * 50).Take(50).ToArray();
                long started = Stopwatch.GetTimestamp();
                await TaxBatch(batch, round % 2 == 0 ? otherTax : originalTax);
                profileSamples.Add(new("bulk-batch-during-billing", Stopwatch.GetElapsedTime(started).TotalMilliseconds, batch.Length, null));
                started = Stopwatch.GetTimestamp();
                await LotBatch(busyLots.Skip(round % 20 * 50).Take(50).ToArray(), round % 2 == 0 ? LotBulkAction.Block : LotBulkAction.Unblock);
                profileSamples.Add(new("bulk-batch-during-billing", Stopwatch.GetElapsedTime(started).TotalMilliseconds, 50, null));
                round++;
            }
        });
        try { await Bills("cashier-bill-commit-with-bulk", 100); }
        finally
        {
            bulkStop.Cancel();
            await bulkLoad;
        }
        // Leave the lots and tax rates as found: unblock every busy lot and restore the tax rate.
        foreach (long[] chunk in busyLots.Chunk(50)) await LotBatch(chunk, LotBulkAction.Unblock);
        foreach (long[] chunk in taxTargets.Chunk(50)) await TaxBatch(chunk, originalTax);
        break;
    }
    case "phase5-return-reconcile":
    {
        if (!Path.GetFileName(root).Contains("rehearsal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Returns may be seeded only in a disposable rehearsal root.");
        using (var check = factory.Open())
            if (check.ExecuteScalar<long>("PRAGMA user_version") != MigrationRunner.LatestVersion)
                throw new InvalidDataException("Migrate the disposable rehearsal copy first.");
        var returnClock = new AuditClock(new DateTime(2026, 10, 18, 12, 0, 0));
        var returnSales = new SalesService(database, ledger, numbers, periods, settings, audit, returnClock);
        long twoLotProduct;
        long otherCategory;
        using (var connection = factory.Open())
        {
            twoLotProduct = connection.ExecuteScalar<long>("""
                SELECT product_id FROM product_lot WHERE lot_code LIKE 'PH5-C-L-%' AND qty_on_hand>0
                GROUP BY product_id HAVING COUNT(*)>=3 ORDER BY product_id DESC LIMIT 1
                """);
            otherCategory = connection.ExecuteScalar<long>("""
                SELECT id FROM category WHERE id<>(SELECT category_id FROM product WHERE id=@twoLotProduct)
                ORDER BY id LIMIT 1
                """, new { twoLotProduct });
        }
        long partialSale = await returnSales.AddProductAsync(null, twoLotProduct, new Quantity(3));
        await returnSales.CompleteAsync(new(partialSale,
            [new(PaymentMode.Cash, new Money(6))], OperationGuard.NewId()));
        SaleDetails partialOriginal = await returnSales.GetAsync(partialSale);
        // A category edit between the sale and its returns must not split the
        // category projection; the last line comes back damaged.
        await database.WriteAsync(tx => tx.Connection.Execute(
            "UPDATE product SET category_id=@otherCategory WHERE id=@twoLotProduct",
            new { otherCategory, twoLotProduct }, tx.Transaction));
        for (int index = 0; index < partialOriginal.Lines.Count; index++)
        {
            SaleLine line = partialOriginal.Lines[index];
            await returnSales.ReturnAsync(new(partialSale, returnClock.BusinessDate, "Full-size partial refund",
                RefundMode.Cash, [new ReturnLineInput(line.Id, line.Quantity,
                    index < partialOriginal.Lines.Count - 1)],
                OperationGuard.NewId()));
        }
        if ((await returnSales.GetAsync(partialSale)).Status != SaleStatus.Refunded)
            throw new InvalidDataException("Repeated partial returns did not fully refund the sale.");

        string suffix = Guid.NewGuid().ToString("N");
        (long Product, long Lot) deleted = await database.WriteAsync(tx =>
        {
            long category = tx.Connection.ExecuteScalar<long>("SELECT id FROM category LIMIT 1",
                transaction: tx.Transaction);
            long unit = tx.Connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'",
                transaction: tx.Transaction);
            long tax = tx.Connection.ExecuteScalar<long>("SELECT id FROM tax_rate ORDER BY id LIMIT 1",
                transaction: tx.Transaction);
            long product = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,tax_rate_id,hsn_code,
                    price_includes_tax,track_expiry,is_loose,default_selling_price)
                VALUES (@sku,'Phase 5 deleted product',@category,@unit,@tax,'1006',1,0,0,2000)
                RETURNING id
                """, new { sku = "PH5-R-" + suffix, category, unit, tax }, tx.Transaction);
            long lot = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,
                  selling_price,qty_received)
                VALUES (@product,@code,'2026-09-01',1000,2000,1000) RETURNING id
                """, new { product, code = "PH5-R-L-" + suffix }, tx.Transaction);
            ledger.Change(tx, new(returnClock.BusinessDate, product, lot,
                GroceryErp.Inventory.Contracts.LotChangeKind.DirectEntry, Quantity.One));
            return (product, lot);
        });
        long deletedSale = await returnSales.AddProductAsync(null, deleted.Product, Quantity.One);
        await returnSales.CompleteAsync(new(deletedSale,
            [new(PaymentMode.Cash, new Money(2))], OperationGuard.NewId()));
        SaleDetails deletedOriginal = await returnSales.GetAsync(deletedSale);
        long detailVersion;
        using (var connection = factory.Open())
            detailVersion = connection.ExecuteScalar<long>(
                "SELECT detail_version FROM product_lot WHERE id=@id", new { id = deleted.Lot });
        await inventory.DeleteZeroLotAsync(deleted.Lot, detailVersion);
        if (await catalog.RemoveAsync(deleted.Product) != ProductRemoval.Deleted)
            throw new InvalidDataException("The zero-stock historical product was not deleted.");
        CompletedReturn recreated = await returnSales.ReturnAsync(new(deletedSale, returnClock.BusinessDate,
            "Return after deletion", RefundMode.Cash,
            [new ReturnLineInput(deletedOriginal.Lines.Single().Id, Quantity.One, true)],
            OperationGuard.NewId()));
        using (var connection = factory.Open())
        {
            long restored = connection.ExecuteScalar<long>("""
                SELECT l.qty_on_hand FROM sale_return_line rl
                JOIN product_lot l ON l.id=rl.restock_lot_id
                WHERE rl.sale_return_id=@id AND rl.restock_product_id_snapshot<>@oldProduct
                """, new { id = recreated.ReturnId, oldProduct = deleted.Product });
            if (restored != Quantity.One.Scaled ||
                LotSourceReconciliation.CheckLot(connection, null, connection.ExecuteScalar<long>("""
                    SELECT restock_lot_id FROM sale_return_line WHERE sale_return_id=@id
                    """, new { id = recreated.ReturnId })) is not null)
                throw new InvalidDataException("Deleted-master return did not recreate direct stock cleanly.");
        }
        phaseTwoChecks = new Dictionary<string, long>
        {
            ["partialSale"] = partialSale,
            ["deletedSale"] = deletedSale,
            ["recreatedReturn"] = recreated.ReturnId,
        };
        break;
    }
    case "phase5-sales-read":
    {
        string invoice;
        string product;
        using (var connection = factory.Open())
        {
            invoice = connection.ExecuteScalar<string>(
                "SELECT invoice_no FROM sale WHERE invoice_no IS NOT NULL ORDER BY id DESC LIMIT 1") ?? "";
            product = connection.ExecuteScalar<string>(
                "SELECT product_name FROM sale_line ORDER BY id DESC LIMIT 1") ?? "";
        }
        if (invoice.Length == 0 || product.Length == 0)
            throw new InvalidDataException("The sale history fixture needs completed lines.");
        await Measure("sales-first-page", () => sales.GetHistoryAsync(new SaleHistoryFilter(Limit: 50)),
            static rows => rows.Count);
        await Measure("sales-product-snapshot", () => sales.GetHistoryAsync(new SaleHistoryFilter(
            Product: product[..Math.Min(16, product.Length)], Limit: 50)), static rows => rows.Count);
        await Measure("sales-invoice-exact", () => sales.GetHistoryAsync(new SaleHistoryFilter(
            InvoiceNumber: invoice, Limit: 50)), static rows => rows.Count);
        await Measure("sales-partial-status", () => sales.GetHistoryAsync(new SaleHistoryFilter(
            Status: SaleStatus.PartiallyRefunded, Limit: 50)), static rows => rows.Count);
        await Measure("sales-refunded-status", () => sales.GetHistoryAsync(new SaleHistoryFilter(
            Status: SaleStatus.Refunded, Limit: 50)), static rows => rows.Count);
        await Measure("refund-progress-reconciliation", () => Task.Run(() =>
        {
            using var connection = factory.Open();
            long drift = connection.ExecuteScalar<long>("""
                WITH expected AS (
                  SELECT sl.sale_id AS sale_id,SUM(rl.qty) AS returned_qty,
                         SUM(rl.refund_amount) AS refunded_total
                  FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
                  GROUP BY sl.sale_id
                )
                SELECT (SELECT COUNT(*) FROM expected e LEFT JOIN sale_refund_progress rp
                  ON rp.sale_id=e.sale_id WHERE rp.sale_id IS NULL OR
                  rp.returned_qty<>e.returned_qty OR rp.refunded_total<>e.refunded_total OR
                  rp.sold_qty<>(SELECT SUM(qty) FROM sale_line WHERE sale_id=e.sale_id))
                +(SELECT COUNT(*) FROM sale_refund_progress rp LEFT JOIN expected e
                  ON e.sale_id=rp.sale_id WHERE e.sale_id IS NULL)
                """);
            if (drift != 0) throw new InvalidDataException($"{drift} sale refund progress rows differ from source documents.");
            return checked((int)drift);
        }), static rows => rows);
        await Measure("refund-day-projections", () => Task.Run(() =>
        {
            using var connection = factory.Open();
            long hourlyDrift = connection.ExecuteScalar<long>("""
                WITH expected AS (
                  SELECT return_date AS day,strftime('%w',created_at) AS weekday,
                         CAST(strftime('%H',created_at) AS INTEGER) AS hour,
                         COUNT(*) AS return_count,SUM(refund_total) AS refund_total
                  FROM sale_return WHERE refund_total<>0
                  GROUP BY return_date,strftime('%w',created_at),strftime('%H',created_at)
                )
                SELECT (SELECT COUNT(*) FROM expected e LEFT JOIN sale_hour_return_day d
                  ON d.day=e.day AND d.weekday=e.weekday AND d.hour=e.hour
                  WHERE d.day IS NULL OR d.return_count<>e.return_count OR d.refund_total<>e.refund_total)
                +(SELECT COUNT(*) FROM sale_hour_return_day d LEFT JOIN expected e
                  ON e.day=d.day AND e.weekday=d.weekday AND e.hour=d.hour WHERE e.day IS NULL)
                """);
            long productDrift = connection.ExecuteScalar<long>("""
                WITH expected AS (
                  SELECT sl.product_id,r.return_date AS day,SUM(rl.qty) AS qty
                  FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
                  JOIN sale_line sl ON sl.id=rl.sale_line_id
                  GROUP BY sl.product_id,r.return_date
                )
                SELECT (SELECT COUNT(*) FROM expected e LEFT JOIN sale_product_return_day d
                  ON d.product_id=e.product_id AND d.day=e.day
                  WHERE d.product_id IS NULL OR d.returned_qty<>e.qty)
                +(SELECT COUNT(*) FROM sale_product_return_day d LEFT JOIN expected e
                  ON e.product_id=d.product_id AND e.day=d.day WHERE e.product_id IS NULL)
                """);
            if (hourlyDrift+productDrift != 0)
                throw new InvalidDataException($"Refund day projection drift: hour={hourlyDrift}, product={productDrift}.");
            return 0;
        }), static rows => rows);
        break;
    }
    case "phase3-purchase-read":
    {
        long vendorId;
        string productTerm;
        using (var connection = factory.Open())
        {
            vendorId = connection.ExecuteScalar<long>(
                "SELECT vendor_id FROM purchase WHERE status='POSTED' ORDER BY id DESC LIMIT 1");
            productTerm = connection.ExecuteScalar<string>("""
                SELECT substr(COALESCE(product_name_snapshot,''),1,12) FROM purchase_line
                WHERE line_kind='PRODUCT' AND product_name_snapshot IS NOT NULL
                ORDER BY id DESC LIMIT 1
                """) ?? "";
        }
        if (vendorId <= 0 || productTerm.Length == 0)
            throw new InvalidDataException("The purchasing read fixture needs a posted purchase and product snapshot.");
        await Measure("purchase-first-page", () => purchasing.GetPurchasesPageAsync(limit: 50),
            static page => page.Items.Count);
        await Measure("purchase-vendor-status-type", () => purchasing.GetPurchasesPageAsync(
            vendorId: vendorId, status: "POSTED", lineKind: "PRODUCT", limit: 50),
            static page => page.Items.Count);
        await Measure("purchase-product-search", () => purchasing.GetPurchasesPageAsync(
            productQuery: productTerm, limit: 50), static page => page.Items.Count);
        await Measure("vendor-ledger-first-page", () => purchasing.GetVendorLedgerPageAsync(vendorId,
            limit: 50), static page => page.Items.Count);
        break;
    }
    case "phase3-read":
    {
        using (var connection = factory.Open())
            connection.Execute("""
                INSERT INTO setting(key,value) VALUES ('maintenance.clean_shutdown','1')
                ON CONFLICT(key) DO UPDATE SET value='1'
                """);
        await Measure("startup-clean", () => maintenance.CheckBeforeShellAsync(), static _ => 1);
        using (var connection = factory.Open())
            connection.Execute("UPDATE setting SET value='0' WHERE key='maintenance.clean_shutdown'");
        await Measure("startup-interrupted", () => maintenance.CheckBeforeShellAsync(), static _ => 1);
        using (var connection = factory.Open())
        {
            long vendorDrift = connection.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM vendor v JOIN vendor_balance_total t ON t.vendor_id=v.id
                WHERE t.purchases<>COALESCE((SELECT SUM(grand_total) FROM purchase p
                    WHERE p.vendor_id=v.id AND p.status='POSTED'),0)
                   OR t.payments<>COALESCE((SELECT SUM(amount) FROM vendor_payment vp
                    WHERE vp.vendor_id=v.id),0)
                   OR t.returns<>COALESCE((SELECT SUM(credit_total) FROM vendor_return vr
                    WHERE vr.vendor_id=v.id),0)
                """);
            if (vendorDrift != 0) throw new InvalidDataException($"{vendorDrift} vendor balances differ from source documents.");
            Console.WriteLine("VENDOR_BALANCE_DRIFT 0");
        }
        for (int iteration = 0; iteration < 5; iteration++)
        {
            await Measure("expiry-badge-source", () => Task.Run(() =>
            {
                using var connection = factory.Open();
                return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM v_expiry_alert WHERE bucket IN ('EXPIRED','CRITICAL')");
            }), static x => x);
            await Measure("expiry-badge-indexed", () => moneyOut.GetActionableExpiryCountAsync(), static x => x);
            await Measure("vendors-projected", () => purchasing.GetVendorsAsync(includeInactive: true), static x => x.Count);
            await Measure("dashboard-snapshot", () => reports.GetDashboardAsync(), static _ => 1);
        }
        using (var connection = factory.Open())
        {
            int sourceExpiry = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM v_expiry_alert WHERE bucket IN ('EXPIRED','CRITICAL')");
            int indexedExpiry = await moneyOut.GetActionableExpiryCountAsync();
            if (sourceExpiry != indexedExpiry)
                throw new InvalidDataException($"Expiry badge differs: source {sourceExpiry}, indexed {indexedExpiry}.");
            Console.WriteLine("EXPIRY_BADGE_DRIFT 0");
        }
        foreach (ReportKind kind in new[] { ReportKind.ItemSales, ReportKind.CategorySales,
                     ReportKind.GstSummary, ReportKind.VendorPayables, ReportKind.SalaryRegister,
                     ReportKind.AssetSchedule })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Measure("phase3-report-" + kind, () => reports.GetReportAsync(kind,
                new ReportFilter(From: monthStart, To: end, AssetId: 1, Limit: 100), timeout.Token),
                static x => x.Rows.Count);
        }
        DateOnly priorTo = monthStart.AddDays(-1);
        DateOnly priorFrom = priorTo.AddDays(monthStart.DayNumber - end.DayNumber);
        foreach (ReportKind kind in new[] { ReportKind.ItemSales, ReportKind.CategorySales })
        {
            async Task<int> SourceRows() => await Task.Run(() =>
            {
                using var connection = factory.Open();
                return connection.Query(ReportDefinitions.All[kind].Sql, new
                {
                    from = monthStart.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    to = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    previousFrom = priorFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    previousTo = priorTo.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    ProductId = (long?)null, Limit = 101,
                }).Count();
            });
            for (int repeat = 0; repeat < 3; repeat++)
            {
                await Measure("phase3-source-" + kind, SourceRows, static x => x);
                await Measure("phase3-window-" + kind, () => reports.GetReportAsync(kind,
                    new ReportFilter(From: monthStart, To: end, Limit: 100)), static x => x.Rows.Count);
            }
        }
        break;
    }
    case "optimized-read":
        await Measure("startup-health", () => maintenance.CheckBeforeShellAsync(), static _ => 1);
        await Measure("catalog-first-page", () => catalog.SearchPageAsync(new ProductSearchFilter(Limit: 100)), static x => x.Items.Count);
        await Measure("catalog-low-stock-page", () => catalog.SearchPageAsync(new ProductSearchFilter(LowStockOnly: true, Limit: 100)), static x => x.Items.Count);
        await Measure("lots-first-page", () => inventory.GetLotPageAsync(), static x => x.Items.Count);
        await Measure("expiry-badge-count", () => moneyOut.GetActionableExpiryCountAsync(), static x => x);
        await Measure("purchases-first-page", () => purchasing.GetPurchasesPageAsync(), static x => x.Items.Count);
        await Measure("saved-bills-first-page", () => sales.GetSavedBillPageAsync(includeDrafts: true), static x => x.Items.Count);
        await Measure("assets-first-page", () => assets.GetAssetPageAsync(), static x => x.Items.Count);
        await Measure("pnl-month", () => reports.GetPnlAsync(monthStart, end), static x => x.Income.Count + x.CostOfGoodsSold.Count + x.Expenses.Count);
        await Measure("pnl-fiscal", () => reports.GetPnlAsync(fiscalStart, end), static x => x.Income.Count + x.CostOfGoodsSold.Count + x.Expenses.Count);
        await Measure("salary-matrix-100", () => reports.GetReportAsync(ReportKind.SalaryRegister,
            new ReportFilter(From: new DateOnly(2025, 10, 1), To: end, Limit: 100)), static x => x.Rows.Count);
        await Measure("dashboard", () => reports.GetDashboardAsync(), static _ => 1);
        await Measure("stock-projection-mismatches", () => Task.Run(() =>
        {
            using var connection = factory.Open();
            long mismatches = connection.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM product p LEFT JOIN product_stock_total t ON t.product_id=p.id
                WHERE COALESCE(t.quantity,0)<>(SELECT COALESCE(SUM(l.qty_on_hand),0) FROM product_lot l WHERE l.product_id=p.id)
                   OR COALESCE(t.cost_value,0)<>(SELECT COALESCE(SUM(CAST(ROUND(l.qty_on_hand*l.unit_cost/1000.0) AS INTEGER)),0)
                                                  FROM product_lot l WHERE l.product_id=p.id)
                """);
            if (mismatches != 0) throw new InvalidDataException($"Stock projection has {mismatches} mismatches.");
            return (int)mismatches;
        }), static x => x);
        CapturePlan("saved-bills", "SELECT id FROM sale WHERE status IN ('HELD','DRAFT') AND (status='HELD' OR (status='DRAFT' AND line_count>0)) ORDER BY created_at DESC,id DESC LIMIT 50");
        CapturePlan("purchase-page", "SELECT id FROM purchase ORDER BY received_date DESC,id DESC LIMIT 100");
        CapturePlan("expiry-page", "SELECT lot_id FROM v_expiry_alert WHERE bucket='EXPIRED' ORDER BY expiry_date,lot_id LIMIT 100");
        break;
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
    case "phase4-opening-import":
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
        var openingImporter = new OpeningStockExcelImporter(database, inventory, clock);
        OpeningStockImportPreview? openingPreview = null;
        await Measure("opening-preview-valid-20000", async () =>
            openingPreview = await openingImporter.DryRunAsync(previewPath), static x => x!.ValidCount);
        if (mode == "phase4-opening-import")
        {
            if (openingPreview?.ValidCount != 20000)
                throw new InvalidDataException("The 20,000 opening-stock rows did not validate.");
            string importId = OperationGuard.NewId();
            Task importTask = Measure("opening-import-commit-20000", () =>
                openingImporter.ImportAsync(openingPreview, importId), static x => (int)x);
            await Task.Delay(100);
            long productId;
            using (var connection = factory.Open())
                productId = connection.ExecuteScalar<long>("""
                    SELECT product_id FROM product_lot WHERE qty_on_hand>1000 AND is_active=1
                    AND expiry_date>='2026-09-26' ORDER BY qty_on_hand DESC LIMIT 1
                    """);
            await Measure("sale-add-during-opening-import", () =>
                sales.AddProductAsync(null, productId, Quantity.One), static _ => 1);
            await importTask;
            long replay = await openingImporter.ImportAsync(openingPreview, importId);
            using var verify = factory.Open();
            long firstLot = verify.ExecuteScalar<long>("SELECT result_id FROM import_job WHERE op_id=@importId", new { importId });
            long batches = verify.ExecuteScalar<long>("SELECT COUNT(*) FROM import_batch WHERE op_id=@importId", new { importId });
            if (replay != firstLot || batches != 200)
                throw new InvalidDataException($"Opening import replay or batch count failed: {replay}, {firstLot}, {batches}.");
        }
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
    case "phase5-label-failure":
    {
        var journal = new PrintJobJournal(factory);
        var printer = new ReceiptPrinter(settings, journal);
        TsplLabel[] labels = Enumerable.Range(1, 50_000)
            .Select(static number => new TsplLabel("Scale Item", null, $"SCALE{number:00000}", 99m, null))
            .ToArray();
        int before = journal.ReviewCount();
        await Measure("label-50000-offline-journal", async () =>
        {
            try
            {
                await printer.PrintLabelsAsync("missing-phase5-printer", labels, useDriver: false);
                throw new InvalidDataException("The missing label printer unexpectedly accepted the job.");
            }
            catch (System.ComponentModel.Win32Exception) { }
            int review = journal.ReviewCount() - before;
            if (review != 2_000)
                throw new InvalidDataException($"Expected 2,000 reviewable chunks, found {review}.");
            using var connection = factory.Open();
            long largestPayload = connection.ExecuteScalar<long>(
                "SELECT MAX(length(payload)) FROM print_job WHERE state = 'REVIEW'");
            if (largestPayload > 256 * 1024)
                throw new InvalidDataException("A saved label chunk exceeded the journal limit.");
            return review;
        }, static x => x);
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
    case "customer-seed":
    {
        await Measure("seed-50000-customers", () => Task.Run(() =>
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            long existing = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM customer WHERE name LIKE 'Scale Customer %'", transaction: transaction);
            if (existing != 0)
                throw new InvalidOperationException("Customer fixture is already seeded; use a fresh disposable copy.");
            connection.Execute("""
                WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<50000)
                INSERT INTO customer(name,phone)
                SELECT 'Scale Customer ' || printf('%05d',x), printf('9%09d',x) FROM seq;
                """, transaction: transaction);
            transaction.Commit();
            return 50000;
        }), static x => x);
        if (results[^1].Error is not null)
            throw new InvalidOperationException(results[^1].Error);
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
    case "phase6-annual":
    {
        // First launch after the Phase 6 upgrade: finalize every locked year, then read
        // open and finalized P&L, export a finalized year twice and run the checks that
        // background maintenance runs. The business date is the fourth argument.
        DateOnly businessDate = args.Length > 3
            ? DateOnly.ParseExact(args[3], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : new DateOnly(2026, 10, 23);
        var annualClock = new AuditClock(businessDate.ToDateTime(new TimeOnly(12, 0)));
        var annualReports = new ReportService(database, settings, annualClock);
        var closes = new AnnualCloseService(database, new PeriodGuard(settings, annualClock), annualClock);
        int fiscalStartMonth = settings.Current.FyStartMonth;
        DateOnly? firstEntry;
        using (var connection = factory.Open())
        {
            firstEntry = PnlSource.FirstEntryDate(connection);
            Console.WriteLine($"PNL_FIRST_ENTRY {firstEntry?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}");
            foreach (AnnualPeriod year in AnnualCloseService.Missing(connection, null, businessDate, fiscalStartMonth))
                Console.WriteLine($"MISSING {year.Code} {year.Label}");
        }
        AnnualPeriod currentFiscal = AnnualPeriod.Fiscal(businessDate, fiscalStartMonth);
        CapturePlan("pnl-source-fiscal-year", """
            SELECT substr(entry_date,1,7) AS Month,section,head_code,category_id,SUM(amount),SUM(source_count)
            FROM v_pnl_entry WHERE entry_date BETWEEN @from AND @to
            GROUP BY substr(entry_date,1,7),section,head_code,category_id
            """, new { from = currentFiscal.Start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                to = currentFiscal.End.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        await Measure("annual-close-first-launch", () => closes.EnsureFinalizedAsync(), static count => count);
        await Measure("annual-close-repeat", () => closes.EnsureFinalizedAsync(), static count => count);
        IReadOnlyList<AnnualCloseInfo> finalized = await closes.GetClosesAsync();
        foreach (AnnualCloseInfo close in finalized)
            Console.WriteLine($"FINALIZED {close.Period.Code} {close.Period.Label} lines={close.LineCount} net={close.NetProfit} checksum={close.Checksum}");
        var annualPeriods = new List<(string Name, PnlPeriod Period)>
        {
            ("month-open", annualReports.MonthPeriod(YearMonth.From(businessDate))),
            ("calendar-open", annualReports.CalendarYearPeriod(businessDate.Year)),
            ("fiscal-open", annualReports.FiscalYearPeriod(currentFiscal.Start.Year)),
            ("custom-all-history", ReportService.CustomPeriod(firstEntry ?? businessDate, businessDate)),
        };
        foreach (AnnualCloseInfo close in finalized)
            annualPeriods.Add(($"{close.Period.Code.ToLowerInvariant()}-{close.Period.Label}-finalized",
                close.Period.Kind == AnnualPeriodKind.Calendar
                    ? annualReports.CalendarYearPeriod(close.Period.Start.Year)
                    : annualReports.FiscalYearPeriod(close.Period.Start.Year)));
        if (finalized.Count > 0)
            annualPeriods.Add(("month-finalized", annualReports.MonthPeriod(YearMonth.From(finalized[^1].Period.Start))));
        foreach ((string name, PnlPeriod period) in annualPeriods)
            for (int repeat = 0; repeat < 3; repeat++)
                await Measure("pnl-" + name, () => annualReports.GetPnlReportAsync(period), static report => report.Lines.Count);
        if (finalized.Count > 0)
        {
            AnnualCloseInfo year = finalized[^1];
            PnlPeriod period = year.Period.Kind == AnnualPeriodKind.Calendar
                ? annualReports.CalendarYearPeriod(year.Period.Start.Year)
                : annualReports.FiscalYearPeriod(year.Period.Start.Year);
            string first = Path.Combine(paths.TempDir, "phase6-finalized-1.csv");
            string second = Path.Combine(paths.TempDir, "phase6-finalized-2.csv");
            Directory.CreateDirectory(paths.TempDir);
            await Measure("pnl-finalized-export-stable", async () =>
            {
                await annualReports.ExportPnlCsvAsync(await annualReports.GetPnlReportAsync(period), true, first);
                await annualReports.ExportPnlCsvAsync(await annualReports.GetPnlReportAsync(period), true, second);
                if (!File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second)))
                    throw new InvalidDataException("Two exports of the finalized year differ.");
                return checked((int)new FileInfo(first).Length);
            }, static bytes => bytes);
        }
        await Measure("pnl-open-day-reconciliation", () => database.ReadAsync(connection =>
        {
            List<string> drift = PnlReconciliation.CheckOpenDays(connection, businessDate).ToList();
            if (drift.Count > 0) throw new InvalidDataException($"P&L days differ from sources: {string.Join(',', drift.Take(5))}");
            return drift.Count;
        }), static rows => rows);
        await Measure("annual-snapshot-verify", () => database.ReadAsync(connection =>
        {
            IReadOnlyList<string> failed = AnnualCloseService.VerifyAll(connection);
            if (failed.Count > 0) throw new InvalidDataException("Finalized years fail their checksum: " + string.Join(',', failed));
            return AnnualCloseService.Closes(connection, null).Count;
        }), static rows => rows);
        await Measure("stock-value-excluding-expired", () => annualReports.GetReportAsync(ReportKind.StockValue,
            new ReportFilter(IncludeExpired: false, Limit: 100)), static report => report.Rows.Count);
        await Measure("stock-value-including-expired", () => annualReports.GetReportAsync(ReportKind.StockValue,
            new ReportFilter(IncludeExpired: true, Limit: 100)), static report => report.Rows.Count);
        await Measure("stock-reconciliation-first-page", () => annualReports.GetReportAsync(ReportKind.StockReconciliation,
            new ReportFilter(Limit: 100)), static report => report.Rows.Count);
        long busiest;
        using (var connection = factory.Open())
            busiest = connection.ExecuteScalar<long>(
                "SELECT product_id FROM product_lot GROUP BY product_id ORDER BY COUNT(*) DESC LIMIT 1");
        await Measure("stock-reconciliation-busiest-product", () => annualReports.GetReportAsync(ReportKind.StockReconciliation,
            new ReportFilter(ProductId: busiest, Limit: 100)), static report => report.Rows.Count);
        await Measure("stock-reconciliation-recorded-differences", () => annualReports.GetReportAsync(
            ReportKind.StockReconciliation, new ReportFilter(MismatchesOnly: true, Limit: 100)), static report => report.Rows.Count);
        // The weekly maintenance check of every lot against all of its documents.
        await Measure("lot-source-full-check", () => database.ReadAsync(connection =>
        {
            int differing = LotSourceReconciliation.CheckAll(connection, null).Count();
            if (differing != 0) throw new InvalidDataException($"{differing} lots differ from their documents.");
            return differing;
        }), static rows => rows);
        break;
    }
    case "rebuild-pnl-days":
        // Support repair for a drifted open-period P&L day projection: an exclusive,
        // month-by-month rebuild from sale and return snapshots.
        await Measure("pnl-day-rebuild", () => Task.Run(() =>
        {
            new MigrationRunner(factory, NullMigrationBackup.Instance).RebuildPnlDays();
            return 1;
        }), static rows => rows);
        break;
    case "phase8-upgrade":
    case "phase8-compact":
    {
        if (!Path.GetFileName(root).Contains("rehearsal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Phase 8 rehearsals need a disposable rehearsal root.");
        string volume = Path.GetPathRoot(paths.DatabaseFile)!;
        static long FolderBytes(string directory, string pattern) => !Directory.Exists(directory) ? 0 :
            Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Sum(static file =>
            {
                try { return new FileInfo(file).Length; }
                catch (IOException) { return 0L; }
            });
        long PageBytes(string pragma)
        {
            using var connection = factory.Open();
            return connection.ExecuteScalar<long>("PRAGMA page_size") * connection.ExecuteScalar<long>("PRAGMA " + pragma);
        }
        long freeBefore = new DriveInfo(volume).AvailableFreeSpace;
        long freelistBefore = PageBytes("freelist_count");
        using var sampling = new CancellationTokenSource();
        Task sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                process.Refresh();
                phase3ResourceSamples.Enqueue(new(auditElapsed.Elapsed.TotalSeconds,
                    File.Exists(paths.DatabaseFile + "-wal") ? new FileInfo(paths.DatabaseFile + "-wal").Length : 0,
                    process.WorkingSet64, 0, FolderBytes(paths.TempDir, "*"),
                    FolderBytes(backups.BackupDirectory, "*.partial"), new DriveInfo(volume).AvailableFreeSpace));
                try { await Task.Delay(250, sampling.Token); }
                catch (OperationCanceledException) { }
            }
        });
        var stageStarts = new System.Collections.Concurrent.ConcurrentDictionary<CompactionStage, double>();
        try
        {
            if (mode == "phase8-upgrade")
                await Measure("phase8-upgrade-with-archive-backup", () => Task.Run(() =>
                {
                    new MigrationRunner(factory, backups).Run();
                    using var connection = factory.Open();
                    return connection.ExecuteScalar<int>("PRAGMA user_version");
                }), static version => version);
            else
            {
                CompactionEstimate estimate = backups.EstimateCompaction();
                Console.WriteLine($"COMPACT_ESTIMATE database={estimate.DatabaseBytes} reclaimable={estimate.ReclaimableBytes} " +
                    $"compact={estimate.CompactBytes} required={estimate.RequiredFreeBytes} available={estimate.AvailableFreeBytes}");
                backups.RequestCompaction();
                var stages = new InlineProgress<CompactionProgress>(step =>
                {
                    if (stageStarts.TryAdd(step.Stage, auditElapsed.Elapsed.TotalSeconds))
                        Console.WriteLine($"STAGE {step.Stage} at {auditElapsed.Elapsed.TotalSeconds:0.0} s");
                });
                await Measure("phase8-compact", () => Task.Run(() =>
                {
                    CompactionResult result = backups.Compact(stages, CancellationToken.None);
                    Console.WriteLine($"COMPACTED before={result.BytesBefore} after={result.BytesAfter} backup={result.BackupPath}");
                    return result;
                }), static _ => 1);
            }
        }
        finally
        {
            sampling.Cancel();
            await sampler;
        }
        Phase3ResourceSample[] samples = phase3ResourceSamples.ToArray();
        phaseOneChecks = new Dictionary<string, long>
        {
            ["freeBeforeBytes"] = freeBefore,
            ["minimumFreeBytes"] = samples.Length == 0 ? freeBefore : samples.Min(static sample => sample.AvailableFreeBytes),
            ["freeAfterBytes"] = new DriveInfo(volume).AvailableFreeSpace,
            ["peakTemporaryBytes"] = samples.Length == 0 ? 0 : samples.Max(static sample => sample.TemporarySnapshotBytes),
            ["peakPartialArchiveBytes"] = samples.Length == 0 ? 0 : samples.Max(static sample => sample.PartialArchiveBytes),
            ["peakWalBytes"] = samples.Length == 0 ? 0 : samples.Max(static sample => sample.WalBytes),
            ["freelistBeforeBytes"] = freelistBefore,
            ["freelistAfterBytes"] = PageBytes("freelist_count"),
            ["backupFolderBytes"] = FolderBytes(backups.BackupDirectory, "*.zip"),
            // The original store waits in a replaced-* folder until the compact store opens again.
            ["keptOriginalBytes"] = Directory.EnumerateDirectories(paths.DbDir, "replaced-*")
                .Sum(static directory => FolderBytes(directory, "*")),
        };
        foreach ((CompactionStage stage, double seconds) in stageStarts.OrderBy(static pair => pair.Value))
            phaseOneChecks["stage" + stage + "StartMs"] = (long)(seconds * 1000);
        break;
    }
    // A verified backup restored on a fresh install, as on a second PC; run verify afterwards.
    case "phase8-restore":
    {
        if (args.Length < 4) throw new ArgumentException("Pass the verified backup zip as the fourth argument.");
        if (rowCounts["product"] != 0) throw new InvalidOperationException("Restore onto a fresh, empty data root.");
        RestoreCandidate candidate = backups.InspectRestore(Path.GetFullPath(args[3]));
        if (candidate.Manifest.SchemaVersion != MigrationRunner.LatestVersion)
            throw new InvalidDataException("The archive must hold the latest schema.");
        await Measure("phase8-restore-fresh-install", () => Task.Run(() =>
        {
            backups.ApplyRestore(candidate);
            backups.FinishPendingRestore();
            using var check = factory.Open();
            if (check.ExecuteScalar<long>("PRAGMA user_version") != MigrationRunner.LatestVersion ||
                check.ExecuteScalar<string>("PRAGMA quick_check") != "ok" ||
                check.QueryFirstOrDefault("PRAGMA foreign_key_check") is not null ||
                backups.InspectCurrent().Summary != candidate.Manifest.Summary)
                throw new InvalidDataException("The restored store failed validation.");
            foreach (string table in rowCounts.Keys)
                Console.WriteLine($"RESTORED_ROWS {table} {check.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}")}");
            return 1;
        }), static rows => rows);
        break;
    }
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
            var filter = new ReportFilter(From: monthStart, To: end,
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
        await Measure("stock-count-review", () => database.WriteAsync(tx =>
        {
            (long onHand, long version) = tx.Connection.QuerySingle<(long, long)>(
                "SELECT qty_on_hand,quantity_version FROM product_lot WHERE id=@adjustmentLot",
                new { adjustmentLot }, tx.Transaction);
            new DirectStockEngine().ReviewCount(tx, adjustmentLot, version, onHand, onHand - 1000, end,
                "Scale audit count", OperationGuard.NewId());
            return 1;
        }), static x => x);


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
    case "backup-cancel":
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        long started = Stopwatch.GetTimestamp();
        try
        {
            await Task.Run(() => backups.Create("cancel-trial", cancellation.Token));
            throw new InvalidDataException("The full-size backup finished before the cancellation trial.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine($"BACKUP_CANCELLED_MS {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0}");
        }
        if (Directory.Exists(backups.BackupDirectory) && Directory.EnumerateFiles(
                backups.BackupDirectory, "*cancel-trial*.zip*").Any())
            throw new InvalidDataException("Canceled backup left an archive or partial archive.");
        using var connection = factory.Open();
        if (connection.ExecuteScalar<string>("PRAGMA quick_check") != "ok")
            throw new InvalidDataException("Database quick check failed after canceled backup.");
        break;
    }
    case "backup":
        await Measure("backup-create", () => Task.Run(() => backups.Create("scale-audit")), static _ => 1);
        break;
    case "phase3-resource":
    {
        long productId;
        using (var connection = factory.Open())
            productId = connection.ExecuteScalar<long>("""
                SELECT product_id FROM product_lot WHERE qty_on_hand>1000 AND is_active=1
                AND expiry_date>='2026-09-26' ORDER BY qty_on_hand DESC LIMIT 1
                """);
        if (productId == 0) throw new InvalidDataException("No saleable high-stock lot is available.");
        long freeBefore = new DriveInfo(Path.GetPathRoot(paths.Root)!).AvailableFreeSpace;
        using var heldConnection = factory.Open();
        using var heldReader = heldConnection.BeginTransaction(deferred: true);
        heldConnection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale", transaction: heldReader);
        using var sampleStop = new CancellationTokenSource();
        long startedAt = Stopwatch.GetTimestamp();
        Task sampler = Task.Run(async () =>
        {
            while (!sampleStop.IsCancellationRequested)
            {
                long temporaryBytes = Directory.Exists(paths.TempDir)
                    ? Directory.EnumerateFiles(paths.TempDir, "grocery.db", SearchOption.AllDirectories)
                        .Where(static path => Path.GetFileName(Path.GetDirectoryName(path)!).StartsWith("backup-", StringComparison.Ordinal))
                        .Sum(static path => new FileInfo(path).Length) : 0;
                long partialBytes = Directory.Exists(backups.BackupDirectory)
                    ? Directory.EnumerateFiles(backups.BackupDirectory, "*.partial")
                        .Sum(static path => new FileInfo(path).Length) : 0;
                string walPath = paths.DatabaseFile + "-wal";
                process.Refresh();
                phase3ResourceSamples.Enqueue(new(Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                    File.Exists(walPath) ? new FileInfo(walPath).Length : 0,
                    process.WorkingSet64, factory.ActiveConnections, temporaryBytes, partialBytes,
                    new DriveInfo(Path.GetPathRoot(paths.Root)!).AvailableFreeSpace));
                try { await Task.Delay(1000, sampleStop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        using var backupCancel = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        Task<BackupInfo> backupTask = Task.Run(() => backups.Create("daily", backupCancel.Token));
        try
        {
            await Measure("active-reader-backup-and-20-sales", async () =>
            {
                for (int index = 0; index < 20; index++)
                {
                    long saleStarted = Stopwatch.GetTimestamp();
                    long saleId = await sales.AddProductAsync(null, productId, Quantity.One);
                    SaleDetails details = await sales.GetAsync(saleId);
                    await sales.CompleteAsync(new CompleteSaleCommand(saleId,
                        [new PaymentInput(PaymentMode.Cash, details.Totals.GrandTotal)], OperationGuard.NewId()));
                    profileSamples.Add(new("reader-overlap-sale", Stopwatch.GetElapsedTime(saleStarted).TotalMilliseconds, 1, null));
                }
                BackupInfo completed = await backupTask;
                BackupService.Verify(completed.Path);
                return 20;
            }, static count => count);
        }
        finally
        {
            backupCancel.Cancel();
            try { await backupTask; } catch (OperationCanceledException) { }
            sampleStop.Cancel();
            await sampler;
            heldReader.Rollback();
        }
        using (var check = factory.Open())
        {
            long actual = check.ExecuteScalar<long>("SELECT COUNT(*) FROM sale");
            if (actual != rowCounts["sale"] + 20)
                throw new InvalidDataException($"Sale count changed from {rowCounts["sale"]} to {actual}, expected +20.");
        }
        string wal = paths.DatabaseFile + "-wal";
        long walBeforeShutdown = File.Exists(wal) ? new FileInfo(wal).Length : 0;
        int archivesBefore = Directory.EnumerateFiles(backups.BackupDirectory, "*.zip").Count();
        var wallClock = new GroceryErp.Core.Time.SystemClock(settings);
        var restartedBackups = new BackupService(paths, factory, wallClock, settings);
        var shutdownMaintenance = new MaintenanceService(paths, factory, database, restartedBackups, wallClock, audit);
        await Measure("shutdown-after-recent-daily", () => Task.Run(() =>
        {
            shutdownMaintenance.Shutdown();
            return 1;
        }), static count => count);
        long walAfterShutdown = File.Exists(wal) ? new FileInfo(wal).Length : 0;
        int archivesAfter = Directory.EnumerateFiles(backups.BackupDirectory, "*.zip").Count();
        using (var check = factory.Open())
            if (check.ExecuteScalar<string?>("SELECT value FROM setting WHERE key='maintenance.clean_shutdown'") != "1")
                throw new InvalidDataException("Clean shutdown marker was not durable.");
        if (archivesAfter != archivesBefore)
            throw new InvalidDataException("Shutdown repeated a recent verified daily backup.");
        phase3ResourceCheck = new(freeBefore,
            phase3ResourceSamples.Min(static sample => sample.AvailableFreeBytes),
            new DriveInfo(Path.GetPathRoot(paths.Root)!).AvailableFreeSpace,
            walBeforeShutdown, walAfterShutdown, archivesBefore, archivesAfter);
        Console.WriteLine($"PHASE3_RESOURCES free-min={phase3ResourceCheck.MinimumFreeBytes}, " +
            $"WAL before/after shutdown={walBeforeShutdown}/{walAfterShutdown}, archives={archivesAfter}");
        break;
    }
    case "restore-race":
    {
        BackupInfo backup = backups.Create("restore-race");
        RestoreCandidate candidate = backups.InspectRestore(backup.Path);
        var otherFactory = new SqliteConnectionFactory(paths);
        var printJournal = new PrintJobJournal(otherFactory);
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
        Task apply = Task.Run(() => backups.ApplyRestore(candidate));
        try
        {
            // A full-size pre-restore backup is deliberately completed before the
            // barrier, so its duration must not be mistaken for a barrier failure.
            if (!SpinWait.SpinUntil(() => otherFactory.IsExclusive, TimeSpan.FromMinutes(5)))
                throw new TimeoutException("Restore did not enter the exclusive barrier.");
            if (apply.IsCompleted) throw new InvalidDataException("Restore did not wait for the active reader.");
            try { printJournal.ReviewCount(); throw new InvalidDataException("Print journal entered the restore barrier."); }
            catch (InvalidOperationException) { }
            try { otherFactory.Open().Dispose(); throw new InvalidDataException("A second factory entered the restore barrier."); }
            catch (InvalidOperationException) { }
            await Measure("restore-active-reader-drain", async () =>
            {
                release.Set();
                await reader;
                await apply;
                return backups.HasPendingRestore ? 1 : 0;
            }, static x => x);
            if (!backups.HasPendingRestore) throw new InvalidDataException("Restore marker is missing.");
            backups.UndoPendingRestore();
            using var restored = otherFactory.Open();
            if (restored.ExecuteScalar<string>("PRAGMA quick_check") != "ok" ||
                restored.Query("PRAGMA foreign_key_check").Any())
                throw new InvalidDataException("Recovered database failed validation.");
            foreach ((string table, long expected) in rowCounts)
                if (restored.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}") != expected)
                    throw new InvalidDataException($"Recovered {table} row count changed.");
        }
        finally
        {
            release.Set();
            await reader;
            try { await apply; }
            finally
            {
                backups.DiscardRestoreCandidate(candidate);
                if (backups.HasPendingRestore) backups.UndoPendingRestore();
            }
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
            // The one-time compaction offer would otherwise stop the journey after sign-in.
            connection.Execute("""
                INSERT INTO setting(key,value) VALUES ('maintenance.compact_offered','1')
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """);
            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_ACCEPTANCE") == "1")
                connection.Execute("""
                    INSERT INTO setting(key,value) VALUES ('maintenance.integrity_last_run',@today)
                    ON CONFLICT(key) DO UPDATE SET value=excluded.value
                    """, new { today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture) });
        }
        if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_ACCEPTANCE") == "1")
            await Measure("ui-setup-verified-daily-backup", () => Task.Run(() =>
            {
                new BackupService(paths, factory, new AuditClock(DateTime.Now), settings).Create("ui-setup");
                return 1;
            }), static x => x);
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
            await Measure("min-lines-per-purchase", () => Task.Run(() =>
                connection.ExecuteScalar<int>("SELECT MIN(lines) FROM (SELECT COUNT(*) AS lines FROM purchase_line GROUP BY purchase_id)")), static x => x);
            await Measure("max-lines-per-purchase", () => Task.Run(() =>
                connection.ExecuteScalar<int>("SELECT MAX(lines) FROM (SELECT COUNT(*) AS lines FROM purchase_line GROUP BY purchase_id)")), static x => x);
            await Measure("zero-padded-loose-plus", () => Task.Run(() =>
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM product WHERE is_loose=1 AND plu_code LIKE '0%'")), static x => x);
            await Measure("void-bills", () => Task.Run(() =>
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sale WHERE status='VOID'")), static x => x);
            await Measure("customer-returns", () => Task.Run(() =>
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sale_return")), static x => x);
            await Measure("category-grandchildren", () => Task.Run(() =>
                connection.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM category child
                    JOIN category parent ON parent.id=child.parent_id
                    WHERE parent.parent_id IS NOT NULL
                    """)), static x => x);
        }
        break;
    case "dashboard-reconcile":
        using (var connection = factory.Open())
        {
            await Measure("dashboard-day-reconciliation", () => Task.Run(() =>
            {
                long drift = CountDashboardDayDrift(connection);
                if (drift != 0) throw new InvalidDataException($"{drift} dashboard day totals differ from source sales.");
                return 0;
            }), static x => x);
            if (connection.ExecuteScalar<int>("PRAGMA user_version") >= 19)
                await Measure("dashboard-top-reconciliation", () => Task.Run(() =>
                {
                    long drift = CountDashboardTopDrift(connection);
                    if (drift != 0) throw new InvalidDataException($"{drift} dashboard top rows differ from source lines.");
                    return 0;
                }), static x => x);
        }
        break;
    case "verify":
        using (var connection = factory.Open())
        {
            if (connection.ExecuteScalar<int>("PRAGMA user_version") >= 25)
            {
                await Measure("sale-report-day-reconciliation", () => Task.Run(() =>
                {
                    long drift = connection.ExecuteScalar<long>("""
                        WITH movements AS (
                          SELECT s.sale_date AS day,COALESCE(sl.category_id_snapshot,p.category_id,0) AS category_id,
                                 COALESCE(sl.hsn_code,'') AS hsn,
                                 sl.tax_rate_pct AS rate,sl.qty AS quantity,sl.taxable_amount AS revenue,
                                 sl.tax_amount AS tax,sl.cost_amount AS cost
                          FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                          LEFT JOIN product p ON p.id=sl.product_id WHERE s.status='COMPLETED'
                          UNION ALL
                          SELECT r.return_date,COALESCE(sl.category_id_snapshot,p.category_id,0),
                                 COALESCE(sl.hsn_code,''),sl.tax_rate_pct,
                                 -rl.qty,-rl.taxable_amount,-rl.tax_amount,-rl.cost_amount
                          FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
                          JOIN sale_line sl ON sl.id=rl.sale_line_id LEFT JOIN product p ON p.id=sl.product_id
                        ), expected AS MATERIALIZED (
                          SELECT day,category_id,hsn,rate,COUNT(*) AS entries,
                                 SUM(quantity) AS quantity,SUM(revenue) AS revenue,
                                 SUM(tax) AS tax,SUM(cost) AS cost
                          FROM movements GROUP BY day,category_id,hsn,rate
                        )
                        SELECT COUNT(*) FROM (
                          SELECT * FROM (SELECT * FROM expected EXCEPT SELECT * FROM sale_report_day)
                          UNION ALL
                          SELECT * FROM (SELECT * FROM sale_report_day EXCEPT SELECT * FROM expected)
                        )
                        """);
                    if (drift != 0) throw new InvalidDataException($"{drift} sales report day rows differ from source.");
                    return drift;
                }), static value => (int)value);
                await Measure("sale-product-last-reconciliation", () => Task.Run(() =>
                {
                    long drift = connection.ExecuteScalar<long>("""
                        WITH expected AS MATERIALIZED (
                          SELECT sl.product_id,MAX(s.sale_date) AS last_day
                          FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                          WHERE s.status='COMPLETED' GROUP BY sl.product_id
                        )
                        SELECT COUNT(*) FROM (
                          SELECT * FROM (SELECT * FROM expected EXCEPT SELECT * FROM sale_product_last)
                          UNION ALL
                          SELECT * FROM (SELECT * FROM sale_product_last EXCEPT SELECT * FROM expected)
                        )
                        """);
                    if (drift != 0) throw new InvalidDataException($"{drift} last-sale rows differ from source.");
                    return drift;
                }), static value => (int)value);
                await Measure("sale-hour-day-reconciliation", () => Task.Run(() =>
                {
                    long drift = connection.ExecuteScalar<long>("""
                        WITH expected AS MATERIALIZED (
                          SELECT sale_date AS day,strftime('%w',completed_at) AS weekday,
                                 CAST(strftime('%H',completed_at) AS INTEGER) AS hour,
                                 COUNT(*) AS bills,SUM(grand_total) AS revenue
                          FROM sale WHERE status='COMPLETED' AND completed_at IS NOT NULL
                          GROUP BY sale_date,strftime('%w',completed_at),strftime('%H',completed_at)
                        )
                        SELECT COUNT(*) FROM (
                          SELECT * FROM (SELECT * FROM expected EXCEPT SELECT * FROM sale_hour_day)
                          UNION ALL
                          SELECT * FROM (SELECT * FROM sale_hour_day EXCEPT SELECT * FROM expected)
                        )
                        """);
                    if (drift != 0) throw new InvalidDataException($"{drift} sale hour rows differ from source.");
                    return drift;
                }), static value => (int)value);
            }
            if (connection.ExecuteScalar<int>("PRAGMA user_version") >= 18)
                await Measure("dashboard-day-reconciliation", () => Task.Run(() =>
                {
                    long drift = CountDashboardDayDrift(connection);
                    if (drift != 0) throw new InvalidDataException($"{drift} dashboard day totals differ from source sales.");
                    return 0;
                }), static x => x);
            if (connection.ExecuteScalar<int>("PRAGMA user_version") >= 19)
                await Measure("dashboard-top-reconciliation", () => Task.Run(() =>
                {
                    long drift = CountDashboardTopDrift(connection);
                    if (drift != 0) throw new InvalidDataException($"{drift} dashboard top rows differ from source lines.");
                    return 0;
                }), static x => x);
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
            bool directStock = connection.ExecuteScalar<int>("PRAGMA user_version") >= 33;
            await Measure(directStock ? "lot-source-reconciliation" : "lot-cache-mismatch", () => Task.Run(() =>
            {
                long mismatches = directStock
                    ? LotSourceReconciliation.CheckAll(connection, null).LongCount()
                    : connection.ExecuteScalar<long>("SELECT COUNT(*) FROM v_lot_cache_mismatch");
                if (mismatches != 0) throw new InvalidDataException($"{mismatches} lot source mismatches.");
                return (int)mismatches;
            }), static x => x);
            await Measure("sale-lines-after-lot-expiry", () => Task.Run(() =>
            {
                long expired = connection.ExecuteScalar<long>("""
                    SELECT COUNT(*) FROM sale_line sl
                    JOIN sale s ON s.id=sl.sale_id
                    JOIN product_lot pl ON pl.id=sl.lot_id
                    WHERE s.sale_date>pl.expiry_date
                    """);
                if (expired != 0) throw new InvalidDataException($"{expired} sale lines are after lot expiry.");
                return (int)expired;
            }), static x => x);
            await Measure("interrupted-write-committed", () => Task.Run(() =>
            {
                int committed = connection.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM expense WHERE paid_to='phase0-interrupted-write'
                    """);
                if (committed != 0) throw new InvalidDataException($"{committed} interrupted writes committed.");
                return committed;
            }), static x => x);
        }
        break;
    default:
        throw new ArgumentException($"Unknown mode '{mode}'.");
}

if (mode is "attribution-profile" or "history-profile" or "shape-profile" or "report-profile" or "phase6-reports" or "gap-read" or "feature-write" or "weekly-maintenance-overlap" or "print-journal-profile" or "phase7-filters")
{
    // Freeze the service trace before audit metadata queries and plan collection.
    factory.SqlTrace = null;
    int index = 0;
    foreach (var entry in tracedQueries
        .OrderBy(static item => item.Key.Operation, StringComparer.Ordinal)
        .ThenBy(static item => item.Key.Shape, StringComparer.Ordinal))
    {
        string operation = entry.Key.Operation;
        string sql = entry.Value;
        // SQLite's expanded SQL can print unary minus followed by a negative
        // parameter as "--1", which EXPLAIN parses as a comment. Keep the
        // executable arithmetic while preserving the traced value.
        string planSql = System.Text.RegularExpressions.Regex.Replace(sql,
            @"--([0-9]+)\b", "-(-$1)");
        string name = $"executed-{operation}-{++index}";
        try { CapturePlan(name, planSql); }
        catch (Exception exception)
        {
            queryPlans.Add(new QueryPlan(name, [], planSql,
                exception.GetType().Name + ": " + exception.Message));
            Console.WriteLine($"PLAN-FAIL {name}: {exception.GetType().Name}");
        }
        if (mode == "attribution-profile" && operation == "GetHistoryAsync")
            await Measure("sql-only-" + name, () => Task.Run(() =>
            {
                using var connection = factory.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                int rows = 0;
                while (reader.Read()) rows++;
                return rows;
            }), static rows => rows);
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
factory.SampleWalBytes();
meterListener.RecordObservableInstruments();
process.Refresh();
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    mode,
    readConcurrency,
    root,
    databaseBytes,
    databaseBytesAfter = new FileInfo(paths.DatabaseFile).Length,
    walBytesBefore,
    rowCounts,
    phaseOneStorage,
    phaseOneChecks,
    phaseTwoChecks,
    phaseNine,
    machine = Environment.MachineName,
    logicalProcessors = Environment.ProcessorCount,
    dotnet = Environment.Version.ToString(),
    sqlite = GetSqliteVersion(),
    schemaVersion = GetUserVersion(),
    walBytes = File.Exists(paths.DatabaseFile + "-wal") ? new FileInfo(paths.DatabaseFile + "-wal").Length : 0,
    elapsedMilliseconds = auditElapsed.Elapsed.TotalMilliseconds,
    peakWorkingSetBytes = process.PeakWorkingSet64,
    managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
    cacheState = "new process; OS file cache was not flushed",
    results,
    queryPlans,
    tracedQueryCount = tracedQueries.Count,
    profiles = profileSamples.GroupBy(static sample => sample.Name).OrderBy(static group => group.Key)
        .Select(group =>
        {
            double[] times = group.Select(static sample => sample.Milliseconds).Order().ToArray();
            return new ProfileSummary(group.Key, times.Length, Percentile(times, .5), Percentile(times, .95),
                Percentile(times, .99), group.Count(static sample => sample.Error is not null),
                group.Max(static sample => sample.Rows));
        }),
    resourceSamples,
    phase3ResourceSamples,
    phase3ResourceCheck,
    sustainedSalesCheck,
    metrics = metricSamples.GroupBy(static sample => sample.Name).OrderBy(static group => group.Key)
        .Select(group =>
        {
            double[] values = group.Select(static sample => sample.Value).Order().ToArray();
            return new MetricSummary(group.Key, values.Length, Percentile(values, .5),
                Percentile(values, .95), Percentile(values, .99));
        }),
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULTS {output}");
if ((mode is "verify" or "phase1-rollback" or "restore-race" or "sustained-profile" or "phase4-combined" or "refresh-storm" or "phase3-resource" or "phase6-reports") &&
    (results.Any(static result => result.Error is not null) ||
     (mode is "sustained-profile" or "phase4-combined" && (profileSamples.Any(static sample => sample.Error is not null) ||
        sustainedSalesCheck is null || sustainedSalesCheck.Failed != 0 ||
        sustainedSalesCheck.After != sustainedSalesCheck.Before + sustainedSalesCheck.Successful))))
    throw new InvalidDataException($"{mode} recorded a failed verification step; see {output}.");

string GetSqliteVersion() { using var connection = factory.Open(); return connection.ExecuteScalar<string>("SELECT sqlite_version()") ?? "unknown"; }
long GetUserVersion() { using var connection = factory.Open(); return connection.ExecuteScalar<long>("PRAGMA user_version"); }
static long CountDashboardDayDrift(SqliteConnection connection) => connection.ExecuteScalar<long>("""
    WITH source AS (
      SELECT sale_date AS day,COUNT(*) AS bills,SUM(grand_total) AS sales,
             SUM(taxable_total) AS taxable,SUM(round_off) AS rounding,SUM(cost_total) AS cost
      FROM sale WHERE status='COMPLETED' GROUP BY sale_date
    )
    SELECT (SELECT COUNT(*) FROM pnl_day p LEFT JOIN source s ON s.day=p.day
            WHERE p.bills<>COALESCE(s.bills,0) OR p.sales<>COALESCE(s.sales,0)
               OR p.taxable<>COALESCE(s.taxable,0) OR p.rounding<>COALESCE(s.rounding,0)
               OR p.cost<>COALESCE(s.cost,0))
         + (SELECT COUNT(*) FROM source s LEFT JOIN pnl_day p ON p.day=s.day
            WHERE p.day IS NULL)
    """);
static long CountDashboardTopDrift(SqliteConnection connection) => connection.ExecuteScalar<long>("""
    WITH movements AS (
      SELECT sl.product_id,sl.product_name AS name,COALESCE(sl.product_name_local,'') AS local_name,
             sl.product_name_local IS NULL AS local_is_null,1 AS movements,
             sl.qty,sl.taxable_amount AS revenue,sl.cost_amount AS cost
      FROM sale s JOIN sale_line sl ON sl.sale_id=s.id
      JOIN dashboard_top_window w ON s.sale_date BETWEEN w.from_day AND w.to_day
      WHERE s.status='COMPLETED'
      UNION ALL
      SELECT sl.product_id,sl.product_name,COALESCE(sl.product_name_local,''),
             sl.product_name_local IS NULL,1,-rl.qty,-rl.taxable_amount,-rl.cost_amount
      FROM sale_return r JOIN sale_return_line rl ON rl.sale_return_id=r.id
      JOIN sale_line sl ON sl.id=rl.sale_line_id
      JOIN dashboard_top_window w ON r.return_date BETWEEN w.from_day AND w.to_day
    ), source AS (
      SELECT product_id,name,local_name,local_is_null,SUM(movements) AS movements,
             SUM(qty) AS qty,SUM(revenue) AS revenue,SUM(cost) AS cost
      FROM movements GROUP BY product_id,name,local_name,local_is_null
    )
    SELECT COUNT(*) FROM (
      SELECT * FROM (
        SELECT product_id,name,local_name,local_is_null,movements,qty,revenue,cost FROM source
        EXCEPT
        SELECT product_id,name,local_name,local_is_null,movements,qty,revenue,cost FROM dashboard_top_summary
      )
      UNION ALL
      SELECT * FROM (
        SELECT product_id,name,local_name,local_is_null,movements,qty,revenue,cost FROM dashboard_top_summary
        EXCEPT
        SELECT product_id,name,local_name,local_is_null,movements,qty,revenue,cost FROM source
      )
    )
    """, commandTimeout: 120);
static double Percentile(double[] sorted, double fraction) => sorted.Length == 0 ? 0 :
    sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];

internal sealed record Measurement(string Name, double Milliseconds, int Rows, long WorkingSetBefore,
    long WorkingSetAfter, long PeakWorkingSet, string? Error);

internal sealed record QueryPlan(string Name, IReadOnlyList<string> Details, string Sql, string? Error = null);
internal sealed class PhaseOneStorageRow
{
    public string Name { get; init; } = "";
    public long Bytes { get; init; }
}
internal sealed record ProfileSample(string Name, double Milliseconds, int Rows, string? Error);
internal sealed record ResourceSample(double ElapsedSeconds, long WalBytes, long WorkingSetBytes,
    double CpuPercent, int ActiveConnections);
internal sealed record Phase3ResourceSample(double ElapsedSeconds, long WalBytes, long WorkingSetBytes,
    int ActiveConnections, long TemporarySnapshotBytes, long PartialArchiveBytes, long AvailableFreeBytes);
internal sealed record Phase3ResourceCheck(long InitialFreeBytes, long MinimumFreeBytes, long FinalFreeBytes,
    long WalBeforeShutdownBytes, long WalAfterShutdownBytes, int ArchivesBeforeShutdown, int ArchivesAfterShutdown);
internal sealed record SustainedSalesCheck(long Before, long Successful, long Failed, long After);
internal sealed record ProfileSummary(string Name, int Samples, double P50Milliseconds, double P95Milliseconds,
    double P99Milliseconds, int Errors, int MaximumRows);
internal sealed record MetricSample(string Name, double Value);
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
internal sealed record MetricSummary(string Name, int Samples, double P50, double P95, double P99);
internal sealed class RealismSale
{
    public long SaleId { get; init; }
    public long LineId { get; init; }
    public long Quantity { get; init; }
}

internal sealed class MatrixAuditRow
{
    public string Section { get; init; } = "";
    public string HeadCode { get; init; } = "";
    public long? ReferenceId { get; init; }
    public string Month { get; init; } = "";
    public long Amount { get; init; }
}

internal sealed class AuditClock(DateTime now) : IClock
{
    public DateTime Now => now;
    public DateOnly BusinessDate => DateOnly.FromDateTime(now);
}
