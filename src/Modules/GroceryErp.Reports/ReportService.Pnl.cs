using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Dapper;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;

namespace GroceryErp.Reports;

// Every P&L figure comes from the one source contract (v_pnl_entry). A month or year
// inside a locked calendar or fiscal year reads that year's saved snapshot; an open
// period reads the live sources as of the business date.
public sealed partial class ReportService
{
    private static readonly string[] HeadOrder =
    [
        "SALES", "SALES_RETURNS", "ROUND_OFF", "COGS", "DAMAGES", "STOCK_ADJUSTMENT", "EXPENSE_CATEGORY",
        "SALARIES", "DEPRECIATION", "ASSET_DISPOSAL", "VENDOR_RETURN_VARIANCE",
    ];

    public PnlPeriod MonthPeriod(YearMonth month) =>
        new(PnlScope.Month, new(month.FirstDay, month.LastDay), MonthLabel(month.ToString()));

    public PnlPeriod CalendarYearPeriod(int year)
    {
        AnnualPeriod period = AnnualPeriod.FromStart(AnnualPeriodKind.Calendar, new DateOnly(year, 1, 1),
            settings.Current.FyStartMonth);
        return new(PnlScope.CalendarYear, new(period.Start, period.End),
            Format(Text("Reports_PnlCalendarYear", "Calendar year {0}"), period.Label));
    }

    public PnlPeriod FiscalYearPeriod(int startYear)
    {
        AnnualPeriod period = AnnualPeriod.FromStart(AnnualPeriodKind.Fiscal,
            new DateOnly(startYear, settings.Current.FyStartMonth, 1), settings.Current.FyStartMonth);
        return new(PnlScope.FiscalYear, new(period.Start, period.End),
            Format(Text("Reports_PnlFiscalYear", "Fiscal year {0}"), period.Label));
    }

    public static PnlPeriod CustomPeriod(DateOnly from, DateOnly to) =>
        new(PnlScope.Custom, new(from, to), $"{Day(from)} — {Day(to)}");

