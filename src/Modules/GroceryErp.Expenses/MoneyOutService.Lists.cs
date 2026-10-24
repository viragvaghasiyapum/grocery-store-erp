using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;

namespace GroceryErp.Expenses;

public sealed partial class MoneyOutService
{
    /// <summary>Reads one keyset page of damages with every filter applied in SQL.</summary>
    public Task<DamagePage> GetDamagePageAsync(DamageFilter filter, DateOnly? beforeDate, long? beforeId,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        EnsureRange(filter.From, filter.To);
        if (beforeDate is null != (beforeId is null) || limit is < 1 or > 200 ||
            filter.Status is not (null or "ACTIVE" or "CORRECTED" or "VOID") ||
            filter.Reason is not null && !DamageReasons.Contains(filter.Reason))
            throw new DomainException(ErrorCode.Validation, "Invalid damage page filter.");
        string status = filter.Status switch
        {
            "ACTIVE" => "AND dc.id IS NULL",
            "CORRECTED" => "AND dc.id IS NOT NULL AND dc.new_qty<>0",
            "VOID" => "AND dc.new_qty=0",
            _ => string.Empty,
        };
        return database.ReadAsync(connection =>
        {
            List<DamageItem> rows = connection.Query<DamageRow>($"""
                SELECT d.id AS Id,d.doc_no AS DocumentNumber,d.damage_date AS Date,
                       COALESCE(d.product_name_snapshot,'Legacy unknown') AS ProductName,
                       COALESCE(d.lot_code_snapshot,'Legacy unknown') AS LotCode,
                       COALESCE(dc.new_qty,d.qty) AS Quantity,d.unit_cost AS UnitCost,
                       CAST(ROUND(COALESCE(dc.new_qty,d.qty)*d.unit_cost/1000.0) AS INTEGER) AS CostValue,
                       COALESCE(dc.new_recovered,d.recovered_amount) AS RecoveredAmount,
                       COALESCE(dc.new_reason,d.reason) AS Reason,
                       CASE WHEN dc.id IS NULL THEN d.notes ELSE dc.new_notes END AS Notes,
                       CASE WHEN dc.id IS NULL THEN 'ACTIVE' WHEN dc.new_qty=0 THEN 'VOID' ELSE 'CORRECTED' END AS Status
                FROM damage d
                LEFT JOIN damage_correction dc ON dc.id=(SELECT MAX(id) FROM damage_correction WHERE damage_id=d.id)
                WHERE d.damage_date BETWEEN @from AND @to {status}
                  AND (@reason IS NULL OR COALESCE(dc.new_reason,d.reason)=@reason)
                  AND (@pattern IS NULL OR d.doc_no LIKE @pattern ESCAPE '\'
                       OR d.product_name_snapshot LIKE @pattern ESCAPE '\'
                       OR d.product_name_local_snapshot LIKE @pattern ESCAPE '\'
                       OR d.lot_code_snapshot LIKE @pattern ESCAPE '\')
                  AND (@beforeDate IS NULL OR d.damage_date<@beforeDate
                       OR (d.damage_date=@beforeDate AND d.id<@beforeId))
                ORDER BY d.damage_date DESC,d.id DESC LIMIT @take
                """, new
            {
                from = filter.From,
                to = filter.To,
                reason = filter.Reason,
                pattern = Contains(filter.Query),
                beforeDate,
                beforeId,
                take = limit + 1
            }).Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            DamageItem? last = rows.LastOrDefault();
            return new DamagePage(rows, hasMore, last?.Date, last?.Id);
        }, cancellationToken);
    }

