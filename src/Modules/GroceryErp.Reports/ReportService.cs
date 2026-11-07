using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Localization;

namespace GroceryErp.Reports;

public sealed partial class ReportService(DbExecutor database, ISettings settings, IClock clock, TranslationSource? translations = null)
{
    private DateOnly? _ensuredTopWindowDate;
    private static readonly string[] LocalizedCodeKeys = ["Status", "Direction", "PaymentMode", "Class", "Source"];
    public DateRange ResolvePeriod(PnlPeriodKind kind, DateOnly? customFrom = null, DateOnly? customTo = null)
    {
        DateOnly today = clock.BusinessDate;
        DateOnly monthStart = new(today.Year, today.Month, 1);
        return kind switch
        {
            PnlPeriodKind.ThisMonth => new(monthStart, today),
            PnlPeriodKind.LastMonth => new(monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            PnlPeriodKind.ThisFiscalYear => Fiscal(today, 0),
            PnlPeriodKind.LastFiscalYear => Fiscal(today, -1),
            PnlPeriodKind.Custom when customFrom is not null && customTo is not null => new(customFrom.Value, customTo.Value),
            _ => throw new DomainException(ErrorCode.Validation, "A valid custom date range is required."),
        };
    }

    // Each tile counts exactly what the list it opens shows (ExpiryAlert). Expired counts every
    // expired lot with stock, blocked or not and of any product, from the expiry index alone; its tile
    // opens Products at any status. Critical and Soon count the expiring lots of active products, as
    // the Products list they open does. Stock value is every lot's cost, archived products included,
    // as the current unsold stock report it opens totals it; low stock stays with active products, as
    // its list does.
    private static readonly string DashboardCoreSql = $"""
        WITH today_sales AS MATERIALIZED (
          SELECT sales,bills,taxable,taxable-cost AS gross_profit
          FROM pnl_day WHERE day=@today
        ), expiry AS MATERIALIZED (
          SELECT (SELECT COUNT(*) FROM product_lot x
                  WHERE {ExpiryAlert.ExpiredSql("x", "@today")} AND x.expiry_date IS NOT NULL) AS expired,
                 COUNT(*) FILTER (WHERE e.expiry_date<={ExpiryAlert.CriticalEndSql("@today")}) AS critical,
                 COUNT(*) FILTER (WHERE e.expiry_date>{ExpiryAlert.CriticalEndSql("@today")}) AS soon
          FROM ({ExpiryAlert.ExpiringLotsSql("@today", activeProductsOnly: true)}) e
        ), stock AS MATERIALIZED (
          SELECT (SELECT COUNT(*) FROM product_stock_total t JOIN product p ON p.id=t.product_id
                  WHERE t.is_low=1 AND p.is_active=1) AS low_products,
                 (SELECT COALESCE(SUM(cost_value),0) FROM product_stock_total) AS stock_value
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
        """;

    public async Task<DashboardSummary> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        DateOnly today = clock.BusinessDate;
        await EnsureTopWindowAsync(today, cancellationToken).ConfigureAwait(false);
        DateOnly monthStart = new(today.Year, today.Month, 1);
        DateOnly previousStart = monthStart.AddMonths(-1);
        DateOnly previousThrough = previousStart.AddDays(Math.Min(today.Day, DateTime.DaysInMonth(previousStart.Year, previousStart.Month)) - 1);
        DateOnly trendFrom = today.AddDays(-29);
        var snapshot = await database.ReadAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            long stageStarted = Stopwatch.GetTimestamp();
            DashboardCore core = connection.QuerySingle<DashboardCore>(DashboardCoreSql, new { today = Iso(today), monthStart = Iso(monthStart), previousStart = Iso(previousStart), previousThrough = Iso(previousThrough) }, transaction);
            PerformanceMetrics.ReportStageMilliseconds.Record(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", "dashboard.core"));
            cancellationToken.ThrowIfCancellationRequested();
            stageStarted = Stopwatch.GetTimestamp();
            List<PaymentRaw> payments = connection.Query<PaymentRaw>("""
            SELECT sp.mode AS Mode,SUM(sp.amount) AS Amount FROM sale_payment sp
            JOIN sale s ON s.id=sp.sale_id WHERE s.status='COMPLETED' AND s.sale_date=@today GROUP BY sp.mode ORDER BY sp.mode
            """, new { today = Iso(today) }, transaction).ToList();
            PerformanceMetrics.ReportStageMilliseconds.Record(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", "dashboard.payments"));
            cancellationToken.ThrowIfCancellationRequested();
            stageStarted = Stopwatch.GetTimestamp();
            List<TrendRaw> trend = connection.Query<TrendRaw>("""
            SELECT day AS Date,sales AS Sales,bills AS Bills,taxable-cost AS GrossProfit
            FROM pnl_day WHERE day BETWEEN @from AND @today AND bills>0 ORDER BY day
            """, new { from = Iso(trendFrom), today = Iso(today) }, transaction).ToList();
            PerformanceMetrics.ReportStageMilliseconds.Record(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", "dashboard.trend"));
            cancellationToken.ThrowIfCancellationRequested();
            stageStarted = Stopwatch.GetTimestamp();
            string? cachedThrough = connection.ExecuteScalar<string>(
                "SELECT to_day FROM dashboard_top_window WHERE id=1", transaction: transaction);
            const string sourceSql = """
                WITH movements AS (
                  SELECT sl.product_id,sl.product_name,sl.product_name_local,sl.qty,
                         sl.taxable_amount,sl.cost_amount
                  FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                  WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @from AND @today
                  UNION ALL
                  SELECT sl.product_id,sl.product_name,sl.product_name_local,-rl.qty,
                         -rl.taxable_amount,-rl.cost_amount
                  FROM sale_return_line rl JOIN sale_return r ON r.id=rl.sale_return_id
                  JOIN sale_line sl ON sl.id=rl.sale_line_id WHERE r.return_date BETWEEN @from AND @today
                )
                , totals AS (
                  SELECT product_id AS ProductId,MAX(product_name) AS Name,MAX(product_name_local) AS LocalName,
                         SUM(qty) AS Quantity,SUM(taxable_amount) AS Revenue,SUM(cost_amount) AS Cost,
                         SUM(taxable_amount-cost_amount) AS Margin
                  FROM movements GROUP BY product_id
                ), ranked AS (
                  SELECT *,ROW_NUMBER() OVER (ORDER BY Revenue DESC,ProductId) AS RevenueRank,
                           ROW_NUMBER() OVER (ORDER BY Margin DESC,ProductId) AS MarginRank
                  FROM totals
                )
                SELECT * FROM ranked WHERE RevenueRank<=10 OR MarginRank<=10
                """;
            const string summarySql = """
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
                """;
            List<TopRaw> all = connection.Query<TopRaw>(
                cachedThrough == Iso(today) ? summarySql : sourceSql,
                new { from = Iso(trendFrom), today = Iso(today) }, transaction).ToList();
            var top = (Revenue: all.Where(static x => x.RevenueRank <= 10).OrderBy(static x => x.RevenueRank).ToList(),
                Margin: all.Where(static x => x.MarginRank <= 10).OrderBy(static x => x.MarginRank).ToList());
            PerformanceMetrics.ReportStageMilliseconds.Record(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", "dashboard.top-products"));
            transaction.Commit();
            return (Core: core, Payments: payments, Trend: trend, Top: top);
        }, cancellationToken, "dashboard.snapshot").ConfigureAwait(false);
        PerformanceMetrics.ReportStageRows.Record(1, new KeyValuePair<string, object?>("operation", "dashboard.core"));
        PerformanceMetrics.ReportStageRows.Record(snapshot.Payments.Count,
            new KeyValuePair<string, object?>("operation", "dashboard.payments"));
        PerformanceMetrics.ReportStageRows.Record(snapshot.Trend.Count,
            new KeyValuePair<string, object?>("operation", "dashboard.trend"));
        PerformanceMetrics.ReportStageRows.Record(snapshot.Top.Revenue.Count + snapshot.Top.Margin.Count,
            new KeyValuePair<string, object?>("operation", "dashboard.top-products"));
        long composing = Stopwatch.GetTimestamp();
        DashboardCore c = snapshot.Core;
        DashboardSummary summary = new(today, Money.FromScaled(c.TodaySales), Money.FromScaled(c.TodayTaxableSales), c.TodayBills,
            c.TodayBills == 0 ? Money.Zero : new Money(Fx3.FromScaled(c.TodaySales) / c.TodayBills), Money.FromScaled(c.TodayGrossProfit),
            Money.FromScaled(c.MonthSales), Money.FromScaled(c.PreviousMonthSales),
            snapshot.Payments.Select(static x => new PaymentSplit(x.Mode, Money.FromScaled(x.Amount))).ToList(),
            snapshot.Trend.Select(static x => new TrendPoint(x.Date, Money.FromScaled(x.Sales), x.Bills, Money.FromScaled(x.GrossProfit))).ToList(),
            snapshot.Top.Revenue.Select(ToTop).ToList(), snapshot.Top.Margin.Select(ToTop).ToList(),
            c.ExpiredLots, c.CriticalLots, c.SoonLots, c.LowStockProducts, Money.FromScaled(c.StockValue),
            Money.FromScaled(c.Payables), LatestBackup(), settings.Current.LastUsbExport);
        PerformanceMetrics.ReportStageMilliseconds.Record(Stopwatch.GetElapsedTime(composing).TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", "dashboard.compose"));
        return summary;
    }

    private async Task EnsureTopWindowAsync(DateOnly today, CancellationToken cancellationToken)
    {
        if (_ensuredTopWindowDate == today) return;
        await database.WriteAsync(transaction =>
        {
            string? cachedThrough = transaction.Connection.ExecuteScalar<string>(
                "SELECT to_day FROM dashboard_top_window WHERE id=1", transaction: transaction.Transaction);
            if (cachedThrough != Iso(today))
                DashboardTopProjection.Rebuild(transaction.Connection, transaction.Transaction, today);
        }, cancellationToken, "dashboard.top-window").ConfigureAwait(false);
        _ensuredTopWindowDate = today;
    }

    public async Task<TabularReport> GetReportAsync(ReportKind kind, ReportFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        TabularReport report = await GetReportCoreAsync(kind, filter, cancellationToken).ConfigureAwait(false);
        string[] filters = FilterParts(kind, filter);
        return filters.Length == 0 ? report : report with { Filters = string.Join(" · ", filters) };
    }

    private Task<TabularReport> GetReportCoreAsync(ReportKind kind, ReportFilter filter, CancellationToken cancellationToken)
    {
        if (filter.Limit is < 1 or > 50000)
            throw new DomainException(ErrorCode.Validation, "Invalid report filter.");
        DateOnly to = filter.To ?? clock.BusinessDate;
        DateOnly from = filter.From ?? to.AddDays(-29);
        DateRange period = new(from, to);
        if (kind == ReportKind.StockReconciliation)
            return GetStockReconciliationAsync(filter, cancellationToken);
        ReportDefinition definition = Definition(kind);
        return database.ReadAsync(connection =>
        {
            // A long period groups millions of sale lines; their sort must not live in the app's memory.
            using IDisposable spill = LargeSort.SpillToDisk(connection);
            string sql = SelectReportSql(kind, filter, definition.Sql);
            string deltaFrom = Iso(from);
            string deltaTo = Iso(from);
            int deltaSign = 1;
            if (kind == ReportKind.ItemSales && filter.ProductId is null)
            {
                DashboardWindowRaw? window = connection.QuerySingleOrDefault<DashboardWindowRaw>("""
                    SELECT from_day AS FromDay,to_day AS ToDay FROM dashboard_top_window WHERE id=1
                    """);
                if (window?.ToDay == Iso(to) &&
                    DateOnly.TryParseExact(window.FromDay, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateOnly windowFrom) &&
                    Math.Abs(windowFrom.DayNumber - from.DayNumber) <= 7)
                {
                    sql = ReportDefinitions.ItemSalesFromTopWindow;
                    deltaFrom = Iso(windowFrom < from ? windowFrom : from);
                    deltaTo = Iso((windowFrom > from ? windowFrom : from).AddDays(-1));
                    deltaSign = windowFrom < from ? -1 : 1;
                }
            }
            IEnumerable<dynamic> queried = connection.Query(sql, new
            {
                from = Iso(from),
                to = Iso(to),
                deltaFrom,
                deltaTo,
                deltaSign,
                filter.ProductId,
                filter.VendorId,
                filter.Status,
                filter.PaymentMode,
                stockAsOf = Iso(clock.BusinessDate),
                Limit = filter.Limit + 1
            });
            List<ReportRow> rows = queried.Select(static row => new ReportRow(
                new Dictionary<string, object?>((IDictionary<string, object?>)row, StringComparer.OrdinalIgnoreCase))).ToList();
            bool truncated = rows.Count > filter.Limit;
            if (truncated) rows.RemoveAt(rows.Count - 1);
            Dictionary<string, decimal> totals = definition.Columns.Where(static c => c.Kind is ReportValueKind.Money or ReportValueKind.Quantity)
                .ToDictionary(static c => c.Key, c => rows.Sum(row => ScaledDecimal(row.Values.GetValueOrDefault(c.Key))));
            if (kind == ReportKind.ItemSales) rows = AddCategorySubtotals(rows);
            if (translations is not null) rows = rows.Select(LocalizeCodes).ToList();
            IReadOnlyList<ReportColumn> columns = definition.Columns.Select(column =>
                column with { Heading = Text($"Reports_Column_{column.Key}", column.Heading) }).ToList();
            return new TabularReport(Text($"Reports_Kind_{kind}", definition.Title),
                definition.UsesPeriod ? period : null, columns, rows, totals, truncated,
                kind == ReportKind.StockValue ? ExpiredStockSummary(connection, filter.IncludeExpired) : null);
        }, cancellationToken);
    }

    public async Task<long> ExportCsvAsync(ReportKind kind, ReportFilter filter, string path,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        DateOnly to = filter.To ?? clock.BusinessDate;
        DateOnly from = filter.From ?? to.AddDays(-29);
        DateRange period = new(from, to);
        string fullPath = Path.GetFullPath(path);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            long exported;
            if (kind == ReportKind.StockReconciliation)
            {
                exported = await ExportStockReconciliationCsvAsync(filter, temporary, progress,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ReportDefinition definition = Definition(kind);
                exported = await database.ReadAsync(connection =>
                {
                    using IDisposable spill = LargeSort.SpillToDisk(connection);
                    using var writer = new StreamWriter(temporary, false, new UTF8Encoding(true), 64 * 1024);
                    writer.WriteLine(CsvContext(kind, filter, period));
                    writer.WriteLine(string.Join(',', definition.Columns.Select(column => CsvText(
                        Text($"Reports_Column_{column.Key}", column.Heading)))));
                    // Same columns as the on-screen totals, summed over every streamed row.
                    Dictionary<string, decimal> totals = definition.Columns
                        .Where(static column => column.Kind is ReportValueKind.Money or ReportValueKind.Quantity)
                        .ToDictionary(static column => column.Key, static _ => 0m);
                    var parameters = new
                    {
                        from = Iso(from),
                        to = Iso(to),
                        filter.ProductId,
                        filter.VendorId,
                        filter.Status,
                        filter.PaymentMode,
                        stockAsOf = Iso(clock.BusinessDate),
                        Limit = int.MaxValue,
                    };
                    long count = 0;
                    string sql = SelectReportSql(kind, filter, definition.Sql);
                    var command = new CommandDefinition(sql, parameters, commandTimeout: 300,
                        flags: CommandFlags.None, cancellationToken: cancellationToken);
                    foreach (dynamic row in connection.Query<dynamic>(command))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var values = (IDictionary<string, object?>)row;
                        writer.WriteLine(string.Join(',', definition.Columns.Select(column =>
                        {
                            values.TryGetValue(column.Key, out object? value);
                            if (totals.ContainsKey(column.Key)) totals[column.Key] += ScaledDecimal(value);
                            return CsvValue(value, column.Kind);
                        })));
                        count++;
                        if (count % 1000 == 0) progress?.Report(count);
                    }
                    if (totals.Count > 0)
                        writer.WriteLine(string.Join(',', definition.Columns.Select((column, index) => index == 0
                            ? CsvText(Text("Reports_Column_Total", "Total"))
                            : totals.TryGetValue(column.Key, out decimal total)
                                ? total.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty)));
                    return count;
                }, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
            progress?.Report(exported);
            return exported;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string CsvValue(object? value, ReportValueKind kind) => kind switch
    {
        ReportValueKind.Money or ReportValueKind.Quantity or ReportValueKind.Percent =>
            ScaledDecimal(value).ToString("0.###", CultureInfo.InvariantCulture),
        ReportValueKind.Count => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
        _ => CsvText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    // The first CSV line names the report, its period and every applied filter, so a saved file
    // always says which version of the data it holds.
    private string CsvContext(ReportKind kind, ReportFilter filter, DateRange? period)
    {
        var parts = new List<string> { Text($"Reports_Kind_{kind}", kind.ToString()) };
        if (period is { } range && Definition(kind).UsesPeriod)
            parts.Add($"{range.From:yyyy-MM-dd} – {range.To:yyyy-MM-dd}");
        parts.AddRange(FilterParts(kind, filter));
        return CsvText(string.Join(" · ", parts));
    }

    // The applied filters in words; the CSV context line, the Excel sheet and the A4 header share them.
    private string[] FilterParts(ReportKind kind, ReportFilter filter)
    {
        var parts = new List<string>();
        void Add(string key, string fallback, object? value)
        {
            if (value is not null && value.ToString() is { Length: > 0 } text)
                parts.Add($"{Text(key, fallback)}: {Convert.ToString(text, CultureInfo.InvariantCulture)}");
        }
        // Names and words, not the stored IDs and codes.
        Add("Reports_FilterProduct", "Product", filter.ProductName ?? (object?)filter.ProductId);
        Add("Reports_FilterVendor", "Vendor", filter.VendorName ?? (object?)filter.VendorId);
        Add("Reports_Status", "Status", filter.Status is null ? null : TranslateCode(filter.Status));
        Add("Reports_Payment", "Payment", filter.PaymentMode is null ? null : TranslateCode(filter.PaymentMode));
        if (kind == ReportKind.StockValue && !filter.IncludeExpired)
            parts.Add(Text("Reports_ExcludesExpired", "Expired lots excluded"));
        if (kind == ReportKind.StockReconciliation && filter.MismatchesOnly)
            parts.Add(Text("Reports_MismatchesOnly", "Differences only"));
        return [.. parts];
    }

    private static string CsvText(string value)
    {
        if (value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) value = "'" + value;
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static List<string> MonthKeys(DateRange period)
    {
        var months = new List<string>();
        DateOnly cursor = new(period.From.Year, period.From.Month, 1);
        DateOnly last = new(period.To.Year, period.To.Month, 1);
        while (cursor <= last)
        {
            months.Add(cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            cursor = cursor.AddMonths(1);
        }
        return months;
    }

    private List<ReportRow> AddCategorySubtotals(List<ReportRow> rows)
    {
        var result = new List<ReportRow>(rows.Count + rows.Count / 4);
        foreach (IGrouping<string, ReportRow> group in rows.GroupBy(row => Convert.ToString(row.Values.GetValueOrDefault("Category"), CultureInfo.InvariantCulture) ?? ""))
        {
            result.AddRange(group);
            result.Add(new ReportRow(new Dictionary<string, object?>
            {
                ["Category"] = group.Key,
                ["Product"] = Text("Reports_Subtotal", "Subtotal"),
                ["Quantity"] = group.Sum(row => Convert.ToInt64(row.Values.GetValueOrDefault("Quantity") ?? 0, CultureInfo.InvariantCulture)),
                ["Revenue"] = group.Sum(row => Convert.ToInt64(row.Values.GetValueOrDefault("Revenue") ?? 0, CultureInfo.InvariantCulture)),
                ["Cost"] = group.Sum(row => Convert.ToInt64(row.Values.GetValueOrDefault("Cost") ?? 0, CultureInfo.InvariantCulture)),
                ["Margin"] = group.Sum(row => Convert.ToInt64(row.Values.GetValueOrDefault("Margin") ?? 0, CultureInfo.InvariantCulture)),
            }));
        }
        return result;
    }

    private ReportRow LocalizeCodes(ReportRow row)
    {
        var values = new Dictionary<string, object?>(row.Values, StringComparer.OrdinalIgnoreCase);
        foreach (string key in LocalizedCodeKeys)
        {
            if (values.GetValueOrDefault(key) is not string code || code.Length == 0) continue;
            values[key] = key == "PaymentMode"
                ? string.Join(", ", code.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(TranslateCode))
                : TranslateCode(code);
        }
        return new ReportRow(values);
    }

    private string TranslateCode(string code) => code switch
    {
        "COMPLETED" => Text("Reports_StatusCompleted", code),
        "VOID" => Text("Reports_StatusVoid", code),
        "OUTWARD" => Text("Reports_Value_OUTWARD", code),
        "INWARD" => Text("Reports_Value_INWARD", code),
        "CASH" => Text("Reports_PaymentCash", code),
        "UPI" => Text("Reports_PaymentUpi", code),
        "CARD" => Text("Reports_PaymentCard", code),
        "OTHER" => Text("Reports_PaymentOther", code),
        "SALEABLE" => Text("Catalog_Saleable", code),
        "INTERNAL" => Text("Catalog_InternalUse", code),
        "PURCHASE_LINE" => Text("Stock_SourceDoc_Purchase", code),
        "DIRECT" => Text("Reports_Value_DIRECT", code),
        "CUSTOMER_RETURN" => Text("MoneyOut_ReasonReturn", code),
        _ => code,
    };

    private DateRange Fiscal(DateOnly today, int yearOffset)
    {
        int startYear = FiscalYear.StartYear(today, settings.Current.FyStartMonth) + yearOffset;
        (DateOnly from, DateOnly to) = FiscalYear.Range(startYear, settings.Current.FyStartMonth);
        return yearOffset == 0 ? new(from, today) : new(from, to);
    }

    private static TopProduct ToTop(TopRaw x) => new(x.ProductId, x.Name, x.LocalName, Quantity.FromScaled(x.Quantity),
        Money.FromScaled(x.Revenue), Money.FromScaled(x.Cost), Money.FromScaled(x.Margin));
    private static decimal ScaledDecimal(object? value) => value switch
    {
        long x => Fx3.FromScaled(x),
        int x => Fx3.FromScaled(x),
        decimal x => x,
        _ => 0m
    };
    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private string Pick(string name, string? localName) =>
        translations?.UiCulture.Name == "gu-IN" && !string.IsNullOrWhiteSpace(localName) ? localName : name;
    private string Text(string key, string fallback) => translations is null ? fallback : translations[key];
    private string MonthLabel(string month)
    {
        DateOnly date = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        CultureInfo culture = translations?.UiCulture ?? CultureInfo.GetCultureInfo("en-IN");
        return $"{culture.DateTimeFormat.GetAbbreviatedMonthName(date.Month)} {date.Year.ToString(CultureInfo.InvariantCulture)}";
    }
    private string? LatestBackup()
    {
        string directory = settings.Current.BackupDirectory;
        if (!Directory.Exists(directory)) return null;
        FileInfo? latest = new DirectoryInfo(directory).EnumerateFiles("GroceryErp_*.zip")
            .OrderByDescending(static file => file.LastWriteTimeUtc).FirstOrDefault();
        return latest?.LastWriteTime.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    private static string SelectReportSql(ReportKind kind, ReportFilter filter, string defaultSql) =>
        (kind, filter.ProductId) switch
        {
            (ReportKind.StockValue, _) when !filter.IncludeExpired => ReportDefinitions.StockValueWithoutExpired,
            (ReportKind.ItemSales, not null) => ReportDefinitions.ItemSalesForProduct,
            (ReportKind.GstSummary, _) => ReportDefinitions.GstSummaryFromDay,
            _ => defaultSql,
        };

    private static ReportDefinition Definition(ReportKind kind) => ReportDefinitions.All[kind];

    private sealed class DashboardCore { public long TodaySales { get; init; } public long TodayTaxableSales { get; init; } public int TodayBills { get; init; } public long TodayGrossProfit { get; init; } public long MonthSales { get; init; } public long PreviousMonthSales { get; init; } public int ExpiredLots { get; init; } public int CriticalLots { get; init; } public int SoonLots { get; init; } public int LowStockProducts { get; init; } public long StockValue { get; init; } public long Payables { get; init; } }
    private sealed class DashboardWindowRaw { public string FromDay { get; init; } = ""; public string ToDay { get; init; } = ""; }
    private sealed class PaymentRaw { public string Mode { get; init; } = ""; public long Amount { get; init; } }
    private sealed class TrendRaw { public DateOnly Date { get; init; } public long Sales { get; init; } public int Bills { get; init; } public long GrossProfit { get; init; } }
    private sealed class TopRaw { public long ProductId { get; init; } public string Name { get; init; } = ""; public string? LocalName { get; init; } public long Quantity { get; init; } public long Revenue { get; init; } public long Cost { get; init; } public long Margin { get; init; } public int RevenueRank { get; init; } public int MarginRank { get; init; } }
    private sealed class NamedRow { public long Id { get; init; } public string Name { get; init; } = ""; public string? LocalName { get; init; } }
}

internal sealed record ReportDefinition(string Title, bool UsesPeriod, string Sql, IReadOnlyList<ReportColumn> Columns);
