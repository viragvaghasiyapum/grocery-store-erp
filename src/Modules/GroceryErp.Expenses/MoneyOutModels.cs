using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;

namespace GroceryErp.Expenses;

public sealed record ExpiryLot(
    long LotId, long ProductId, string Sku, string ProductName, string? ProductNameLocal, string LotCode,
    string? BatchNumber, DateOnly ExpiryDate, int DaysLeft, Quantity Quantity,
    Money CostValue, string Bucket, long? VendorId, string? VendorName);

public sealed record ExpiryPage(IReadOnlyList<ExpiryLot> Items, bool HasMore,
    DateOnly? NextDate, long? NextId);

public sealed record DamageInput(
    long LotId, DateOnly Date, Quantity Quantity, string Reason,
    Money RecoveredAmount, string? Notes);

public sealed record BulkDamageInput(long LotId, long ExpectedOnHandScaled,
    long ExpectedDetailVersion, Quantity Quantity);

/// Status is ACTIVE, CORRECTED or VOID (corrected to zero quantity).
public sealed record DamageItem(
    long Id, string DocumentNumber, DateOnly Date, string ProductName, string LotCode,
    Quantity Quantity, Money UnitCost, Money CostValue, Money RecoveredAmount, string Reason, string? Notes,
    string Status = "ACTIVE");

public sealed record DamagePage(IReadOnlyList<DamageItem> Items, bool HasMore, DateOnly? NextDate, long? NextId);
public sealed record DamageCorrection(Quantity Quantity, Money RecoveredAmount, string Reason, string? Notes);

/// One line of a damage's history: the original posting, then each correction.
public sealed record DamageHistoryItem(string Kind, DateOnly RecordedOn, string? Treatment, DateOnly EffectiveDate,
    Quantity Quantity, Money RecoveredAmount, string Reason, string? Notes, Money LossChange,
    string? CorrectionReason, long? RestoredLotId);

public sealed record ExpenseCategoryItem(long Id, string Name, string? LocalName, bool IsActive);
public sealed record ExpenseInput(DateOnly Date, long CategoryId, Money Amount, string Mode, string? PaidTo, string? Reference, string? Notes);

/// Current values of an expense. Status is ACTIVE, CORRECTED, VOID or PURCHASE
/// (created by a posted purchase line and changed only by voiding that purchase).
public sealed record ExpenseItem(long Id, DateOnly Date, long CategoryId, string CategoryName, Money Amount, string Mode,
    string? PaidTo, string? Reference, string? Notes, string Status = "ACTIVE");

public sealed record ExpensePage(IReadOnlyList<ExpenseItem> Items, bool HasMore, DateOnly? NextDate, long? NextId);

public sealed record ExpenseHistoryItem(string Kind, DateOnly RecordedOn, string? Treatment, DateOnly Date,
    string CategoryName, Money Amount, string Mode, string? PaidTo, string? Reference, string? Notes,
    DateOnly? ReversedOn, DateOnly? AppliedOn, string? CorrectionReason);

public sealed record EmployeeInput(
    string Name, string? Phone, string? Role, DateOnly? JoinDate, DateOnly? LeaveDate,
    Money MonthlySalary, bool IsActive, string? Notes);
public sealed record EmployeeItem(
    long Id, string Name, string? Phone, string? Role, DateOnly? JoinDate, DateOnly? LeaveDate,
    Money MonthlySalary, bool IsActive, string? Notes);

/// A dated monthly-salary term; the newest term effective on or before a month applies to it.
public sealed record SalaryTermItem(long Id, YearMonth EffectiveMonth, Money MonthlySalary, string? Reason, DateTime CreatedAt);

/// What a salary change would do: the month it takes effect from and the posted
/// payments from that month on, which stay as paid.
public sealed record SalaryTermPreview(YearMonth EffectiveMonth, YearMonth FirstOpenMonth, int PostedMonths);

public sealed record SalaryRunLine(
    long EmployeeId, Money BaseSalary, Money LeaveDeduction, Money Overtime, Money Bonus, string? Notes);
public sealed record SalaryRunInput(YearMonth Period, DateOnly PayDate, string Mode, IReadOnlyList<SalaryRunLine> Lines);

/// Status is ACTIVE, CORRECTED or VOID.
public sealed record SalaryPaymentItem(long Id, long EmployeeId, string EmployeeName, YearMonth Period, DateOnly PayDate,
    string Kind, Money Amount, string Mode, string? Notes, string Status = "ACTIVE");

public sealed record SalaryCorrection(DateOnly PayDate, Money Amount, string Mode, string? Notes);

public sealed record SalaryHistoryItem(string Kind, DateOnly RecordedOn, string? Treatment, DateOnly EffectiveDate,
    Money Amount, DateOnly PayDate, string Mode, string? Notes, Money AmountChange, string? CorrectionReason);

/// <summary>An expired lot with stock that a reviewed write-off would turn into EXPIRED damage.</summary>
public sealed record ExpiredWriteOffLot(long LotId, string ProductName, string LotCode, DateOnly ExpiryDate,
    Quantity Quantity, Money CostValue);

/// <summary>Damage list filters. Status: ACTIVE, CORRECTED or VOID. Query matches document, product or lot.</summary>
public sealed record DamageFilter(DateOnly From, DateOnly To)
{
    public string? Status { get; init; }
    public string? Reason { get; init; }
    public string? Query { get; init; }
}

/// <summary>Expense list filters. Status: ACTIVE, CORRECTED, VOID or PURCHASE. Query matches paid-to, reference or notes.</summary>
public sealed record ExpenseFilter(DateOnly From, DateOnly To)
{
    public string? Status { get; init; }
    public long? CategoryId { get; init; }
    public string? Query { get; init; }
}

/// <summary>Employee filters. Status: ACTIVE, INACTIVE or ALL. Query matches name, role or phone.</summary>
public sealed record EmployeeFilter
{
    public string Status { get; init; } = "ACTIVE";
    public string? Query { get; init; }
}
