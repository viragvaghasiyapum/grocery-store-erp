using System.Globalization;
using System.IO;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Localization;

namespace GroceryErp.Reports;

public sealed class ReportService(DbExecutor database, ISettings settings, IClock clock, TranslationSource? translations = null)
{
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

    public Task<PnlStatement> GetPnlAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        DateRange period = new(from, to);
        return database.ReadAsync(connection =>
        {
            List<PnlRaw> raw = connection.Query<PnlRaw>("""
                SELECT section AS Section, head_code AS HeadCode, ref_id AS ReferenceId, SUM(amount) AS Amount
                FROM v_pnl_entry
                WHERE entry_date BETWEEN @from AND @to
                GROUP BY section, head_code, ref_id
                ORDER BY CASE section WHEN 'INCOME' THEN 1 WHEN 'COGS' THEN 2 ELSE 3 END, head_code, ref_id
                """, new { from = Iso(from), to = Iso(to) }).ToList();
            Dictionary<long, string> categories = connection.Query<NamedRow>(
                "SELECT id AS Id,name AS Name,name_local AS LocalName FROM expense_category")
                .ToDictionary(static x => x.Id, x => Pick(x.Name, x.LocalName));
            return BuildStatement(period, raw, categories);
        }, cancellationToken);
    }

    public async Task<PnlComparison> GetPnlComparisonAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        DateRange current = new(from, to);
        int days = current.To.DayNumber - current.From.DayNumber + 1;
        DateOnly previousTo = current.From.AddDays(-1);
        DateOnly previousFrom = previousTo.AddDays(1 - days);
        Task<PnlStatement> currentTask = GetPnlAsync(current.From, current.To, cancellationToken);
        Task<PnlStatement> previousTask = GetPnlAsync(previousFrom, previousTo, cancellationToken);
        await Task.WhenAll(currentTask, previousTask).ConfigureAwait(false);
        return new(await currentTask, await previousTask);
    }

    public Task<PnlMatrix> GetPnlMatrixAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        DateRange period = new(from, to);
        return database.ReadAsync(connection =>
        {
            List<PnlMonthRaw> raw = connection.Query<PnlMonthRaw>("""
                SELECT section AS Section,head_code AS HeadCode,ref_id AS ReferenceId,
                       strftime('%Y-%m',entry_date) AS Month,SUM(amount) AS Amount
                FROM v_pnl_entry WHERE entry_date BETWEEN @from AND @to
                GROUP BY section,head_code,ref_id,strftime('%Y-%m',entry_date)
                ORDER BY section,head_code,ref_id,Month
                """, new { from = Iso(from), to = Iso(to) }).ToList();
            Dictionary<long, string> categories = connection.Query<NamedRow>(
                "SELECT id AS Id,name AS Name,name_local AS LocalName FROM expense_category")
                .ToDictionary(static x => x.Id, x => Pick(x.Name, x.LocalName));
            List<string> months = [];
            DateOnly cursor = new(from.Year, from.Month, 1);
            DateOnly last = new(to.Year, to.Month, 1);
            while (cursor <= last) { months.Add(cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture)); cursor = cursor.AddMonths(1); }
            List<PnlMatrixRow> rows = raw.GroupBy(static x => (x.Section, x.HeadCode, x.ReferenceId))
                .Select(group =>
                {
                    Dictionary<string, long> values = group.ToDictionary(static x => x.Month, static x => x.Amount);
                    List<Money> cells = months.Select(month => Money.FromScaled(values.GetValueOrDefault(month))).ToList();
                    return new PnlMatrixRow(group.Key.Section, group.Key.HeadCode, group.Key.ReferenceId,
                        Label(group.Key.HeadCode, group.Key.ReferenceId, categories), cells,
                        Money.FromScaled(cells.Sum(static x => x.Scaled)));
                }).ToList();
            return new PnlMatrix(period, months, rows);
        }, cancellationToken);
    }

    public async Task<DashboardSummary> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        DateOnly today = clock.BusinessDate;
        DateOnly monthStart = new(today.Year, today.Month, 1);
        DateOnly previousStart = monthStart.AddMonths(-1);
        DateOnly previousThrough = previousStart.AddDays(Math.Min(today.Day, DateTime.DaysInMonth(previousStart.Year, previousStart.Month)) - 1);
        DateOnly trendFrom = today.AddDays(-29);
        Task<DashboardCore> core = database.ReadAsync(connection => connection.QuerySingle<DashboardCore>("""
            SELECT
              COALESCE((SELECT sales FROM v_daily_sales WHERE sale_date=@today),0) AS TodaySales,
              COALESCE((SELECT bills FROM v_daily_sales WHERE sale_date=@today),0) AS TodayBills,
              COALESCE((SELECT gross_profit FROM v_daily_sales WHERE sale_date=@today),0) AS TodayGrossProfit,
              COALESCE((SELECT SUM(grand_total) FROM sale WHERE status='COMPLETED' AND sale_date BETWEEN @monthStart AND @today),0) AS MonthSales,
              COALESCE((SELECT SUM(grand_total) FROM sale WHERE status='COMPLETED' AND sale_date BETWEEN @previousStart AND @previousThrough),0) AS PreviousMonthSales,
              COALESCE((SELECT COUNT(*) FROM v_expiry_alert WHERE bucket='EXPIRED'),0) AS ExpiredLots,
              COALESCE((SELECT COUNT(*) FROM v_expiry_alert WHERE bucket='CRITICAL'),0) AS CriticalLots,
              COALESCE((SELECT COUNT(*) FROM v_expiry_alert WHERE bucket='SOON'),0) AS SoonLots,
              COALESCE((SELECT COUNT(*) FROM v_product_stock WHERE is_low=1),0) AS LowStockProducts,
              COALESCE((SELECT SUM(stock_value) FROM v_product_stock),0) AS StockValue,
              COALESCE((SELECT SUM(balance_due) FROM v_vendor_balance),0) AS Payables
            """, new { today = Iso(today), monthStart = Iso(monthStart), previousStart = Iso(previousStart), previousThrough = Iso(previousThrough) }), cancellationToken);
        Task<List<PaymentRaw>> payments = database.ReadAsync(connection => connection.Query<PaymentRaw>("""
            SELECT sp.mode AS Mode,SUM(sp.amount) AS Amount FROM sale_payment sp
            JOIN sale s ON s.id=sp.sale_id WHERE s.status='COMPLETED' AND s.sale_date=@today GROUP BY sp.mode ORDER BY sp.mode
            """, new { today = Iso(today) }).ToList(), cancellationToken);
        Task<List<TrendRaw>> trend = database.ReadAsync(connection => connection.Query<TrendRaw>("""
            SELECT sale_date AS Date,sales AS Sales,bills AS Bills,gross_profit AS GrossProfit
            FROM v_daily_sales WHERE sale_date BETWEEN @from AND @today ORDER BY sale_date
            """, new { from = Iso(trendFrom), today = Iso(today) }).ToList(), cancellationToken);
        Task<(List<TopRaw> Revenue, List<TopRaw> Margin)> top = database.ReadAsync(connection =>
        {
            List<TopRaw> all = connection.Query<TopRaw>("""
                SELECT sl.product_id AS ProductId,MAX(sl.product_name) AS Name,MAX(sl.product_name_local) AS LocalName,
                       SUM(sl.qty) AS Quantity,SUM(sl.taxable_amount) AS Revenue,SUM(sl.cost_amount) AS Cost,
                       SUM(sl.taxable_amount-sl.cost_amount) AS Margin
                FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                WHERE s.status='COMPLETED' AND s.sale_date BETWEEN @from AND @today
                GROUP BY sl.product_id
                """, new { from = Iso(trendFrom), today = Iso(today) }).ToList();
            return (all.OrderByDescending(static x => x.Revenue).Take(10).ToList(), all.OrderByDescending(static x => x.Margin).Take(10).ToList());
        }, cancellationToken);
        await Task.WhenAll(core, payments, trend, top).ConfigureAwait(false);
        DashboardCore c = await core;
        return new DashboardSummary(today, Money.FromScaled(c.TodaySales), c.TodayBills,
            c.TodayBills == 0 ? Money.Zero : new Money(Fx3.FromScaled(c.TodaySales) / c.TodayBills), Money.FromScaled(c.TodayGrossProfit),
            Money.FromScaled(c.MonthSales), Money.FromScaled(c.PreviousMonthSales),
            (await payments).Select(static x => new PaymentSplit(x.Mode, Money.FromScaled(x.Amount))).ToList(),
            (await trend).Select(static x => new TrendPoint(x.Date, Money.FromScaled(x.Sales), x.Bills, Money.FromScaled(x.GrossProfit))).ToList(),
            (await top).Revenue.Select(ToTop).ToList(), (await top).Margin.Select(ToTop).ToList(),
            c.ExpiredLots, c.CriticalLots, c.SoonLots, c.LowStockProducts, Money.FromScaled(c.StockValue),
            Money.FromScaled(c.Payables), LatestBackup(), settings.Current.LastUsbExport);
    }

    public Task<TabularReport> GetReportAsync(ReportKind kind, ReportFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Limit is < 1 or > 50000 || filter.SlowMoverDays is < 1 or > 3650)
            throw new DomainException(ErrorCode.Validation, "Invalid report filter.");
        DateOnly to = filter.To ?? clock.BusinessDate;
        DateOnly from = filter.From ?? to.AddDays(-29);
        DateRange period = new(from, to);
        if (kind == ReportKind.SalaryRegister)
            return GetSalaryMatrixAsync(period, cancellationToken);
        if (kind == ReportKind.AssetSchedule)
            return GetAssetScheduleAsync(filter.AssetId, period, cancellationToken);
        int days = period.To.DayNumber - period.From.DayNumber + 1;
        DateOnly previousTo = period.From.AddDays(-1);
        DateOnly previousFrom = previousTo.AddDays(1 - days);
        ReportDefinition definition = Definition(kind);
        return database.ReadAsync(connection =>
        {
            IEnumerable<dynamic> queried = connection.Query(definition.Sql, new
            {
                from = Iso(from),
                to = Iso(to),
                filter.ProductId,
                filter.LotId,
                filter.VendorId,
                filter.Status,
                filter.PaymentMode,
                filter.Entity,
                filter.Action,
                previousFrom = Iso(previousFrom),
                previousTo = Iso(previousTo),
                slowFrom = Iso(to.AddDays(-filter.SlowMoverDays)),
                filter.Limit
            });
            List<ReportRow> rows = queried.Select(static row => new ReportRow(
                new Dictionary<string, object?>((IDictionary<string, object?>)row, StringComparer.OrdinalIgnoreCase))).ToList();
            Dictionary<string, decimal> totals = definition.Columns.Where(static c => c.Kind is ReportValueKind.Money or ReportValueKind.Quantity)
                .ToDictionary(static c => c.Key, c => rows.Sum(row => ScaledDecimal(row.Values.GetValueOrDefault(c.Key))));
            if (kind == ReportKind.ItemSales) rows = AddCategorySubtotals(rows);
            return new TabularReport(definition.Title, definition.UsesPeriod ? period : null, definition.Columns, rows, totals);
        }, cancellationToken);
    }

    private Task<TabularReport> GetSalaryMatrixAsync(DateRange period, CancellationToken cancellationToken) =>
        database.ReadAsync(connection =>
        {
            List<string> months = MonthKeys(period);
            List<SalaryMatrixRaw> raw = connection.Query<SalaryMatrixRaw>("""
                SELECT e.id AS EmployeeId,e.name AS Employee,e.role AS Role,sp.period_month AS Month,SUM(sp.amount) AS Amount
                FROM salary_payment sp JOIN employee e ON e.id=sp.employee_id
                WHERE date(sp.period_month||'-01','+1 month','-1 day') BETWEEN @from AND @to
                GROUP BY e.id,sp.period_month ORDER BY e.name,sp.period_month
                """, new { from = Iso(period.From), to = Iso(period.To) }).ToList();
            var columns = new List<ReportColumn>
            {
                new("Employee", "Employee", ReportValueKind.Text),
                new("Role", "Role", ReportValueKind.Text),
            };
            columns.AddRange(months.Select(month => new ReportColumn(month, MonthLabel(month), ReportValueKind.Money)));
            columns.Add(new("Total", "Total", ReportValueKind.Money));
            List<ReportRow> rows = raw.GroupBy(static row => (row.EmployeeId, row.Employee, row.Role))
                .Select(group =>
                {
                    Dictionary<string, long> monthValues = group.ToDictionary(static row => row.Month, static row => row.Amount);
                    var values = new Dictionary<string, object?> { ["Employee"] = group.Key.Employee, ["Role"] = group.Key.Role };
                    foreach (string month in months) values[month] = monthValues.GetValueOrDefault(month);
                    values["Total"] = monthValues.Values.Sum();
                    return new ReportRow(values);
                }).ToList();
            Dictionary<string, decimal> totals = months.Append("Total")
                .ToDictionary(static month => month, month => rows.Sum(row => ScaledDecimal(row.Values.GetValueOrDefault(month))));
            return new TabularReport("Salary register", period, columns, rows, totals);
        }, cancellationToken);

    private Task<TabularReport> GetAssetScheduleAsync(long? assetId, DateRange period, CancellationToken cancellationToken) =>
        database.ReadAsync(connection =>
        {
            IReadOnlyList<ReportColumn> columns =
            [
                new("Period", "Period", ReportValueKind.Text),
                new("Status", "Status", ReportValueKind.Text),
                new("Opening", "Opening value", ReportValueKind.Money),
                new("Depreciation", "Depreciation", ReportValueKind.Money),
                new("Closing", "Closing value", ReportValueKind.Money),
            ];
            if (assetId is null) return new TabularReport("Asset depreciation schedule", period, columns, [], new Dictionary<string, decimal>());
            AssetRaw? asset = connection.QuerySingleOrDefault<AssetRaw>("""
                SELECT id AS Id,name AS Name,cost AS Cost,salvage_value AS Salvage,rate_pct AS Rate,
                       method AS Method,in_use_from AS InUseFrom,disposed_date AS DisposedDate
                FROM asset WHERE id=@assetId
                """, new { assetId });
            if (asset is null) return new TabularReport("Asset depreciation schedule", period, columns, [], new Dictionary<string, decimal>());
            List<AssetPeriodRaw> posted = connection.Query<AssetPeriodRaw>("""
                SELECT period_month AS Period,opening_value AS Opening,amount AS Amount,closing_value AS Closing
                FROM asset_depreciation WHERE asset_id=@assetId ORDER BY period_month
                """, new { assetId }).ToList();
            var schedule = posted.ToDictionary(static row => YearMonth.Parse(row.Period));
            var calculated = new List<AssetPeriodRaw>(posted);
            YearMonth cursor = posted.Count == 0 ? YearMonth.From(asset.InUseFrom) : YearMonth.Parse(posted[^1].Period).AddMonths(1);
            YearMonth target = YearMonth.From(period.To);
            if (asset.DisposedDate is { } disposed && YearMonth.From(disposed) < target) target = YearMonth.From(disposed);
            long opening = posted.Count == 0 ? asset.Cost : posted[^1].Closing;
            var info = new AssetDepreciationInfo(Money.FromScaled(asset.Cost), Money.FromScaled(asset.Salvage),
                Percent.FromScaled(asset.Rate), asset.Method == "WDV" ? DepreciationMethod.Wdv : DepreciationMethod.Slm,
                asset.InUseFrom, asset.DisposedDate);
            while (cursor <= target)
            {
                int fiscalStartYear = FiscalYear.StartYear(cursor.FirstDay, settings.Current.FyStartMonth);
                (DateOnly fiscalFrom, _) = FiscalYear.Range(fiscalStartYear, settings.Current.FyStartMonth);
                YearMonth fiscalMonth = YearMonth.From(fiscalFrom);
                long fiscalOpening = calculated.Where(row => YearMonth.Parse(row.Period) < fiscalMonth)
                    .Select(static row => row.Closing).LastOrDefault(asset.Cost);
                long charged = calculated.Where(row =>
                    YearMonth.Parse(row.Period) >= fiscalMonth && YearMonth.Parse(row.Period) < cursor).Sum(static row => row.Amount);
                Money charge = Depreciation.MonthlyCharge(info, cursor, Money.FromScaled(opening), Money.FromScaled(fiscalOpening),
                    Money.FromScaled(charged), settings.Current.FyStartMonth);
                long closing = opening - charge.Scaled;
                var projected = new AssetPeriodRaw { Period = cursor.ToString(), Opening = opening, Amount = charge.Scaled, Closing = closing };
                calculated.Add(projected);
                opening = closing;
                cursor = cursor.AddMonths(1);
            }
            List<ReportRow> rows = calculated.Where(row =>
                YearMonth.Parse(row.Period).LastDay >= period.From && YearMonth.Parse(row.Period).FirstDay <= period.To)
                .Select(row => new ReportRow(new Dictionary<string, object?>
                {
                    ["Period"] = row.Period,
                    ["Status"] = schedule.ContainsKey(YearMonth.Parse(row.Period)) ? "POSTED" : "PROJECTED",
                    ["Opening"] = row.Opening,
                    ["Depreciation"] = row.Amount,
                    ["Closing"] = row.Closing,
                })).ToList();
            Dictionary<string, decimal> totals = new() { ["Depreciation"] = rows.Sum(row => ScaledDecimal(row.Values["Depreciation"])) };
            return new TabularReport($"{asset.Name} — depreciation schedule", period, columns, rows, totals);
        }, cancellationToken);

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

    private static List<ReportRow> AddCategorySubtotals(List<ReportRow> rows)
    {
        var result = new List<ReportRow>(rows.Count + rows.Count / 4);
        foreach (IGrouping<string, ReportRow> group in rows.GroupBy(row => Convert.ToString(row.Values.GetValueOrDefault("Category"), CultureInfo.InvariantCulture) ?? ""))
        {
            result.AddRange(group);
            result.Add(new ReportRow(new Dictionary<string, object?>
            {
                ["Category"] = group.Key,
                ["Product"] = "Subtotal",
                ["Quantity"] = group.Sum(row => Convert.ToInt64(row.Values.GetValueOrDefault("Quantity") ?? 0, CultureInfo.InvariantCulture)),
                ["Revenue"] = group.Sum(row => Convert.ToInt64(row.Values.GetValueOrDefault("Revenue") ?? 0, CultureInfo.InvariantCulture)),
                ["Cost"] = group.Sum(row => Convert.ToInt64(row.Values.GetValueOrDefault("Cost") ?? 0, CultureInfo.InvariantCulture)),
                ["Margin"] = group.Sum(row => Convert.ToInt64(row.Values.GetValueOrDefault("Margin") ?? 0, CultureInfo.InvariantCulture)),
            }));
        }
        return result;
    }

    private DateRange Fiscal(DateOnly today, int yearOffset)
    {
        int startYear = FiscalYear.StartYear(today, settings.Current.FyStartMonth) + yearOffset;
        (DateOnly from, DateOnly to) = FiscalYear.Range(startYear, settings.Current.FyStartMonth);
        return yearOffset == 0 ? new(from, today) : new(from, to);
    }

    private PnlStatement BuildStatement(DateRange period, IReadOnlyList<PnlRaw> raw, IReadOnlyDictionary<long, string> categories)
    {
        List<PnlLine> lines = raw.Select(row => new PnlLine(row.Section, row.HeadCode, row.ReferenceId,
            Label(row.HeadCode, row.ReferenceId, categories), Money.FromScaled(row.Amount))).ToList();
        List<PnlLine> other = lines.Where(static x => x.HeadCode == "ASSET_DISPOSAL").ToList();
        List<PnlLine> income = lines.Where(static x => x.Section == "INCOME" && x.HeadCode != "ASSET_DISPOSAL").ToList();
        List<PnlLine> cogs = lines.Where(static x => x.Section == "COGS").ToList();
        List<PnlLine> expenses = lines.Where(static x => x.Section == "EXPENSE").ToList();
        Money netSales = Sum(income);
        Money gross = netSales - Sum(cogs);
        Money totalExpenses = Sum(expenses);
        Money net = gross - totalExpenses + Sum(other);
        return new(period, income, cogs, expenses, other, netSales, gross, Ratio(gross, netSales),
            totalExpenses, net, Ratio(net, netSales));
    }

    private static Money Sum(IEnumerable<PnlLine> lines) => Money.FromScaled(lines.Sum(static x => x.Amount.Scaled));
    private static Percent Ratio(Money amount, Money basis) => basis == Money.Zero ? Percent.Zero : new(amount.Value * 100m / basis.Value);
    private string Label(string code, long? referenceId, IReadOnlyDictionary<long, string> categories) => code switch
    {
        "SALES" => Text("Pnl_Head_Sales", "Sales (net of GST)"),
        "SALES_RETURNS" => Text("Pnl_Head_SalesReturns", "Sales returns"),
        "ROUND_OFF" => Text("Pnl_Head_RoundOff", "Round off"),
        "COGS" => Text("Pnl_Head_Cogs", "Cost of goods sold"),
        "DAMAGES" => Text("Pnl_Head_Damages", "Damages and expiry"),
        "STOCK_ADJUSTMENT" => Text("Pnl_Head_StockAdjustment", "Stock adjustments"),
        "SALARIES" => Text("Pnl_Head_Salaries", "Salaries"),
        "DEPRECIATION" => Text("Pnl_Head_Depreciation", "Depreciation"),
        "ASSET_DISPOSAL" => Text("Pnl_Head_AssetDisposal", "Asset disposal gain/(loss)"),
        "EXPENSE_CATEGORY" when referenceId is { } id => categories.GetValueOrDefault(id, $"Expense {id}"),
        _ => code
    };
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

    private static ReportDefinition Definition(ReportKind kind) => ReportDefinitions.All[kind];

    private class PnlRaw { public string Section { get; init; } = ""; public string HeadCode { get; init; } = ""; public long? ReferenceId { get; init; } public long Amount { get; init; } }
    private sealed class PnlMonthRaw : PnlRaw { public string Month { get; init; } = ""; }
    private sealed class DashboardCore { public long TodaySales { get; init; } public int TodayBills { get; init; } public long TodayGrossProfit { get; init; } public long MonthSales { get; init; } public long PreviousMonthSales { get; init; } public int ExpiredLots { get; init; } public int CriticalLots { get; init; } public int SoonLots { get; init; } public int LowStockProducts { get; init; } public long StockValue { get; init; } public long Payables { get; init; } }
    private sealed class PaymentRaw { public string Mode { get; init; } = ""; public long Amount { get; init; } }
    private sealed class TrendRaw { public DateOnly Date { get; init; } public long Sales { get; init; } public int Bills { get; init; } public long GrossProfit { get; init; } }
    private sealed class TopRaw { public long ProductId { get; init; } public string Name { get; init; } = ""; public string? LocalName { get; init; } public long Quantity { get; init; } public long Revenue { get; init; } public long Cost { get; init; } public long Margin { get; init; } }
    private sealed class NamedRow { public long Id { get; init; } public string Name { get; init; } = ""; public string? LocalName { get; init; } }
    private sealed class SalaryMatrixRaw { public long EmployeeId { get; init; } public string Employee { get; init; } = ""; public string? Role { get; init; } public string Month { get; init; } = ""; public long Amount { get; init; } }
    private sealed class AssetRaw { public long Id { get; init; } public string Name { get; init; } = ""; public long Cost { get; init; } public long Salvage { get; init; } public long Rate { get; init; } public string Method { get; init; } = ""; public DateOnly InUseFrom { get; init; } public DateOnly? DisposedDate { get; init; } }
    private sealed class AssetPeriodRaw { public string Period { get; init; } = ""; public long Opening { get; init; } public long Amount { get; init; } public long Closing { get; init; } }
}

internal sealed record ReportDefinition(string Title, bool UsesPeriod, string Sql, IReadOnlyList<ReportColumn> Columns);
