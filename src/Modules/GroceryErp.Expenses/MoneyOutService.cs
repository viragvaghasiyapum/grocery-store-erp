using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Expenses;

public sealed partial class MoneyOutService(
    DbExecutor database,
    ILotQuantities stock,
    DocNumberService numbers,
    PeriodGuard periods,
    IAuditLog audit)
{
    private static readonly HashSet<string> DamageReasons =
        ["EXPIRED", "BROKEN", "SPOILED", "PEST", "LOST", "CUSTOMER_RETURN", "OTHER"];
    private static readonly HashSet<string> ExpenseModes = ["CASH", "UPI", "BANK", "CARD", "OTHER"];
    private static readonly HashSet<string> SalaryModes = ["CASH", "UPI", "BANK", "OTHER"];

    // The Products badge, counted as of the business date: the dashboard's Expired and Critical lots
    // (ExpiryAlert), so it agrees with those tiles and the Products filters they open.
    private static readonly string ActionableExpirySql = $"""
        SELECT
          (SELECT COUNT(*) FROM product_lot l INDEXED BY ix_lot_expiry
           WHERE {ExpiryAlert.ExpiredSql("l", "@today")} AND l.expiry_date IS NOT NULL)
          +
          (SELECT COUNT(*) FROM product_lot l INDEXED BY ix_lot_expiry
           JOIN product p ON p.id=l.product_id
           WHERE {ExpiryAlert.ExpiringSql("l", "p", "@today")}
             AND l.expiry_date<={ExpiryAlert.CriticalEndSql("@today")} AND +p.is_active=1)
        """;

    public Task<int> GetActionableExpiryCountAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => connection.ExecuteScalar<int>(ActionableExpirySql,
            new { today = periods.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }),
            cancellationToken);

    public async Task<long> RecordDamageAsync(DamageInput input, string operationId, CancellationToken cancellationToken = default)
    {
        ValidateDamage(input);
        return await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.Damage);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(transaction, input.Date);
            LotRow? lot = transaction.Connection.QuerySingleOrDefault<LotRow>("""
                SELECT product_id AS ProductId,qty_on_hand AS OnHand,unit_cost AS UnitCost
                FROM product_lot WHERE id=@id
                """, new { id = input.LotId }, transaction.Transaction);
            if (lot is null) throw new DomainException(ErrorCode.NotFound, "lot", input.LotId);
            if (input.Quantity.Scaled > lot.OnHand) throw new DomainException(ErrorCode.InsufficientStock, input.Quantity);
            long productId = lot.ProductId;
            Money unitCost = Money.FromScaled(lot.UnitCost);
            Money cost = unitCost * input.Quantity;
            string document = numbers.Next(transaction, DocType.Damage, input.Date);
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO damage(doc_no,damage_date,product_id,lot_id,qty,unit_cost,cost_value,recovered_amount,reason,notes,
                    product_name_snapshot,product_sku_snapshot,product_name_local_snapshot,uom_snapshot,
                    product_class_snapshot,lot_code_snapshot,batch_no_snapshot,expiry_date_snapshot,
                    snapshot_provenance)
                SELECT @document,@date,p.id,l.id,@qty,@unitCost,@cost,@recovered,@reason,@notes,
                    p.name,p.sku,p.name_local,u.code,p.product_class,l.lot_code,l.batch_no,l.expiry_date,'AT_POST'
                FROM product_lot l JOIN product p ON p.id=l.product_id JOIN uom u ON u.id=p.uom_id
                WHERE l.id=@lotId RETURNING id
                """, new
            {
                document,
                date = Format(input.Date),
                productId,
                lotId = input.LotId,
                qty = input.Quantity.Scaled,
                unitCost = unitCost.Scaled,
                cost = cost.Scaled,
                recovered = input.RecoveredAmount.Scaled,
                reason = input.Reason,
                notes = Optional(input.Notes, 500),
            }, transaction.Transaction);
            stock.Change(transaction, new(input.Date, productId, input.LotId, LotChangeKind.Damage,
                new Quantity(-input.Quantity.Value)));
            audit.Write(transaction, "damage", id, "CREATE", new { document, quantity = input.Quantity.Scaled, cost = cost.Scaled });
            OperationGuard.Complete(transaction, operationId, "damage", id);
            return id;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ExpenseCategoryItem>> GetExpenseCategoriesAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<ExpenseCategoryItem>)connection.Query<CategoryRow>("""
            SELECT id AS Id,name AS Name,name_local AS LocalName,is_active AS IsActive FROM expense_category
            WHERE @includeInactive=1 OR is_active=1 ORDER BY name COLLATE NOCASE
            """, new { includeInactive = includeInactive ? 1 : 0 }).Select(static row => row.ToModel()).ToList(), cancellationToken);

    public Task<long> SaveExpenseCategoryAsync(long? id, string name, string? localName, bool active = true, CancellationToken cancellationToken = default)
    {
        string cleanName = Required(name, 100);
        string? cleanLocal = Optional(localName, 100);
        return database.WriteAsync(transaction =>
        {
            long result;
            if (id is null)
                result = transaction.Connection.ExecuteScalar<long>(
                    "INSERT INTO expense_category(name,name_local,is_active) VALUES (@cleanName,@cleanLocal,@active) RETURNING id",
                    new { cleanName, cleanLocal, active = active ? 1 : 0 }, transaction.Transaction);
            else
            {
                result = id.Value;
                int changed = transaction.Connection.Execute(
                    "UPDATE expense_category SET name=@cleanName,name_local=@cleanLocal,is_active=@active WHERE id=@id",
                    new { id, cleanName, cleanLocal, active = active ? 1 : 0 }, transaction.Transaction);
                if (changed == 0) throw new DomainException(ErrorCode.NotFound, "expense_category", id);
            }
            audit.Write(transaction, "expense_category", result, id is null ? "CREATE" : "UPDATE", new { cleanName, cleanLocal, active });
            return result;
        }, cancellationToken);
    }

    // An expense's current values are its newest correction or, when never corrected,
    // the original row. Both branches read their own date index.
    private const string CurrentExpenseSql = """
        WITH current AS (
          SELECT e.id,e.expense_date AS date,e.category_id,e.amount,e.mode,e.paid_to,e.reference,e.notes,
                 CASE WHEN EXISTS(SELECT 1 FROM purchase_expense_reversal r WHERE r.expense_id=e.id) THEN 'VOID'
                      WHEN e.purchase_line_id IS NOT NULL THEN 'PURCHASE' ELSE 'ACTIVE' END AS status
          FROM expense e
          WHERE e.expense_date BETWEEN @from AND @to
            AND NOT EXISTS(SELECT 1 FROM expense_correction c WHERE c.expense_id=e.id)
          UNION ALL
          SELECT c.expense_id,c.new_date,c.new_category_id,c.new_amount,c.new_mode,c.new_paid_to,
                 c.new_reference,c.new_notes,CASE c.kind WHEN 'VOID' THEN 'VOID' ELSE 'CORRECTED' END
          FROM expense_correction c
          WHERE c.new_date BETWEEN @from AND @to
            AND c.id=(SELECT MAX(id) FROM expense_correction x WHERE x.expense_id=c.expense_id)
        )
        """;

    private const string CurrentExpenseColumns = """
        SELECT x.id AS Id,x.date AS Date,x.category_id AS CategoryId,c.name AS CategoryName,
               x.amount AS Amount,x.mode AS Mode,x.paid_to AS PaidTo,x.reference AS Reference,x.notes AS Notes,
               x.status AS Status
        FROM current x JOIN expense_category c ON c.id=x.category_id
        """;

    public Task<long> RecordExpenseAsync(ExpenseInput input, string operationId, CancellationToken cancellationToken = default)
    {
        ValidateExpense(input);
        return database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.Expense);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(transaction, input.Date);
            long? id = transaction.Connection.QuerySingleOrDefault<long?>("""
                INSERT INTO expense(expense_date,category_id,amount,mode,paid_to,reference,notes)
                SELECT @date,id,@amount,@mode,@paidTo,@reference,@notes FROM expense_category
                WHERE id=@categoryId AND is_active=1 RETURNING id
                """, new
            {
                date = Format(input.Date),
                categoryId = input.CategoryId,
                amount = input.Amount.Scaled,
                mode = input.Mode,
                paidTo = Optional(input.PaidTo, 150),
                reference = Optional(input.Reference, 100),
                notes = Optional(input.Notes, 500),
            }, transaction.Transaction);
            if (id is null) throw new DomainException(ErrorCode.NotFound, "expense_category", input.CategoryId);
            audit.Write(transaction, "expense", id.Value, "CREATE", new { amount = input.Amount.Scaled, input.CategoryId });
            OperationGuard.Complete(transaction, operationId, "expense", id.Value);
            return id.Value;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ExpenseInput>> RepeatLastMonthAsync(
        YearMonth target, IReadOnlyCollection<long> categoryIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);
        if (categoryIds.Count == 0) return Task.FromResult<IReadOnlyList<ExpenseInput>>([]);
        YearMonth source = target.AddMonths(-1);
        return database.ReadAsync(connection => (IReadOnlyList<ExpenseInput>)connection.Query<ExpenseDraftRow>(
            CurrentExpenseSql + """
            SELECT category_id AS CategoryId,amount AS Amount,mode AS Mode,paid_to AS PaidTo,
                   reference AS Reference,notes AS Notes,CAST(strftime('%d',date) AS INTEGER) AS Day
            FROM current WHERE category_id IN @categoryIds AND status IN ('ACTIVE','CORRECTED')
            ORDER BY date,id
            """, new { from = source.FirstDay, to = source.LastDay, categoryIds }).Select(row => row.ToModel(target)).ToList(), cancellationToken);
    }

    public Task<IReadOnlyList<EmployeeItem>> GetEmployeesAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        GetEmployeesAsync(new EmployeeFilter { Status = includeInactive ? "ALL" : "ACTIVE" }, cancellationToken);

    public Task<long> SaveEmployeeAsync(long? id, EmployeeInput input, CancellationToken cancellationToken = default) =>
        SaveEmployeeAsync(id, input, null, cancellationToken);

    // A salary change is a dated term. It may start no earlier than the first month
    // whose calendar and fiscal years are both open; posted payments never change.
    public Task<long> SaveEmployeeAsync(long? id, EmployeeInput input, YearMonth? salaryEffectiveFrom,
        CancellationToken cancellationToken = default)
    {
        ValidateEmployee(input);
        return database.WriteAsync(transaction =>
        {
            long? previousSalary = id is null ? null : transaction.Connection.ExecuteScalar<long?>(
                "SELECT monthly_salary FROM employee WHERE id=@id", new { id }, transaction.Transaction);
            if (id is not null && previousSalary is null) throw new DomainException(ErrorCode.NotFound, "employee", id);
            YearMonth? termMonth = null;
            if (id is null)
                termMonth = input.JoinDate is { } joined ? YearMonth.From(joined) : YearMonth.From(periods.Today);
            else if (previousSalary != input.MonthlySalary.Scaled)
            {
                YearMonth firstOpen = periods.FirstOpenMonth(transaction.Connection, transaction.Transaction);
                termMonth = salaryEffectiveFrom ?? YearMonth.From(periods.Today);
                if (termMonth < firstOpen)
                    throw new DomainException(ErrorCode.EffectiveMonthClosed, firstOpen.ToString());
            }
            var parameters = new
            {
                id,
                name = Required(input.Name, 150),
                phone = Optional(input.Phone, 30),
                role = Optional(input.Role, 100),
                joinDate = input.JoinDate is null ? null : Format(input.JoinDate.Value),
                leaveDate = input.LeaveDate is null ? null : Format(input.LeaveDate.Value),
                salary = input.MonthlySalary.Scaled,
                active = input.IsActive ? 1 : 0,
                notes = Optional(input.Notes, 500),
            };
            long result;
            if (id is null)
                result = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO employee(name,phone,role,join_date,leave_date,monthly_salary,is_active,notes)
                    VALUES (@name,@phone,@role,@joinDate,@leaveDate,@salary,@active,@notes) RETURNING id
                    """, parameters, transaction.Transaction);
            else
            {
                result = id.Value;
                int changed = transaction.Connection.Execute("""
                    UPDATE employee SET name=@name,phone=@phone,role=@role,join_date=@joinDate,leave_date=@leaveDate,
                        monthly_salary=@salary,is_active=@active,notes=@notes WHERE id=@id
                    """, parameters, transaction.Transaction);
                if (changed == 0) throw new DomainException(ErrorCode.NotFound, "employee", id);
            }
            if (termMonth is { } effective)
                transaction.Connection.Execute("""
                    INSERT INTO employee_salary_term(employee_id,effective_month,monthly_salary,reason)
                    VALUES (@result,@month,@salary,@reason)
                    """, new
                {
                    result,
                    month = effective.ToString(),
                    salary = input.MonthlySalary.Scaled,
                    reason = id is null ? "INITIAL" : "CHANGE"
                }, transaction.Transaction);
            audit.Write(transaction, "employee", result, id is null ? "CREATE" : "UPDATE", new
            {
                input.Name,
                salary = input.MonthlySalary.Scaled,
                previousSalary,
                salaryEffectiveFrom = termMonth?.ToString(),
            });
            return result;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SalaryTermItem>> GetSalaryTermsAsync(long employeeId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<SalaryTermItem>)connection.Query<SalaryTermRow>("""
            SELECT id AS Id,effective_month AS EffectiveMonth,monthly_salary AS MonthlySalary,reason AS Reason,
                   created_at AS CreatedAt
            FROM employee_salary_term WHERE employee_id=@employeeId ORDER BY id DESC LIMIT 100
            """, new { employeeId }).Select(static row => row.ToModel()).ToList(), cancellationToken);

    public Task<SalaryTermPreview> PreviewSalaryTermAsync(long employeeId, YearMonth? effectiveFrom,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        YearMonth firstOpen = periods.FirstOpenMonth(connection, null);
        YearMonth effective = effectiveFrom ?? YearMonth.From(periods.Today);
        int posted = connection.ExecuteScalar<int>("""
            SELECT COUNT(DISTINCT period_month) FROM salary_payment
            WHERE employee_id=@employeeId AND period_month>=@month AND kind='SALARY'
            """, new { employeeId, month = effective.ToString() });
        return new SalaryTermPreview(effective, firstOpen, posted);
    }, cancellationToken);

    // The newest term effective on or before the month pre-fills it.
    public Task<IReadOnlyList<SalaryRunLine>> PrepareSalaryRunAsync(YearMonth period, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<SalaryRunLine>)connection.Query<SalaryPrepareRow>("""
            SELECT e.id AS EmployeeId,
                   COALESCE((SELECT t.monthly_salary FROM employee_salary_term t
                             WHERE t.employee_id=e.id AND t.effective_month<=@period
                             ORDER BY t.id DESC LIMIT 1),e.monthly_salary) AS BaseSalary
            FROM employee e
            WHERE e.is_active=1 AND (e.join_date IS NULL OR e.join_date<=@to) AND (e.leave_date IS NULL OR e.leave_date>=@from)
              AND NOT EXISTS (SELECT 1 FROM salary_payment s WHERE s.employee_id=e.id AND s.period_month=@period AND s.kind IN ('SALARY','BONUS','OVERTIME'))
            ORDER BY e.name COLLATE NOCASE
            """, new { from = period.FirstDay, to = period.LastDay, period = period.ToString() })
            .Select(static row => new SalaryRunLine(row.EmployeeId, Money.FromScaled(row.BaseSalary), Money.Zero, Money.Zero, Money.Zero, null)).ToList(), cancellationToken);

    public Task<long> PostSalaryRunAsync(SalaryRunInput input, string operationId, CancellationToken cancellationToken = default)
    {
        ValidateSalaryRun(input);
        return database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.SalaryRun);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(transaction, input.Period.LastDay);
            long firstId = 0;
            foreach (SalaryRunLine line in input.Lines)
            {
                bool alreadyPaid = transaction.Connection.ExecuteScalar<long>("""
                    SELECT EXISTS(SELECT 1 FROM salary_payment
                                  WHERE employee_id=@employeeId AND period_month=@period
                                    AND kind IN ('SALARY','BONUS','OVERTIME'))
                    """, new { employeeId = line.EmployeeId, period = input.Period.ToString() }, transaction.Transaction) != 0;
                if (alreadyPaid)
                    throw new DomainException(ErrorCode.SalaryAlreadyPosted, transaction.Connection.ExecuteScalar<string>(
                        "SELECT name FROM employee WHERE id=@id", new { id = line.EmployeeId }, transaction.Transaction) ?? "");
                Money salary = line.BaseSalary - line.LeaveDeduction;
                if (salary > Money.Zero) firstId = InsertSalary(transaction, line.EmployeeId, input, "SALARY", salary, line.Notes, firstId);
                if (line.Overtime > Money.Zero) firstId = InsertSalary(transaction, line.EmployeeId, input, "OVERTIME", line.Overtime, line.Notes, firstId);
                if (line.Bonus > Money.Zero) firstId = InsertSalary(transaction, line.EmployeeId, input, "BONUS", line.Bonus, line.Notes, firstId);
            }
            audit.Write(transaction, "salary_run", firstId, "POST", new { period = input.Period.ToString(), employees = input.Lines.Count });
            OperationGuard.Complete(transaction, operationId, "salary_payment", firstId);
            return firstId;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SalaryPaymentItem>> GetSalaryRegisterAsync(YearMonth from, YearMonth to, CancellationToken cancellationToken = default) =>
        GetSalaryRegisterAsync(from, to, null, null, cancellationToken);

    private static long InsertSalary(DbTx transaction, long employeeId, SalaryRunInput run, string kind, Money amount, string? notes, long firstId)
    {
        var parameters = new DynamicParameters();
        parameters.Add("employeeId", employeeId);
        parameters.Add("period", run.Period.ToString());
        parameters.Add("payDate", Format(run.PayDate));
        parameters.Add("kind", kind);
        parameters.Add("amount", amount.Scaled);
        parameters.Add("mode", run.Mode);
        parameters.Add("notes", Optional(notes, 500));
        long id = transaction.Connection.ExecuteScalar<long>("""
            INSERT INTO salary_payment(employee_id,period_month,pay_date,kind,amount,mode,notes)
            VALUES (@employeeId,@period,@payDate,@kind,@amount,@mode,@notes) RETURNING id
            """, parameters, transaction.Transaction);
        return firstId == 0 ? id : firstId;
    }

    private static void ValidateDamage(DamageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.LotId <= 0 || input.Quantity <= Quantity.Zero || input.RecoveredAmount < Money.Zero ||
            !DamageReasons.Contains(input.Reason))
            throw new DomainException(ErrorCode.Validation, "Invalid damage entry.");
    }

    private static void ValidateExpense(ExpenseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.CategoryId <= 0 || input.Amount <= Money.Zero || !ExpenseModes.Contains(input.Mode))
            throw new DomainException(ErrorCode.Validation, "Invalid expense entry.");
    }

    private static void ValidateEmployee(EmployeeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Required(input.Name, 150);
        if (input.MonthlySalary < Money.Zero || input.LeaveDate < input.JoinDate)
            throw new DomainException(ErrorCode.Validation, "Invalid employee.");
    }

    private static void ValidateSalaryRun(SalaryRunInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Lines.Count == 0 || !SalaryModes.Contains(input.Mode) || input.Lines.Select(static line => line.EmployeeId).Distinct().Count() != input.Lines.Count)
            throw new DomainException(ErrorCode.Validation, "Invalid salary run.");
        foreach (SalaryRunLine line in input.Lines)
            if (line.EmployeeId <= 0 || line.BaseSalary < Money.Zero || line.LeaveDeduction < Money.Zero ||
                line.LeaveDeduction > line.BaseSalary || line.Overtime < Money.Zero || line.Bonus < Money.Zero ||
                line.BaseSalary - line.LeaveDeduction + line.Overtime + line.Bonus <= Money.Zero)
                throw new DomainException(ErrorCode.Validation, "Invalid salary line.");
    }

    private static void EnsureRange(DateOnly from, DateOnly to)
    {
        if (from > to) throw new DomainException(ErrorCode.Validation, "Invalid date range.");
    }

    private static string Required(string? value, int maximum)
    {
        string clean = value?.Trim() ?? "";
        if (clean.Length == 0 || clean.Length > maximum) throw new DomainException(ErrorCode.Validation, "Required text is invalid.");
        return clean;
    }

    private static string? Optional(string? value, int maximum)
    {
        string? clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (clean?.Length > maximum) throw new DomainException(ErrorCode.Validation, "Text is too long.");
        return clean;
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class LotRow { public long ProductId { get; init; } public long OnHand { get; init; } public long UnitCost { get; init; } }
    private sealed class BulkLotRow { public long LotId { get; init; } public long ProductId { get; init; } public long OnHand { get; init; } public long UnitCost { get; init; } public DateOnly? ExpiryDate { get; init; } }
    private sealed class DamageRow { public long Id { get; init; } public string DocumentNumber { get; init; } = ""; public DateOnly Date { get; init; } public string ProductName { get; init; } = ""; public string LotCode { get; init; } = ""; public long Quantity { get; init; } public long UnitCost { get; init; } public long CostValue { get; init; } public long RecoveredAmount { get; init; } public string Reason { get; init; } = ""; public string? Notes { get; init; } public string Status { get; init; } = "ACTIVE"; public DamageItem ToModel() => new(Id, DocumentNumber, Date, ProductName, LotCode, GroceryErp.Core.Numerics.Quantity.FromScaled(Quantity), Money.FromScaled(UnitCost), Money.FromScaled(CostValue), Money.FromScaled(RecoveredAmount), Reason, Notes, Status); }
    private sealed class CategoryRow { public long Id { get; init; } public string Name { get; init; } = ""; public string? LocalName { get; init; } public bool IsActive { get; init; } public ExpenseCategoryItem ToModel() => new(Id, Name, LocalName, IsActive); }
    private sealed class ExpenseRow { public long Id { get; init; } public DateOnly Date { get; init; } public long CategoryId { get; init; } public string CategoryName { get; init; } = ""; public long Amount { get; init; } public string Mode { get; init; } = ""; public string? PaidTo { get; init; } public string? Reference { get; init; } public string? Notes { get; init; } public string Status { get; init; } = "ACTIVE"; public ExpenseItem ToModel() => new(Id, Date, CategoryId, CategoryName, Money.FromScaled(Amount), Mode, PaidTo, Reference, Notes, Status); }
    private sealed class ExpenseDraftRow { public long CategoryId { get; init; } public long Amount { get; init; } public string Mode { get; init; } = ""; public string? PaidTo { get; init; } public string? Reference { get; init; } public string? Notes { get; init; } public int Day { get; init; } public ExpenseInput ToModel(YearMonth target) => new(new DateOnly(target.Year, target.Month, Math.Min(Day, DateTime.DaysInMonth(target.Year, target.Month))), CategoryId, Money.FromScaled(Amount), Mode, PaidTo, Reference, Notes); }
    private sealed class EmployeeRow { public long Id { get; init; } public string Name { get; init; } = ""; public string? Phone { get; init; } public string? Role { get; init; } public DateOnly? JoinDate { get; init; } public DateOnly? LeaveDate { get; init; } public long MonthlySalary { get; init; } public bool IsActive { get; init; } public string? Notes { get; init; } public EmployeeItem ToModel() => new(Id, Name, Phone, Role, JoinDate, LeaveDate, Money.FromScaled(MonthlySalary), IsActive, Notes); }
    private sealed class SalaryPrepareRow { public long EmployeeId { get; init; } public long BaseSalary { get; init; } }
    private sealed class SalaryRow { public long Id { get; init; } public long EmployeeId { get; init; } public string EmployeeName { get; init; } = ""; public string Period { get; init; } = ""; public DateOnly PayDate { get; init; } public string Kind { get; init; } = ""; public long Amount { get; init; } public string Mode { get; init; } = ""; public string? Notes { get; init; } public string Status { get; init; } = "ACTIVE"; public SalaryPaymentItem ToModel() => new(Id, EmployeeId, EmployeeName, YearMonth.Parse(Period), PayDate, Kind, Money.FromScaled(Amount), Mode, Notes, Status); }
    private sealed class SalaryTermRow { public long Id { get; init; } public string EffectiveMonth { get; init; } = ""; public long MonthlySalary { get; init; } public string? Reason { get; init; } public DateTime CreatedAt { get; init; } public SalaryTermItem ToModel() => new(Id, YearMonth.Parse(EffectiveMonth), Money.FromScaled(MonthlySalary), Reason, CreatedAt); }
}