    public Task<PnlPeriodRange> GetPnlPeriodRangeAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => new PnlPeriodRange(PnlSource.FirstEntryDate(connection), clock.BusinessDate,
            settings.Current.FyStartMonth, AnnualCloseService.Closes(connection, null)
                .Select(static close => $"{close.Period.Code}:{close.Period.Label}").ToList()), cancellationToken);

    public async Task<PnlReport> GetPnlReportAsync(PnlPeriod period, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(period);
        DateOnly today = clock.BusinessDate;
        int fiscalStartMonth = settings.Current.FyStartMonth;
        AnnualPeriod[] years = period.Scope switch
        {
            PnlScope.CalendarYear => [AnnualPeriod.Calendar(period.Range.From)],
            PnlScope.FiscalYear => [AnnualPeriod.Fiscal(period.Range.From, fiscalStartMonth)],
            PnlScope.Month => [AnnualPeriod.Calendar(period.Range.From), AnnualPeriod.Fiscal(period.Range.From, fiscalStartMonth)],
            _ => [],
        };
        years = [.. years.OrderBy(static year => year.LockDate)];
        AnnualPeriod[] locked = years.Where(year => year.IsLockedOn(today)).ToArray();
        // A locked year is frozen already; save a missing snapshot before the first read.
        if (locked.Length > 0 && await database.ReadAsync(connection =>
                locked.Any(year => !AnnualCloseService.Exists(connection, null, year)), cancellationToken).ConfigureAwait(false))
            await database.WriteAsync(transaction =>
            {
                foreach (AnnualPeriod year in locked) AnnualCloseService.Finalize(transaction, year, today, clock.Now);
            }, cancellationToken, "pnl.finalize").ConfigureAwait(false);
        string operation = period.Scope == PnlScope.Month ? "pnl.month" : "pnl.year";
        return await database.ReadAsync(connection =>
        {
            long started = Stopwatch.GetTimestamp();
            // A finalized year stays final even if the business date is later moved back.
            foreach (AnnualPeriod year in years)
            {
                if (AnnualCloseService.Load(connection, null, year) is not { } snapshot) continue;
                string? month = period.Scope == PnlScope.Month ? period.Range.From.ToString("yyyy-MM", CultureInfo.InvariantCulture) : null;
                List<PnlReportLine> saved = snapshot.Lines.Where(line => month is null || line.Month == month)
                    .Select(line => new PnlReportLine(line.Month, line.Section, line.HeadCode, line.CategoryId,
                        HeadLabel(line.HeadCode, line.CategoryId == 0 ? null : Pick(line.CategoryName ?? "", line.CategoryNameLocal)),
                        Money.FromScaled(line.Amount), line.SourceCount)).ToList();
                Record(operation + ".finalized", started, saved.Count);
                return Compose(period, "FINALIZED", snapshot.Info.EditCutoff,
                    $"{(year.Kind == AnnualPeriodKind.Calendar ? "CY" : "FY")} {year.Label}",
                    snapshot.Info.FormatVersion, snapshot.Info.Checksum, snapshot.Info.GeneratedAt, saved);
            }
            List<PnlReportLine> live = LiveLines(connection, period.Range.From, period.Range.To, cancellationToken);
            Record(operation + ".live", started, live.Count);
            // An open period states the last date its entries may still change and the
            // business date its live figures were read on.
            DateOnly cutoff = years.Length == 0 ? today : years.Min(static year => year.EditCutoff);
            return Compose(period, period.Scope == PnlScope.Custom ? "LIVE" : "OPEN", cutoff, null, null, null,
                today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), live);
        }, cancellationToken, operation).ConfigureAwait(false);
    }

    public Task<PnlStatement> GetPnlAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        DateRange period = new(from, to);
        string operation = to.DayNumber - from.DayNumber > 45 ? "pnl.long-range" : "pnl.short-range";
        return database.ReadAsync(connection =>
        {
            long started = Stopwatch.GetTimestamp();
            List<PnlReportLine> lines = LiveLines(connection, from, to, cancellationToken);
            Record(operation + ".entries", started, lines.Count);
            long buildStarted = Stopwatch.GetTimestamp();
            PnlStatement statement = BuildStatement(period, lines);
            PerformanceMetrics.ReportStageMilliseconds.Record(Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", operation + ".build"));
            return statement;
        }, cancellationToken, operation);
    }

    public Task<PnlMatrix> GetPnlMatrixAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        DateRange period = new(from, to);
        return database.ReadAsync(connection =>
            BuildMatrix(period, LiveLines(connection, from, to, cancellationToken)), cancellationToken);
    }

    // Writes exactly the displayed report. A finalized year carries only saved values
    // and metadata, so its file is identical every time it is exported.
    public Task ExportPnlCsvAsync(PnlReport report, bool byMonth, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() =>
        {
            string fullPath = Path.GetFullPath(path);
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(true), 64 * 1024))
                {
                    writer.NewLine = "\r\n";
                    writer.WriteLine(string.Join(',', CsvText(Text("Reports_Kind_ProfitAndLoss", "Profit and loss")),
                        CsvText(report.Period.Label)));
                    writer.WriteLine(string.Join(',', CsvText(Text("Reports_Period", "Period")),
                        CsvText(Day(report.Period.Range.From)), CsvText(Day(report.Period.Range.To))));
                    writer.WriteLine(string.Join(',', CsvText(Text("Reports_PnlStatus", "Status")),
                        CsvText(Text($"Reports_PnlStatus_{report.Status}", report.Status)),
                        CsvText(Text("Reports_PnlCutoff", "Cutoff")), CsvText(Day(report.Cutoff))));
                    if (report.Checksum is not null)
                        writer.WriteLine(string.Join(',', CsvText(Text("Reports_PnlFinalizedYear", "Finalized year")),
                            CsvText(report.FinalizedYear ?? ""), CsvText(Text("Reports_PnlVersion", "Version")),
                            CsvText(report.FormatVersion?.ToString(CultureInfo.InvariantCulture) ?? ""),
                            CsvText(Text("Reports_PnlChecksum", "Checksum")), CsvText(report.Checksum)));
                    writer.WriteLine();
                    var header = new List<string> { CsvText(Text("Reports_Particulars", "Particulars")) };
                    if (byMonth) header.AddRange(report.Matrix.MonthKeys.Select(month => CsvText(MonthLabel(month))));
                    header.Add(CsvText(Text("Reports_Amount", "Amount")));
                    writer.WriteLine(string.Join(',', header));
                    foreach (PnlLayoutRow row in report.Rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var cells = new List<string> { CsvText(row.Kind == "CATEGORY" ? "  " + row.Label : row.Label) };
                        bool blank = row.Kind == "SECTION";
                        if (byMonth) cells.AddRange(row.Months.Select(value => blank ? "" : CsvValue(value.Scaled, ReportValueKind.Money)));
                        cells.Add(blank ? "" : CsvValue(row.Amount.Scaled, ReportValueKind.Money));
                        writer.WriteLine(string.Join(',', cells));
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }, cancellationToken);
    }

    private List<PnlReportLine> LiveLines(IDbConnection connection, DateOnly from, DateOnly to,
        CancellationToken cancellationToken)
    {
        List<PnlSourceLine> source = PnlSource.Lines(connection, from, to, null, cancellationToken);
        long[] ids = source.Where(static line => line.CategoryId != 0).Select(static line => line.CategoryId).Distinct().ToArray();
        Dictionary<long, string> names = ids.Length == 0 ? [] : connection.Query<NamedRow>(
                "SELECT id AS Id,name AS Name,name_local AS LocalName FROM expense_category WHERE id IN @ids", new { ids })
            .ToDictionary(static row => row.Id, row => Pick(row.Name, row.LocalName));
        return source.Select(line => new PnlReportLine(line.Month, line.Section, line.HeadCode, line.CategoryId,
            HeadLabel(line.HeadCode, line.CategoryId == 0 ? null : names.GetValueOrDefault(line.CategoryId)),
            Money.FromScaled(line.Amount), line.SourceCount)).ToList();
    }

    private PnlReport Compose(PnlPeriod period, string status, DateOnly cutoff, string? finalizedYear, int? version,
        string? checksum, string? generatedAt, List<PnlReportLine> lines)
    {
        PnlMatrix matrix = BuildMatrix(period.Range, lines);
        return new(period, status, cutoff, finalizedYear, version, checksum, generatedAt, lines,
            BuildStatement(period.Range, lines), matrix, Layout(lines, matrix.MonthKeys));
    }

    private static PnlStatement BuildStatement(DateRange period, IEnumerable<PnlReportLine> source)
    {
        List<PnlLine> lines = source.GroupBy(static line => (line.Section, line.HeadCode, line.CategoryId))
            .OrderBy(static group => Order(group.Key.HeadCode)).ThenBy(group => group.First().Label, StringComparer.CurrentCulture)
            .Select(static group => new PnlLine(group.Key.Section, group.Key.HeadCode,
                group.Key.CategoryId == 0 ? null : group.Key.CategoryId, group.First().Label,
                Money.FromScaled(group.Sum(static line => line.Amount.Scaled)))).ToList();
        List<PnlLine> income = lines.Where(static x => x.Section == "INCOME").ToList();
        List<PnlLine> cogs = lines.Where(static x => x.Section == "COGS").ToList();
        List<PnlLine> expenses = lines.Where(static x => x.Section == "EXPENSE").ToList();
        List<PnlLine> other = lines.Where(static x => x.Section == "OTHER").ToList();
        Money netSales = Sum(income);
        Money gross = netSales - Sum(cogs);
        Money totalExpenses = Sum(expenses);
        Money net = gross - totalExpenses + Sum(other);
        return new(period, income, cogs, expenses, other, netSales, gross, Ratio(gross, netSales),
            totalExpenses, net, Ratio(net, netSales));
    }

    private PnlMatrix BuildMatrix(DateRange period, IReadOnlyList<PnlReportLine> lines)
    {
        List<string> months = MonthKeys(period);
        List<PnlMatrixRow> rows = lines.GroupBy(static line => (line.Section, line.HeadCode, line.CategoryId))
            .OrderBy(static group => Order(group.Key.HeadCode)).ThenBy(group => group.First().Label, StringComparer.CurrentCulture)
            .Select(group =>
            {
                Money[] cells = Cells(group, months);
                return new PnlMatrixRow(group.Key.Section, group.Key.HeadCode,
                    group.Key.CategoryId == 0 ? null : group.Key.CategoryId, group.First().Label, cells, Total(cells));
            }).ToList();
        Money[] netSales = Months(rows, "INCOME", months.Count);
        Money[] gross = Combine(netSales, Months(rows, "COGS", months.Count), -1);
        Money[] expenses = Months(rows, "EXPENSE", months.Count);
        Money[] net = Combine(Combine(gross, expenses, -1), Months(rows, "OTHER", months.Count), 1);
        List<PnlMatrixRow> ordered =
        [
            .. rows.Where(static row => row.Section == "INCOME"),
            TotalRow("NET_SALES", Text("Reports_Section_NetSales", "Net sales"), netSales),
            .. rows.Where(static row => row.Section == "COGS"),
            TotalRow("GROSS_PROFIT", Text("Reports_Section_GrossProfit", "Gross profit"), gross),
            .. rows.Where(static row => row.Section == "EXPENSE"),
            TotalRow("TOTAL_EXPENSES", Text("Reports_Section_TotalExpenses", "Total expenses"), expenses),
            .. rows.Where(static row => row.Section == "OTHER"),
            TotalRow("NET_PROFIT", Text("Reports_Section_NetProfit", "Net profit"), net),
        ];
        return new PnlMatrix(period, months, ordered);
    }

    // Head totals first; expense categories sit under their head for expandable detail.
    private List<PnlLayoutRow> Layout(IReadOnlyList<PnlReportLine> lines, IReadOnlyList<string> months)
    {
        var rows = new List<PnlLayoutRow>();
        Money[] Section(string section, string key, string fallback)
        {
            rows.Add(new("SECTION", section, null, Text(key, fallback), Money.Zero, Zeroes(months.Count)));
            var total = new Money[months.Count];
            Array.Fill(total, Money.Zero);
            foreach (IGrouping<string, PnlReportLine> head in lines.Where(line => line.Section == section)
                         .GroupBy(static line => line.HeadCode).OrderBy(static group => Order(group.Key)))
            {
                Money[] cells = Cells(head, months);
                rows.Add(new("HEAD", head.Key, null, head.Key == "EXPENSE_CATEGORY"
                    ? Text("Pnl_Head_Expenses", "Expenses") : head.First().Label, Total(cells), cells));
                if (head.Key == "EXPENSE_CATEGORY")
                    foreach (IGrouping<long, PnlReportLine> category in head.GroupBy(static line => line.CategoryId)
                                 .OrderBy(group => group.First().Label, StringComparer.CurrentCulture))
                    {
                        Money[] categoryCells = Cells(category, months);
                        rows.Add(new("CATEGORY", category.Key.ToString(CultureInfo.InvariantCulture), head.Key,
                            category.First().Label, Total(categoryCells), categoryCells));
                    }
                total = Combine(total, cells, 1);
            }
            return total;
        }
        Money[] netSales = Section("INCOME", "Reports_Section_Income", "Income");
        rows.Add(new("TOTAL", "NET_SALES", null, Text("Reports_Section_NetSales", "Net sales"), Total(netSales), netSales));
        Money[] gross = Combine(netSales, Section("COGS", "Reports_Section_Cogs", "Cost of goods sold"), -1);
        rows.Add(new("TOTAL", "GROSS_PROFIT", null, Text("Reports_Section_GrossProfit", "Gross profit"), Total(gross), gross));
        Money[] expenses = Section("EXPENSE", "Reports_Section_Expenses", "Expenses");
        rows.Add(new("TOTAL", "TOTAL_EXPENSES", null, Text("Reports_Section_TotalExpenses", "Total expenses"), Total(expenses), expenses));
        Money[] net = Combine(Combine(gross, expenses, -1), Section("OTHER", "Reports_Section_OtherIncome", "Other income and losses"), 1);
        rows.Add(new("TOTAL", "NET_PROFIT", null, Text("Reports_Section_NetProfit", "Net profit"), Total(net), net));
        return rows;
    }

    private string HeadLabel(string code, string? categoryName) => code switch
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
        "EXPENSE_CATEGORY" => string.IsNullOrWhiteSpace(categoryName) ? Text("Pnl_Head_Expenses", "Expenses") : categoryName,
        _ => code,
    };

    private static void Record(string operation, long started, int rows)
    {
        var tag = new KeyValuePair<string, object?>("operation", operation);
        PerformanceMetrics.ReportStageMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tag);
        PerformanceMetrics.ReportStageRows.Record(rows, tag);
    }

    private static int Order(string head)
    {
        int index = Array.IndexOf(HeadOrder, head);
        return index < 0 ? HeadOrder.Length : index;
    }

    private static Money[] Cells(IEnumerable<PnlReportLine> lines, IReadOnlyList<string> months)
    {
        var cells = Zeroes(months.Count);
        foreach (PnlReportLine line in lines)
        {
            int index = IndexOf(months, line.Month);
            if (index >= 0) cells[index] += line.Amount;
        }
        return cells;
    }

    private static int IndexOf(IReadOnlyList<string> months, string month)
    {
        for (int index = 0; index < months.Count; index++)
            if (string.Equals(months[index], month, StringComparison.Ordinal)) return index;
        return -1;
    }

    private static Money[] Zeroes(int count)
    {
        var values = new Money[count];
        Array.Fill(values, Money.Zero);
        return values;
    }

    private static Money[] Months(IEnumerable<PnlMatrixRow> rows, string section, int count)
    {
        Money[] total = Zeroes(count);
        foreach (PnlMatrixRow row in rows.Where(row => row.Section == section))
            for (int index = 0; index < count; index++) total[index] += row.Months[index];
        return total;
    }

    private static Money[] Combine(Money[] left, Money[] right, int sign) =>
        Enumerable.Range(0, left.Length).Select(index => sign > 0 ? left[index] + right[index] : left[index] - right[index]).ToArray();

    private static Money Total(IEnumerable<Money> values) => Money.FromScaled(values.Sum(static value => value.Scaled));

    private static PnlMatrixRow TotalRow(string code, string label, Money[] months) =>
        new("TOTAL", code, null, label, months, Total(months));

    private static Money Sum(IEnumerable<PnlLine> lines) => Money.FromScaled(lines.Sum(static x => x.Amount.Scaled));
    private static Percent Ratio(Money amount, Money basis) => basis == Money.Zero ? Percent.Zero : new(amount.Value * 100m / basis.Value);
    private static string Day(DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
    private static string Format(string template, string value) => string.Format(CultureInfo.InvariantCulture, template, value);
}