    /// <summary>Reads one keyset page of expenses (current values after corrections) with filters in SQL.</summary>
    public Task<ExpensePage> GetExpensePageAsync(ExpenseFilter filter, DateOnly? beforeDate, long? beforeId,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        EnsureRange(filter.From, filter.To);
        if (beforeDate is null != (beforeId is null) || limit is < 1 or > 200 ||
            filter.Status is not (null or "ACTIVE" or "CORRECTED" or "VOID" or "PURCHASE"))
            throw new DomainException(ErrorCode.Validation, "Invalid expense page filter.");
        return database.ReadAsync(connection =>
        {
            List<ExpenseItem> rows = connection.Query<ExpenseRow>(CurrentExpenseSql + CurrentExpenseColumns + """
                 WHERE (@beforeDate IS NULL OR x.date<@beforeDate OR (x.date=@beforeDate AND x.id<@beforeId))
                   AND (@status IS NULL OR x.status=@status)
                   AND (@categoryId IS NULL OR x.category_id=@categoryId)
                   AND (@pattern IS NULL OR x.paid_to LIKE @pattern ESCAPE '\' OR x.reference LIKE @pattern ESCAPE '\'
                        OR x.notes LIKE @pattern ESCAPE '\')
                ORDER BY x.date DESC,x.id DESC LIMIT @take
                """, new
            {
                from = filter.From,
                to = filter.To,
                status = filter.Status,
                categoryId = filter.CategoryId,
                pattern = Contains(filter.Query),
                beforeDate,
                beforeId,
                take = limit + 1
            }).Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            ExpenseItem? last = rows.LastOrDefault();
            return new ExpensePage(rows, hasMore, last?.Date, last?.Id);
        }, cancellationToken);
    }

    /// <summary>Lists employees matching the filters, by name. A shop's employee list is small and bounded.</summary>
    public Task<IReadOnlyList<EmployeeItem>> GetEmployeesAsync(EmployeeFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Status is not ("ACTIVE" or "INACTIVE" or "ALL"))
            throw new DomainException(ErrorCode.Validation, "Invalid employee filter.");
        return database.ReadAsync(connection => (IReadOnlyList<EmployeeItem>)connection.Query<EmployeeRow>("""
            SELECT id AS Id,name AS Name,phone AS Phone,role AS Role,join_date AS JoinDate,leave_date AS LeaveDate,
                   monthly_salary AS MonthlySalary,is_active AS IsActive,notes AS Notes FROM employee
            WHERE (@status='ALL' OR (@status='ACTIVE' AND is_active=1) OR (@status='INACTIVE' AND is_active=0))
              AND (@pattern IS NULL OR name LIKE @pattern ESCAPE '\' OR role LIKE @pattern ESCAPE '\'
                   OR phone LIKE @pattern ESCAPE '\')
            ORDER BY name COLLATE NOCASE,id LIMIT 500
            """, new { status = filter.Status, pattern = Contains(filter.Query) })
            .Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    /// <summary>Salary payments for a month range, optionally for one employee and status.</summary>
    public Task<IReadOnlyList<SalaryPaymentItem>> GetSalaryRegisterAsync(YearMonth from, YearMonth to, long? employeeId,
        string? status, CancellationToken cancellationToken = default)
    {
        if (from > to || status is not (null or "ACTIVE" or "CORRECTED" or "VOID"))
            throw new DomainException(ErrorCode.Validation, "Invalid salary period.");
        return database.ReadAsync(connection => (IReadOnlyList<SalaryPaymentItem>)connection.Query<SalaryRow>("""
            SELECT * FROM (
                SELECT s.id AS Id,s.employee_id AS EmployeeId,e.name AS EmployeeName,s.period_month AS Period,
                       COALESCE(sc.new_pay_date,s.pay_date) AS PayDate,s.kind AS Kind,
                       COALESCE(sc.new_amount,s.amount) AS Amount,COALESCE(sc.new_mode,s.mode) AS Mode,
                       CASE WHEN sc.id IS NULL THEN s.notes ELSE sc.new_notes END AS Notes,
                       CASE WHEN sc.id IS NULL THEN 'ACTIVE' WHEN sc.kind='VOID' THEN 'VOID' ELSE 'CORRECTED' END AS Status
                FROM salary_payment s JOIN employee e ON e.id=s.employee_id
                LEFT JOIN salary_correction sc ON sc.id=(SELECT MAX(id) FROM salary_correction WHERE salary_payment_id=s.id)
                WHERE s.period_month BETWEEN @from AND @to AND (@employeeId IS NULL OR s.employee_id=@employeeId)
            ) WHERE @status IS NULL OR Status=@status
            ORDER BY Period DESC,EmployeeName,Kind LIMIT 2000
            """, new { from = from.ToString(), to = to.ToString(), employeeId, status })
            .Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    private static string? Contains(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (trimmed.Length > 120) throw new DomainException(ErrorCode.Validation, "Search term is too long.");
        return "%" + trimmed.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
    }
}
