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

/// The P&L period selector: one month, a calendar year, a fiscal year, or any range.
public enum PnlScope { Month, CalendarYear, FiscalYear, Custom }

public sealed record PnlPeriod(PnlScope Scope, DateRange Range, string Label);

/// One saved or live P&L figure for a month, head and expense category.
public sealed record PnlReportLine(string Month, string Section, string HeadCode, long CategoryId, string Label,
    Money Amount, long SourceCount);

/// Status is FINALIZED (read from a locked year's saved snapshot), OPEN (live source
/// records of an open period, as of the cutoff date) or LIVE (a custom range).
public sealed record PnlReport(PnlPeriod Period, string Status, DateOnly Cutoff, string? FinalizedYear,
    int? FormatVersion, string? Checksum, string? GeneratedAt, IReadOnlyList<PnlReportLine> Lines,
    PnlStatement Statement, PnlMatrix Matrix, IReadOnlyList<PnlLayoutRow> Rows);

/// One displayed or exported P&L row. Kind is SECTION, HEAD, CATEGORY (under its
/// ParentCode head) or TOTAL; Months aligns with the report's matrix month keys.
public sealed record PnlLayoutRow(string Kind, string Code, string? ParentCode, string Label, Money Amount,
    IReadOnlyList<Money> Months);

/// Bounds for the period pickers and the years already finalized.
public sealed record PnlPeriodRange(DateOnly? FirstEntry, DateOnly Today, int FiscalStartMonth,
    IReadOnlyList<string> FinalizedYears);

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
    SalesRegister, ItemSales, CategorySales, HourlySales, StockValue, LowStock,
    SlowMovers, Expiry, Damages, Expenses, SalaryRegister, AssetRegister, VendorPayables,
    PurchaseRegister, GstSummary, AuditLog, AssetSchedule, StockReconciliation
}

public enum ReportValueKind { Text, Date, Count, Quantity, Money, Percent }

public sealed record ReportColumn(string Key, string Heading, ReportValueKind Kind);
public sealed record ReportRow(IReadOnlyDictionary<string, object?> Values);
/// <summary>A displayed report; <see cref="Filters"/> states the applied filters for every export of it.</summary>
public sealed record TabularReport(string Title, DateRange? Period, IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<ReportRow> Rows, IReadOnlyDictionary<string, decimal> Totals, bool IsTruncated = false,
    string? Summary = null, string? Filters = null);

public sealed record ReportFilter(DateOnly? From = null, DateOnly? To = null, long? ProductId = null,
    long? VendorId = null, long? AssetId = null, string? Status = null, string? PaymentMode = null,
    string? Entity = null, string? Action = null, int SlowMoverDays = 90, int Limit = 5000,
    bool IncludeExpired = true, bool MismatchesOnly = false);
