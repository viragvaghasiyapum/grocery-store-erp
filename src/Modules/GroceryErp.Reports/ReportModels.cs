using GroceryErp.Core.Numerics;

namespace GroceryErp.Reports;

public enum PnlPeriodKind { ThisMonth, LastMonth, ThisFiscalYear, LastFiscalYear, Custom }

public sealed record DateRange
{
    public DateRange(DateOnly from, DateOnly to)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        From = from;
        To = to;
    }

    public DateOnly From { get; }
    public DateOnly To { get; }
}

public sealed record PnlLine(string Section, string HeadCode, long? ReferenceId, string Label, Money Amount);

public sealed record PnlStatement(
    DateRange Period,
    IReadOnlyList<PnlLine> Income,
    IReadOnlyList<PnlLine> CostOfGoodsSold,
    IReadOnlyList<PnlLine> Expenses,
    IReadOnlyList<PnlLine> OtherIncome,
    Money NetSales,
    Money GrossProfit,
    Percent GrossMargin,
    Money TotalExpenses,
    Money NetProfit,
    Percent NetMargin);

public sealed record PnlComparison(PnlStatement Current, PnlStatement Previous);

public sealed record PnlMatrixRow(string Section, string HeadCode, long? ReferenceId, string Label,
    IReadOnlyList<Money> Months, Money Total);

public sealed record PnlMatrix(DateRange Period, IReadOnlyList<string> MonthKeys,
    IReadOnlyList<PnlMatrixRow> Rows);

public sealed record DashboardSummary(
    DateOnly AsOf,
    Money TodaySales,
    Money TodayTaxableSales,
    int TodayBills,
    Money AverageBill,
    Money TodayGrossProfit,
    Money MonthSales,
    Money PreviousComparableMonthSales,
    IReadOnlyList<PaymentSplit> Payments,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<TopProduct> TopProductsByRevenue,
    IReadOnlyList<TopProduct> TopProductsByMargin,
    int ExpiredLots,
    int CriticalLots,
    int SoonLots,
    int LowStockProducts,
    Money StockValue,
    Money Payables,
    string? LastBackup,
    string? LastUsbExport);

public sealed record PaymentSplit(string Mode, Money Amount);
public sealed record TrendPoint(DateOnly Date, Money Sales, int Bills, Money GrossProfit);
public sealed record TopProduct(long ProductId, string Name, string? LocalName, Quantity Quantity,
    Money Revenue, Money Cost, Money Margin);

public enum ReportKind
{
    SalesRegister, ItemSales, CategorySales, HourlySales, StockValue, StockLedger, LowStock,
    SlowMovers, Expiry, Damages, Expenses, SalaryRegister, AssetRegister, VendorPayables,
    PurchaseRegister, GstSummary, AuditLog, AssetSchedule
}

public enum ReportValueKind { Text, Date, Count, Quantity, Money, Percent }

public sealed record ReportColumn(string Key, string Heading, ReportValueKind Kind);
public sealed record ReportRow(IReadOnlyDictionary<string, object?> Values);
public sealed record TabularReport(string Title, DateRange? Period, IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<ReportRow> Rows, IReadOnlyDictionary<string, decimal> Totals, bool IsTruncated = false);

public sealed record ReportFilter(DateOnly? From = null, DateOnly? To = null, long? ProductId = null,
    long? LotId = null, long? VendorId = null, long? AssetId = null, string? Status = null, string? PaymentMode = null,
    string? Entity = null, string? Action = null, int SlowMoverDays = 90, int Limit = 5000);
