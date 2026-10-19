using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Messages;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Expenses;

public sealed partial class MoneyOutService(
    DbExecutor database,
    IStockLedger stock,
    DocNumberService numbers,
    PeriodGuard periods,
    IAuditLog audit)
{
    private static readonly HashSet<string> DamageReasons =
        ["EXPIRED", "BROKEN", "SPOILED", "PEST", "LOST", "CUSTOMER_RETURN", "OTHER"];
    private static readonly HashSet<string> ExpenseModes = ["CASH", "UPI", "BANK", "CARD", "OTHER"];
    private static readonly HashSet<string> SalaryModes = ["CASH", "UPI", "BANK", "OTHER"];

    public Task<int> GetActionableExpiryCountAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => connection.ExecuteScalar<int>("""
            SELECT
              (SELECT COUNT(*) FROM product_lot l INDEXED BY ix_lot_expiry
               WHERE l.qty_on_hand>0 AND l.is_active=1 AND l.expiry_date IS NOT NULL
                 AND l.expiry_date<date('now','localtime'))
              +
              (SELECT COUNT(*) FROM product_lot l INDEXED BY ix_lot_expiry
               JOIN product p ON p.id=l.product_id
               JOIN category c ON c.id=p.category_id
               LEFT JOIN category pc ON pc.id=c.parent_id
               WHERE l.qty_on_hand>0 AND l.is_active=1 AND l.expiry_date IS NOT NULL
                 AND l.expiry_date BETWEEN date('now','localtime') AND date('now','localtime','+7 days')
                 AND l.expiry_date<=date('now','localtime','+'||COALESCE(
                     p.expiry_alert_days,c.expiry_alert_days,pc.expiry_alert_days,
                     CAST((SELECT value FROM setting WHERE key='expiry.default_alert_days') AS INTEGER),30
                 )||' days'))
            """), cancellationToken);

    public Task<IReadOnlyList<ExpiryLot>> GetExpiryAsync(string? bucket = null, CancellationToken cancellationToken = default)
    {
        if (bucket is not null && bucket is not ("EXPIRED" or "CRITICAL" or "SOON"))
            throw new DomainException(ErrorCode.Validation, nameof(bucket));
        return database.ReadAsync(connection => (IReadOnlyList<ExpiryLot>)connection.Query<ExpiryRow>("""
            SELECT e.lot_id AS LotId,e.product_id AS ProductId,e.sku AS Sku,e.name AS ProductName,
                   e.name_local AS ProductNameLocal,
                   e.lot_code AS LotCode,e.batch_no AS BatchNumber,e.expiry_date AS ExpiryDate,
                   e.days_left AS DaysLeft,e.qty_on_hand AS Quantity,e.value_at_cost AS CostValue,
                   e.bucket AS Bucket,e.vendor_id AS VendorId,v.name AS VendorName
            FROM v_expiry_alert e LEFT JOIN vendor v ON v.id=e.vendor_id
            WHERE @bucket IS NULL OR e.bucket=@bucket
            ORDER BY e.expiry_date,e.name COLLATE NOCASE,e.lot_id
            """, new { bucket }).Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    public Task<ExpiryPage> GetExpiryPageAsync(string bucket, DateOnly? afterDate = null,
        long? afterId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (bucket is not ("EXPIRED" or "CRITICAL" or "SOON") ||
            afterDate is null != (afterId is null) || limit is < 1 or > 200)
            throw new DomainException(ErrorCode.Validation, "Invalid expiry page filter.");
        return database.ReadAsync(connection =>
        {
            List<ExpiryLot> rows = connection.Query<ExpiryRow>("""
                SELECT e.lot_id AS LotId,e.product_id AS ProductId,e.sku AS Sku,e.name AS ProductName,
                       e.name_local AS ProductNameLocal,e.lot_code AS LotCode,e.batch_no AS BatchNumber,
                       e.expiry_date AS ExpiryDate,e.days_left AS DaysLeft,e.qty_on_hand AS Quantity,
                       e.value_at_cost AS CostValue,e.bucket AS Bucket,e.vendor_id AS VendorId,
                       v.name AS VendorName
                FROM v_expiry_alert e LEFT JOIN vendor v ON v.id=e.vendor_id
                WHERE e.bucket=@bucket AND (@afterDate IS NULL OR e.expiry_date>@afterDate
                      OR (e.expiry_date=@afterDate AND e.lot_id>@afterId))
                ORDER BY e.expiry_date,e.lot_id LIMIT @take
                """, new { bucket, afterDate, afterId, take = limit + 1 })
                .Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            ExpiryLot? last = rows.LastOrDefault();
            return new ExpiryPage(rows, hasMore, last?.ExpiryDate, last?.LotId);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DamageItem>> GetDamagesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        EnsureRange(from, to);
        return database.ReadAsync(connection => (IReadOnlyList<DamageItem>)connection.Query<DamageRow>("""
            SELECT d.id AS Id,d.doc_no AS DocumentNumber,d.damage_date AS Date,
                   COALESCE(d.product_name_snapshot,'Legacy unknown') AS ProductName,
                   COALESCE(d.lot_code_snapshot,'Legacy unknown') AS LotCode,
                   COALESCE(dc.new_qty,d.qty) AS Quantity,d.unit_cost AS UnitCost,
                   CAST(ROUND(COALESCE(dc.new_qty,d.qty)*d.unit_cost/1000.0) AS INTEGER) AS CostValue,
                   COALESCE(dc.new_recovered,d.recovered_amount) AS RecoveredAmount,
                   COALESCE(dc.new_reason,d.reason) AS Reason,
                   CASE WHEN dc.id IS NULL THEN d.notes ELSE dc.new_notes END AS Notes
            FROM damage d
            LEFT JOIN damage_correction dc ON dc.id=(SELECT MAX(id) FROM damage_correction WHERE damage_id=d.id)
            WHERE d.damage_date BETWEEN @from AND @to ORDER BY d.damage_date DESC,d.id DESC
            """, new { from, to }).Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    public Task<DamagePage> GetDamagePageAsync(DateOnly from, DateOnly to, DateOnly? beforeDate = null,
        long? beforeId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        EnsureRange(from, to);
        if (beforeDate is null != (beforeId is null) || limit is < 1 or > 200)
            throw new DomainException(ErrorCode.Validation, "Invalid damage page filter.");
        return database.ReadAsync(connection =>
        {
            List<DamageItem> rows = connection.Query<DamageRow>("""
                SELECT d.id AS Id,d.doc_no AS DocumentNumber,d.damage_date AS Date,
                       COALESCE(d.product_name_snapshot,'Legacy unknown') AS ProductName,
                       COALESCE(d.lot_code_snapshot,'Legacy unknown') AS LotCode,
                       COALESCE(dc.new_qty,d.qty) AS Quantity,d.unit_cost AS UnitCost,
                       CAST(ROUND(COALESCE(dc.new_qty,d.qty)*d.unit_cost/1000.0) AS INTEGER) AS CostValue,
                       COALESCE(dc.new_recovered,d.recovered_amount) AS RecoveredAmount,
                       COALESCE(dc.new_reason,d.reason) AS Reason,
                       CASE WHEN dc.id IS NULL THEN d.notes ELSE dc.new_notes END AS Notes
                FROM damage d
                LEFT JOIN damage_correction dc ON dc.id=(SELECT MAX(id) FROM damage_correction WHERE damage_id=d.id)
                WHERE d.damage_date BETWEEN @from AND @to
                  AND (@beforeDate IS NULL OR d.damage_date<@beforeDate
                       OR (d.damage_date=@beforeDate AND d.id<@beforeId))
                ORDER BY d.damage_date DESC,d.id DESC LIMIT @take
                """, new { from, to, beforeDate, beforeId, take = limit + 1 })
                .Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            DamageItem? last = rows.LastOrDefault();
            return new DamagePage(rows, hasMore, last?.Date, last?.Id);
        }, cancellationToken);
    }

    public async Task<long> CorrectDamageAsync(DamageItem original, DamageCorrection replacement,
        DateOnly correctionDate, string reason, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Quantity < Quantity.Zero || replacement.RecoveredAmount < Money.Zero ||
            !DamageReasons.Contains(replacement.Reason))
            throw new DomainException(ErrorCode.Validation, "Invalid damage correction.");
        string correctionReason = Required(reason, 240);
        string? notes = Optional(replacement.Notes, 500);
        long productId = 0;
        long result = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.DamageCorrection);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(correctionDate);
            DamageStateRow? current = transaction.Connection.QuerySingleOrDefault<DamageStateRow>("""
                SELECT d.product_id AS ProductId,d.lot_id AS LotId,d.unit_cost AS UnitCost,
                       COALESCE(dc.new_qty,d.qty) AS Quantity,
                       COALESCE(dc.new_recovered,d.recovered_amount) AS RecoveredAmount,
                       COALESCE(dc.new_reason,d.reason) AS Reason,
                       CASE WHEN dc.id IS NULL THEN d.notes ELSE dc.new_notes END AS Notes
                FROM damage d
                LEFT JOIN damage_correction dc ON dc.id=(SELECT MAX(id) FROM damage_correction WHERE damage_id=d.id)
                WHERE d.id=@id
                """, new { id = original.Id }, transaction.Transaction);
            if (current is null) throw new DomainException(ErrorCode.NotFound, "damage", original.Id);
            if (current.Quantity != original.Quantity.Scaled || current.RecoveredAmount != original.RecoveredAmount.Scaled ||
                current.UnitCost != original.UnitCost.Scaled || current.Reason != original.Reason || current.Notes != original.Notes)
                throw new DomainException(ErrorCode.Validation, "This damage entry changed since it was loaded. Refresh and try again.");
            productId = current.ProductId;
            Quantity previousQty = Quantity.FromScaled(current.Quantity);
            Money unitCost = Money.FromScaled(current.UnitCost);
            Money previousExpense = unitCost * previousQty - Money.FromScaled(current.RecoveredAmount);
            Money newExpense = unitCost * replacement.Quantity - replacement.RecoveredAmount;
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO damage_correction(damage_id,correction_date,previous_qty,new_qty,
                    previous_recovered,new_recovered,new_reason,new_notes,expense_delta,correction_reason)
                VALUES (@damageId,@date,@previousQty,@newQty,@previousRecovered,@newRecovered,
                    @newReason,@notes,@delta,@correctionReason) RETURNING id
                """, new { damageId = original.Id, date = Format(correctionDate), previousQty = current.Quantity,
                    newQty = replacement.Quantity.Scaled, previousRecovered = current.RecoveredAmount,
                    newRecovered = replacement.RecoveredAmount.Scaled, newReason = replacement.Reason,
                    notes, delta = (newExpense - previousExpense).Scaled, correctionReason }, transaction.Transaction);
            Quantity stockChange = new(previousQty.Value - replacement.Quantity.Value);
            if (stockChange != Quantity.Zero)
                stock.Post(transaction, new(correctionDate, current.ProductId, current.LotId, "ADJUSTMENT",
                    stockChange, unitCost, "damage_correction", id, correctionReason));
            audit.Write(transaction, "damage", original.Id, "CORRECT", new { correctionId = id,
                reason = correctionReason, before = original, after = replacement,
                expenseDelta = (newExpense - previousExpense).Scaled });
            OperationGuard.Complete(transaction, operationId, "damage_correction", id);
            return id;
        }, cancellationToken);
        if (productId > 0) WeakReferenceMessenger.Default.Send(new StockChangedMessage([productId]));
        return result;
    }

    public async Task<long> RecordDamageAsync(DamageInput input, string operationId, CancellationToken cancellationToken = default)
    {
        ValidateDamage(input);
        long productId = 0;
        long result = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.Damage);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(input.Date);
            LotRow? lot = transaction.Connection.QuerySingleOrDefault<LotRow>("""
                SELECT product_id AS ProductId,qty_on_hand AS OnHand,unit_cost AS UnitCost
                FROM product_lot WHERE id=@id
                """, new { id = input.LotId }, transaction.Transaction);
            if (lot is null) throw new DomainException(ErrorCode.NotFound, "lot", input.LotId);
            if (input.Quantity.Scaled > lot.OnHand) throw new DomainException(ErrorCode.InsufficientStock, input.Quantity);
            productId = lot.ProductId;
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
            stock.Post(transaction, new(input.Date, productId, input.LotId, "DAMAGE", new Quantity(-input.Quantity.Value),
                unitCost, "damage", id, input.Reason));
            audit.Write(transaction, "damage", id, "CREATE", new { document, quantity = input.Quantity.Scaled, cost = cost.Scaled });
            OperationGuard.Complete(transaction, operationId, "damage", id);
            return id;
        }, cancellationToken);
        if (productId > 0) WeakReferenceMessenger.Default.Send(new StockChangedMessage([productId]));
        return result;
    }

    public async Task<IReadOnlyList<long>> WriteOffExpiredAsync(
        IReadOnlyCollection<long> lotIds, DateOnly date, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lotIds);
        if (lotIds.Count == 0 || lotIds.Any(static id => id <= 0))
            throw new DomainException(ErrorCode.Validation, "Select at least one lot.");
        var products = new HashSet<long>();
        IReadOnlyList<long> result = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.DamageBulk);
            if (replay is not null)
                return (IReadOnlyList<long>)transaction.Connection.Query<long>(
                    "SELECT id FROM damage WHERE bulk_operation_id=@operationId ORDER BY id", new { operationId }, transaction.Transaction).ToList();
            periods.EnsureOpen(date);
            List<BulkLotRow> lots = transaction.Connection.Query<BulkLotRow>("""
                SELECT id AS LotId,product_id AS ProductId,qty_on_hand AS OnHand,unit_cost AS UnitCost,expiry_date AS ExpiryDate
                FROM product_lot WHERE id IN @lotIds ORDER BY id
                """, new { lotIds }, transaction.Transaction).ToList();
            if (lots.Count != lotIds.Distinct().Count()) throw new DomainException(ErrorCode.NotFound, "lot");
            var ids = new List<long>(lots.Count);
            foreach (BulkLotRow lot in lots)
            {
                if (lot.OnHand <= 0 || lot.ExpiryDate is null || lot.ExpiryDate >= date)
                    throw new DomainException(ErrorCode.Validation, "Only expired lots with stock can be written off in bulk.");
                Quantity quantity = Quantity.FromScaled(lot.OnHand);
                Money unitCost = Money.FromScaled(lot.UnitCost);
                Money cost = unitCost * quantity;
                string document = numbers.Next(transaction, DocType.Damage, date);
                var parameters = new DynamicParameters();
                parameters.Add("document", document);
                parameters.Add("date", Format(date));
                parameters.Add("productId", lot.ProductId);
                parameters.Add("lotId", lot.LotId);
                parameters.Add("qty", quantity.Scaled);
                parameters.Add("unitCost", unitCost.Scaled);
                parameters.Add("cost", cost.Scaled);
                parameters.Add("notes", "Bulk expiry write-off");
                parameters.Add("operationId", operationId);
                long id = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO damage(doc_no,damage_date,product_id,lot_id,qty,unit_cost,cost_value,recovered_amount,
                                       reason,notes,bulk_operation_id,product_name_snapshot,product_sku_snapshot,
                                       product_name_local_snapshot,uom_snapshot,product_class_snapshot,lot_code_snapshot,
                                       batch_no_snapshot,expiry_date_snapshot,snapshot_provenance)
                    SELECT @document,@date,p.id,l.id,@qty,@unitCost,@cost,0,'EXPIRED',@notes,@operationId,
                           p.name,p.sku,p.name_local,u.code,p.product_class,l.lot_code,l.batch_no,
                           l.expiry_date,'AT_POST'
                    FROM product_lot l JOIN product p ON p.id=l.product_id JOIN uom u ON u.id=p.uom_id
                    WHERE l.id=@lotId
                    RETURNING id
                    """, parameters, transaction.Transaction);
                stock.Post(transaction, new(date, lot.ProductId, lot.LotId, "DAMAGE", new Quantity(-quantity.Value),
                    unitCost, "damage", id, "EXPIRED"));
                audit.Write(transaction, "damage", id, "CREATE", new { document, bulk = operationId, quantity = quantity.Scaled });
                ids.Add(id);
                products.Add(lot.ProductId);
            }
            OperationGuard.Complete(transaction, operationId, "damage", ids[0]);
            return (IReadOnlyList<long>)ids;
        }, cancellationToken);
        if (products.Count > 0) WeakReferenceMessenger.Default.Send(new StockChangedMessage(products.ToArray()));
        return result;
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

    public Task<IReadOnlyList<ExpenseItem>> GetExpensesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        EnsureRange(from, to);
        return database.ReadAsync(connection => (IReadOnlyList<ExpenseItem>)connection.Query<ExpenseRow>("""
            SELECT e.id AS Id,e.expense_date AS Date,e.category_id AS CategoryId,c.name AS CategoryName,
                   e.amount AS Amount,e.mode AS Mode,e.paid_to AS PaidTo,e.reference AS Reference,e.notes AS Notes
            FROM expense e JOIN expense_category c ON c.id=e.category_id
            WHERE e.expense_date BETWEEN @from AND @to ORDER BY e.expense_date DESC,e.id DESC
            """, new { from, to }).Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    public Task<ExpensePage> GetExpensePageAsync(DateOnly from, DateOnly to, DateOnly? beforeDate = null,
        long? beforeId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        EnsureRange(from, to);
        if (beforeDate is null != (beforeId is null) || limit is < 1 or > 200)
            throw new DomainException(ErrorCode.Validation, "Invalid expense page filter.");
        return database.ReadAsync(connection =>
        {
            List<ExpenseItem> rows = connection.Query<ExpenseRow>("""
                SELECT e.id AS Id,e.expense_date AS Date,e.category_id AS CategoryId,c.name AS CategoryName,
                       e.amount AS Amount,e.mode AS Mode,e.paid_to AS PaidTo,e.reference AS Reference,e.notes AS Notes
                FROM expense e JOIN expense_category c ON c.id=e.category_id
                WHERE e.expense_date BETWEEN @from AND @to
                  AND (@beforeDate IS NULL OR e.expense_date<@beforeDate
                       OR (e.expense_date=@beforeDate AND e.id<@beforeId))
                ORDER BY e.expense_date DESC,e.id DESC LIMIT @take
                """, new { from, to, beforeDate, beforeId, take = limit + 1 })
                .Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            ExpenseItem? last = rows.LastOrDefault();
            return new ExpensePage(rows, hasMore, last?.Date, last?.Id);
        }, cancellationToken);
    }

    public Task<long> RecordExpenseAsync(ExpenseInput input, string operationId, CancellationToken cancellationToken = default)
    {
        ValidateExpense(input);
        return database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.Expense);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(input.Date);
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

    public Task CorrectExpenseAsync(ExpenseItem original, ExpenseInput replacement, string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ValidateExpense(replacement);
        string note = Required(reason, 240);
        return database.WriteAsync(transaction =>
        {
            periods.EnsureOpen(original.Date);
            periods.EnsureOpen(replacement.Date);
            ExpenseRow? current = transaction.Connection.QuerySingleOrDefault<ExpenseRow>("""
                SELECT e.id AS Id,e.expense_date AS Date,e.category_id AS CategoryId,c.name AS CategoryName,
                       e.amount AS Amount,e.mode AS Mode,e.paid_to AS PaidTo,e.reference AS Reference,e.notes AS Notes
                FROM expense e JOIN expense_category c ON c.id=e.category_id WHERE e.id=@id
                """, new { id = original.Id }, transaction.Transaction);
            if (current is null) throw new DomainException(ErrorCode.NotFound, "expense", original.Id);
            if (transaction.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM expense WHERE id=@id AND purchase_line_id IS NOT NULL",
                new { id = original.Id }, transaction.Transaction) != 0)
                throw new DomainException(ErrorCode.Validation, "Purchase expense lines cannot be edited; void the untouched purchase.");
            if (current.ToModel() != original)
                throw new DomainException(ErrorCode.Validation, "This expense changed since it was loaded. Refresh and try again.");
            int changed = transaction.Connection.Execute("""
                UPDATE expense SET expense_date=@date,category_id=@categoryId,amount=@amount,
                    mode=@mode,paid_to=@paidTo,reference=@reference,notes=@notes
                WHERE id=@id AND EXISTS(SELECT 1 FROM expense_category
                                        WHERE id=@categoryId AND is_active=1)
                """, new { id = original.Id, date = Format(replacement.Date), categoryId = replacement.CategoryId,
                    amount = replacement.Amount.Scaled, mode = replacement.Mode,
                    paidTo = Optional(replacement.PaidTo, 150), reference = Optional(replacement.Reference, 100),
                    notes = Optional(replacement.Notes, 500) }, transaction.Transaction);
            if (changed != 1) throw new DomainException(ErrorCode.NotFound, "active expense category", replacement.CategoryId);
            audit.Write(transaction, "expense", original.Id, "CORRECT", new { reason = note, before = original, after = replacement });
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ExpenseInput>> RepeatLastMonthAsync(
        YearMonth target, IReadOnlyCollection<long> categoryIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);
        if (categoryIds.Count == 0) return Task.FromResult<IReadOnlyList<ExpenseInput>>([]);
        YearMonth source = target.AddMonths(-1);
        return database.ReadAsync(connection => (IReadOnlyList<ExpenseInput>)connection.Query<ExpenseDraftRow>("""
            SELECT category_id AS CategoryId,amount AS Amount,mode AS Mode,paid_to AS PaidTo,
                   reference AS Reference,notes AS Notes,CAST(strftime('%d',expense_date) AS INTEGER) AS Day
            FROM expense WHERE expense_date BETWEEN @from AND @to AND category_id IN @categoryIds ORDER BY expense_date,id
            """, new { from = source.FirstDay, to = source.LastDay, categoryIds }).Select(row => row.ToModel(target)).ToList(), cancellationToken);
    }

    public Task<IReadOnlyList<EmployeeItem>> GetEmployeesAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<EmployeeItem>)connection.Query<EmployeeRow>("""
            SELECT id AS Id,name AS Name,phone AS Phone,role AS Role,join_date AS JoinDate,leave_date AS LeaveDate,
                   monthly_salary AS MonthlySalary,is_active AS IsActive,notes AS Notes FROM employee
            WHERE @includeInactive=1 OR is_active=1 ORDER BY name COLLATE NOCASE
            """, new { includeInactive = includeInactive ? 1 : 0 }).Select(static row => row.ToModel()).ToList(), cancellationToken);

    public Task<long> SaveEmployeeAsync(long? id, EmployeeInput input, CancellationToken cancellationToken = default)
    {
        ValidateEmployee(input);
        return database.WriteAsync(transaction =>
        {
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
            audit.Write(transaction, "employee", result, id is null ? "CREATE" : "UPDATE", new { input.Name, salary = input.MonthlySalary.Scaled });
            return result;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SalaryRunLine>> PrepareSalaryRunAsync(YearMonth period, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<SalaryRunLine>)connection.Query<SalaryPrepareRow>("""
            SELECT id AS EmployeeId,monthly_salary AS BaseSalary FROM employee
            WHERE is_active=1 AND (join_date IS NULL OR join_date<=@to) AND (leave_date IS NULL OR leave_date>=@from)
              AND NOT EXISTS (SELECT 1 FROM salary_payment s WHERE s.employee_id=employee.id AND s.period_month=@period AND s.kind IN ('SALARY','BONUS','OVERTIME'))
            ORDER BY name COLLATE NOCASE
            """, new { from = period.FirstDay, to = period.LastDay, period = period.ToString() })
            .Select(static row => new SalaryRunLine(row.EmployeeId, Money.FromScaled(row.BaseSalary), Money.Zero, Money.Zero, Money.Zero, null)).ToList(), cancellationToken);

    public Task<long> PostSalaryRunAsync(SalaryRunInput input, string operationId, CancellationToken cancellationToken = default)
    {
        ValidateSalaryRun(input);
        return database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.SalaryRun);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(input.Period.LastDay);
            long firstId = 0;
            foreach (SalaryRunLine line in input.Lines)
            {
                bool alreadyPaid = transaction.Connection.ExecuteScalar<long>("""
                    SELECT EXISTS(SELECT 1 FROM salary_payment
                                  WHERE employee_id=@employeeId AND period_month=@period
                                    AND kind IN ('SALARY','BONUS','OVERTIME'))
                    """, new { employeeId = line.EmployeeId, period = input.Period.ToString() }, transaction.Transaction) != 0;
                if (alreadyPaid) throw new DomainException(ErrorCode.Validation, "Salary for this employee and month is already posted.");
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

    public Task<long> RecordOtherSalaryPaymentAsync(
        long employeeId,
        DateOnly payDate,
        Money amount,
        string mode,
        string notes,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (employeeId <= 0 || amount <= Money.Zero || !SalaryModes.Contains(mode))
            throw new DomainException(ErrorCode.Validation, "Invalid employee payment.");
        string cleanNotes = Required(notes, 500);
        return database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.SalaryRun);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(payDate);
            var run = new SalaryRunInput(YearMonth.From(payDate), payDate, mode, []);
            long id = InsertSalary(transaction, employeeId, run, "OTHER", amount, cleanNotes, 0);
            audit.Write(transaction, "salary_payment", id, "OTHER", new { employeeId, amount = amount.Scaled });
            OperationGuard.Complete(transaction, operationId, "salary_payment", id);
            return id;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SalaryPaymentItem>> GetSalaryRegisterAsync(YearMonth from, YearMonth to, CancellationToken cancellationToken = default)
    {
        if (from > to) throw new DomainException(ErrorCode.Validation, "Invalid salary period.");
        return database.ReadAsync(connection => (IReadOnlyList<SalaryPaymentItem>)connection.Query<SalaryRow>("""
            SELECT s.id AS Id,s.employee_id AS EmployeeId,e.name AS EmployeeName,s.period_month AS Period,
                   COALESCE(sc.new_pay_date,s.pay_date) AS PayDate,s.kind AS Kind,
                   COALESCE(sc.new_amount,s.amount) AS Amount,COALESCE(sc.new_mode,s.mode) AS Mode,
                   CASE WHEN sc.id IS NULL THEN s.notes ELSE sc.new_notes END AS Notes
            FROM salary_payment s JOIN employee e ON e.id=s.employee_id
            LEFT JOIN salary_correction sc ON sc.id=(SELECT MAX(id) FROM salary_correction WHERE salary_payment_id=s.id)
            WHERE s.period_month BETWEEN @from AND @to ORDER BY s.period_month DESC,e.name,s.kind
            """, new { from = from.ToString(), to = to.ToString() }).Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    public Task<long> CorrectSalaryPaymentAsync(SalaryPaymentItem original, SalaryCorrection replacement,
        DateOnly correctionDate, string reason, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Amount <= Money.Zero || !SalaryModes.Contains(replacement.Mode))
            throw new DomainException(ErrorCode.Validation, "Invalid salary correction.");
        string correctionReason = Required(reason, 240);
        string? notes = Optional(replacement.Notes, 500);
        return database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.SalaryCorrection);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(correctionDate);
            SalaryRow? current = transaction.Connection.QuerySingleOrDefault<SalaryRow>("""
                SELECT s.id AS Id,s.employee_id AS EmployeeId,e.name AS EmployeeName,s.period_month AS Period,
                       COALESCE(sc.new_pay_date,s.pay_date) AS PayDate,s.kind AS Kind,
                       COALESCE(sc.new_amount,s.amount) AS Amount,COALESCE(sc.new_mode,s.mode) AS Mode,
                       CASE WHEN sc.id IS NULL THEN s.notes ELSE sc.new_notes END AS Notes
                FROM salary_payment s JOIN employee e ON e.id=s.employee_id
                LEFT JOIN salary_correction sc ON sc.id=(SELECT MAX(id) FROM salary_correction WHERE salary_payment_id=s.id)
                WHERE s.id=@id
                """, new { id = original.Id }, transaction.Transaction);
            if (current is null) throw new DomainException(ErrorCode.NotFound, "salary_payment", original.Id);
            SalaryPaymentItem posted = current.ToModel();
            if (posted.Amount != original.Amount || posted.PayDate != original.PayDate ||
                posted.Mode != original.Mode || posted.Notes != original.Notes ||
                posted.EmployeeId != original.EmployeeId || posted.Period != original.Period || posted.Kind != original.Kind)
                throw new DomainException(ErrorCode.Validation, "This payment changed since it was loaded. Refresh and try again.");
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO salary_correction(salary_payment_id,correction_date,previous_amount,new_amount,
                    new_pay_date,new_mode,new_notes,amount_delta,correction_reason)
                VALUES (@paymentId,@date,@previousAmount,@newAmount,@newPayDate,@newMode,@notes,@delta,@correctionReason)
                RETURNING id
                """, new { paymentId = original.Id, date = Format(correctionDate),
                    previousAmount = original.Amount.Scaled, newAmount = replacement.Amount.Scaled,
                    newPayDate = Format(replacement.PayDate), newMode = replacement.Mode, notes,
                    delta = (replacement.Amount - original.Amount).Scaled, correctionReason }, transaction.Transaction);
            audit.Write(transaction, "salary_payment", original.Id, "CORRECT", new { correctionId = id,
                reason = correctionReason, before = original, after = replacement });
            OperationGuard.Complete(transaction, operationId, "salary_correction", id);
            return id;
        }, cancellationToken);
    }

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

    private sealed class ExpiryRow { public long LotId { get; init; } public long ProductId { get; init; } public string Sku { get; init; } = ""; public string ProductName { get; init; } = ""; public string? ProductNameLocal { get; init; } public string LotCode { get; init; } = ""; public string? BatchNumber { get; init; } public DateOnly ExpiryDate { get; init; } public int DaysLeft { get; init; } public long Quantity { get; init; } public long CostValue { get; init; } public string Bucket { get; init; } = ""; public long? VendorId { get; init; } public string? VendorName { get; init; } public ExpiryLot ToModel() => new(LotId, ProductId, Sku, ProductName, ProductNameLocal, LotCode, BatchNumber, ExpiryDate, DaysLeft, GroceryErp.Core.Numerics.Quantity.FromScaled(Quantity), Money.FromScaled(CostValue), Bucket, VendorId, VendorName); }
    private sealed class LotRow { public long ProductId { get; init; } public long OnHand { get; init; } public long UnitCost { get; init; } }
    private sealed class BulkLotRow { public long LotId { get; init; } public long ProductId { get; init; } public long OnHand { get; init; } public long UnitCost { get; init; } public DateOnly? ExpiryDate { get; init; } }
    private sealed class DamageRow { public long Id { get; init; } public string DocumentNumber { get; init; } = ""; public DateOnly Date { get; init; } public string ProductName { get; init; } = ""; public string LotCode { get; init; } = ""; public long Quantity { get; init; } public long UnitCost { get; init; } public long CostValue { get; init; } public long RecoveredAmount { get; init; } public string Reason { get; init; } = ""; public string? Notes { get; init; } public DamageItem ToModel() => new(Id, DocumentNumber, Date, ProductName, LotCode, GroceryErp.Core.Numerics.Quantity.FromScaled(Quantity), Money.FromScaled(UnitCost), Money.FromScaled(CostValue), Money.FromScaled(RecoveredAmount), Reason, Notes); }
    private sealed class DamageStateRow { public long ProductId { get; init; } public long LotId { get; init; } public long UnitCost { get; init; } public long Quantity { get; init; } public long RecoveredAmount { get; init; } public string Reason { get; init; } = ""; public string? Notes { get; init; } }
    private sealed class CategoryRow { public long Id { get; init; } public string Name { get; init; } = ""; public string? LocalName { get; init; } public bool IsActive { get; init; } public ExpenseCategoryItem ToModel() => new(Id, Name, LocalName, IsActive); }
    private sealed class ExpenseRow { public long Id { get; init; } public DateOnly Date { get; init; } public long CategoryId { get; init; } public string CategoryName { get; init; } = ""; public long Amount { get; init; } public string Mode { get; init; } = ""; public string? PaidTo { get; init; } public string? Reference { get; init; } public string? Notes { get; init; } public ExpenseItem ToModel() => new(Id, Date, CategoryId, CategoryName, Money.FromScaled(Amount), Mode, PaidTo, Reference, Notes); }
    private sealed class ExpenseDraftRow { public long CategoryId { get; init; } public long Amount { get; init; } public string Mode { get; init; } = ""; public string? PaidTo { get; init; } public string? Reference { get; init; } public string? Notes { get; init; } public int Day { get; init; } public ExpenseInput ToModel(YearMonth target) => new(new DateOnly(target.Year, target.Month, Math.Min(Day, DateTime.DaysInMonth(target.Year, target.Month))), CategoryId, Money.FromScaled(Amount), Mode, PaidTo, Reference, Notes); }
    private sealed class EmployeeRow { public long Id { get; init; } public string Name { get; init; } = ""; public string? Phone { get; init; } public string? Role { get; init; } public DateOnly? JoinDate { get; init; } public DateOnly? LeaveDate { get; init; } public long MonthlySalary { get; init; } public bool IsActive { get; init; } public string? Notes { get; init; } public EmployeeItem ToModel() => new(Id, Name, Phone, Role, JoinDate, LeaveDate, Money.FromScaled(MonthlySalary), IsActive, Notes); }
    private sealed class SalaryPrepareRow { public long EmployeeId { get; init; } public long BaseSalary { get; init; } }
    private sealed class SalaryRow { public long Id { get; init; } public long EmployeeId { get; init; } public string EmployeeName { get; init; } = ""; public string Period { get; init; } = ""; public DateOnly PayDate { get; init; } public string Kind { get; init; } = ""; public long Amount { get; init; } public string Mode { get; init; } = ""; public string? Notes { get; init; } public SalaryPaymentItem ToModel() => new(Id, EmployeeId, EmployeeName, YearMonth.Parse(Period), PayDate, Kind, Money.FromScaled(Amount), Mode, Notes); }
}
