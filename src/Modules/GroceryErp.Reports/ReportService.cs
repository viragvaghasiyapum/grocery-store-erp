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
    private static readonly string[] LocalizedCodeKeys =
        ["Status", "Direction", "PaymentMode", "Method", "Weekday", "Type", "ReferenceType", "Reason", "Action", "Entity"];
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
            DateOnly lastCompleteMonth = to.Day == DateTime.DaysInMonth(to.Year, to.Month)
                ? to : to.AddMonths(-1);
            List<PnlRaw> raw = connection.Query<PnlRaw>("""
                WITH sale_totals AS MATERIALIZED (
                    SELECT COUNT(*) AS bills,SUM(taxable_total) AS taxable,
                           SUM(round_off) AS rounding,SUM(cost_total) AS cost
                    FROM sale WHERE status='COMPLETED' AND sale_date BETWEEN @from AND @to
                ), return_totals AS MATERIALIZED (
                    SELECT COUNT(*) AS returns,SUM(taxable_total) AS taxable,SUM(cost_total) AS cost
                    FROM sale_return WHERE return_date BETWEEN @from AND @to
                ), entries AS (
                    SELECT 'INCOME' AS section,'SALES' AS head_code,NULL AS ref_id,taxable AS amount
                    FROM sale_totals WHERE bills>0
                    UNION ALL SELECT 'INCOME','ROUND_OFF',NULL,rounding FROM sale_totals WHERE rounding<>0
                    UNION ALL SELECT 'COGS','COGS',NULL,cost FROM sale_totals WHERE bills>0
                    UNION ALL SELECT 'INCOME','SALES_RETURNS',NULL,-taxable FROM return_totals WHERE returns>0
                    UNION ALL SELECT 'COGS','COGS',NULL,-cost FROM return_totals WHERE returns>0
                    UNION ALL SELECT 'EXPENSE','DAMAGES',NULL,d.cost_value-d.recovered_amount
                        FROM damage d WHERE d.damage_date BETWEEN @from AND @to
                    UNION ALL SELECT 'EXPENSE','STOCK_ADJUSTMENT',NULL,
                        -CAST(ROUND(m.qty*m.unit_cost/1000.0) AS INTEGER)
                        FROM stock_movement m WHERE m.type_code='ADJUSTMENT' AND m.move_date BETWEEN @from AND @to
                    UNION ALL SELECT 'EXPENSE','EXPENSE_CATEGORY',e.category_id,e.amount
                        FROM expense e WHERE e.expense_date BETWEEN @from AND @to
                    UNION ALL SELECT 'EXPENSE','SALARIES',NULL,sp.amount
                        FROM salary_payment sp WHERE sp.period_month BETWEEN @firstMonth AND @lastCompleteMonth
                    UNION ALL SELECT 'EXPENSE','DEPRECIATION',NULL,ad.amount
                        FROM asset_depreciation ad WHERE ad.period_month BETWEEN @firstMonth AND @lastCompleteMonth
                    UNION ALL SELECT 'OTHER','ASSET_DISPOSAL',a.id,
                        a.disposal_value-COALESCE((SELECT ad.closing_value FROM asset_depreciation ad
                            WHERE ad.asset_id=a.id ORDER BY ad.period_month DESC LIMIT 1),a.cost)
                        FROM asset a WHERE a.disposed_date BETWEEN @from AND @to
                    UNION ALL SELECT 'OTHER','VENDOR_RETURN_VARIANCE',NULL,
                        vr.credit_before_tax+CASE WHEN vr.tax_mode='REGULAR' THEN 0 ELSE vr.tax_credit END-vr.cost_total
                        FROM vendor_return vr WHERE vr.return_date BETWEEN @from AND @to
                )
                SELECT section AS Section,head_code AS HeadCode,ref_id AS ReferenceId,SUM(amount) AS Amount
                FROM entries GROUP BY section,head_code,ref_id
                ORDER BY CASE section WHEN 'INCOME' THEN 1 WHEN 'COGS' THEN 2 ELSE 3 END,head_code,ref_id
                """, new { from = Iso(from), to = Iso(to),
                    firstMonth = from.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    lastCompleteMonth = lastCompleteMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture) }).ToList();
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
            List<PnlMatrixRow> income = rows.Where(static row => row.Section == "INCOME").ToList();
            List<PnlMatrixRow> cogs = rows.Where(static row => row.Section == "COGS").ToList();
            List<PnlMatrixRow> expenses = rows.Where(static row => row.Section == "EXPENSE").ToList();
            List<PnlMatrixRow> other = rows.Where(static row => row.Section == "OTHER").ToList();
            Money[] netSales = SumMonths(income, months.Count);
            Money[] grossProfit = SubtractMonths(netSales, SumMonths(cogs, months.Count));
            Money[] totalExpenses = SumMonths(expenses, months.Count);
            Money[] netProfit = AddMonths(SubtractMonths(grossProfit, totalExpenses), SumMonths(other, months.Count));
            List<PnlMatrixRow> ordered =
            [
                ..income,
                SummaryRow("NET_SALES", Text("Reports_Section_NetSales", "Net sales"), netSales),
                ..cogs,
                SummaryRow("GROSS_PROFIT", Text("Reports_Section_GrossProfit", "Gross profit"), grossProfit),
                ..expenses,
                SummaryRow("TOTAL_EXPENSES", Text("Reports_Section_TotalExpenses", "Total expenses"), totalExpenses),
                ..other,
                SummaryRow("NET_PROFIT", Text("Reports_Section_NetProfit", "Net profit"), netProfit),
            ];
            return new PnlMatrix(period, months, ordered);
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
              COALESCE((SELECT SUM(taxable_total) FROM sale WHERE status='COMPLETED' AND sale_date=@today),0) AS TodayTaxableSales,
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
                """, new { from = Iso(trendFrom), today = Iso(today) }).ToList();
            return (all.Where(static x => x.RevenueRank <= 10).OrderBy(static x => x.RevenueRank).ToList(),
                all.Where(static x => x.MarginRank <= 10).OrderBy(static x => x.MarginRank).ToList());
        }, cancellationToken);
        await Task.WhenAll(core, payments, trend, top).ConfigureAwait(false);
        DashboardCore c = await core;
        return new DashboardSummary(today, Money.FromScaled(c.TodaySales), Money.FromScaled(c.TodayTaxableSales), c.TodayBills,
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
            return GetSalaryMatrixAsync(period, Math.Min(filter.Limit, 500), cancellationToken);
        if (kind == ReportKind.AssetSchedule)
            return GetAssetScheduleAsync(filter.AssetId, period, Math.Min(filter.Limit, 500), cancellationToken);
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
                definition.UsesPeriod ? period : null, columns, rows, totals, truncated);
        }, cancellationToken);
    }

    private Task<TabularReport> GetSalaryMatrixAsync(DateRange period, int limit, CancellationToken cancellationToken) =>
        database.ReadAsync(connection =>
        {
            List<string> months = MonthKeys(period);
            if (months.Count > 24)
                throw new DomainException(ErrorCode.Validation, "Salary register ranges are limited to 24 months. Narrow the dates to view other periods.");
            string firstMonth = months[0];
            DateOnly lastCompleteMonth = period.To.Day == DateTime.DaysInMonth(period.To.Year, period.To.Month)
                ? period.To : period.To.AddMonths(-1);
            string lastMonth = lastCompleteMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            List<long> employeeIds = connection.Query<long>("""
                SELECT e.id FROM employee e JOIN salary_payment sp ON sp.employee_id=e.id
                WHERE sp.period_month BETWEEN @firstMonth AND @lastMonth
                GROUP BY e.id ORDER BY e.name COLLATE NOCASE,e.id LIMIT @take
                """, new { firstMonth, lastMonth, take = limit + 1 }).ToList();
            bool truncated = employeeIds.Count > limit;
            if (truncated) employeeIds.RemoveAt(limit);
            List<SalaryMatrixRaw> raw = connection.Query<SalaryMatrixRaw>("""
                SELECT e.id AS EmployeeId,e.name AS Employee,e.role AS Role,sp.period_month AS Month,SUM(sp.amount) AS Amount
                FROM salary_payment sp JOIN employee e ON e.id=sp.employee_id
                WHERE e.id IN @employeeIds AND sp.period_month BETWEEN @firstMonth AND @lastMonth
                GROUP BY e.id,sp.period_month ORDER BY e.name COLLATE NOCASE,e.id,sp.period_month
                """, new { employeeIds, firstMonth, lastMonth }).ToList();
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
            return new TabularReport(Text("Reports_Kind_SalaryRegister", "Salary register"), period,
                columns.Select(column => column with
                {
                    Heading = column.Key.Length == 7 && column.Key[4] == '-'
                    ? column.Heading : Text($"Reports_Column_{column.Key}", column.Heading)
                }).ToList(), rows, totals, truncated);
        }, cancellationToken);

    private Task<TabularReport> GetAssetScheduleAsync(long? assetId, DateRange period, int limit, CancellationToken cancellationToken) =>
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
            columns = columns.Select(column => column with { Heading = Text($"Reports_Column_{column.Key}", column.Heading) }).ToList();
            if (assetId is null) return new TabularReport(Text("Reports_Kind_AssetSchedule", "Asset depreciation schedule"), period, columns, [], new Dictionary<string, decimal>());
            AssetRaw? asset = connection.QuerySingleOrDefault<AssetRaw>("""
                SELECT id AS Id,name AS Name,cost AS Cost,salvage_value AS Salvage,rate_pct AS Rate,
                       method AS Method,in_use_from AS InUseFrom,disposed_date AS DisposedDate
                FROM asset WHERE id=@assetId
                """, new { assetId });
            if (asset is null) return new TabularReport(Text("Reports_Kind_AssetSchedule", "Asset depreciation schedule"), period, columns, [], new Dictionary<string, decimal>());
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
                    ["Status"] = schedule.ContainsKey(YearMonth.Parse(row.Period))
                        ? Text("Reports_Value_POSTED", "POSTED") : Text("Reports_Value_PROJECTED", "PROJECTED"),
                    ["Opening"] = row.Opening,
                    ["Depreciation"] = row.Amount,
                    ["Closing"] = row.Closing,
                })).Take(limit + 1).ToList();
            bool truncated = rows.Count > limit;
            if (truncated) rows.RemoveAt(limit);
            Dictionary<string, decimal> totals = new() { ["Depreciation"] = rows.Sum(row => ScaledDecimal(row.Values["Depreciation"])) };
            return new TabularReport($"{asset.Name} — depreciation schedule", period, columns, rows, totals, truncated);
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

    private static Money[] SumMonths(IReadOnlyList<PnlMatrixRow> rows, int count) =>
        Enumerable.Range(0, count).Select(index => Money.FromScaled(rows.Sum(row => row.Months[index].Scaled))).ToArray();

    private static Money[] SubtractMonths(Money[] left, Money[] right) =>
        Enumerable.Range(0, left.Length).Select(index => left[index] - right[index]).ToArray();

    private static Money[] AddMonths(Money[] left, Money[] right) =>
        Enumerable.Range(0, left.Length).Select(index => left[index] + right[index]).ToArray();

    private static PnlMatrixRow SummaryRow(string code, string label, Money[] months) =>
        new("TOTAL", code, null, label, months, Money.FromScaled(months.Sum(static month => month.Scaled)));

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
            values[key] = key switch
            {
                "PaymentMode" => string.Join(", ", code.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(TranslateCode)),
                "Action" => Text($"Reports_Action_{code}", code),
                "Entity" => Text($"Reports_Entity_{code}", code),
                _ => TranslateCode(code),
            };
        }
        return new ReportRow(values);
    }

    private string TranslateCode(string code) => code switch
    {
        "COMPLETED" => Text("Reports_StatusCompleted", code),
        "VOID" => Text("Reports_StatusVoid", code),
        "EXPIRED" => Text("Reports_StatusExpired", code),
        "CRITICAL" => Text("Reports_StatusCritical", code),
        "SOON" => Text("Reports_StatusSoon", code),
        "POSTED" => Text("Reports_Value_POSTED", code),
        "PROJECTED" => Text("Reports_Value_PROJECTED", code),
        "OUTWARD" => Text("Reports_Value_OUTWARD", code),
        "INWARD" => Text("Reports_Value_INWARD", code),
        "CASH" => Text("Reports_PaymentCash", code),
        "UPI" => Text("Reports_PaymentUpi", code),
        "CARD" => Text("Reports_PaymentCard", code),
        "OTHER" => Text("Reports_PaymentOther", code),
        "SLM" => Text("Reports_Value_SLM", code),
        "WDV" => Text("Reports_Value_WDV", code),
        "OPENING" => Text("Stock_SourceDoc_Opening", code),
        "PURCHASE" or "PURCHASE_CANCEL" => Text("Stock_SourceDoc_Purchase", code),
        "VENDOR_RETURN" => Text("Stock_SourceDoc_VendorReturn", code),
        "SALE" or "SALE_VOID" => Text("Stock_SourceDoc_Sale", code),
        "SALE_RETURN" => Text("Stock_SourceDoc_Return", code),
        "DAMAGE" => Text("Stock_SourceDoc_Damage", code),
        "ADJUSTMENT" => Text("Stock_SourceDoc_Adjustment", code),
        "BROKEN" => Text("MoneyOut_ReasonBroken", code),
        "SPOILED" => Text("MoneyOut_ReasonSpoiled", code),
        "PEST" => Text("MoneyOut_ReasonPest", code),
        "LOST" => Text("MoneyOut_ReasonLost", code),
        "CUSTOMER_RETURN" => Text("MoneyOut_ReasonReturn", code),
        "Mon" or "Tue" or "Wed" or "Thu" or "Fri" or "Sat" or "Sun" => Text($"Reports_Weekday_{code}", code),
        _ => code,
    };

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
        List<PnlLine> other = lines.Where(static x => x.Section == "OTHER" || x.HeadCode == "ASSET_DISPOSAL").ToList();
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
        "VENDOR_RETURN_VARIANCE" => Text("Pnl_Head_VendorReturnVariance", "Vendor return variance"),
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
    private sealed class DashboardCore { public long TodaySales { get; init; } public long TodayTaxableSales { get; init; } public int TodayBills { get; init; } public long TodayGrossProfit { get; init; } public long MonthSales { get; init; } public long PreviousMonthSales { get; init; } public int ExpiredLots { get; init; } public int CriticalLots { get; init; } public int SoonLots { get; init; } public int LowStockProducts { get; init; } public long StockValue { get; init; } public long Payables { get; init; } }
    private sealed class PaymentRaw { public string Mode { get; init; } = ""; public long Amount { get; init; } }
    private sealed class TrendRaw { public DateOnly Date { get; init; } public long Sales { get; init; } public int Bills { get; init; } public long GrossProfit { get; init; } }
    private sealed class TopRaw { public long ProductId { get; init; } public string Name { get; init; } = ""; public string? LocalName { get; init; } public long Quantity { get; init; } public long Revenue { get; init; } public long Cost { get; init; } public long Margin { get; init; } public int RevenueRank { get; init; } public int MarginRank { get; init; } }
    private sealed class NamedRow { public long Id { get; init; } public string Name { get; init; } = ""; public string? LocalName { get; init; } }
    private sealed class SalaryMatrixRaw { public long EmployeeId { get; init; } public string Employee { get; init; } = ""; public string? Role { get; init; } public string Month { get; init; } = ""; public long Amount { get; init; } }
    private sealed class AssetRaw { public long Id { get; init; } public string Name { get; init; } = ""; public long Cost { get; init; } public long Salvage { get; init; } public long Rate { get; init; } public string Method { get; init; } = ""; public DateOnly InUseFrom { get; init; } public DateOnly? DisposedDate { get; init; } }
    private sealed class AssetPeriodRaw { public string Period { get; init; } = ""; public long Opening { get; init; } public long Amount { get; init; } public long Closing { get; init; } }
}

internal sealed record ReportDefinition(string Title, bool UsesPeriod, string Sql, IReadOnlyList<ReportColumn> Columns);
