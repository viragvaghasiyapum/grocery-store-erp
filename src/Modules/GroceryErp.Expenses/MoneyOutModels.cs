using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;

namespace GroceryErp.Expenses;

public sealed record ExpiryLot(
    long LotId, long ProductId, string Sku, string ProductName, string? ProductNameLocal, string LotCode,
    string? BatchNumber, DateOnly ExpiryDate, int DaysLeft, Quantity Quantity,
    Money CostValue, string Bucket, long? VendorId, string? VendorName);

public sealed record DamageInput(
    long LotId, DateOnly Date, Quantity Quantity, string Reason,
    Money RecoveredAmount, string? Notes);

public sealed record DamageItem(
    long Id, string DocumentNumber, DateOnly Date, string ProductName, string LotCode,
    Quantity Quantity, Money UnitCost, Money CostValue, Money RecoveredAmount, string Reason, string? Notes);

public sealed record ExpenseCategoryItem(long Id, string Name, string? LocalName, bool IsActive);
public sealed record ExpenseInput(DateOnly Date, long CategoryId, Money Amount, string Mode, string? PaidTo, string? Reference, string? Notes);
public sealed record ExpenseItem(long Id, DateOnly Date, long CategoryId, string CategoryName, Money Amount, string Mode, string? PaidTo, string? Reference, string? Notes);

public sealed record EmployeeInput(
    string Name, string? Phone, string? Role, DateOnly? JoinDate, DateOnly? LeaveDate,
    Money MonthlySalary, bool IsActive, string? Notes);
public sealed record EmployeeItem(
    long Id, string Name, string? Phone, string? Role, DateOnly? JoinDate, DateOnly? LeaveDate,
    Money MonthlySalary, bool IsActive, string? Notes);

public sealed record SalaryRunLine(
    long EmployeeId, Money BaseSalary, Money LeaveDeduction, Money Overtime, Money Bonus, string? Notes);
public sealed record SalaryRunInput(YearMonth Period, DateOnly PayDate, string Mode, IReadOnlyList<SalaryRunLine> Lines);
public sealed record SalaryPaymentItem(long Id, long EmployeeId, string EmployeeName, YearMonth Period, DateOnly PayDate, string Kind, Money Amount, string Mode, string? Notes);
