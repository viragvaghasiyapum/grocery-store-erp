using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Dapper;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Purchasing;
using GroceryErp.Reports;
using GroceryErp.Sales;
using Microsoft.Data.Sqlite;
using SQLitePCL;

/// <summary>
/// Phase 9 query and write review on a disposable full-size copy. Each operation first runs once
/// under the SQL trace, so every statement it executed is planned and re-run read-only for rows
/// visited; then alone for duration, allocation and GC pause; then mixed with readers, a cashier,
/// back-office writes and a backup, with writer wait, read queue, WAL and working set sampled.
/// </summary>
internal sealed class PhaseNineProfile(AppPaths paths, SqliteConnectionFactory factory, DbExecutor database,
    SettingsService settings, AuditLog audit, BackupService backups)
{
    private readonly AuditClock _clock = new(new DateTime(2026, 10, 18, 12, 0, 0));
    private readonly ConcurrentDictionary<(string Profile, string Operation, string Sql), int> _traced = new();
    private volatile string _profiling = "";
    private long _untimedTicks;
    private long _untimedBytes;
    private readonly List<OperationProfile> _isolated = [];
    private readonly ConcurrentBag<(string Name, double Milliseconds, string? Error)> _mixed = [];
    private readonly ConcurrentBag<(string Name, double Value)> _waits = [];
    private readonly ConcurrentQueue<(double Seconds, long WalBytes, long WorkingSetBytes)> _resources = new();
    private DateOnly Today => _clock.BusinessDate;

    public async Task<object> RunAsync()
    {
        using (SqliteConnection check = factory.Open())
            if (check.ExecuteScalar<long>("PRAGMA user_version") != MigrationRunner.LatestVersion)
                throw new InvalidDataException("Migrate the disposable copy first.");
        var periods = new PeriodGuard(settings, _clock);
        var numbers = new DocNumberService(settings);
        var lots = new LotQuantities(settings);
        var sales = new SalesService(database, lots, numbers, periods, settings, audit, _clock);
        var purchasing = new PurchasingService(database, lots, numbers, periods, settings, audit);
        var moneyOut = new MoneyOutService(database, lots, numbers, periods, audit);
        var inventory = new InventoryService(database, lots, numbers, periods, audit);
        var catalog = new CatalogService(database, audit, settings, _clock);
        var reports = new ReportService(database, settings, _clock);
        var scans = new ScanResolver(database);
        var counts = new DirectStockCountService(database, new DirectStockEngine(numbers), periods);
        Fixture fixture = await PrepareAsync(lots);

        // Every operation the plan names, each a closure that leaves the store consistent. Work an
        // operation needs first (a bill to void, a purchase to return) runs through Untimed, so the
        // figures describe only the action itself.
        long? openBill = null;
        async Task<long> NewBill(Quantity quantity)
        {
            long sale = await sales.CreateDraftAsync(Today);
            await sales.AddProductAsync(sale, fixture.HotProduct, quantity);
            return sale;
        }
        async Task Complete(long sale)
        {
            Money total = (await sales.GetAsync(sale)).Totals.GrandTotal;
            await sales.CompleteAsync(new(sale, [new(PaymentMode.Cash, total)], OperationGuard.NewId()));
        }
        async Task<long> Bill(Quantity quantity)
        {
            long sale = await NewBill(quantity);
            await Complete(sale);
            return sale;
        }
        async Task<long> PostPurchase()
        {
            long draft = await purchasing.SaveDraftAsync(null, new PurchaseDraftInput(fixture.Vendor,
                "P9-" + Guid.NewGuid().ToString("N")[..12], Today, Today, null, Money.Zero, "Phase 9 profile",
                [new PurchaseLineInput(fixture.HotProduct, "P9-BATCH", Today.AddMonths(-1), Today.AddMonths(9),
                    new Quantity(10), Quantity.Zero, new Money(10), Money.Zero, fixture.TaxRate, new Money(25), new Money(20))]));
            return (await purchasing.PostAsync(draft, OperationGuard.NewId())).Id;
        }
        var operations = new (string Name, Func<Task<int>> Run)[]
        {
            ("pos-scan-resolve", async () => (await scans.ResolveAsync(fixture.Barcode)) is null ? 0 : 1),
            ("pos-search-8", async () => (await catalog.SearchAsync(new(Query: fixture.NameWord, Limit: 8, SaleableOnly: true))).Count),
            // Scanning adds to the bill on screen; a fresh bill every ten scans keeps bills realistic.
            ("pos-add-line", async () =>
            {
                openBill ??= await Untimed(() => sales.CreateDraftAsync(Today));
                await sales.AddProductAsync(openBill, fixture.HotProduct, Quantity.One);
                if ((await Untimed(() => sales.GetAsync(openBill.Value))).Lines.Sum(static line => line.Quantity.Value) >= 10)
                {
                    await Untimed(async () => { await Complete(openBill.Value); return 0; });
                    openBill = null;
                }
                return 1;
            }),
            ("pos-complete", async () =>
            {
                (long sale, Money total) = await Untimed(async () =>
                {
                    long created = await NewBill(new Quantity(3));
                    return (created, (await sales.GetAsync(created)).Totals.GrandTotal);
                });
                await sales.CompleteAsync(new(sale, [new(PaymentMode.Cash, total)], OperationGuard.NewId()));
                return 1;
            }),
            ("pos-void", async () =>
            {
                long sale = await Untimed(() => Bill(Quantity.One));
                await sales.VoidAsync(sale, "Phase 9 profile void", OperationGuard.NewId());
                return 1;
            }),
            ("pos-partial-return", async () =>
            {
                (long sale, long line) = await Untimed(async () =>
                {
                    long sold = await Bill(new Quantity(2));
                    return (sold, (await sales.GetAsync(sold)).Lines[0].Id);
                });
                await sales.ReturnAsync(new(sale, Today, "Phase 9 profile return", RefundMode.Cash,
                    [new ReturnLineInput(line, Quantity.One, true)], OperationGuard.NewId()));
                return 1;
            }),
            ("products-browse-page", async () => (await catalog.SearchPageAsync(new(Limit: 15))).Items.Count),
            ("products-search-broad", async () => (await catalog.SearchPageAsync(new(Query: fixture.NameWord, Limit: 8))).Items.Count),
            ("products-search-gujarati", async () => (await catalog.SearchPageAsync(new(Query: fixture.LocalWord, Limit: 8))).Items.Count),
            ("products-search-sku", async () => (await catalog.SearchPageAsync(new(Query: fixture.Sku, Limit: 8))).Items.Count),
            ("products-search-barcode", async () => (await catalog.SearchPageAsync(new(Query: fixture.Barcode, Limit: 8))).Items.Count),
            ("products-search-no-match", async () => (await catalog.SearchPageAsync(new(Query: "zqxjv", Limit: 8))).Items.Count),
            ("lots-open-busiest", async () => (await inventory.GetLotPageAsync(new LotFilter { ProductId = fixture.BusiestProduct }, null, 15)).Items.Count),
            ("sales-history-page", async () => (await sales.GetHistoryAsync(new SaleHistoryFilter(Limit: 15))).Count),
            ("purchase-post", async () =>
            {
                long draft = await Untimed(() => purchasing.SaveDraftAsync(null, new PurchaseDraftInput(fixture.Vendor,
                    "P9-" + Guid.NewGuid().ToString("N")[..12], Today, Today, null, Money.Zero, "Phase 9 profile",
                    [new PurchaseLineInput(fixture.HotProduct, "P9-BATCH", Today.AddMonths(-1), Today.AddMonths(9),
                        new Quantity(10), Quantity.Zero, new Money(10), Money.Zero, fixture.TaxRate, new Money(25), new Money(20))])));
                await purchasing.PostAsync(draft, OperationGuard.NewId());
                return 1;
            }),
            ("purchase-void", async () =>
            {
                long purchase = await Untimed(PostPurchase);
                await purchasing.CancelAsync(purchase, Today, OperationGuard.NewId());
                return 1;
            }),
            ("vendor-return-and-cancel", async () =>
            {
                (long purchase, VendorReturnLot lot) = await Untimed(async () =>
                {
                    long posted = await PostPurchase();
                    return (posted, (await purchasing.GetVendorReturnLotsAsync(posted))[0]);
                });
                PostedVendorReturn posted = await purchasing.PostVendorReturnAsync(new(purchase, Today,
                    "P9-CR-" + Guid.NewGuid().ToString("N")[..8], "Phase 9 profile",
                    [new VendorReturnLineInput(lot.LotId, lot.Returnable, lot.OriginalBeforeTax, lot.OriginalTax)]),
                    OperationGuard.NewId());
                await purchasing.CancelVendorReturnAsync(posted.Id, Today, "Phase 9 profile", OperationGuard.NewId());
                return 1;
            }),
            ("damage", async () =>
            {
                await moneyOut.RecordDamageAsync(new(fixture.DamageLot, Today, Quantity.One, "BROKEN", Money.Zero, null), OperationGuard.NewId());
                return 1;
            }),
            ("count-review", async () =>
            {
                // Not the damage lot, which the damage loop changes at the same time, and not one
                // FEFO is selling, so a conflict here would be a real one.
                DirectCountLot lot = await Untimed(async () => (await counts.GetPageAsync([fixture.HotProduct], limit: 50)).Items
                    .First(item => item.LotId != fixture.DamageLot && item.OnHand >= Quantity.One.Scaled));
                return (await counts.PostReviewedAsync([new DirectCountReview(lot.LotId, lot.Version, lot.OnHand,
                    lot.OnHand - Quantity.One.Scaled, Today, "Phase 9 profile count", OperationGuard.NewId())])).Count;
            }),
            ("pnl-open-month", async () => (await reports.GetPnlReportAsync(new(PnlScope.Month,
                new DateRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), "Oct 2026"))).Lines.Count),
            ("pnl-open-fiscal-year", async () => (await reports.GetPnlReportAsync(new(PnlScope.FiscalYear,
                new DateRange(new DateOnly(2026, 4, 1), new DateOnly(2027, 3, 31)), "FY 2026-27"))).Lines.Count),
            ("pnl-finalized-fiscal-year", async () => (await reports.GetPnlReportAsync(new(PnlScope.FiscalYear,
                new DateRange(new DateOnly(2025, 4, 1), new DateOnly(2026, 3, 31)), "FY 2025-26"))).Lines.Count),
            ("pnl-finalized-calendar-year", async () => (await reports.GetPnlReportAsync(new(PnlScope.CalendarYear,
                new DateRange(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)), "CY 2025"))).Lines.Count),
            ("current-stock-excluding-expired", async () => (await reports.GetReportAsync(ReportKind.StockValue,
                new ReportFilter(IncludeExpired: false, Limit: 500))).Rows.Count),
            ("stock-reconciliation-busiest", async () => (await reports.GetReportAsync(ReportKind.StockReconciliation,
                new ReportFilter(ProductId: fixture.BusiestProduct, Limit: 500))).Rows.Count),
            ("dashboard", async () => { await reports.GetDashboardAsync(); return 1; }),
        };

        // 1. Traced pass: every statement each operation ran.
        // Operations run one at a time here, so the statement belongs to the one being profiled;
        // DbExecutor adds the service method that issued it.
        factory.SqlTrace = (operation, sql) =>
        {
            if (_traced.Count < 4096) _traced.TryAdd((_profiling, operation, sql.Trim()), 0);
        };
        foreach ((string name, Func<Task<int>> run) in operations)
        {
            _profiling = name;
            await run();
        }
        factory.SqlTrace = null;
        List<StatementProfile> statements = ProfileStatements();

        // 2. Isolated: each operation repeated alone.
        foreach ((string name, Func<Task<int>> run) in operations)
            _isolated.Add(await MeasureAsync(name, run, name.StartsWith("pnl-finalized", StringComparison.Ordinal) ||
                name.StartsWith("pos-", StringComparison.Ordinal) || name.StartsWith("products-", StringComparison.Ordinal) ? 30 : 10));

        // 3. Mixed: cashier bills with readers, back-office writes and one backup at once.
        object mixed = await RunMixedAsync(operations.ToDictionary(static item => item.Name, static item => item.Run), sales, fixture.HotProduct);

        using SqliteConnection after = factory.Open();
        int mismatches = after.Query<long>("SELECT id FROM product_lot WHERE product_id IN (@hot, @damageProduct)",
                new { hot = fixture.HotProduct, damageProduct = fixture.DamageProduct })
            .Count(lot => LotSourceReconciliation.CheckLot(after, null, lot) is not null);
        if (mismatches != 0) throw new InvalidDataException($"{mismatches} profiled lots differ from their documents.");
        return new
        {
            businessDate = Today,
            fixture,
            isolated = _isolated,
            mixed,
            statements,
            profiledLotSourceMismatches = mismatches,
        };
    }

    // Setup an operation needs before the measured action; its time and allocation are taken out
    // of the figures for that operation.
    private async Task<T> Untimed<T>(Func<Task<T>> work)
    {
        long bytes = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        try { return await work(); }
        finally
        {
            Interlocked.Add(ref _untimedTicks, Stopwatch.GetTimestamp() - started);
            Interlocked.Add(ref _untimedBytes, GC.GetTotalAllocatedBytes(precise: true) - bytes);
        }
    }

    private async Task<Fixture> PrepareAsync(LotQuantities lots)
    {
        string suffix = Guid.NewGuid().ToString("N")[..10];
        // A hot product with many small lots stands in for a fast mover whose FEFO list is long.
        (long hot, long damageLot, long damageProduct) = await database.WriteAsync(tx =>
        {
            long category = tx.Connection.ExecuteScalar<long>("SELECT id FROM category ORDER BY id LIMIT 1", transaction: tx.Transaction);
            long unit = tx.Connection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'", transaction: tx.Transaction);
            long tax = tx.Connection.ExecuteScalar<long>("SELECT id FROM tax_rate ORDER BY id LIMIT 1", transaction: tx.Transaction);
            long product = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,category_id,uom_id,tax_rate_id,hsn_code,price_includes_tax,
                    track_expiry,is_loose,default_selling_price)
                VALUES (@sku,'Phase 9 profile product',@category,@unit,@tax,'1006',1,1,0,2000) RETURNING id
                """, new { sku = "P9-" + suffix, category, unit, tax }, tx.Transaction);
            // FEFO sells the oldest receipt first, so damage goes to the newest lot, which holds
            // enough that it never runs out.
            long newest = 0;
            for (int index = 0; index < 128; index++)
            {
                decimal quantity = index == 0 ? 1000 : 100;
                // Bought at the product's rate, saved on the lot as every lot writer saves it.
                long lot = tx.Connection.ExecuteScalar<long>("""
                    INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,unit_cost,selling_price,qty_received,
                      tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,unit_tax,unit_cost_incl_tax)
                    SELECT @product,@code,'P9-BATCH',@received,'2027-10-18',1000,2000,@receivedScaled,
                      t.id,t.name,t.rate_pct,(1000*t.rate_pct+50000)/100000,1000+(1000*t.rate_pct+50000)/100000
                    FROM tax_rate t WHERE t.id=@tax RETURNING id
                    """, new { product, tax, code = $"P9-L-{suffix}-{index:D3}", received = Today.AddDays(-index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        receivedScaled = new Quantity(quantity).Scaled },
                    tx.Transaction);
                lots.Change(tx, new(Today, product, lot, LotChangeKind.DirectEntry, new Quantity(quantity)));
                if (index == 0) newest = lot;
            }
            return (product, newest, product);
        });
        using SqliteConnection connection = factory.Open();
        long vendor = connection.ExecuteScalar<long>("SELECT id FROM vendor WHERE is_active=1 ORDER BY id LIMIT 1");
        long busiest = connection.ExecuteScalar<long>("""
            SELECT product_id FROM product_lot GROUP BY product_id ORDER BY COUNT(*) DESC, product_id LIMIT 1
            """);
        (string sku, string name) = connection.QuerySingle<(string, string)>("""
            SELECT sku,name FROM product WHERE is_active=1 AND id<>@hot ORDER BY id LIMIT 1 OFFSET 500
            """, new { hot });
        string barcode = connection.ExecuteScalar<string>("SELECT barcode FROM product_barcode ORDER BY id LIMIT 1 OFFSET 200") ?? sku;
        string local = connection.ExecuteScalar<string>("""
            SELECT name_local FROM product WHERE name_local IS NOT NULL AND length(name_local)>=3 ORDER BY id LIMIT 1 OFFSET 300
            """) ?? "ખાંડ";
        long gst5 = connection.ExecuteScalar<long>(
            "SELECT id FROM tax_rate WHERE rate_pct=5000 AND is_active=1 ORDER BY id LIMIT 1");
        return new Fixture(hot, damageLot, damageProduct, vendor, busiest, sku, barcode,
            name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], local.Split(' ')[0], gst5);
    }

    private async Task<OperationProfile> MeasureAsync(string name, Func<Task<int>> run, int iterations)
    {
        var times = new List<double>(iterations);
        long allocated = 0;
        TimeSpan pause = GC.GetTotalPauseDuration();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        int rows = 0;
        for (int index = 0; index < iterations; index++)
        {
            Interlocked.Exchange(ref _untimedTicks, 0);
            Interlocked.Exchange(ref _untimedBytes, 0);
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long started = Stopwatch.GetTimestamp();
            rows = await run();
            long ticks = Stopwatch.GetTimestamp() - started - Interlocked.Read(ref _untimedTicks);
            times.Add(ticks * 1000.0 / Stopwatch.Frequency);
            allocated += GC.GetTotalAllocatedBytes(precise: true) - before - Interlocked.Read(ref _untimedBytes);
        }
        double[] sorted = [.. times.Order()];
        var profile = new OperationProfile(name, iterations, Percentile(sorted, .5), Percentile(sorted, .95),
            Percentile(sorted, .99), sorted[^1], rows, allocated / iterations,
            (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
        Console.WriteLine($"OK {name}: p50 {profile.P50:0.00} ms, p95 {profile.P95:0.00} ms, {profile.AllocatedBytesPerCall / 1024.0:0} KiB/call");
        return profile;
    }

    private async Task<object> RunMixedAsync(Dictionary<string, Func<Task<int>>> operations, SalesService sales, long hot)
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meters) =>
        {
            if (instrument.Name is "db.write.wait" or "db.read.queue") meters.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => _waits.Add((instrument.Name, value)));
        listener.Start();
        using var stop = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        async Task Loop(string name, Func<Task<int>> run, TimeSpan pause)
        {
            while (!stop.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();
                string? error = null;
                try { await run(); }
                catch (Exception exception) when (exception is not OperationCanceledException) { error = exception.GetType().Name + ": " + exception.Message; }
                _mixed.Add((name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, error));
                try { await Task.Delay(pause, stop.Token); }
                catch (OperationCanceledException) { }
            }
        }
        Task sampler = Task.Run(async () =>
        {
            using var process = Process.GetCurrentProcess();
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                string wal = paths.DatabaseFile + "-wal";
                _resources.Enqueue((elapsed.Elapsed.TotalSeconds, File.Exists(wal) ? new FileInfo(wal).Length : 0, process.WorkingSet64));
                try { await Task.Delay(500, stop.Token); }
                catch (OperationCanceledException) { }
            }
        });
        Task[] background =
        [
            Loop("reader-sales-history", operations["sales-history-page"], TimeSpan.FromMilliseconds(50)),
            Loop("reader-product-search", operations["products-search-broad"], TimeSpan.FromMilliseconds(50)),
            Loop("reader-lots", operations["lots-open-busiest"], TimeSpan.FromMilliseconds(100)),
            Loop("reader-pnl-open-month", operations["pnl-open-month"], TimeSpan.FromMilliseconds(500)),
            Loop("reader-current-stock", operations["current-stock-excluding-expired"], TimeSpan.FromSeconds(2)),
            Loop("office-purchase-post", operations["purchase-post"], TimeSpan.FromSeconds(1)),
            Loop("office-damage", operations["damage"], TimeSpan.FromSeconds(1)),
            Loop("office-count-review", operations["count-review"], TimeSpan.FromSeconds(2)),
            Loop("office-vendor-return-and-cancel", operations["vendor-return-and-cancel"], TimeSpan.FromSeconds(3)),
        ];
        var backupTimer = Stopwatch.StartNew();
        double? backupMilliseconds = null;
        string? backupError = null;
        double backupStartedAt = 0, backupEndedAt = 0;
        Task backup = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            backupStartedAt = elapsed.Elapsed.TotalSeconds;
            backupTimer.Restart();
            try { backups.Create("phase9-mixed"); backupMilliseconds = backupTimer.Elapsed.TotalMilliseconds; }
            catch (Exception exception) { backupError = exception.GetType().Name + ": " + exception.Message; }
            backupEndedAt = elapsed.Elapsed.TotalSeconds;
        });
        var cashier = new List<(double Seconds, double Scan, double Commit, string? Error)>();
        async Task<(double Scan, double Commit)> Cashier()
        {
            long scanStarted = Stopwatch.GetTimestamp();
            long sale = await sales.CreateDraftAsync(_clock.BusinessDate);
            await sales.AddProductAsync(sale, hot, Quantity.One);
            double scan = Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds;
            Money total = (await sales.GetAsync(sale)).Totals.GrandTotal;
            long commitStarted = Stopwatch.GetTimestamp();
            await sales.CompleteAsync(new(sale, [new(PaymentMode.Cash, total)], OperationGuard.NewId()));
            return (scan, Stopwatch.GetElapsedTime(commitStarted).TotalMilliseconds);
        }
        for (int index = 0; index < 300; index++)
        {
            double at = elapsed.Elapsed.TotalSeconds;
            try
            {
                (double scan, double commit) = await Cashier();
                cashier.Add((at, scan, commit, null));
                if (index % 15 == 7) _mixed.Add(("cashier-void", await TimeAsync(() => operations["pos-void"]()), null));
                if (index % 15 == 11) _mixed.Add(("cashier-return", await TimeAsync(() => operations["pos-partial-return"]()), null));
            }
            catch (Exception exception) { cashier.Add((at, 0, 0, exception.GetType().Name + ": " + exception.Message)); }
            await Task.Delay(100);
        }
        await backup;
        stop.Cancel();
        await Task.WhenAll(background.Append(sampler));
        listener.Dispose();

        static async Task<double> TimeAsync(Func<Task<int>> run)
        {
            long started = Stopwatch.GetTimestamp();
            await run();
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        object Summary(IEnumerable<double> values)
        {
            double[] sorted = [.. values.Order()];
            return sorted.Length == 0 ? new { samples = 0 } : new
            {
                samples = sorted.Length, p50 = Percentile(sorted, .5), p95 = Percentile(sorted, .95),
                p99 = Percentile(sorted, .99), max = sorted[^1],
            };
        }
        var bills = cashier.Where(static row => row.Error is null).ToArray();
        return new
        {
            bills = new
            {
                completed = bills.Length,
                errors = cashier.Where(static row => row.Error is not null).Select(static row => row.Error).ToArray(),
                scanToLine = Summary(bills.Select(static row => row.Scan)),
                commit = Summary(bills.Select(static row => row.Commit)),
                commitDuringBackup = Summary(bills.Where(row => row.Seconds >= backupStartedAt && row.Seconds <= backupEndedAt)
                    .Select(static row => row.Commit)),
            },
            backup = new { milliseconds = backupMilliseconds, error = backupError, startedAtSeconds = backupStartedAt, endedAtSeconds = backupEndedAt },
            operations = _mixed.GroupBy(static row => row.Name).OrderBy(static group => group.Key).Select(group => new
            {
                name = group.Key,
                timing = Summary(group.Where(static row => row.Error is null).Select(static row => row.Milliseconds)),
                errors = group.Where(static row => row.Error is not null).Select(static row => row.Error).Distinct().ToArray(),
            }),
            writerWait = Summary(_waits.Where(static row => row.Name == "db.write.wait").Select(static row => row.Value)),
            readQueue = Summary(_waits.Where(static row => row.Name == "db.read.queue").Select(static row => row.Value)),
            walPeakBytes = _resources.IsEmpty ? 0 : _resources.Max(static row => row.WalBytes),
            workingSetPeakBytes = _resources.IsEmpty ? 0 : _resources.Max(static row => row.WorkingSetBytes),
            seconds = elapsed.Elapsed.TotalSeconds,
        };
    }

    // Plans every traced statement and re-runs each read-only one through the raw statement API for
    // its rows returned and virtual-machine, full-scan, sort and automatic-index counters.
    private List<StatementProfile> ProfileStatements()
    {
        var results = new List<StatementProfile>();
        using SqliteConnection connection = factory.Open();
        foreach (((string profile, string operation, string sql), _) in _traced
                     .OrderBy(static item => item.Key.Profile, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Operation, StringComparer.Ordinal))
        {
            // Expanded SQL can print a negated negative parameter as "--1", which parses as a comment.
            string planSql = System.Text.RegularExpressions.Regex.Replace(sql, @"--([0-9]+)\b", "-(-$1)");
            bool read = planSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                        planSql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
            string[] plan;
            try
            {
                plan = connection.Query("EXPLAIN QUERY PLAN " + planSql)
                    .Select(static row => Convert.ToString(((IDictionary<string, object>)row)["detail"], CultureInfo.InvariantCulture) ?? "")
                    .ToArray();
            }
            catch (SqliteException exception) { plan = ["(not planned: " + exception.Message + ")"]; }
            long rows = 0, steps = 0, fullScanSteps = 0, sorts = 0, autoIndexes = 0;
            double milliseconds = 0;
            if (read)
            {
                long started = Stopwatch.GetTimestamp();
                if (raw.sqlite3_prepare_v2(connection.Handle, planSql, out sqlite3_stmt statement) == raw.SQLITE_OK && statement is not null)
                {
                    using (statement)
                    {
                        while (raw.sqlite3_step(statement) == raw.SQLITE_ROW) rows++;
                        fullScanSteps = raw.sqlite3_stmt_status(statement, raw.SQLITE_STMTSTATUS_FULLSCAN_STEP, 0);
                        sorts = raw.sqlite3_stmt_status(statement, raw.SQLITE_STMTSTATUS_SORT, 0);
                        autoIndexes = raw.sqlite3_stmt_status(statement, raw.SQLITE_STMTSTATUS_AUTOINDEX, 0);
                        steps = raw.sqlite3_stmt_status(statement, raw.SQLITE_STMTSTATUS_VM_STEP, 0);
                    }
                }
                milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            results.Add(new StatementProfile(profile, operation, read, sql.Length > 1500 ? sql[..1500] + " …" : sql, plan,
                plan.Any(static detail => detail.StartsWith("SCAN ", StringComparison.Ordinal) &&
                    !detail.StartsWith("SCAN CONSTANT", StringComparison.Ordinal)),
                rows, steps, fullScanSteps, sorts, autoIndexes, milliseconds));
        }
        return results;
    }

    private static double Percentile(double[] sorted, double fraction) => sorted.Length == 0 ? 0 :
        sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];

    private sealed record Fixture(long HotProduct, long DamageLot, long DamageProduct, long Vendor, long BusiestProduct,
        string Sku, string Barcode, string NameWord, string LocalWord, long TaxRate);

    internal sealed record OperationProfile(string Name, int Samples, double P50, double P95, double P99, double Max,
        int Rows, long AllocatedBytesPerCall, double GcPauseMilliseconds, int Gen0Collections, int Gen1Collections,
        int Gen2Collections);

    internal sealed record StatementProfile(string Profile, string Operation, bool ReadOnly, string Sql, IReadOnlyList<string> Plan,
        bool HasScan, long Rows, long VirtualMachineSteps, long FullScanSteps, long Sorts, long AutomaticIndexes,
        double Milliseconds);

}
