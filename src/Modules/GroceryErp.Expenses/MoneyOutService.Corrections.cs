using System.Data;
using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Messages;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Expenses;

// Posted money-out entries are never rewritten. A correction is its own dated record:
// while both annual periods of the original date are open its P&L difference belongs
// to that date; once either is finalized the original stays and the difference is
// dated on the business date of the correction.
public sealed partial class MoneyOutService
{
    public Task<CorrectionDating> PreviewCorrectionAsync(DateOnly originalDate, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => periods.Preview(connection, null, originalDate), cancellationToken);

    public Task<long> CorrectExpenseAsync(ExpenseItem original, ExpenseInput replacement, string reason,
        string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ValidateExpense(replacement);
        return SaveExpenseCorrectionAsync(original, replacement, "EDIT", Required(reason, 240), operationId,
            cancellationToken);
    }

    public Task<long> VoidExpenseAsync(ExpenseItem original, string reason, string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        var unchanged = new ExpenseInput(original.Date, original.CategoryId, Money.Zero, original.Mode,
            original.PaidTo, original.Reference, original.Notes);
        return SaveExpenseCorrectionAsync(original, unchanged, "VOID", Required(reason, 240), operationId,
            cancellationToken);
    }

    private Task<long> SaveExpenseCorrectionAsync(ExpenseItem original, ExpenseInput replacement, string kind,
        string reason, string operationId, CancellationToken cancellationToken) => database.WriteAsync(transaction =>
    {
        OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.ExpenseCorrection);
        if (replay is not null) return replay.RefId ?? 0;
        ExpenseItem current = LoadExpense(transaction, original.Id);
        if (current.Status == "PURCHASE")
            throw new DomainException(ErrorCode.CancelNotAllowed, original.Id);
        if (current.Status == "VOID") throw new DomainException(ErrorCode.EntryVoided);
        if (current != original) throw new DomainException(ErrorCode.EntryChanged);
        if (kind == "EDIT" && transaction.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM expense_category WHERE id=@id AND is_active=1",
                new { id = replacement.CategoryId }, transaction.Transaction) != 1)
            throw new DomainException(ErrorCode.NotFound, "expense_category", replacement.CategoryId);
        CorrectionDating dating = periods.DateCorrection(transaction, current.Date);
        DateOnly applyDate = dating.EffectiveDate;
        if (replacement.Date != current.Date)
        {
            periods.EnsureOpen(transaction, replacement.Date);
            applyDate = replacement.Date;
        }
        long id = transaction.Connection.ExecuteScalar<long>("""
            INSERT INTO expense_correction(expense_id,correction_date,treatment,kind,previous_date,
                previous_category_id,previous_amount,new_date,new_category_id,new_amount,new_mode,new_paid_to,
                new_reference,new_notes,reverse_date,apply_date,correction_reason)
            VALUES (@expenseId,@today,@treatment,@kind,@previousDate,@previousCategory,@previousAmount,@newDate,
                @newCategory,@newAmount,@mode,@paidTo,@reference,@notes,@reverseDate,@applyDate,@reason)
            RETURNING id
            """, new
        {
            expenseId = original.Id,
            today = Format(periods.Today),
            treatment = Code(dating.Treatment),
            kind,
            previousDate = Format(current.Date),
            previousCategory = current.CategoryId,
            previousAmount = current.Amount.Scaled,
            newDate = Format(replacement.Date),
            newCategory = replacement.CategoryId,
            newAmount = replacement.Amount.Scaled,
            mode = replacement.Mode,
            paidTo = Optional(replacement.PaidTo, 150),
            reference = Optional(replacement.Reference, 100),
            notes = Optional(replacement.Notes, 500),
            reverseDate = Format(dating.EffectiveDate),
            applyDate = Format(applyDate),
            reason,
        }, transaction.Transaction);
        audit.Write(transaction, "expense", original.Id, kind == "VOID" ? "VOID" : "CORRECT", new
        {
            correctionId = id,
            reason,
            treatment = Code(dating.Treatment),
            before = current,
            after = replacement,
        });
        OperationGuard.Complete(transaction, operationId, "expense_correction", id);
        return id;
    }, cancellationToken);

    public Task<IReadOnlyList<ExpenseHistoryItem>> GetExpenseHistoryAsync(long expenseId,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
        (IReadOnlyList<ExpenseHistoryItem>)connection.Query<ExpenseHistoryRow>("""
            SELECT 'ORIGINAL' AS Kind,date(e.created_at) AS RecordedOn,NULL AS Treatment,e.expense_date AS Date,
                   c.name AS CategoryName,e.amount AS Amount,e.mode AS Mode,e.paid_to AS PaidTo,
                   e.reference AS Reference,e.notes AS Notes,NULL AS ReversedOn,NULL AS AppliedOn,
                   NULL AS CorrectionReason,0 AS Sequence
            FROM expense e JOIN expense_category c ON c.id=e.category_id WHERE e.id=@expenseId
            UNION ALL
            SELECT x.kind,x.correction_date,x.treatment,x.new_date,c.name,x.new_amount,x.new_mode,x.new_paid_to,
                   x.new_reference,x.new_notes,x.reverse_date,x.apply_date,x.correction_reason,x.id
            FROM expense_correction x JOIN expense_category c ON c.id=x.new_category_id
            WHERE x.expense_id=@expenseId
            ORDER BY Sequence LIMIT 200
            """, new { expenseId }).Select(static row => row.ToModel()).ToList(), cancellationToken);

    private static ExpenseItem LoadExpense(DbTx transaction, long expenseId) =>
        transaction.Connection.QuerySingleOrDefault<ExpenseRow>("""
            SELECT e.id AS Id,COALESCE(x.new_date,e.expense_date) AS Date,
                   COALESCE(x.new_category_id,e.category_id) AS CategoryId,c.name AS CategoryName,
                   COALESCE(x.new_amount,e.amount) AS Amount,COALESCE(x.new_mode,e.mode) AS Mode,
                   CASE WHEN x.id IS NULL THEN e.paid_to ELSE x.new_paid_to END AS PaidTo,
                   CASE WHEN x.id IS NULL THEN e.reference ELSE x.new_reference END AS Reference,
                   CASE WHEN x.id IS NULL THEN e.notes ELSE x.new_notes END AS Notes,
                   CASE WHEN x.kind='VOID' OR EXISTS(SELECT 1 FROM purchase_expense_reversal r WHERE r.expense_id=e.id)
                        THEN 'VOID' WHEN e.purchase_line_id IS NOT NULL THEN 'PURCHASE'
                        WHEN x.id IS NOT NULL THEN 'CORRECTED' ELSE 'ACTIVE' END AS Status
            FROM expense e
            LEFT JOIN expense_correction x ON x.id=(SELECT MAX(id) FROM expense_correction WHERE expense_id=e.id)
            JOIN expense_category c ON c.id=COALESCE(x.new_category_id,e.category_id)
            WHERE e.id=@expenseId
            """, new { expenseId }, transaction.Transaction)?.ToModel()
        ?? throw new DomainException(ErrorCode.NotFound, "expense", expenseId);

    // A damage's units sit in its lot or, after a restore recreated it, in the newest restored
    // lot, which may belong to a product the operator chose. That lot gives the product.
    private const string DamageStateSql = """
        SELECT s.*,COALESCE(l.product_id,s.OriginalProductId) AS ProductId,l.id IS NOT NULL AS LotExists,
               EXISTS(SELECT 1 FROM product p WHERE p.id=COALESCE(l.product_id,s.OriginalProductId)) AS ProductExists
        FROM (SELECT d.id AS DamageId,d.doc_no AS DocumentNumber,d.product_id AS OriginalProductId,
                     COALESCE((SELECT x.restored_lot_id FROM damage_correction x
                               WHERE x.damage_id=d.id AND x.restored_lot_id IS NOT NULL
                               ORDER BY x.id DESC LIMIT 1),d.lot_id) AS LotId,
                     d.unit_cost AS UnitCost,d.damage_date AS DamageDate,
                     COALESCE(dc.new_qty,d.qty) AS Quantity,
                     COALESCE(dc.new_recovered,d.recovered_amount) AS RecoveredAmount,
                     COALESCE(dc.new_reason,d.reason) AS Reason,
                     CASE WHEN dc.id IS NULL THEN d.notes ELSE dc.new_notes END AS Notes,
                     d.batch_no_snapshot AS BatchNumber,d.expiry_date_snapshot AS ExpiryDate,
                     d.product_name_snapshot AS ProductName,d.product_name_local_snapshot AS ProductNameLocal,
                     d.product_sku_snapshot AS Sku,d.uom_snapshot AS UnitCode,
                     COALESCE(d.product_class_snapshot,'SALEABLE') AS ProductClass
              FROM damage d
              LEFT JOIN damage_correction dc ON dc.id=(SELECT MAX(id) FROM damage_correction WHERE damage_id=d.id)
              WHERE d.id=@id) s
        LEFT JOIN product_lot l ON l.id=s.LotId
        """;

    /// <summary>
    /// The saved details for restoring units of a damage whose lot and product were both deleted,
    /// or null when the units have somewhere to go.
    /// </summary>
    public Task<RestoreDraft?> GetDamageRestoreDraftAsync(long damageId, Quantity restored,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        DamageState state = connection.QuerySingleOrDefault<DamageState>(DamageStateSql, new { id = damageId })
            ?? throw new DomainException(ErrorCode.NotFound, "damage", damageId);
        return state.LotExists || state.ProductExists ? null : DamageDraft(connection, null, state, restored);
    }, cancellationToken);

    private static RestoreDraft DamageDraft(IDbConnection connection, IDbTransaction? transaction, DamageState damage,
        Quantity quantity) =>
        new(damage.DamageId, damage.DocumentNumber ?? $"#{damage.DamageId}", damage.ProductName ?? "",
            damage.ProductNameLocal,
            RestoreProducts.SuggestSku(connection, transaction, damage.Sku, $"D{damage.DamageId:X}"), null,
            connection.ExecuteScalar<long?>("SELECT id FROM uom WHERE code=@UnitCode COLLATE NOCASE", damage, transaction),
            damage.UnitCode, null, null, null, null, damage.ProductClass, damage.BatchNumber, damage.ExpiryDate,
            quantity, Money.FromScaled(damage.UnitCost), null, null);

    // A quantity difference changes today's lot balance in the same transaction. If the
    // damaged lot was deleted at zero, restored units start a new direct lot from the
    // damage snapshot; if the product was deleted too, they go into the product the
    // operator chose.
    public async Task<long> CorrectDamageAsync(DamageItem original, DamageCorrection replacement, string reason,
        string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Quantity < Quantity.Zero || replacement.RecoveredAmount < Money.Zero ||
            !DamageReasons.Contains(replacement.Reason) ||
            replacement.Quantity == Quantity.Zero && replacement.RecoveredAmount != Money.Zero)
            throw new DomainException(ErrorCode.Validation, "Invalid damage correction.");
        string correctionReason = Required(reason, 240);
        string? notes = Optional(replacement.Notes, 500);
        long productId = 0;
        long result = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.DamageCorrection);
            if (replay is not null) return replay.RefId ?? 0;
            DamageState current = transaction.Connection.QuerySingleOrDefault<DamageState>(DamageStateSql,
                new { id = original.Id }, transaction.Transaction)
                ?? throw new DomainException(ErrorCode.NotFound, "damage", original.Id);
            if (current.Quantity == 0) throw new DomainException(ErrorCode.EntryVoided);
            if (current.Quantity != original.Quantity.Scaled || current.RecoveredAmount != original.RecoveredAmount.Scaled ||
                current.UnitCost != original.UnitCost.Scaled || current.Reason != original.Reason || current.Notes != original.Notes)
                throw new DomainException(ErrorCode.EntryChanged);
            CorrectionDating dating = periods.DateCorrection(transaction, current.DamageDate);
            productId = current.ProductId;
            Money unitCost = Money.FromScaled(current.UnitCost);
            Money previousLoss = unitCost * Quantity.FromScaled(current.Quantity) - Money.FromScaled(current.RecoveredAmount);
            Money newLoss = unitCost * replacement.Quantity - replacement.RecoveredAmount;
            Quantity restored = new(Quantity.FromScaled(current.Quantity).Value - replacement.Quantity.Value);
            (long LotId, long ProductId, RestoredProduct? Chosen)? recreated = restored > Quantity.Zero && !current.LotExists
                ? RecreateDamagedLot(transaction, current, restored, correctionReason, replacement.Restore) : null;
            long? restoredLot = recreated?.LotId;
            if (recreated is { } lot) productId = lot.ProductId;
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO damage_correction(damage_id,correction_date,effective_date,treatment,previous_qty,new_qty,
                    previous_recovered,new_recovered,new_reason,new_notes,expense_delta,restored_lot_id,correction_reason)
                VALUES (@damageId,@today,@effective,@treatment,@previousQty,@newQty,@previousRecovered,@newRecovered,
                    @newReason,@notes,@delta,@restoredLot,@correctionReason) RETURNING id
                """, new
            {
                damageId = original.Id,
                today = Format(periods.Today),
                effective = Format(dating.EffectiveDate),
                treatment = Code(dating.Treatment),
                previousQty = current.Quantity,
                newQty = replacement.Quantity.Scaled,
                previousRecovered = current.RecoveredAmount,
                newRecovered = replacement.RecoveredAmount.Scaled,
                newReason = replacement.Reason,
                notes,
                delta = (newLoss - previousLoss).Scaled,
                restoredLot,
                correctionReason,
            }, transaction.Transaction);
            if (restoredLot is null && restored != Quantity.Zero)
                stock.CorrectDamage(transaction, new(periods.Today, current.ProductId, current.LotId,
                    restored, unitCost, id, correctionReason));
            audit.Write(transaction, "damage", original.Id, replacement.Quantity == Quantity.Zero ? "VOID" : "CORRECT", new
            {
                correctionId = id,
                reason = correctionReason,
                treatment = Code(dating.Treatment),
                before = original,
                after = replacement,
                lossChange = (newLoss - previousLoss).Scaled,
                restoredLot,
                restoredProduct = recreated?.Chosen?.ProductId,
                createdProduct = recreated?.Chosen?.Created,
            });
            OperationGuard.Complete(transaction, operationId, "damage_correction", id);
            return id;
        }, cancellationToken);
        if (productId > 0) WeakReferenceMessenger.Default.Send(new StockChangedMessage([productId]));
        return result;
    }

    public Task<long> VoidDamageAsync(DamageItem original, string reason, string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        return CorrectDamageAsync(original, new(Quantity.Zero, Money.Zero, original.Reason, original.Notes),
            reason, operationId, cancellationToken);
    }

    private (long LotId, long ProductId, RestoredProduct? Chosen) RecreateDamagedLot(DbTx transaction,
        DamageState damage, Quantity quantity, string reason, RestoreProductChoice? restore)
    {
        long productId = damage.ProductId;
        long price;
        long? mrp;
        DateOnly? expiry = damage.ExpiryDate;
        RestoredProduct? chosen = null;
        if (damage.ProductExists)
        {
            ProductPrice product = transaction.Connection.QuerySingle<ProductPrice>("""
                SELECT default_selling_price AS SellingPrice,default_mrp AS Mrp FROM product WHERE id=@ProductId
                """, damage, transaction.Transaction);
            price = product.SellingPrice ?? damage.UnitCost;
            mrp = product.Mrp is { } value && value >= price ? value : null;
        }
        else
        {
            if (restore is null) throw new DomainException(ErrorCode.DamageProductDeleted);
            chosen = RestoreProducts.Resolve(transaction,
                DamageDraft(transaction.Connection, transaction.Transaction, damage, quantity), restore);
            (productId, price, mrp, expiry) = (chosen.ProductId, chosen.SellingPrice.Scaled, chosen.Mrp?.Scaled,
                chosen.ExpiryDate);
        }
        // The damage saved only the booked cost, so the product's rate as it is now gives the tax.
        LotTax tax = LotTaxes.FromBooked(transaction, LotTaxes.ProductRate(transaction, productId),
            Money.FromScaled(damage.UnitCost));
        string lotCode = numbers.Next(transaction, DocType.Lot, periods.Today);
        long lotId = transaction.Connection.ExecuteScalar<long>("""
            INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,unit_cost,mrp,
                selling_price,qty_received,direct_reason,
                tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,unit_tax,unit_cost_incl_tax)
            VALUES (@productId,@lotCode,@batch,@today,@expiry,@unitCost,@mrp,@price,@quantity,@reason,
                @taxRateId,@taxLabel,@taxRatePct,@unitTax,@unitCostInclTax)
            RETURNING id
            """, tax.AddTo(new DynamicParameters(new
        {
            productId,
            lotCode,
            batch = damage.BatchNumber,
            today = Format(periods.Today),
            expiry,
            unitCost = damage.UnitCost,
            mrp,
            price,
            quantity = quantity.Scaled,
            reason = $"Damage {damage.DocumentNumber ?? "#" + damage.DamageId} correction restore: {reason}",
        })), transaction.Transaction);
        stock.Change(transaction, new(periods.Today, productId, lotId, LotChangeKind.DirectEntry, quantity));
        return (lotId, productId, chosen);
    }

    public Task<IReadOnlyList<DamageHistoryItem>> GetDamageHistoryAsync(long damageId,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
        (IReadOnlyList<DamageHistoryItem>)connection.Query<DamageHistoryRow>("""
            SELECT 'ORIGINAL' AS Kind,date(d.created_at) AS RecordedOn,NULL AS Treatment,d.damage_date AS EffectiveDate,
                   d.qty AS Quantity,d.recovered_amount AS RecoveredAmount,d.reason AS Reason,d.notes AS Notes,
                   d.cost_value-d.recovered_amount AS LossChange,NULL AS CorrectionReason,NULL AS RestoredLotId,
                   0 AS Sequence
            FROM damage d WHERE d.id=@damageId
            UNION ALL
            SELECT CASE WHEN x.new_qty=0 THEN 'VOID' ELSE 'EDIT' END,x.correction_date,x.treatment,x.effective_date,
                   x.new_qty,x.new_recovered,x.new_reason,x.new_notes,x.expense_delta,x.correction_reason,
                   x.restored_lot_id,x.id
            FROM damage_correction x WHERE x.damage_id=@damageId
            ORDER BY Sequence LIMIT 200
            """, new { damageId }).Select(static row => row.ToModel()).ToList(), cancellationToken);

    // Salary is charged to the month worked, so that month decides the treatment.
    public Task<long> CorrectSalaryPaymentAsync(SalaryPaymentItem original, SalaryCorrection replacement,
        string reason, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Amount <= Money.Zero || !SalaryModes.Contains(replacement.Mode))
            throw new DomainException(ErrorCode.Validation, "Invalid salary correction.");
        return SaveSalaryCorrectionAsync(original, replacement, "EDIT", Required(reason, 240), operationId,
            cancellationToken);
    }

    public Task<long> VoidSalaryPaymentAsync(SalaryPaymentItem original, string reason, string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        return SaveSalaryCorrectionAsync(original, new(original.PayDate, Money.Zero, original.Mode, original.Notes),
            "VOID", Required(reason, 240), operationId, cancellationToken);
    }

    private Task<long> SaveSalaryCorrectionAsync(SalaryPaymentItem original, SalaryCorrection replacement, string kind,
        string reason, string operationId, CancellationToken cancellationToken) => database.WriteAsync(transaction =>
    {
        OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.SalaryCorrection);
        if (replay is not null) return replay.RefId ?? 0;
        SalaryRow current = transaction.Connection.QuerySingleOrDefault<SalaryRow>("""
            SELECT s.id AS Id,s.employee_id AS EmployeeId,e.name AS EmployeeName,s.period_month AS Period,
                   COALESCE(sc.new_pay_date,s.pay_date) AS PayDate,s.kind AS Kind,
                   COALESCE(sc.new_amount,s.amount) AS Amount,COALESCE(sc.new_mode,s.mode) AS Mode,
                   CASE WHEN sc.id IS NULL THEN s.notes ELSE sc.new_notes END AS Notes,
                   CASE WHEN sc.id IS NULL THEN 'ACTIVE' WHEN sc.kind='VOID' THEN 'VOID' ELSE 'CORRECTED' END AS Status
            FROM salary_payment s JOIN employee e ON e.id=s.employee_id
            LEFT JOIN salary_correction sc ON sc.id=(SELECT MAX(id) FROM salary_correction WHERE salary_payment_id=s.id)
            WHERE s.id=@id
            """, new { id = original.Id }, transaction.Transaction)
            ?? throw new DomainException(ErrorCode.NotFound, "salary_payment", original.Id);
        SalaryPaymentItem posted = current.ToModel();
        if (posted.Status == "VOID") throw new DomainException(ErrorCode.EntryVoided);
        if (posted != original) throw new DomainException(ErrorCode.EntryChanged);
        CorrectionDating dating = periods.DateCorrection(transaction, posted.Period.LastDay);
        long id = transaction.Connection.ExecuteScalar<long>("""
            INSERT INTO salary_correction(salary_payment_id,correction_date,effective_date,treatment,kind,
                previous_amount,new_amount,new_pay_date,new_mode,new_notes,amount_delta,correction_reason)
            VALUES (@paymentId,@today,@effective,@treatment,@kind,@previousAmount,@newAmount,@newPayDate,@newMode,
                @notes,@delta,@reason) RETURNING id
            """, new
        {
            paymentId = original.Id,
            today = Format(periods.Today),
            effective = Format(dating.EffectiveDate),
            treatment = Code(dating.Treatment),
            kind,
            previousAmount = posted.Amount.Scaled,
            newAmount = replacement.Amount.Scaled,
            newPayDate = Format(replacement.PayDate),
            newMode = replacement.Mode,
            notes = Optional(replacement.Notes, 500),
            delta = (replacement.Amount - posted.Amount).Scaled,
            reason,
        }, transaction.Transaction);
        audit.Write(transaction, "salary_payment", original.Id, kind == "VOID" ? "VOID" : "CORRECT", new
        {
            correctionId = id,
            reason,
            treatment = Code(dating.Treatment),
            before = posted,
            after = replacement,
        });
        OperationGuard.Complete(transaction, operationId, "salary_correction", id);
        return id;
    }, cancellationToken);

    public Task<IReadOnlyList<SalaryHistoryItem>> GetSalaryHistoryAsync(long paymentId,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
        (IReadOnlyList<SalaryHistoryItem>)connection.Query<SalaryHistoryRow>("""
            SELECT 'ORIGINAL' AS Kind,date(s.created_at) AS RecordedOn,NULL AS Treatment,
                   date(s.period_month||'-01','+1 month','-1 day') AS EffectiveDate,s.amount AS Amount,
                   s.pay_date AS PayDate,s.mode AS Mode,s.notes AS Notes,s.amount AS AmountChange,
                   NULL AS CorrectionReason,0 AS Sequence
            FROM salary_payment s WHERE s.id=@paymentId
            UNION ALL
            SELECT x.kind,x.correction_date,x.treatment,x.effective_date,x.new_amount,x.new_pay_date,x.new_mode,
                   x.new_notes,x.amount_delta,x.correction_reason,x.id
            FROM salary_correction x WHERE x.salary_payment_id=@paymentId
            ORDER BY Sequence LIMIT 200
            """, new { paymentId }).Select(static row => row.ToModel()).ToList(), cancellationToken);

    private static string Code(PeriodTreatment treatment) =>
        treatment == PeriodTreatment.OpenPeriod ? "OPEN_PERIOD" : "CURRENT_DATED";

    private sealed class DamageState
    {
        public long DamageId { get; init; }
        public string? DocumentNumber { get; init; }
        public long OriginalProductId { get; init; }
        public long ProductId { get; init; }
        public long LotId { get; init; }
        public bool LotExists { get; init; }
        public bool ProductExists { get; init; }
        public long UnitCost { get; init; }
        public DateOnly DamageDate { get; init; }
        public long Quantity { get; init; }
        public long RecoveredAmount { get; init; }
        public string Reason { get; init; } = "";
        public string? Notes { get; init; }
        public string? BatchNumber { get; init; }
        public DateOnly? ExpiryDate { get; init; }
        public string? ProductName { get; init; }
        public string? ProductNameLocal { get; init; }
        public string? Sku { get; init; }
        public string? UnitCode { get; init; }
        public string ProductClass { get; init; } = "SALEABLE";
    }

    private sealed class ProductPrice
    {
        public long? SellingPrice { get; init; }
        public long? Mrp { get; init; }
    }

    private sealed class ExpenseHistoryRow
    {
        public string Kind { get; init; } = "";
        public DateOnly RecordedOn { get; init; }
        public string? Treatment { get; init; }
        public DateOnly Date { get; init; }
        public string CategoryName { get; init; } = "";
        public long Amount { get; init; }
        public string Mode { get; init; } = "";
        public string? PaidTo { get; init; }
        public string? Reference { get; init; }
        public string? Notes { get; init; }
        public DateOnly? ReversedOn { get; init; }
        public DateOnly? AppliedOn { get; init; }
        public string? CorrectionReason { get; init; }
        public ExpenseHistoryItem ToModel() => new(Kind, RecordedOn, Treatment, Date, CategoryName,
            Money.FromScaled(Amount), Mode, PaidTo, Reference, Notes, ReversedOn, AppliedOn, CorrectionReason);
    }

    private sealed class DamageHistoryRow
    {
        public string Kind { get; init; } = "";
        public DateOnly RecordedOn { get; init; }
        public string? Treatment { get; init; }
        public DateOnly EffectiveDate { get; init; }
        public long Quantity { get; init; }
        public long RecoveredAmount { get; init; }
        public string Reason { get; init; } = "";
        public string? Notes { get; init; }
        public long LossChange { get; init; }
        public string? CorrectionReason { get; init; }
        public long? RestoredLotId { get; init; }
        public DamageHistoryItem ToModel() => new(Kind, RecordedOn, Treatment, EffectiveDate,
            GroceryErp.Core.Numerics.Quantity.FromScaled(Quantity), Money.FromScaled(RecoveredAmount), Reason, Notes,
            Money.FromScaled(LossChange), CorrectionReason, RestoredLotId);
    }

    private sealed class SalaryHistoryRow
    {
        public string Kind { get; init; } = "";
        public DateOnly RecordedOn { get; init; }
        public string? Treatment { get; init; }
        public DateOnly EffectiveDate { get; init; }
        public long Amount { get; init; }
        public DateOnly PayDate { get; init; }
        public string Mode { get; init; } = "";
        public string? Notes { get; init; }
        public long AmountChange { get; init; }
        public string? CorrectionReason { get; init; }
        public SalaryHistoryItem ToModel() => new(Kind, RecordedOn, Treatment, EffectiveDate, Money.FromScaled(Amount),
            PayDate, Mode, Notes, Money.FromScaled(AmountChange), CorrectionReason);
    }
}
