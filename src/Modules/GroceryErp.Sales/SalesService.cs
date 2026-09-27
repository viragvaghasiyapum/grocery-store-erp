using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Messages;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Sales;

public sealed class SalesService(
    DbExecutor database,
    IStockLedger stockLedger,
    DocNumberService documentNumbers,
    PeriodGuard periodGuard,
    ISettings settings,
    IAuditLog auditLog,
    IClock clock)
{
    public Task<long> CreateDraftAsync(DateOnly? date = null, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction => transaction.Connection.ExecuteScalar<long>("""
            INSERT INTO sale(sale_date,status) VALUES (@date,'DRAFT') RETURNING id
            """, new { date = DateText(date ?? clock.BusinessDate) }, transaction.Transaction), cancellationToken);

    public Task<long> AddProductAsync(long? saleId, long productId, Quantity quantity,
        long? preferredLotId = null, string? scannedBarcode = null,
        CancellationToken cancellationToken = default) => database.WriteAsync(transaction =>
    {
        if (productId <= 0 || quantity <= Quantity.Zero)
            throw new DomainException(ErrorCode.Validation, "A product and positive quantity are required.");
        long id = saleId ?? transaction.Connection.ExecuteScalar<long>("""
            INSERT INTO sale(sale_date,status) VALUES (@date,'DRAFT') RETURNING id
            """, new { date = DateText(clock.BusinessDate) }, transaction.Transaction);
        EnsureEditable(transaction, id);
        ProductRow product = transaction.Connection.QuerySingleOrDefault<ProductRow>("""
            SELECT p.name AS Name,p.name_local AS NameLocal,p.hsn_code AS HsnCode,
                   p.price_includes_tax AS PriceIncludesTax,p.is_active AS IsActive,
                   u.allow_fraction AS AllowFraction,COALESCE(t.rate_pct,0) AS TaxRate
            FROM product p JOIN uom u ON u.id=p.uom_id
            LEFT JOIN tax_rate t ON t.id=p.tax_rate_id WHERE p.id=@productId
            """, new { productId }, transaction.Transaction)
            ?? throw new DomainException(ErrorCode.NotFound, productId);
        if (!product.IsActive) throw new DomainException(ErrorCode.Validation, "Archived products cannot be sold.");
        if (!product.AllowFraction && !quantity.IsWhole)
            throw new DomainException(ErrorCode.FractionalQtyNotAllowed, productId);

        Dictionary<long, Quantity> reserved = transaction.Connection.Query<(long LotId, long Qty)>("""
            SELECT lot_id AS LotId,SUM(qty) AS Qty FROM sale_line WHERE sale_id=@id GROUP BY lot_id
            """, new { id }, transaction.Transaction).ToDictionary(static row => row.LotId,
                static row => Quantity.FromScaled(row.Qty));
        IReadOnlyList<LotAllocation> allocations = preferredLotId is { } lotId
            ? [AllocatePreferred(transaction, productId, lotId, quantity, reserved)]
            : stockLedger.AllocateFefo(transaction, productId, quantity, clock.BusinessDate, reserved);
        int nextLine = transaction.Connection.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(line_no),0)+1 FROM sale_line WHERE sale_id=@id", new { id }, transaction.Transaction);
        foreach (LotAllocation allocation in allocations)
        {
            long? lineId = transaction.Connection.QuerySingleOrDefault<long?>(
                "SELECT id FROM sale_line WHERE sale_id=@id AND lot_id=@lotId",
                new { id, lotId = allocation.Lot.LotId }, transaction.Transaction);
            if (lineId is not null)
                transaction.Connection.Execute("""
                    UPDATE sale_line SET qty=qty+@qty,scanned_barcode=COALESCE(@barcode,scanned_barcode)
                    WHERE id=@lineId
                    """, new { qty = allocation.Quantity.Scaled, barcode = scannedBarcode, lineId }, transaction.Transaction);
            else
                transaction.Connection.Execute("""
                    INSERT INTO sale_line(
                        sale_id,line_no,product_id,lot_id,product_name,product_name_local,hsn_code,
                        scanned_barcode,qty,unit_price,mrp,discount,line_discount,tax_rate_pct,
                        price_includes_tax,taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
                    VALUES (@id,@lineNo,@productId,@lotId,@name,@localName,@hsn,@barcode,@qty,@price,
                        @mrp,0,0,@taxRate,@includesTax,0,0,0,@cost,0)
                    """, new
                {
                    id,
                    lineNo = nextLine++,
                    productId,
                    lotId = allocation.Lot.LotId,
                    name = product.Name,
                    localName = product.NameLocal,
                    hsn = product.HsnCode,
                    barcode = scannedBarcode,
                    qty = allocation.Quantity.Scaled,
                    price = allocation.Lot.Price.Scaled,
                    mrp = allocation.Lot.Mrp?.Scaled,
                    taxRate = EffectiveTaxRate(product.TaxRate),
                    includesTax = product.PriceIncludesTax ? 1 : 0,
                    cost = allocation.Lot.Cost.Scaled,
                }, transaction.Transaction);
        }
        Reprice(transaction, id);
        return id;
    }, cancellationToken);

    public async Task<long> AddPriceLabelAsync(long? saleId, long productId, Money totalPrice,
        string? scannedBarcode = null, CancellationToken cancellationToken = default)
    {
        if (totalPrice <= Money.Zero) throw new DomainException(ErrorCode.Validation, totalPrice);
        (long LotId, long Price) lot = await database.ReadAsync(connection =>
            connection.QuerySingleOrDefault<(long LotId, long Price)>("""
                SELECT id AS LotId,selling_price AS Price FROM product_lot
                WHERE product_id=@productId AND is_active=1 AND qty_on_hand>0
                  AND (@blockExpired=0 OR expiry_date IS NULL OR expiry_date>=@date)
                ORDER BY expiry_date IS NULL,expiry_date,received_date,id LIMIT 1
                """, new
            {
                productId,
                blockExpired = settings.Current.BlockExpiredLots ? 1 : 0,
                date = DateText(clock.BusinessDate),
            }), cancellationToken);
        if (lot.LotId == 0) throw new DomainException(ErrorCode.NoLotForProduct, productId);
        Money price = Money.FromScaled(lot.Price);
        if (price <= Money.Zero) throw new DomainException(ErrorCode.Validation, "A price label requires a positive lot price.");
        return await AddProductAsync(saleId, productId, new Quantity(totalPrice.Value / price.Value),
            lot.LotId, scannedBarcode, cancellationToken);
    }

    public Task SetLineQuantityAsync(long saleId, long lineId, Quantity quantity,
        CancellationToken cancellationToken = default) => database.WriteAsync(transaction =>
    {
        EnsureEditable(transaction, saleId);
        if (quantity <= Quantity.Zero)
            transaction.Connection.Execute("DELETE FROM sale_line WHERE id=@lineId AND sale_id=@saleId",
                new { lineId, saleId }, transaction.Transaction);
        else
        {
            LineCheckRow line = transaction.Connection.QuerySingleOrDefault<LineCheckRow>("""
                SELECT sl.product_id AS ProductId,l.qty_on_hand AS OnHand,u.allow_fraction AS AllowFraction
                FROM sale_line sl JOIN product p ON p.id=sl.product_id JOIN uom u ON u.id=p.uom_id
                JOIN product_lot l ON l.id=sl.lot_id WHERE sl.id=@lineId AND sl.sale_id=@saleId
                """, new { lineId, saleId }, transaction.Transaction)
                ?? throw new DomainException(ErrorCode.NotFound, lineId);
            if (!line.AllowFraction && !quantity.IsWhole)
                throw new DomainException(ErrorCode.FractionalQtyNotAllowed, line.ProductId);
            if (!settings.Current.AllowNegativeStock && quantity.Scaled > line.OnHand)
                throw new DomainException(ErrorCode.InsufficientStock, quantity);
            transaction.Connection.Execute("UPDATE sale_line SET qty=@qty WHERE id=@lineId",
                new { qty = quantity.Scaled, lineId }, transaction.Transaction);
        }
        Reprice(transaction, saleId);
    }, cancellationToken);

    public Task SetLineDiscountAsync(long saleId, long lineId, Money discount,
        CancellationToken cancellationToken = default) => database.WriteAsync(transaction =>
    {
        EnsureEditable(transaction, saleId);
        if (discount < Money.Zero) throw new DomainException(ErrorCode.Validation, discount);
        transaction.Connection.Execute(
            "UPDATE sale_line SET line_discount=@discount WHERE id=@lineId AND sale_id=@saleId",
            new { discount = discount.Scaled, lineId, saleId }, transaction.Transaction);
        Reprice(transaction, saleId);
    }, cancellationToken);

    public Task SetBillDiscountAsync(long saleId, Money discount,
        CancellationToken cancellationToken = default) => database.WriteAsync(transaction =>
    {
        EnsureEditable(transaction, saleId);
        if (discount < Money.Zero) throw new DomainException(ErrorCode.Validation, discount);
        transaction.Connection.Execute("UPDATE sale SET bill_discount=@discount WHERE id=@saleId",
            new { discount = discount.Scaled, saleId }, transaction.Transaction);
        Reprice(transaction, saleId);
    }, cancellationToken);

    public Task HoldAsync(long saleId, string? label = null, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            EnsureEditable(transaction, saleId);
            transaction.Connection.Execute("UPDATE sale SET status='HELD',notes=@label WHERE id=@saleId",
                new { label = Clean(label), saleId }, transaction.Transaction);
        }, cancellationToken);

    public Task ResumeAsync(long saleId, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            string? status = transaction.Connection.QuerySingleOrDefault<string>(
                "SELECT status FROM sale WHERE id=@saleId", new { saleId }, transaction.Transaction);
            if (status != "HELD") throw new DomainException(ErrorCode.SaleFinalized, saleId);
            RevalidateStock(transaction, saleId);
            transaction.Connection.Execute("""
                UPDATE sale_line SET
                    unit_price=(SELECT selling_price FROM product_lot WHERE id=sale_line.lot_id),
                    mrp=(SELECT mrp FROM product_lot WHERE id=sale_line.lot_id),
                    unit_cost=(SELECT unit_cost FROM product_lot WHERE id=sale_line.lot_id)
                WHERE sale_id=@saleId
                """, new { saleId }, transaction.Transaction);
            transaction.Connection.Execute("UPDATE sale SET status='DRAFT' WHERE id=@saleId",
                new { saleId }, transaction.Transaction);
            Reprice(transaction, saleId);
        }, cancellationToken);

    public Task<IReadOnlyList<HeldSale>> GetHeldAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync<IReadOnlyList<HeldSale>>(connection => connection.Query<HeldRow>("""
            SELECT id AS Id,created_at AS CreatedAt,notes AS Label,line_count AS ItemCount,grand_total AS Total
            FROM sale WHERE status='HELD' ORDER BY created_at,id
            """).Select(static row => new HeldSale(row.Id, row.CreatedAt, row.Label, row.ItemCount,
                Money.FromScaled(row.Total))).ToArray(), cancellationToken);

    public async Task<CompletedSale> CompleteAsync(CompleteSaleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        CompletedSale result = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, command.OperationId, OpType.SaleComplete);
            if (replay?.RefId is { } doneId) return LoadCompleted(transaction, doneId) with { WasReplay = true };
            if (command.Payments.Count == 0 || command.Payments.Any(static item => item.Amount <= Money.Zero))
                throw new DomainException(ErrorCode.PaymentShort);
            HeaderRow sale = GetHeader(transaction, command.SaleId);
            if (sale.Status is not ("DRAFT" or "HELD")) throw new DomainException(ErrorCode.SaleFinalized);
            DateOnly date = ParseDate(sale.SaleDate);
            periodGuard.EnsureOpen(date);
            RevalidateStock(transaction, command.SaleId);
            PricedBill bill = Reprice(transaction, command.SaleId);
            if (bill.Lines.Count == 0) throw new DomainException(ErrorCode.EmptyBill);
            Money paid = command.Payments.Aggregate(Money.Zero, static (sum, item) => sum + item.Amount);
            if (paid < bill.GrandTotal) throw new DomainException(ErrorCode.PaymentShort, bill.GrandTotal - paid);
            Money change = paid - bill.GrandTotal;
            if (change > Money.Zero && command.Payments.All(static item => item.Mode != PaymentMode.Cash))
                throw new DomainException(ErrorCode.OverpaymentNonCash, change);
            string invoice = documentNumbers.Next(transaction, DocType.Sale, date);
            foreach (DbLineRow line in LoadLines(transaction, command.SaleId))
                stockLedger.Post(transaction, new(date, line.ProductId, line.LotId, "SALE",
                    Quantity.FromScaled(-line.Quantity), Money.FromScaled(line.UnitCost), "sale_line", line.Id));
            foreach (PaymentInput payment in command.Payments)
                transaction.Connection.Execute("""
                    INSERT INTO sale_payment(sale_id,mode,amount,reference,paid_at)
                    VALUES (@saleId,@mode,@amount,@reference,@paidAt)
                    """, new
                {
                    saleId = command.SaleId,
                    mode = PaymentCode(payment.Mode),
                    amount = payment.Amount.Scaled,
                    reference = Clean(payment.Reference),
                    paidAt = Timestamp(clock.Now),
                }, transaction.Transaction);
            transaction.Connection.Execute("""
                UPDATE sale SET invoice_no=@invoice,status='COMPLETED',paid_total=@paid,
                    change_returned=@change,completed_at=@completedAt WHERE id=@saleId
                """, new
            {
                invoice,
                paid = paid.Scaled,
                change = change.Scaled,
                completedAt = Timestamp(clock.Now),
                saleId = command.SaleId,
            }, transaction.Transaction);
            OperationGuard.Complete(transaction, command.OperationId, "sale", command.SaleId);
            return new CompletedSale(command.SaleId, invoice, bill, change);
        }, cancellationToken);
        if (!result.WasReplay) WeakReferenceMessenger.Default.Send(new SaleCompletedMessage(result.SaleId));
        return result;
    }

    public Task VoidAsync(long saleId, string reason, string operationId,
        CancellationToken cancellationToken = default) => database.WriteAsync(transaction =>
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCode.Validation, nameof(reason));
        OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.SaleVoid);
        if (replay?.RefId is not null) return;
        HeaderRow sale = GetHeader(transaction, saleId);
        if (sale.Status != "COMPLETED" || transaction.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sale_return WHERE sale_id=@saleId", new { saleId }, transaction.Transaction) > 0)
            throw new DomainException(ErrorCode.VoidNotAllowed);
        DateOnly date = ParseDate(sale.SaleDate);
        if (settings.Current.VoidSameDayOnly && date != clock.BusinessDate)
            throw new DomainException(ErrorCode.VoidNotAllowed, date);
        periodGuard.EnsureOpen(date);
        foreach (DbLineRow line in LoadLines(transaction, saleId))
            stockLedger.Post(transaction, new(clock.BusinessDate, line.ProductId, line.LotId, "SALE_VOID",
                Quantity.FromScaled(line.Quantity), Money.FromScaled(line.UnitCost), "sale_line", line.Id, reason.Trim()));
        transaction.Connection.Execute("""
            UPDATE sale SET status='VOID',void_reason=@reason,voided_at=@voidedAt WHERE id=@saleId
            """, new { reason = reason.Trim(), voidedAt = Timestamp(clock.Now), saleId }, transaction.Transaction);
        auditLog.Write(transaction, "sale", saleId, "VOID", new { reason = reason.Trim() });
        OperationGuard.Complete(transaction, operationId, "sale", saleId);
    }, cancellationToken);

    public Task<CompletedReturn> ReturnAsync(CreateReturnCommand command,
        CancellationToken cancellationToken = default) => database.WriteAsync(transaction =>
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Reason) || command.Lines.Count == 0 ||
            command.Lines.Any(static item => item.Quantity <= Quantity.Zero))
            throw new DomainException(ErrorCode.Validation, "A reason and return quantities are required.");
        OperationRef? replay = OperationGuard.Claim(transaction, command.OperationId, OpType.SaleReturn);
        if (replay?.RefId is { } oldId) return LoadReturn(transaction, oldId) with { WasReplay = true };
        if (GetHeader(transaction, command.SaleId).Status != "COMPLETED")
            throw new DomainException(ErrorCode.SaleFinalized);
        periodGuard.EnsureOpen(command.ReturnDate);
        string returnNumber = documentNumbers.Next(transaction, DocType.SaleReturn, command.ReturnDate);
        long returnId = transaction.Connection.ExecuteScalar<long>("""
            INSERT INTO sale_return(return_no,sale_id,return_date,reason,refund_mode)
            VALUES (@number,@saleId,@date,@reason,@mode) RETURNING id
            """, new
        {
            number = returnNumber,
            saleId = command.SaleId,
            date = DateText(command.ReturnDate),
            reason = command.Reason.Trim(),
            mode = RefundCode(command.RefundMode),
        }, transaction.Transaction);
        Money taxableTotal = Money.Zero;
        Money taxTotal = Money.Zero;
        Money refundTotal = Money.Zero;
        Money costTotal = Money.Zero;
        foreach (ReturnLineInput requested in command.Lines)
        {
            ReturnableRow original = transaction.Connection.QuerySingleOrDefault<ReturnableRow>("""
                SELECT sl.id AS Id,sl.product_id AS ProductId,sl.lot_id AS LotId,sl.qty AS Quantity,
                       sl.taxable_amount AS Taxable,sl.tax_amount AS Tax,sl.line_total AS Total,
                       sl.cost_amount AS Cost,sl.unit_cost AS UnitCost,
                       COALESCE(SUM(rl.qty),0) AS ReturnedQuantity,
                       COALESCE(SUM(rl.taxable_amount),0) AS ReturnedTaxable,
                       COALESCE(SUM(rl.tax_amount),0) AS ReturnedTax,
                       COALESCE(SUM(rl.refund_amount),0) AS ReturnedTotal,
                       COALESCE(SUM(rl.cost_amount),0) AS ReturnedCost
                FROM sale_line sl LEFT JOIN sale_return_line rl ON rl.sale_line_id=sl.id
                WHERE sl.id=@lineId AND sl.sale_id=@saleId GROUP BY sl.id
                """, new { lineId = requested.SaleLineId, saleId = command.SaleId }, transaction.Transaction)
                ?? throw new DomainException(ErrorCode.NotFound, requested.SaleLineId);
            Quantity remaining = Quantity.FromScaled(original.Quantity - original.ReturnedQuantity);
            if (requested.Quantity > remaining)
                throw new DomainException(ErrorCode.ReturnQtyExceedsSold, remaining);
            bool final = requested.Quantity == remaining;
            Money taxable = Prorate(original.Taxable, original.ReturnedTaxable, original.Quantity, requested.Quantity, final);
            Money tax = Prorate(original.Tax, original.ReturnedTax, original.Quantity, requested.Quantity, final);
            Money refund = Prorate(original.Total, original.ReturnedTotal, original.Quantity, requested.Quantity, final);
            Money cost = Prorate(original.Cost, original.ReturnedCost, original.Quantity, requested.Quantity, final);
            transaction.Connection.Execute("""
                INSERT INTO sale_return_line(sale_return_id,sale_line_id,qty,taxable_amount,tax_amount,
                    refund_amount,cost_amount,resellable)
                VALUES (@returnId,@lineId,@qty,@taxable,@tax,@refund,@cost,@resellable)
                """, new
            {
                returnId,
                lineId = original.Id,
                qty = requested.Quantity.Scaled,
                taxable = taxable.Scaled,
                tax = tax.Scaled,
                refund = refund.Scaled,
                cost = cost.Scaled,
                resellable = requested.Resellable ? 1 : 0,
            }, transaction.Transaction);
            stockLedger.Post(transaction, new(command.ReturnDate, original.ProductId, original.LotId,
                "SALE_RETURN", requested.Quantity, Money.FromScaled(original.UnitCost),
                "sale_return", returnId, command.Reason.Trim()));
            if (!requested.Resellable)
            {
                string damageNumber = documentNumbers.Next(transaction, DocType.Damage, command.ReturnDate);
                long damageId = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO damage(doc_no,damage_date,product_id,lot_id,qty,unit_cost,cost_value,
                        reason,notes) VALUES (@number,@date,@productId,@lotId,@qty,@unitCost,
                        @cost,'CUSTOMER_RETURN',@notes) RETURNING id
                    """, new
                {
                    number = damageNumber,
                    date = DateText(command.ReturnDate),
                    productId = original.ProductId,
                    lotId = original.LotId,
                    qty = requested.Quantity.Scaled,
                    unitCost = original.UnitCost,
                    cost = cost.Scaled,
                    notes = command.Reason.Trim(),
                }, transaction.Transaction);
                stockLedger.Post(transaction, new(command.ReturnDate, original.ProductId, original.LotId,
                    "DAMAGE", Quantity.FromScaled(-requested.Quantity.Scaled), Money.FromScaled(original.UnitCost),
                    "damage", damageId, "CUSTOMER_RETURN"));
            }
            taxableTotal += taxable;
            taxTotal += tax;
            refundTotal += refund;
            costTotal += cost;
        }
        transaction.Connection.Execute("""
            UPDATE sale_return SET taxable_total=@taxable,tax_total=@tax,refund_total=@refund,
                cost_total=@cost WHERE id=@returnId
            """, new
        {
            taxable = taxableTotal.Scaled,
            tax = taxTotal.Scaled,
            refund = refundTotal.Scaled,
            cost = costTotal.Scaled,
            returnId,
        }, transaction.Transaction);
        OperationGuard.Complete(transaction, command.OperationId, "sale_return", returnId);
        return new CompletedReturn(returnId, returnNumber, taxableTotal, taxTotal, refundTotal, costTotal);
    }, cancellationToken);

    public Task<SaleDetails> GetAsync(long saleId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => LoadDetails(connection, saleId), cancellationToken);

    public Task<IReadOnlyList<SaleHistoryItem>> GetHistoryAsync(SaleHistoryFilter filter,
        CancellationToken cancellationToken = default) => database.ReadAsync<IReadOnlyList<SaleHistoryItem>>(connection =>
    {
        ArgumentNullException.ThrowIfNull(filter);
        string? status = filter.Status is null ? null : StatusCode(filter.Status.Value);
        string? payment = filter.PaymentMode is null ? null : PaymentCode(filter.PaymentMode.Value);
        return connection.Query<HistoryRow>("""
            SELECT s.id AS Id,s.invoice_no AS InvoiceNumber,s.sale_date AS SaleDate,s.status AS Status,
                   s.line_count AS LineCount,s.grand_total AS GrandTotal,s.completed_at AS CompletedAt,
                   s.void_reason AS VoidReason,
                   COALESCE((SELECT group_concat(DISTINCT p.mode) FROM sale_payment p WHERE p.sale_id=s.id),'') AS PaymentModes
            FROM sale s
            WHERE (@fromDate IS NULL OR s.sale_date>=@fromDate) AND (@toDate IS NULL OR s.sale_date<=@toDate)
              AND (@status IS NULL OR s.status=@status)
              AND (@payment IS NULL OR EXISTS(SELECT 1 FROM sale_payment p WHERE p.sale_id=s.id AND p.mode=@payment))
              AND (@minimum IS NULL OR s.grand_total>=@minimum) AND (@maximum IS NULL OR s.grand_total<=@maximum)
              AND (@invoice IS NULL OR s.invoice_no LIKE @invoice || '%')
              AND (@beforeDate IS NULL OR (s.sale_date,s.id)<(@beforeDate,@beforeId))
            ORDER BY s.sale_date DESC,s.id DESC LIMIT @limit
            """, new
        {
            fromDate = filter.From is null ? null : DateText(filter.From.Value),
            toDate = filter.To is null ? null : DateText(filter.To.Value),
            status,
            payment,
            minimum = filter.MinimumAmount?.Scaled,
            maximum = filter.MaximumAmount?.Scaled,
            invoice = Clean(filter.InvoiceNumber),
            beforeDate = filter.BeforeDate is null ? null : DateText(filter.BeforeDate.Value),
            beforeId = filter.BeforeId,
            limit = Math.Clamp(filter.Limit, 1, 500),
        }).Select(static row => new SaleHistoryItem(row.Id, row.InvoiceNumber, ParseDate(row.SaleDate),
            ParseStatus(row.Status), row.LineCount, Money.FromScaled(row.GrandTotal), row.PaymentModes,
            row.CompletedAt, row.VoidReason)).ToArray();
    }, cancellationToken);

    public Task<DayEndSummary> GetDayEndAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection =>
        {
            string day = DateText(date);
            DayRow sale = connection.QuerySingle<DayRow>("""
                SELECT COUNT(*) AS BillCount,COALESCE(SUM(gross_total),0) AS Gross,
                       COALESCE(SUM(discount_total),0) AS Discounts,COALESCE(SUM(taxable_total),0) AS Net,
                       COALESCE(SUM(tax_total),0) AS Tax,COALESCE(SUM(round_off),0) AS RoundOff
                FROM sale WHERE sale_date=@day AND status='COMPLETED'
                """, new { day });
            PaymentTotal[] payments = connection.Query<PaymentRow>("""
                SELECT p.mode AS Mode,COALESCE(SUM(p.amount),0) AS Amount FROM sale_payment p
                JOIN sale s ON s.id=p.sale_id WHERE s.sale_date=@day AND s.status='COMPLETED' GROUP BY p.mode
                """, new { day }).Select(static row =>
                    new PaymentTotal(ParsePayment(row.Mode), Money.FromScaled(row.Amount))).ToArray();
            CountValueRow voids = connection.QuerySingle<CountValueRow>("""
                SELECT COUNT(*) AS Count,COALESCE(SUM(grand_total),0) AS Value
                FROM sale WHERE sale_date=@day AND status='VOID'
                """, new { day });
            CountValueRow returns = connection.QuerySingle<CountValueRow>("""
                SELECT COUNT(*) AS Count,COALESCE(SUM(refund_total),0) AS Value
                FROM sale_return WHERE return_date=@day
                """, new { day });
            long cashRefunds = connection.ExecuteScalar<long>("""
                SELECT COALESCE(SUM(refund_total),0) FROM sale_return WHERE return_date=@day AND refund_mode='CASH'
                """, new { day });
            long cashExpenses = connection.ExecuteScalar<long>("""
                SELECT COALESCE(SUM(amount),0) FROM expense WHERE expense_date=@day AND mode='CASH'
                """, new { day });
            Money cashSales = payments.FirstOrDefault(static item => item.Mode == PaymentMode.Cash)?.Amount ?? Money.Zero;
            return new DayEndSummary(date, sale.BillCount, Money.FromScaled(sale.Gross),
                Money.FromScaled(sale.Discounts), Money.FromScaled(sale.Net), Money.FromScaled(sale.Tax),
                Money.FromScaled(sale.RoundOff), payments, voids.Count, Money.FromScaled(voids.Value),
                returns.Count, Money.FromScaled(returns.Value), Money.FromScaled(cashExpenses),
                cashSales - Money.FromScaled(cashRefunds) - Money.FromScaled(cashExpenses));
        }, cancellationToken);

    public Task<ReceiptData> GetReceiptAsync(long saleId, bool duplicate = false,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        SaleDetails sale = LoadDetails(connection, saleId);
        if (sale.Status is not (SaleStatus.Completed or SaleStatus.Void) ||
            sale.InvoiceNumber is null || sale.CompletedAt is null)
            throw new DomainException(ErrorCode.SaleFinalized, saleId);
        AppSettings current = settings.Current;
        long change = connection.ExecuteScalar<long>("SELECT change_returned FROM sale WHERE id=@saleId", new { saleId });
        return new ReceiptData(current.StoreName, current.StoreAddress, current.StorePhone, current.StoreGstin,
            current.TaxMode, sale.InvoiceNumber, sale.CompletedAt.Value, sale.CustomerName,
            sale.Lines.Select(static line => new ReceiptLine(line.ProductName, line.ProductNameLocal,
                line.Quantity, line.UnitPrice, line.Mrp, line.Discount, line.Taxable, line.Tax,
                line.Total, line.TaxRate)).ToArray(), sale.Totals.Gross, sale.Totals.Discount,
            sale.Totals.Taxable, sale.Totals.Tax, sale.Totals.RoundOff, sale.Totals.GrandTotal,
            sale.Totals.MrpSavings, sale.Payments, Money.FromScaled(change),
            current.PrintLanguage,
            current.PrintLanguage == "gu-IN" ? current.GujaratiReceiptFooter : current.ReceiptFooter, duplicate);
    }, cancellationToken);

    private PricedBill Reprice(DbTx transaction, long saleId)
    {
        HeaderRow sale = GetHeader(transaction, saleId);
        DbLineRow[] rows = LoadLines(transaction, saleId);
        PricedBill bill = PricingEngine.Price(rows.Select(ToInput).ToArray(),
            Money.FromScaled(sale.BillDiscount), Money.FromScaled(settings.Current.BillRoundTo));
        for (int index = 0; index < rows.Length; index++)
        {
            PricedLine line = bill.Lines[index];
            transaction.Connection.Execute("""
                UPDATE sale_line SET discount=@discount,taxable_amount=@taxable,tax_amount=@tax,
                    line_total=@total,cost_amount=@cost WHERE id=@id
                """, new
            {
                discount = line.Discount.Scaled,
                taxable = line.Taxable.Scaled,
                tax = line.Tax.Scaled,
                total = line.Total.Scaled,
                cost = line.Cost.Scaled,
                id = rows[index].Id,
            }, transaction.Transaction);
        }
        transaction.Connection.Execute("""
            UPDATE sale SET line_count=@count,gross_total=@gross,discount_total=@discount,
                taxable_total=@taxable,tax_total=@tax,round_off=@roundOff,grand_total=@grand,
                cost_total=@cost WHERE id=@saleId
            """, new
        {
            count = rows.Length,
            gross = bill.Gross.Scaled,
            discount = bill.Discount.Scaled,
            taxable = bill.Taxable.Scaled,
            tax = bill.Tax.Scaled,
            roundOff = bill.RoundOff.Scaled,
            grand = bill.GrandTotal.Scaled,
            cost = bill.Cost.Scaled,
            saleId,
        }, transaction.Transaction);
        return bill;
    }

    private CartLineInput ToInput(DbLineRow row) => new(
        row.ProductId, row.LotId, Quantity.FromScaled(row.Quantity), Money.FromScaled(row.UnitPrice),
        row.Mrp is null ? null : Money.FromScaled(row.Mrp.Value),
        Percent.FromScaled(EffectiveTaxRate(row.TaxRate)), row.PriceIncludesTax,
        Money.FromScaled(row.LineDiscount), Money.FromScaled(row.UnitCost));

    private LotAllocation AllocatePreferred(DbTx transaction, long productId, long lotId,
        Quantity quantity, IReadOnlyDictionary<long, Quantity> reserved)
    {
        LotRow row = transaction.Connection.QuerySingleOrDefault<LotRow>("""
            SELECT id AS LotId,qty_on_hand AS OnHand,expiry_date AS Expiry,selling_price AS Price,
                   mrp AS Mrp,unit_cost AS Cost,is_active AS IsActive FROM product_lot
            WHERE id=@lotId AND product_id=@productId
            """, new { lotId, productId }, transaction.Transaction)
            ?? throw new DomainException(ErrorCode.LotProductMismatch, lotId, productId);
        if (!row.IsActive) throw new DomainException(ErrorCode.Validation, "The selected lot is blocked.");
        if (settings.Current.BlockExpiredLots && row.Expiry is { } expiry && expiry < clock.BusinessDate)
            throw new DomainException(ErrorCode.ExpiredLot, expiry);
        Quantity available = Quantity.FromScaled(row.OnHand) - reserved.GetValueOrDefault(lotId);
        if (!settings.Current.AllowNegativeStock && quantity > available)
            throw new DomainException(ErrorCode.InsufficientStock, quantity - available);
        return new(new(row.LotId, Quantity.FromScaled(row.OnHand), row.Expiry, Money.FromScaled(row.Price),
            row.Mrp is null ? null : Money.FromScaled(row.Mrp.Value), Money.FromScaled(row.Cost)), quantity);
    }

    private void RevalidateStock(DbTx transaction, long saleId)
    {
        foreach (StockRow row in transaction.Connection.Query<StockRow>("""
            SELECT sl.product_id AS ProductId,sl.qty AS Quantity,l.qty_on_hand AS OnHand,
                   l.expiry_date AS Expiry,l.is_active AS IsActive
            FROM sale_line sl JOIN product_lot l ON l.id=sl.lot_id WHERE sl.sale_id=@saleId
            """, new { saleId }, transaction.Transaction))
        {
            if (!row.IsActive) throw new DomainException(ErrorCode.Validation, "A bill lot is blocked.");
            if (settings.Current.BlockExpiredLots && row.Expiry is { } expiry && expiry < clock.BusinessDate)
                throw new DomainException(ErrorCode.ExpiredLot, expiry);
            if (!settings.Current.AllowNegativeStock && row.Quantity > row.OnHand)
                throw new DomainException(ErrorCode.InsufficientStock, row.ProductId);
        }
    }

    private static SaleDetails LoadDetails(System.Data.IDbConnection connection, long saleId)
    {
        DetailHeaderRow header = connection.QuerySingleOrDefault<DetailHeaderRow>("""
            SELECT s.id AS Id,s.invoice_no AS InvoiceNo,s.sale_date AS SaleDate,
                   s.customer_id AS CustomerId,s.status AS Status,s.gross_total AS GrossTotal,
                   s.discount_total AS DiscountTotal,s.taxable_total AS TaxableTotal,
                   s.tax_total AS TaxTotal,s.round_off AS RoundOff,s.grand_total AS GrandTotal,
                   s.cost_total AS CostTotal,s.notes AS Notes,s.void_reason AS VoidReason,
                   s.created_at AS CreatedAt,s.completed_at AS CompletedAt,
                   s.bill_discount AS BillDiscount,c.name AS CustomerName
            FROM sale s LEFT JOIN customer c ON c.id=s.customer_id WHERE s.id=@saleId
            """, new { saleId }) ?? throw new DomainException(ErrorCode.NotFound, saleId);
        SaleLine[] lines = connection.Query<DetailLineRow>("""
            SELECT sl.id AS Id,sl.line_no AS LineNo,sl.product_id AS ProductId,sl.lot_id AS LotId,
                   sl.product_name AS ProductName,sl.product_name_local AS ProductNameLocal,
                   l.batch_no AS BatchNumber,l.expiry_date AS ExpiryDate,sl.qty AS Qty,
                   sl.unit_price AS UnitPrice,sl.mrp AS Mrp,sl.discount AS Discount,
                   sl.tax_rate_pct AS TaxRatePct,sl.taxable_amount AS TaxableAmount,
                   sl.tax_amount AS TaxAmount,sl.line_total AS LineTotal,sl.unit_cost AS UnitCost,
                   sl.cost_amount AS CostAmount,sl.scanned_barcode AS ScannedBarcode
            FROM sale_line sl JOIN product_lot l ON l.id=sl.lot_id
            WHERE sl.sale_id=@saleId ORDER BY sl.line_no
            """, new { saleId }).Select(static row => row.ToModel()).ToArray();
        Money grand = Money.FromScaled(header.GrandTotal);
        Money atMrp = lines.Aggregate(Money.Zero, static (sum, line) =>
            sum + (line.Mrp ?? line.UnitPrice) * line.Quantity);
        var totals = new PricedBill([], Money.FromScaled(header.GrossTotal),
            Money.FromScaled(header.DiscountTotal), Money.FromScaled(header.TaxableTotal),
            Money.FromScaled(header.TaxTotal), Money.FromScaled(header.RoundOff), grand,
            Money.FromScaled(header.CostTotal), atMrp > grand ? atMrp - grand : Money.Zero);
        PaymentInput[] payments = connection.Query<PaymentRow>("""
            SELECT mode AS Mode,amount AS Amount,reference AS Reference
            FROM sale_payment WHERE sale_id=@saleId ORDER BY id
            """, new { saleId }).Select(static row =>
                new PaymentInput(ParsePayment(row.Mode), Money.FromScaled(row.Amount), row.Reference)).ToArray();
        return new(header.Id, header.InvoiceNo, ParseDate(header.SaleDate), ParseStatus(header.Status),
            header.CustomerId, header.CustomerName, header.Notes, Money.FromScaled(header.BillDiscount),
            lines, totals, payments, header.CreatedAt, header.CompletedAt, header.VoidReason);
    }

    private static CompletedSale LoadCompleted(DbTx transaction, long saleId)
    {
        HeaderRow header = GetHeader(transaction, saleId);
        if (header.InvoiceNo is null) throw new DomainException(ErrorCode.SaleFinalized, saleId);
        return new(saleId, header.InvoiceNo, StoredBill(transaction, saleId, header),
            Money.FromScaled(header.ChangeReturned));
    }

    private static CompletedReturn LoadReturn(DbTx transaction, long returnId)
    {
        ReturnSummaryRow row = transaction.Connection.QuerySingleOrDefault<ReturnSummaryRow>("""
            SELECT id AS Id,return_no AS ReturnNo,taxable_total AS Taxable,tax_total AS Tax,
                   refund_total AS Refund,cost_total AS Cost FROM sale_return WHERE id=@returnId
            """, new { returnId }, transaction.Transaction)
            ?? throw new DomainException(ErrorCode.NotFound, returnId);
        return new(row.Id, row.ReturnNo, Money.FromScaled(row.Taxable), Money.FromScaled(row.Tax),
            Money.FromScaled(row.Refund), Money.FromScaled(row.Cost));
    }

    private static PricedBill StoredBill(DbTx transaction, long saleId, HeaderRow header)
    {
        DbLineRow[] lines = LoadLines(transaction, saleId);
        Money atMrp = lines.Aggregate(Money.Zero, static (sum, line) =>
            sum + Money.FromScaled(line.Mrp ?? line.UnitPrice) * Quantity.FromScaled(line.Quantity));
        Money grand = Money.FromScaled(header.GrandTotal);
        return new([], Money.FromScaled(header.GrossTotal), Money.FromScaled(header.DiscountTotal),
            Money.FromScaled(header.TaxableTotal), Money.FromScaled(header.TaxTotal),
            Money.FromScaled(header.RoundOff), grand, Money.FromScaled(header.CostTotal),
            atMrp > grand ? atMrp - grand : Money.Zero);
    }

    private static void EnsureEditable(DbTx transaction, long saleId)
    {
        string? status = transaction.Connection.QuerySingleOrDefault<string>(
            "SELECT status FROM sale WHERE id=@saleId", new { saleId }, transaction.Transaction);
        if (status is null) throw new DomainException(ErrorCode.NotFound, saleId);
        if (status != "DRAFT") throw new DomainException(ErrorCode.SaleFinalized, saleId);
    }

    private static HeaderRow GetHeader(DbTx transaction, long saleId) =>
        transaction.Connection.QuerySingleOrDefault<HeaderRow>("""
            SELECT id AS Id,invoice_no AS InvoiceNo,sale_date AS SaleDate,status AS Status,
                   bill_discount AS BillDiscount,gross_total AS GrossTotal,discount_total AS DiscountTotal,
                   taxable_total AS TaxableTotal,tax_total AS TaxTotal,round_off AS RoundOff,
                   grand_total AS GrandTotal,cost_total AS CostTotal,change_returned AS ChangeReturned
            FROM sale WHERE id=@saleId
            """, new { saleId }, transaction.Transaction) ?? throw new DomainException(ErrorCode.NotFound, saleId);

    private static DbLineRow[] LoadLines(DbTx transaction, long saleId) =>
        transaction.Connection.Query<DbLineRow>("""
            SELECT id AS Id,product_id AS ProductId,lot_id AS LotId,qty AS Quantity,
                   unit_price AS UnitPrice,mrp AS Mrp,line_discount AS LineDiscount,
                   tax_rate_pct AS TaxRate,price_includes_tax AS PriceIncludesTax,unit_cost AS UnitCost
            FROM sale_line WHERE sale_id=@saleId ORDER BY line_no
            """, new { saleId }, transaction.Transaction).ToArray();

    private long EffectiveTaxRate(long rate) => settings.Current.TaxMode == "REGULAR" ? rate : 0;
    private static Money Prorate(long original, long returned, long originalQuantity, Quantity requested, bool final) =>
        final ? Money.FromScaled(original - returned) :
        new Money(Fx3.FromScaled(original) * requested.Value / Fx3.FromScaled(originalQuantity));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string DateText(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Timestamp(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string PaymentCode(PaymentMode mode) => mode switch
    {
        PaymentMode.Cash => "CASH",
        PaymentMode.Upi => "UPI",
        PaymentMode.Card => "CARD",
        PaymentMode.Other => "OTHER",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    private static PaymentMode ParsePayment(string mode) => mode switch
    {
        "CASH" => PaymentMode.Cash,
        "UPI" => PaymentMode.Upi,
        "CARD" => PaymentMode.Card,
        _ => PaymentMode.Other,
    };
    private static string RefundCode(RefundMode mode) => mode switch
    {
        RefundMode.Cash => "CASH",
        RefundMode.Upi => "UPI",
        RefundMode.Card => "CARD",
        RefundMode.CreditNote => "CREDIT_NOTE",
        RefundMode.Other => "OTHER",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    private static string StatusCode(SaleStatus status) => status.ToString().ToUpperInvariant();
    private static SaleStatus ParseStatus(string status) => status switch
    {
        "DRAFT" => SaleStatus.Draft,
        "HELD" => SaleStatus.Held,
        "COMPLETED" => SaleStatus.Completed,
        "VOID" => SaleStatus.Void,
        _ => throw new InvalidOperationException($"Unknown sale status '{status}'."),
    };

    private sealed class ProductRow { public string Name { get; init; } = ""; public string? NameLocal { get; init; } public string? HsnCode { get; init; } public bool PriceIncludesTax { get; init; } public bool IsActive { get; init; } public bool AllowFraction { get; init; } public long TaxRate { get; init; } }
    private sealed class DbLineRow { public long Id { get; init; } public long ProductId { get; init; } public long LotId { get; init; } public long Quantity { get; init; } public long UnitPrice { get; init; } public long? Mrp { get; init; } public long LineDiscount { get; init; } public long TaxRate { get; init; } public bool PriceIncludesTax { get; init; } public long UnitCost { get; init; } }
    private sealed class HeaderRow { public long Id { get; init; } public string? InvoiceNo { get; init; } public string SaleDate { get; init; } = ""; public string Status { get; init; } = ""; public long BillDiscount { get; init; } public long GrossTotal { get; init; } public long DiscountTotal { get; init; } public long TaxableTotal { get; init; } public long TaxTotal { get; init; } public long RoundOff { get; init; } public long GrandTotal { get; init; } public long CostTotal { get; init; } public long ChangeReturned { get; init; } }
    private sealed class LotRow { public long LotId { get; init; } public long OnHand { get; init; } public DateOnly? Expiry { get; init; } public long Price { get; init; } public long? Mrp { get; init; } public long Cost { get; init; } public bool IsActive { get; init; } }
    private sealed class LineCheckRow { public long ProductId { get; init; } public long OnHand { get; init; } public bool AllowFraction { get; init; } }
    private sealed class StockRow { public long ProductId { get; init; } public long Quantity { get; init; } public long OnHand { get; init; } public DateOnly? Expiry { get; init; } public bool IsActive { get; init; } }
    private sealed class HeldRow { public long Id { get; init; } public DateTime CreatedAt { get; init; } public string? Label { get; init; } public int ItemCount { get; init; } public long Total { get; init; } }
    private sealed class HistoryRow { public long Id { get; init; } public string? InvoiceNumber { get; init; } public string SaleDate { get; init; } = ""; public string Status { get; init; } = ""; public int LineCount { get; init; } public long GrandTotal { get; init; } public string PaymentModes { get; init; } = ""; public DateTime? CompletedAt { get; init; } public string? VoidReason { get; init; } }
    private sealed class ReturnableRow { public long Id { get; init; } public long ProductId { get; init; } public long LotId { get; init; } public long Quantity { get; init; } public long Taxable { get; init; } public long Tax { get; init; } public long Total { get; init; } public long Cost { get; init; } public long UnitCost { get; init; } public long ReturnedQuantity { get; init; } public long ReturnedTaxable { get; init; } public long ReturnedTax { get; init; } public long ReturnedTotal { get; init; } public long ReturnedCost { get; init; } }
    private sealed class ReturnSummaryRow { public long Id { get; init; } public string ReturnNo { get; init; } = ""; public long Taxable { get; init; } public long Tax { get; init; } public long Refund { get; init; } public long Cost { get; init; } }
    private sealed class DayRow { public int BillCount { get; init; } public long Gross { get; init; } public long Discounts { get; init; } public long Net { get; init; } public long Tax { get; init; } public long RoundOff { get; init; } }
    private sealed class CountValueRow { public int Count { get; init; } public long Value { get; init; } }
    private sealed class PaymentRow { public string Mode { get; init; } = ""; public long Amount { get; init; } public string? Reference { get; init; } }
    private sealed class DetailHeaderRow { public long Id { get; init; } public string? InvoiceNo { get; init; } public string SaleDate { get; init; } = ""; public long? CustomerId { get; init; } public string Status { get; init; } = ""; public long GrossTotal { get; init; } public long DiscountTotal { get; init; } public long TaxableTotal { get; init; } public long TaxTotal { get; init; } public long RoundOff { get; init; } public long GrandTotal { get; init; } public long CostTotal { get; init; } public string? Notes { get; init; } public string? VoidReason { get; init; } public DateTime CreatedAt { get; init; } public DateTime? CompletedAt { get; init; } public long BillDiscount { get; init; } public string? CustomerName { get; init; } }
    private sealed class DetailLineRow
    {
        public long Id { get; init; }
        public int LineNo { get; init; }
        public long ProductId { get; init; }
        public long LotId { get; init; }
        public string ProductName { get; init; } = "";
        public string? ProductNameLocal { get; init; }
        public string? BatchNumber { get; init; }
        public DateOnly? ExpiryDate { get; init; }
        public long Qty { get; init; }
        public long UnitPrice { get; init; }
        public long? Mrp { get; init; }
        public long Discount { get; init; }
        public long TaxRatePct { get; init; }
        public long TaxableAmount { get; init; }
        public long TaxAmount { get; init; }
        public long LineTotal { get; init; }
        public long UnitCost { get; init; }
        public long CostAmount { get; init; }
        public string? ScannedBarcode { get; init; }
        public SaleLine ToModel() => new(Id, LineNo, ProductId, LotId, ProductName, ProductNameLocal,
            BatchNumber, ExpiryDate, Quantity.FromScaled(Qty), Money.FromScaled(UnitPrice),
            Mrp is null ? null : Money.FromScaled(Mrp.Value), Money.FromScaled(Discount),
            Percent.FromScaled(TaxRatePct), Money.FromScaled(TaxableAmount), Money.FromScaled(TaxAmount),
            Money.FromScaled(LineTotal), Money.FromScaled(UnitCost), Money.FromScaled(CostAmount), ScannedBarcode);
    }
}
