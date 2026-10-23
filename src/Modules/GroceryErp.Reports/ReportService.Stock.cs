using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using Dapper;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Localization;

namespace GroceryErp.Reports;

public sealed partial class ReportService
{
    private const int ReconciliationScanPage = 1000;

    // States the expired portion of unsold stock as of the business date, whether the
    // report includes it or not, so both totals can be interpreted.
    private string ExpiredStockSummary(IDbConnection connection, bool included)
    {
        ExpiredTotals totals = connection.QuerySingle<ExpiredTotals>(ReportDefinitions.ExpiredStockTotals,
            new { stockAsOf = Iso(clock.BusinessDate) });
        string template = included
            ? Text("Reports_StockExpiredIncluded", "Includes expired stock: {0} lots, quantity {1}, cost value {2}.")
            : Text("Reports_StockExpiredExcluded", "Excludes expired stock: {0} lots, quantity {1}, cost value {2}.");
        return string.Format(TranslationSource.FormatCulture, template, totals.Lots,
            ScaledDecimal(totals.Quantity).ToString("N3", TranslationSource.FormatCulture),
            ScaledDecimal(totals.CostValue).ToString("N3", TranslationSource.FormatCulture));
    }

    // Newest lots first: each lot's received quantity against what its documents sold,
    // returned, damaged, used and corrected. Differences are shown, never hidden. A full
    // check of every lot reads all sale history, so it runs in weekly maintenance; the
    // differences-only view rechecks the lots that check recorded.
    private Task<TabularReport> GetStockReconciliationAsync(ReportFilter filter, CancellationToken cancellationToken) =>
        database.ReadAsync(connection =>
        {
            int limit = Math.Min(filter.Limit, 500);
            string summary;
            List<LotSourceBalance> balances;
            bool truncated;
            if (filter.MismatchesOnly)
            {
                LotCheck check = LastLotCheck(connection);
                balances = RecordedDifferences(connection, filter.ProductId);
                truncated = balances.Count > limit;
                summary = check.Date is null
                    ? Text("Reports_ReconciliationNoCheck", "No full lot check has run yet; weekly maintenance runs it.")
                    : string.Format(TranslationSource.FormatCulture, Text("Reports_ReconciliationLastCheck",
                        "Full check on {0} found {1} lots that differ; {2} still differ."),
                        Day(DateOnly.ParseExact(check.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        check.Differences, balances.Count);
            }
            else
            {
                balances = LotSourceReconciliation.PageBefore(connection, null, long.MaxValue, limit + 1, filter.ProductId);
                truncated = balances.Count > limit;
                summary = string.Format(TranslationSource.FormatCulture,
                    Text("Reports_ReconciliationSummary", "{0} lots shown, newest first; {1} differ from their documents."),
                    Math.Min(balances.Count, limit), balances.Take(limit).Count(static row => row.Difference != 0));
            }
            if (truncated) balances.RemoveRange(limit, balances.Count - limit);
            List<ReportRow> rows = ReconciliationRows(connection, balances);
            ReportDefinition definition = Definition(ReportKind.StockReconciliation);
            Dictionary<string, decimal> totals = definition.Columns.Where(static c => c.Kind == ReportValueKind.Quantity)
                .ToDictionary(static c => c.Key, c => rows.Sum(row => ScaledDecimal(row.Values.GetValueOrDefault(c.Key))));
            if (translations is not null) rows = rows.Select(LocalizeCodes).ToList();
            return new TabularReport(Text("Reports_Kind_StockReconciliation", definition.Title), null,
                definition.Columns.Select(column => column with { Heading = Text($"Reports_Column_{column.Key}", column.Heading) }).ToList(),
                rows, totals, truncated, summary);
        }, cancellationToken, "report.stock-reconciliation");

    private Task<long> ExportStockReconciliationCsvAsync(ReportFilter filter, string temporary,
        IProgress<long>? progress, CancellationToken cancellationToken) => database.ReadAsync(connection =>
    {
        ReportDefinition definition = Definition(ReportKind.StockReconciliation);
        using var writer = new StreamWriter(temporary, false, new UTF8Encoding(true), 64 * 1024);
        writer.WriteLine(string.Join(',', definition.Columns.Select(column =>
            CsvText(Text($"Reports_Column_{column.Key}", column.Heading)))));
        long count = 0;
        void Write(List<LotSourceBalance> balances)
        {
            foreach (ReportRow row in ReconciliationRows(connection, balances))
            {
                IReadOnlyDictionary<string, object?> values = translations is null ? row.Values : LocalizeCodes(row).Values;
                writer.WriteLine(string.Join(',', definition.Columns.Select(column =>
                    CsvValue(values.GetValueOrDefault(column.Key), column.Kind))));
                count++;
                if (count % 1000 == 0) progress?.Report(count);
            }
        }
        if (filter.MismatchesOnly)
        {
            Write(RecordedDifferences(connection, filter.ProductId));
            return count;
        }
        long cursor = filter.ProductId is null ? 0 : long.MaxValue;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<LotSourceBalance> page = filter.ProductId is null
                ? LotSourceReconciliation.PageAfter(connection, null, cursor, ReconciliationScanPage)
                : LotSourceReconciliation.PageBefore(connection, null, cursor, ReconciliationScanPage, filter.ProductId);
            if (page.Count == 0) break;
            cursor = page[^1].LotId;
            Write(page);
        }
        return count;
    }, cancellationToken);

    // Lots the last full check recorded, rechecked now; a lot fixed since is left out.
    private static List<LotSourceBalance> RecordedDifferences(IDbConnection connection, long? productId)
    {
        long[] ids = connection.Query<long>("""
            SELECT lot_id FROM lot_source_issue WHERE @productId IS NULL OR product_id=@productId
            """, new { productId }).ToArray();
        return LotSourceReconciliation.ForLots(connection, null, ids).Where(static row => row.Difference != 0).ToList();
    }

    private static LotCheck LastLotCheck(IDbConnection connection) => connection.QuerySingle<LotCheck>("""
        SELECT (SELECT value FROM setting WHERE key='maintenance.lot_check_last_run') AS Date,
               COALESCE(CAST((SELECT value FROM setting WHERE key='maintenance.lot_check_differences') AS INTEGER),0)
                 AS Differences
        """);

    private sealed class LotCheck
    {
        public string? Date { get; init; }
        public long Differences { get; init; }
    }

    private List<ReportRow> ReconciliationRows(IDbConnection connection, List<LotSourceBalance> balances)
    {
        if (balances.Count == 0) return [];
        Dictionary<long, LotDetail> details = connection.Query<LotDetail>("""
            SELECT l.id AS LotId,p.name AS Product,p.name_local AS LocalName,p.sku AS Sku,l.lot_code AS Lot,
                   l.origin_kind AS Source,l.received_date AS Received
            FROM product_lot l JOIN product p ON p.id=l.product_id WHERE l.id IN @ids
            """, new { ids = balances.Select(static row => row.LotId).ToArray() }).ToDictionary(static row => row.LotId);
        return balances.Select(balance =>
        {
            details.TryGetValue(balance.LotId, out LotDetail? detail);
            return new ReportRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Product"] = detail is null ? "" : Pick(detail.Product, detail.LocalName),
                ["Sku"] = detail?.Sku,
                ["Lot"] = detail?.Lot,
                ["Source"] = detail?.Source,
                ["Received"] = detail?.Received,
                ["ReceivedQty"] = balance.Received,
                ["Sold"] = balance.Sold,
                ["Returned"] = balance.Returned,
                ["Damaged"] = balance.Damaged,
                ["InternalUse"] = balance.InternalUse,
                ["VendorReturned"] = balance.VendorReturned,
                ["Corrections"] = balance.Corrections,
                ["Expected"] = balance.Expected,
                ["Available"] = balance.Actual,
                ["Difference"] = balance.Difference,
            });
        }).ToList();
    }

    private sealed class ExpiredTotals
    {
        public long Lots { get; init; }
        public long Quantity { get; init; }
        public long CostValue { get; init; }
    }

    private sealed class LotDetail
    {
        public long LotId { get; init; }
        public string Product { get; init; } = "";
        public string? LocalName { get; init; }
        public string Sku { get; init; } = "";
        public string Lot { get; init; } = "";
        public string Source { get; init; } = "";
        public string? Received { get; init; }
    }
}
