using System.Data;
using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Messages;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Validation;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Purchasing;

public sealed class PurchasingService(
    DbExecutor database,
    IStockLedger ledger,
    DocNumberService numbers,
    PeriodGuard periods,
    ISettings settings,
    IAuditLog audit)
{
    private static readonly HashSet<string> PaymentModes = ["CASH", "UPI", "BANK", "CHEQUE", "OTHER"];

    public Task<IReadOnlyList<VendorItem>> GetVendorsAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<VendorItem>)connection.Query<VendorRow>("""
            SELECT v.id AS Id,v.name AS Name,v.phone AS Phone,v.gstin AS Gstin,
                   b.balance_due AS BalanceDue,v.is_active AS IsActive
            FROM vendor v JOIN v_vendor_balance b ON b.vendor_id=v.id
            WHERE @includeInactive=1 OR v.is_active=1 ORDER BY v.name COLLATE NOCASE
            """, new { includeInactive = includeInactive ? 1 : 0 }).Select(static row => row.ToModel()).ToList(), cancellationToken);

    public Task<VendorInput> GetVendorAsync(long vendorId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection =>
        {
            VendorDetailsRow? row = connection.QuerySingleOrDefault<VendorDetailsRow>("""
                SELECT name AS Name,contact_person AS ContactPerson,phone AS Phone,alt_phone AS AlternatePhone,
                       email AS Email,address AS Address,city AS City,state AS State,pincode AS Pincode,
                       gstin AS Gstin,pan AS Pan,upi_id AS UpiId,bank_details AS BankDetails,
                       payment_terms_days AS PaymentTermsDays,opening_balance AS OpeningBalance,notes AS Notes
                FROM vendor WHERE id=@vendorId
                """, new { vendorId });
            if (row is null) throw new DomainException(ErrorCode.NotFound, "vendor", vendorId);
            return new VendorInput(row.Name, row.ContactPerson, row.Phone, row.AlternatePhone, row.Email,
                row.Address, row.City, row.State, row.Pincode, row.Gstin, row.Pan, row.UpiId,
                row.BankDetails, row.PaymentTermsDays, Money.FromScaled(row.OpeningBalance), row.Notes);
        }, cancellationToken);

    public Task<IReadOnlyList<PurchaseListItem>> GetPurchasesAsync(int limit = 500, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000) throw new DomainException(ErrorCode.Validation, nameof(limit));
        return database.ReadAsync(connection => (IReadOnlyList<PurchaseListItem>)connection.Query<PurchaseListRow>("""
            SELECT p.id AS Id,p.doc_no AS DocumentNumber,v.name AS VendorName,
                   p.vendor_invoice_no AS VendorInvoiceNumber,p.received_date AS ReceivedDate,
                   p.status AS Status,p.grand_total AS GrandTotal,p.vendor_bill_total AS VendorBillTotal
            FROM purchase p JOIN vendor v ON v.id=p.vendor_id
            ORDER BY p.received_date DESC,p.id DESC LIMIT @limit
            """, new { limit }).Select(static row => row.ToModel()).ToList(), cancellationToken);
    }

    public Task<PurchasePage> GetPurchasesPageAsync(DateOnly? beforeDate = null, long? beforeId = null,
        DateOnly? from = null, DateOnly? to = null, long? vendorId = null, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (beforeDate is null != (beforeId is null) || limit is < 1 or > 200 || from > to)
            throw new DomainException(ErrorCode.Validation, "Invalid purchase page filter.");
        return database.ReadAsync(connection =>
        {
            List<PurchaseListItem> rows = connection.Query<PurchaseListRow>("""
                SELECT p.id AS Id,p.doc_no AS DocumentNumber,v.name AS VendorName,
                       p.vendor_invoice_no AS VendorInvoiceNumber,p.received_date AS ReceivedDate,
                       p.status AS Status,p.grand_total AS GrandTotal,p.vendor_bill_total AS VendorBillTotal
                FROM purchase p JOIN vendor v ON v.id=p.vendor_id
                WHERE (@from IS NULL OR p.received_date>=@from)
                  AND (@to IS NULL OR p.received_date<=@to)
                  AND (@vendorId IS NULL OR p.vendor_id=@vendorId)
                  AND (@beforeDate IS NULL OR p.received_date<@beforeDate
                       OR (p.received_date=@beforeDate AND p.id<@beforeId))
                ORDER BY p.received_date DESC,p.id DESC LIMIT @take
                """, new { beforeDate, beforeId, from, to, vendorId, take = limit + 1 })
                .Select(static row => row.ToModel()).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            PurchaseListItem? last = rows.LastOrDefault();
            return new PurchasePage(rows, hasMore, last?.ReceivedDate, last?.Id);
        }, cancellationToken);
    }

    public Task<long> CreateVendorAsync(VendorInput input, CancellationToken cancellationToken = default)
    {
        ValidVendor vendor = ValidateVendor(input);
        return database.WriteAsync(transaction =>
        {
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO vendor(name,contact_person,phone,alt_phone,email,address,city,state,pincode,gstin,pan,
                                   upi_id,bank_details,payment_terms_days,opening_balance,notes)
                VALUES (@Name,@ContactPerson,@Phone,@AlternatePhone,@Email,@Address,@City,@State,@Pincode,@Gstin,@Pan,
                        @UpiId,@BankDetails,@PaymentTermsDays,@OpeningBalance,@Notes) RETURNING id
                """, vendor, transaction.Transaction);
            audit.Write(transaction, "vendor", id, "CREATE", new { vendor.Name, vendor.Gstin });
            return id;
        }, cancellationToken);
    }

    public Task UpdateVendorAsync(long id, VendorInput input, bool isActive = true, CancellationToken cancellationToken = default)
    {
        ValidVendor vendor = ValidateVendor(input);
        if (id <= 0) throw new DomainException(ErrorCode.Validation, nameof(id));
        return database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute("""
                UPDATE vendor SET name=@Name,contact_person=@ContactPerson,phone=@Phone,alt_phone=@AlternatePhone,
                    email=@Email,address=@Address,city=@City,state=@State,pincode=@Pincode,gstin=@Gstin,pan=@Pan,
                    upi_id=@UpiId,bank_details=@BankDetails,payment_terms_days=@PaymentTermsDays,
                    opening_balance=@OpeningBalance,notes=@Notes,is_active=@isActive WHERE id=@id
                """, new
            {
                id,
                vendor.Name,
                vendor.ContactPerson,
                vendor.Phone,
                vendor.AlternatePhone,
                vendor.Email,
                vendor.Address,
                vendor.City,
                vendor.State,
                vendor.Pincode,
                vendor.Gstin,
                vendor.Pan,
                vendor.UpiId,
                vendor.BankDetails,
                vendor.PaymentTermsDays,
                vendor.OpeningBalance,
                vendor.Notes,
                isActive = isActive ? 1 : 0,
            }, transaction.Transaction);
            if (changed == 0) throw new DomainException(ErrorCode.NotFound, "vendor", id);
            audit.Write(transaction, "vendor", id, "UPDATE", new { vendor.Name, vendor.Gstin, isActive });
        }, cancellationToken);
    }

    public Task<long> SaveDraftAsync(long? purchaseId, PurchaseDraftInput input, CancellationToken cancellationToken = default)
    {
        ValidateDraft(input);
        return database.WriteAsync(transaction =>
        {
            EnsureVendor(transaction, input.VendorId);
            long id;
            if (purchaseId is null)
            {
                id = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO purchase(vendor_id,vendor_invoice_no,vendor_invoice_date,received_date,vendor_bill_total,
                                         other_charges,notes)
                    VALUES (@VendorId,@invoice,@invoiceDate,@receivedDate,@bill,@charges,@notes) RETURNING id
                    """, HeaderParameters(input), transaction.Transaction);
            }
            else
            {
                id = purchaseId.Value;
                int changed = transaction.Connection.Execute("""
                    UPDATE purchase SET vendor_id=@VendorId,vendor_invoice_no=@invoice,vendor_invoice_date=@invoiceDate,
                        received_date=@receivedDate,vendor_bill_total=@bill,other_charges=@charges,notes=@notes
                    WHERE id=@id AND status='DRAFT'
                    """, Merge(HeaderParameters(input), id), transaction.Transaction);
                if (changed == 0) throw PurchaseMissingOrFinalized(transaction, id);
                transaction.Connection.Execute("DELETE FROM purchase_line WHERE purchase_id=@id", new { id }, transaction.Transaction);
            }

            InsertDraftLines(transaction, id, input.Lines);
            audit.Write(transaction, "purchase", id, purchaseId is null ? "CREATE_DRAFT" : "UPDATE_DRAFT", new { lines = input.Lines.Count });
            return id;
        }, cancellationToken);
    }

    public Task<PurchaseDraft> GetDraftAsync(long purchaseId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection =>
        {
            DraftHeader? header = connection.QuerySingleOrDefault<DraftHeader>("""
                SELECT id AS Id,vendor_id AS VendorId,vendor_invoice_no AS VendorInvoiceNumber,
                       vendor_invoice_date AS VendorInvoiceDate,received_date AS ReceivedDate,
                       vendor_bill_total AS VendorBillTotal,other_charges AS OtherCharges,notes AS Notes
                FROM purchase WHERE id=@purchaseId AND status='DRAFT'
                """, new { purchaseId });
            if (header is null) throw new DomainException(ErrorCode.NotFound, "purchase draft", purchaseId);
            IReadOnlyList<PurchaseLineInput> lines = connection.Query<PurchaseLineRow>("""
                SELECT id AS Id,product_id AS ProductId,batch_no AS BatchNumber,mfg_date AS ManufacturingDate,
                       expiry_date AS ExpiryDate,qty AS Quantity,free_qty AS FreeQuantity,
                       unit_cost AS Rate,discount AS Discount,tax_rate_pct AS TaxRate,
                       mrp AS Mrp,selling_price AS SellingPrice
                FROM purchase_line WHERE purchase_id=@purchaseId ORDER BY line_no
                """, new { purchaseId }).Select(static row => row.ToInput()).ToList();
            return new PurchaseDraft(header.Id, new PurchaseDraftInput(header.VendorId, header.VendorInvoiceNumber,
                header.VendorInvoiceDate, header.ReceivedDate,
                header.VendorBillTotal is null ? null : Money.FromScaled(header.VendorBillTotal.Value),
                Money.FromScaled(header.OtherCharges), header.Notes, lines));
        }, cancellationToken);

    public Task<PurchaseLineDefaults> GetLineDefaultsAsync(long vendorId, long productId, DateOnly receivedDate,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        LineDefaultsRow? row = connection.QuerySingleOrDefault<LineDefaultsRow>("""
            SELECT p.track_expiry AS TrackExpiry,p.price_includes_tax AS PriceIncludesTax,
                   p.default_shelf_life_days AS ShelfLifeDays,
                   COALESCE(p.expiry_alert_days,c.expiry_alert_days,@globalAlert) AS ExpiryAlertDays,
                   COALESCE(t.rate_pct,0) AS TaxRate,
                   COALESCE((SELECT l.mrp FROM product_lot l WHERE l.product_id=p.id ORDER BY l.id DESC LIMIT 1),
                            p.default_mrp) AS Mrp,
                   COALESCE((SELECT l.selling_price FROM product_lot l WHERE l.product_id=p.id ORDER BY l.id DESC LIMIT 1),
                            p.default_selling_price) AS SellingPrice,
                   (SELECT pl.unit_cost FROM purchase_line pl JOIN purchase po ON po.id=pl.purchase_id
                    WHERE pl.product_id=p.id AND po.vendor_id=@vendorId AND po.status='POSTED'
                    ORDER BY po.received_date DESC,pl.id DESC LIMIT 1) AS LastRate
            FROM product p JOIN category c ON c.id=p.category_id
            LEFT JOIN tax_rate t ON t.id=p.tax_rate_id
            WHERE p.id=@productId AND p.is_active=1
            """, new { vendorId, productId, globalAlert = settings.Current.ExpiryDefaultAlertDays });
        if (row is null) throw new DomainException(ErrorCode.NotFound, "product", productId);
        return new PurchaseLineDefaults(row.LastRate is null ? null : Money.FromScaled(row.LastRate.Value),
            Percent.FromScaled(row.TaxRate), row.Mrp is null ? null : Money.FromScaled(row.Mrp.Value),
            row.SellingPrice is null ? null : Money.FromScaled(row.SellingPrice.Value),
            row.TrackExpiry && row.ShelfLifeDays is int shelfLife ? receivedDate.AddDays(shelfLife) : null,
            row.ExpiryAlertDays, row.PriceIncludesTax);
    }, cancellationToken);

    public async Task<PostedPurchase> PostAsync(long purchaseId, string operationId, CancellationToken cancellationToken = default)
    {
        var changedProducts = new HashSet<long>();
        PostedPurchase posted = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.PurchasePost);
            if (replay is not null)
            {
                long existingId = replay.RefId ?? purchaseId;
                return LoadPosted(transaction, existingId) with { WasReplay = true };
            }

            PurchaseHeader? header = transaction.Connection.QuerySingleOrDefault<PurchaseHeader>("""
                SELECT id AS Id,vendor_id AS VendorId,received_date AS ReceivedDate,status AS Status,
                       other_charges AS OtherCharges,vendor_bill_total AS VendorBillTotal
                FROM purchase WHERE id=@purchaseId
                """, new { purchaseId }, transaction.Transaction);
            if (header is null) throw new DomainException(ErrorCode.NotFound, "purchase", purchaseId);
            if (header.Status != "DRAFT") throw new DomainException(ErrorCode.PurchaseFinalized, purchaseId);
            periods.EnsureOpen(header.ReceivedDate);
            List<PurchaseLineRow> rows = transaction.Connection.Query<PurchaseLineRow>("""
                SELECT pl.id AS Id,pl.product_id AS ProductId,pl.batch_no AS BatchNumber,pl.mfg_date AS ManufacturingDate,
                       pl.expiry_date AS ExpiryDate,pl.qty AS Quantity,pl.free_qty AS FreeQuantity,
                       pl.unit_cost AS Rate,pl.discount AS Discount,pl.tax_rate_pct AS TaxRate,
                       pl.mrp AS Mrp,pl.selling_price AS SellingPrice,
                       p.track_expiry AS TrackExpiry,u.allow_fraction AS AllowFraction
                FROM purchase_line pl JOIN product p ON p.id=pl.product_id JOIN uom u ON u.id=p.uom_id
                WHERE pl.purchase_id=@purchaseId ORDER BY pl.line_no
                """, new { purchaseId }, transaction.Transaction).ToList();
            if (rows.Count == 0) throw new DomainException(ErrorCode.EmptyBill);

            var costLines = new List<PurchaseCostLine>(rows.Count);
            var taxAmounts = new List<Money>(rows.Count);
            foreach (PurchaseLineRow row in rows)
            {
                ValidateLine(row.ToInput(), row.TrackExpiry, row.AllowFraction, header.ReceivedDate);
                Money taxable = Money.FromScaled(row.Rate) * Quantity.FromScaled(row.Quantity) - Money.FromScaled(row.Discount);
                Money tax = Tax.FromExclusive(taxable, Percent.FromScaled(row.TaxRate)).Tax;
                taxAmounts.Add(tax);
                costLines.Add(new(Quantity.FromScaled(row.Quantity), Quantity.FromScaled(row.FreeQuantity),
                    Money.FromScaled(row.Rate), Money.FromScaled(row.Discount), tax));
            }
            Money otherCharges = Money.FromScaled(header.OtherCharges);
            IReadOnlyList<Money> landed = LandedCost.Compute(costLines, otherCharges,
                !string.Equals(settings.Current.TaxMode, "REGULAR", StringComparison.Ordinal));
            Money subtotal = rows.Aggregate(Money.Zero, static (sum, row) =>
                sum + Money.FromScaled(row.Rate) * Quantity.FromScaled(row.Quantity));
            Money discounts = rows.Aggregate(Money.Zero, static (sum, row) => sum + Money.FromScaled(row.Discount));
            Money taxTotal = taxAmounts.Aggregate(Money.Zero, static (sum, tax) => sum + tax);
            Money beforeRound = subtotal - discounts + taxTotal + otherCharges;
            Money roundOff = MoneyMath.RoundOff(beforeRound, Money.FromScaled(settings.Current.BillRoundTo));
            Money grandTotal = beforeRound + roundOff;
            string docNo = numbers.Next(transaction, DocType.Purchase, header.ReceivedDate);
            var lotIds = new List<long>(rows.Count);

            for (int index = 0; index < rows.Count; index++)
            {
                PurchaseLineRow row = rows[index];
                transaction.Connection.Execute("""
                    UPDATE purchase_line SET tax_amount=@tax,line_total=@lineTotal,landed_unit_cost=@landed WHERE id=@id
                    """, new
                {
                    id = row.Id,
                    tax = taxAmounts[index].Scaled,
                    lineTotal = (Money.FromScaled(row.Rate) * Quantity.FromScaled(row.Quantity) - Money.FromScaled(row.Discount) + taxAmounts[index]).Scaled,
                    landed = landed[index].Scaled,
                }, transaction.Transaction);
                string lotCode = numbers.Next(transaction, DocType.Lot, header.ReceivedDate);
                Quantity received = Quantity.FromScaled(row.Quantity) + Quantity.FromScaled(row.FreeQuantity);
                long lotId = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO product_lot(product_id,lot_code,batch_no,purchase_line_id,vendor_id,received_date,mfg_date,
                                            expiry_date,unit_cost,mrp,selling_price,qty_received)
                    VALUES (@ProductId,@lotCode,@BatchNumber,@lineId,@vendorId,@receivedDate,@ManufacturingDate,
                            @ExpiryDate,@cost,@Mrp,@SellingPrice,@received) RETURNING id
                    """, new
                {
                    row.ProductId,
                    lotCode,
                    row.BatchNumber,
                    lineId = row.Id,
                    vendorId = header.VendorId,
                    receivedDate = Format(header.ReceivedDate),
                    row.ManufacturingDate,
                    row.ExpiryDate,
                    cost = landed[index].Scaled,
                    row.Mrp,
                    row.SellingPrice,
                    received = received.Scaled,
                }, transaction.Transaction);
                ledger.Post(transaction, new(header.ReceivedDate, row.ProductId, lotId, "PURCHASE", received,
                    landed[index], "purchase_line", row.Id));
                lotIds.Add(lotId);
                changedProducts.Add(row.ProductId);
            }
            transaction.Connection.Execute("""
                UPDATE purchase SET status='POSTED',doc_no=@docNo,subtotal=@subtotal,discount_total=@discounts,
                    tax_total=@taxTotal,round_off=@roundOff,grand_total=@grandTotal,tax_mode=@taxMode,
                    posted_at=datetime('now','localtime')
                WHERE id=@purchaseId
                """, new
            {
                purchaseId,
                docNo,
                subtotal = subtotal.Scaled,
                discounts = discounts.Scaled,
                taxTotal = taxTotal.Scaled,
                roundOff = roundOff.Scaled,
                grandTotal = grandTotal.Scaled,
                taxMode = settings.Current.TaxMode,
            }, transaction.Transaction);
            audit.Write(transaction, "purchase", purchaseId, "POST", new { docNo, grandTotal = grandTotal.Scaled, lots = lotIds.Count });
            OperationGuard.Complete(transaction, operationId, "purchase", purchaseId);
            return new PostedPurchase(purchaseId, docNo,
                new(subtotal, discounts, taxTotal, otherCharges, roundOff, grandTotal,
                    header.VendorBillTotal is null ? null : Money.FromScaled(header.VendorBillTotal.Value)), lotIds);
        }, cancellationToken);
        if (changedProducts.Count > 0) WeakReferenceMessenger.Default.Send(new StockChangedMessage(changedProducts.ToArray()));
        return posted;
    }

    public async Task CancelAsync(long purchaseId, DateOnly cancellationDate, string operationId, CancellationToken cancellationToken = default)
    {
        var products = new HashSet<long>();
        await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.PurchaseCancel);
            if (replay is not null) return;
            PurchaseHeader? header = transaction.Connection.QuerySingleOrDefault<PurchaseHeader>("""
                SELECT id AS Id,vendor_id AS VendorId,received_date AS ReceivedDate,status AS Status,
                       other_charges AS OtherCharges,vendor_bill_total AS VendorBillTotal
                FROM purchase WHERE id=@purchaseId
                """, new { purchaseId }, transaction.Transaction);
            if (header is null) throw new DomainException(ErrorCode.NotFound, "purchase", purchaseId);
            if (header.Status != "POSTED") throw new DomainException(ErrorCode.PurchaseFinalized, purchaseId);
            periods.EnsureOpen(cancellationDate);
            List<CancelLotRow> lots = transaction.Connection.Query<CancelLotRow>("""
                SELECT l.id AS LotId,l.product_id AS ProductId,l.qty_received AS Received,l.qty_on_hand AS OnHand,
                       l.unit_cost AS UnitCost,pl.id AS PurchaseLineId,
                       (SELECT COUNT(*) FROM stock_movement m WHERE m.lot_id=l.id) AS MovementCount
                FROM product_lot l JOIN purchase_line pl ON pl.id=l.purchase_line_id
                WHERE pl.purchase_id=@purchaseId
                """, new { purchaseId }, transaction.Transaction).ToList();
            if (lots.Count == 0 || lots.Any(static lot => lot.OnHand != lot.Received || lot.MovementCount != 1))
                throw new DomainException(ErrorCode.CancelNotAllowed, purchaseId);
            foreach (CancelLotRow lot in lots)
            {
                ledger.Post(transaction, new(cancellationDate, lot.ProductId, lot.LotId, "PURCHASE_CANCEL",
                    Quantity.FromScaled(-lot.Received), Money.FromScaled(lot.UnitCost), "purchase_line", lot.PurchaseLineId));
                transaction.Connection.Execute("UPDATE product_lot SET is_active=0 WHERE id=@LotId", lot, transaction.Transaction);
                products.Add(lot.ProductId);
            }
            transaction.Connection.Execute("UPDATE purchase SET status='CANCELLED' WHERE id=@purchaseId",
                new { purchaseId }, transaction.Transaction);
            audit.Write(transaction, "purchase", purchaseId, "CANCEL", new { lots = lots.Count });
            OperationGuard.Complete(transaction, operationId, "purchase", purchaseId);
        }, cancellationToken);
        if (products.Count > 0) WeakReferenceMessenger.Default.Send(new StockChangedMessage(products.ToArray()));
    }

    public Task<long> RecordPaymentAsync(VendorPaymentInput input, string operationId, CancellationToken cancellationToken = default)
    {
        ValidatePayment(input);
        return database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.VendorPayment);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(input.Date);
            EnsureVendor(transaction, input.VendorId);
            if (input.PurchaseId is not null && transaction.Connection.ExecuteScalar<long>("""
                    SELECT COUNT(*) FROM purchase WHERE id=@PurchaseId AND vendor_id=@VendorId AND status='POSTED'
                    """, input, transaction.Transaction) != 1)
                throw new DomainException(ErrorCode.Validation, "Payment purchase must be posted and belong to this vendor.");
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO vendor_payment(vendor_id,purchase_id,pay_date,amount,mode,reference,notes)
                VALUES (@VendorId,@PurchaseId,@date,@amount,@Mode,@Reference,@Notes) RETURNING id
                """, new
            {
                input.VendorId,
                input.PurchaseId,
                date = Format(input.Date),
                amount = input.Amount.Scaled,
                Mode = input.Mode.ToUpperInvariant(),
                Reference = Optional(input.Reference, 120),
                Notes = Optional(input.Notes, 500),
            }, transaction.Transaction);
            audit.Write(transaction, "vendor_payment", id, "CREATE", new { input.VendorId, amount = input.Amount.Scaled });
            OperationGuard.Complete(transaction, operationId, "vendor_payment", id);
            return id;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<VendorLedgerItem>> GetVendorLedgerAsync(long vendorId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection =>
        {
            long opening = connection.QuerySingleOrDefault<long?>("SELECT opening_balance FROM vendor WHERE id=@vendorId", new { vendorId })
                ?? throw new DomainException(ErrorCode.NotFound, "vendor", vendorId);
            var entries = connection.Query<LedgerRow>("""
                SELECT received_date AS Date,'PURCHASE' AS Kind,doc_no AS Reference,grand_total AS Debit,0 AS Credit,id AS SortId
                  FROM purchase WHERE vendor_id=@vendorId AND status='POSTED'
                UNION ALL
                SELECT pay_date AS Date,'PAYMENT' AS Kind,reference AS Reference,0 AS Debit,amount AS Credit,id AS SortId
                  FROM vendor_payment WHERE vendor_id=@vendorId
                UNION ALL
                SELECT return_date AS Date,'VENDOR_RETURN' AS Kind,doc_no AS Reference,0 AS Debit,
                       credit_total AS Credit,id AS SortId
                  FROM vendor_return WHERE vendor_id=@vendorId
                ORDER BY Date,Kind,SortId
                """, new { vendorId }).ToList();
            Money balance = Money.FromScaled(opening);
            var result = new List<VendorLedgerItem>(entries.Count + 1)
            {
                new(DateOnly.MinValue, "OPENING", null, balance, Money.Zero, balance),
            };
            foreach (LedgerRow row in entries)
            {
                Money debit = Money.FromScaled(row.Debit);
                Money credit = Money.FromScaled(row.Credit);
                balance += debit - credit;
                result.Add(new(row.Date, row.Kind, row.Reference, debit, credit, balance));
            }
            return (IReadOnlyList<VendorLedgerItem>)result;
        }, cancellationToken);

    public Task<IReadOnlyList<VendorReturnLot>> GetVendorReturnLotsAsync(long purchaseId,
        CancellationToken cancellationToken = default) => database.ReadAsync<IReadOnlyList<VendorReturnLot>>(connection =>
    {
        if (connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM purchase WHERE id=@purchaseId AND status='POSTED'", new { purchaseId }) != 1)
            throw new DomainException(ErrorCode.NotFound, "posted purchase", purchaseId);
        return LoadVendorReturnLots(connection, purchaseId)
            .Where(static row => row.Received > row.Returned && row.OnHand > 0)
            .Select(static row => row.ToModel()).ToArray();
    }, cancellationToken);

    public async Task<PostedVendorReturn> PostVendorReturnAsync(VendorReturnInput input, string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        string creditNumber = Required(input.VendorCreditNumber, 120, nameof(input.VendorCreditNumber));
        string reason = Required(input.Reason, 500, nameof(input.Reason));
        if (input.Lines.Count == 0 || input.Lines.Select(static line => line.LotId).Distinct().Count() != input.Lines.Count)
            throw new DomainException(ErrorCode.Validation, "Select distinct lots to return.");
        var changedProducts = new HashSet<long>();
        PostedVendorReturn result = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.VendorReturn);
            if (replay?.RefId is { } oldId)
            {
                var row = transaction.Connection.QuerySingle<VendorReturnPostedRow>("""
                    SELECT id AS Id,doc_no AS DocumentNumber,credit_total AS CreditTotal,cost_total AS CostTotal
                    FROM vendor_return WHERE id=@oldId
                    """, new { oldId }, transaction.Transaction);
                return new PostedVendorReturn(row.Id, row.DocumentNumber, Money.FromScaled(row.CreditTotal),
                    Money.FromScaled(row.CostTotal), true);
            }
            periods.EnsureOpen(input.Date);
            (long VendorId, string Status, string? TaxMode) purchase =
                transaction.Connection.QuerySingleOrDefault<(long VendorId, string Status, string? TaxMode)>(
                "SELECT vendor_id AS VendorId,status AS Status,tax_mode AS TaxMode FROM purchase WHERE id=@purchaseId",
                new { purchaseId = input.PurchaseId }, transaction.Transaction);
            if (purchase.VendorId == 0 || purchase.Status != "POSTED")
                throw new DomainException(ErrorCode.NotFound, "posted purchase", input.PurchaseId);
            Dictionary<long, VendorReturnLotRow> eligible = LoadVendorReturnLots(transaction.Connection,
                input.PurchaseId, transaction.Transaction).ToDictionary(static row => row.LotId);
            Money beforeTax = Money.Zero;
            Money tax = Money.Zero;
            Money cost = Money.Zero;
            foreach (VendorReturnLineInput line in input.Lines)
            {
                if (!eligible.TryGetValue(line.LotId, out VendorReturnLotRow? lot) ||
                    line.Quantity <= Quantity.Zero ||
                    (!lot.AllowFraction && !line.Quantity.IsWhole) ||
                    line.Quantity.Scaled > Math.Min(lot.OnHand, lot.Received - lot.Returned) ||
                    line.CreditBeforeTax < Money.Zero || line.TaxCredit < Money.Zero ||
                    line.CreditBeforeTax.Scaled > lot.OriginalBeforeTax - lot.CreditedBeforeTax ||
                    line.TaxCredit.Scaled > lot.OriginalTax - lot.CreditedTax ||
                    line.CreditBeforeTax + line.TaxCredit <= Money.Zero)
                    throw new DomainException(ErrorCode.Validation, "Return quantity or credit exceeds the source lot.");
                beforeTax += line.CreditBeforeTax;
                tax += line.TaxCredit;
                cost += Money.FromScaled(lot.UnitCost) * line.Quantity;
                changedProducts.Add(lot.ProductId);
            }
            string document = numbers.Next(transaction, DocType.VendorReturn, input.Date);
            long returnId = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO vendor_return(doc_no,vendor_id,purchase_id,return_date,vendor_credit_no,
                    reason,tax_mode,credit_before_tax,tax_credit,credit_total,cost_total)
                VALUES (@document,@vendorId,@purchaseId,@date,@creditNumber,@reason,@taxMode,
                    @beforeTax,@tax,@total,@cost) RETURNING id
                """, new
            {
                document,
                vendorId = purchase.VendorId,
                purchaseId = input.PurchaseId,
                date = Format(input.Date),
                creditNumber,
                reason,
                taxMode = purchase.TaxMode ?? settings.Current.TaxMode,
                beforeTax = beforeTax.Scaled,
                tax = tax.Scaled,
                total = (beforeTax + tax).Scaled,
                cost = cost.Scaled,
            }, transaction.Transaction);
            foreach (VendorReturnLineInput line in input.Lines)
            {
                VendorReturnLotRow lot = eligible[line.LotId];
                Money lineCost = Money.FromScaled(lot.UnitCost) * line.Quantity;
                transaction.Connection.Execute("""
                    INSERT INTO vendor_return_line(vendor_return_id,product_id,lot_id,qty,
                        credit_before_tax,tax_credit,cost_value)
                    VALUES (@returnId,@productId,@lotId,@qty,@beforeTax,@tax,@cost)
                    """, new DynamicParameters(new
                {
                    returnId,
                    productId = lot.ProductId,
                    lotId = line.LotId,
                    qty = line.Quantity.Scaled,
                    beforeTax = line.CreditBeforeTax.Scaled,
                    tax = line.TaxCredit.Scaled,
                    cost = lineCost.Scaled,
                }), transaction.Transaction);
                ledger.Post(transaction, new(input.Date, lot.ProductId, lot.LotId, "VENDOR_RETURN",
                    Quantity.FromScaled(-line.Quantity.Scaled), Money.FromScaled(lot.UnitCost),
                    "vendor_return", returnId, reason));
            }
            audit.Write(transaction, "vendor_return", returnId, "POST", new
            {
                purchaseId = input.PurchaseId,
                vendorId = purchase.VendorId,
                credit = (beforeTax + tax).Scaled,
                cost = cost.Scaled,
            });
            OperationGuard.Complete(transaction, operationId, "vendor_return", returnId);
            return new PostedVendorReturn(returnId, document, beforeTax + tax, cost);
        }, cancellationToken);
        if (!result.WasReplay && changedProducts.Count > 0)
            WeakReferenceMessenger.Default.Send(new StockChangedMessage(changedProducts.ToArray()));
        return result;
    }

    private static VendorReturnLotRow[] LoadVendorReturnLots(IDbConnection connection, long purchaseId,
        IDbTransaction? transaction = null) => connection.Query<VendorReturnLotRow>("""
            SELECT l.id AS LotId,l.product_id AS ProductId,p.name AS ProductName,l.lot_code AS LotCode,
                   l.qty_received AS Received,l.qty_on_hand AS OnHand,l.unit_cost AS UnitCost,
                   pl.line_total-pl.tax_amount AS OriginalBeforeTax,pl.tax_amount AS OriginalTax,
                   COALESCE((SELECT SUM(vrl.qty) FROM vendor_return_line vrl WHERE vrl.lot_id=l.id),0) AS Returned,
                   COALESCE((SELECT SUM(vrl.credit_before_tax) FROM vendor_return_line vrl WHERE vrl.lot_id=l.id),0) AS CreditedBeforeTax,
                   COALESCE((SELECT SUM(vrl.tax_credit) FROM vendor_return_line vrl WHERE vrl.lot_id=l.id),0) AS CreditedTax,
                   u.allow_fraction AS AllowFraction
            FROM purchase_line pl JOIN product_lot l ON l.purchase_line_id=pl.id
            JOIN product p ON p.id=l.product_id JOIN uom u ON u.id=p.uom_id
            WHERE pl.purchase_id=@purchaseId
            ORDER BY pl.line_no
            """, new { purchaseId }, transaction).ToArray();

    public Task<IReadOnlyList<VendorPurchaseChoice>> GetVendorPurchaseChoicesAsync(long vendorId,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
            (IReadOnlyList<VendorPurchaseChoice>)connection.Query<VendorPurchaseChoiceRow>("""
                SELECT id AS Id,doc_no AS DocumentNumber,vendor_invoice_no AS InvoiceNumber
                FROM purchase WHERE vendor_id=@vendorId AND status='POSTED'
                ORDER BY received_date DESC,id DESC LIMIT 500
                """, new { vendorId }).Select(static row => new VendorPurchaseChoice(row.Id,
                string.IsNullOrWhiteSpace(row.InvoiceNumber) ? row.DocumentNumber : $"{row.DocumentNumber} · {row.InvoiceNumber}"))
                .ToList(), cancellationToken);

    public Task<IReadOnlyList<VendorLotItem>> GetVendorLotsAsync(long vendorId,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
            (IReadOnlyList<VendorLotItem>)connection.Query<VendorLotRow>("""
                SELECT p.name AS ProductName,l.lot_code AS LotCode,l.batch_no AS BatchNumber,
                       l.received_date AS ReceivedDate,l.expiry_date AS ExpiryDate,l.qty_on_hand AS OnHand
                FROM product_lot l JOIN product p ON p.id=l.product_id
                WHERE l.vendor_id=@vendorId ORDER BY l.received_date DESC,l.id DESC
                """, new { vendorId }).Select(static row => new VendorLotItem(row.ProductName, row.LotCode,
                row.BatchNumber, row.ReceivedDate, row.ExpiryDate, Quantity.FromScaled(row.OnHand))).ToList(), cancellationToken);

    private static void InsertDraftLines(DbTx transaction, long purchaseId, IReadOnlyList<PurchaseLineInput> lines)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            PurchaseLineInput line = lines[index];
            ProductRule rule = transaction.Connection.QuerySingleOrDefault<ProductRule>("""
                SELECT p.track_expiry AS TrackExpiry,u.allow_fraction AS AllowFraction
                FROM product p JOIN uom u ON u.id=p.uom_id WHERE p.id=@ProductId AND p.is_active=1
                """, line, transaction.Transaction) ?? throw new DomainException(ErrorCode.NotFound, "product", line.ProductId);
            ValidateLine(line, rule.TrackExpiry, rule.AllowFraction, default);
            Money taxable = line.Rate * line.Quantity - line.Discount;
            Money tax = Tax.FromExclusive(taxable, line.TaxRate).Tax;
            transaction.Connection.Execute("""
                INSERT INTO purchase_line(purchase_id,line_no,product_id,batch_no,mfg_date,expiry_date,qty,free_qty,
                                          unit_cost,discount,tax_rate_pct,tax_amount,line_total,landed_unit_cost,mrp,selling_price)
                VALUES (@purchaseId,@lineNo,@ProductId,@BatchNumber,@ManufacturingDate,@ExpiryDate,@Quantity,@FreeQuantity,
                        @Rate,@Discount,@TaxRate,@tax,@lineTotal,0,@Mrp,@SellingPrice)
                """, new
            {
                purchaseId,
                lineNo = index + 1,
                line.ProductId,
                BatchNumber = Optional(line.BatchNumber, 120),
                line.ManufacturingDate,
                line.ExpiryDate,
                Quantity = line.Quantity.Scaled,
                FreeQuantity = line.FreeQuantity.Scaled,
                Rate = line.Rate.Scaled,
                Discount = line.Discount.Scaled,
                TaxRate = line.TaxRate.Scaled,
                tax = tax.Scaled,
                lineTotal = (taxable + tax).Scaled,
                Mrp = line.Mrp?.Scaled,
                SellingPrice = line.SellingPrice.Scaled,
            }, transaction.Transaction);
        }
    }

    private static PostedPurchase LoadPosted(DbTx transaction, long purchaseId)
    {
        PostedRow? row = transaction.Connection.QuerySingleOrDefault<PostedRow>("""
            SELECT id AS Id,doc_no AS DocumentNumber,subtotal AS Subtotal,discount_total AS Discount,tax_total AS Tax,
                   other_charges AS OtherCharges,round_off AS RoundOff,grand_total AS GrandTotal,
                   vendor_bill_total AS VendorBillTotal FROM purchase WHERE id=@purchaseId AND status='POSTED'
            """, new { purchaseId }, transaction.Transaction);
        if (row is null) throw new DomainException(ErrorCode.NotFound, "posted purchase", purchaseId);
        IReadOnlyList<long> lotIds = transaction.Connection.Query<long>("""
            SELECT l.id FROM product_lot l JOIN purchase_line pl ON pl.id=l.purchase_line_id
            WHERE pl.purchase_id=@purchaseId ORDER BY pl.line_no
            """, new { purchaseId }, transaction.Transaction).ToList();
        return new(row.Id, row.DocumentNumber,
            new(Money.FromScaled(row.Subtotal), Money.FromScaled(row.Discount), Money.FromScaled(row.Tax),
                Money.FromScaled(row.OtherCharges), Money.FromScaled(row.RoundOff), Money.FromScaled(row.GrandTotal),
                row.VendorBillTotal is null ? null : Money.FromScaled(row.VendorBillTotal.Value)), lotIds);
    }

    private static void ValidateDraft(PurchaseDraftInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Lines);
        if (input.VendorId <= 0 || input.ReceivedDate == default || input.OtherCharges < Money.Zero ||
            input.VendorBillTotal < Money.Zero || input.Lines.Count == 0)
            throw new DomainException(ErrorCode.Validation, "Invalid purchase draft.");
        if (input.VendorInvoiceDate is not null && input.VendorInvoiceDate > input.ReceivedDate)
            throw new DomainException(ErrorCode.InvalidDate);
    }

    private static void ValidateLine(PurchaseLineInput line, bool trackExpiry, bool allowFraction, DateOnly receivedDate)
    {
        if (line.ProductId <= 0 || line.Quantity <= Quantity.Zero || line.FreeQuantity < Quantity.Zero || line.Rate < Money.Zero ||
            line.Discount < Money.Zero || line.Discount > line.Rate * line.Quantity || line.TaxRate < Percent.Zero ||
            line.TaxRate.Value > 100m || line.SellingPrice < Money.Zero)
            throw new DomainException(ErrorCode.Validation, "Invalid purchase line.");
        if (!allowFraction && (!line.Quantity.IsWhole || !line.FreeQuantity.IsWhole))
            throw new DomainException(ErrorCode.FractionalQtyNotAllowed);
        if (trackExpiry && line.ExpiryDate is null) throw new DomainException(ErrorCode.Validation, "Expiry is required.");
        if (line.Mrp is not null && line.SellingPrice > line.Mrp.Value) throw new DomainException(ErrorCode.PriceAboveMrp);
        if (line.ManufacturingDate is not null && line.ExpiryDate is not null && line.ManufacturingDate > line.ExpiryDate)
            throw new DomainException(ErrorCode.InvalidDate);
        if (receivedDate != default && line.ExpiryDate is not null && line.ExpiryDate < receivedDate)
            throw new DomainException(ErrorCode.InvalidDate);
    }

    private static ValidVendor ValidateVendor(VendorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string name = Required(input.Name, 160, nameof(input.Name));
        string? gstin = Upper(input.Gstin, 15);
        string? pan = Upper(input.Pan, 10);
        string? pincode = Optional(input.Pincode, 6);
        if (gstin is not null && !IndianIds.IsGstin(gstin)) throw new DomainException(ErrorCode.Validation, "GSTIN");
        if (pan is null && gstin is not null) pan = gstin.Substring(2, 10);
        if (pan is not null && !IndianIds.IsPan(pan)) throw new DomainException(ErrorCode.Validation, "PAN");
        if (pincode is not null && !IndianIds.IsPincode(pincode)) throw new DomainException(ErrorCode.Validation, "Pincode");
        if (input.PaymentTermsDays < 0 || input.PaymentTermsDays > 3650 || input.OpeningBalance < Money.Zero)
            throw new DomainException(ErrorCode.Validation, "Vendor terms or opening balance.");
        return new(name, Optional(input.ContactPerson, 120), Optional(input.Phone, 40), Optional(input.AlternatePhone, 40),
            Optional(input.Email, 160), Optional(input.Address, 500), Optional(input.City, 100), Optional(input.State, 100),
            pincode, gstin, pan, Optional(input.UpiId, 160), Optional(input.BankDetails, 500), input.PaymentTermsDays,
            input.OpeningBalance.Scaled, Optional(input.Notes, 1000));
    }

    private static void ValidatePayment(VendorPaymentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.VendorId <= 0 || input.Date == default || input.Amount <= Money.Zero ||
            !PaymentModes.Contains(input.Mode.ToUpperInvariant()))
            throw new DomainException(ErrorCode.Validation, "Invalid vendor payment.");
    }

    private static void EnsureVendor(DbTx transaction, long vendorId)
    {
        if (transaction.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM vendor WHERE id=@vendorId AND is_active=1",
                new { vendorId }, transaction.Transaction) != 1)
            throw new DomainException(ErrorCode.NotFound, "vendor", vendorId);
    }

    private static DomainException PurchaseMissingOrFinalized(DbTx transaction, long id)
    {
        string? status = transaction.Connection.QuerySingleOrDefault<string>("SELECT status FROM purchase WHERE id=@id", new { id }, transaction.Transaction);
        return status is null ? new DomainException(ErrorCode.NotFound, "purchase", id) : new DomainException(ErrorCode.PurchaseFinalized, id);
    }

    private static object HeaderParameters(PurchaseDraftInput input) => new
    {
        input.VendorId,
        invoice = Optional(input.VendorInvoiceNumber, 120),
        invoiceDate = input.VendorInvoiceDate,
        receivedDate = input.ReceivedDate,
        bill = input.VendorBillTotal?.Scaled,
        charges = input.OtherCharges.Scaled,
        notes = Optional(input.Notes, 1000),
    };

    private static Dictionary<string, object?> Merge(object source, long id)
    {
        var values = source.GetType().GetProperties().ToDictionary(static property => property.Name,
            property => property.GetValue(source), StringComparer.Ordinal);
        values["id"] = id;
        return values;
    }

    private static string Required(string? value, int maximum, string parameter)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.Length == 0 || normalized.Length > maximum) throw new DomainException(ErrorCode.Validation, parameter);
        return normalized;
    }
    private static string? Optional(string? value, int maximum) { string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); if (normalized?.Length > maximum) throw new DomainException(ErrorCode.Validation, nameof(value)); return normalized; }
    private static string? Upper(string? value, int maximum) => Optional(value, maximum)?.ToUpperInvariant();
    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record ValidVendor(string Name, string? ContactPerson, string? Phone, string? AlternatePhone, string? Email, string? Address, string? City, string? State, string? Pincode, string? Gstin, string? Pan, string? UpiId, string? BankDetails, int PaymentTermsDays, long OpeningBalance, string? Notes);
    private sealed class VendorRow { public long Id { get; init; } public string Name { get; init; } = ""; public string? Phone { get; init; } public string? Gstin { get; init; } public long BalanceDue { get; init; } public bool IsActive { get; init; } public VendorItem ToModel() => new(Id, Name, Phone, Gstin, Money.FromScaled(BalanceDue), IsActive); }
    private sealed class VendorDetailsRow { public string Name { get; init; } = ""; public string? ContactPerson { get; init; } public string? Phone { get; init; } public string? AlternatePhone { get; init; } public string? Email { get; init; } public string? Address { get; init; } public string? City { get; init; } public string? State { get; init; } public string? Pincode { get; init; } public string? Gstin { get; init; } public string? Pan { get; init; } public string? UpiId { get; init; } public string? BankDetails { get; init; } public int PaymentTermsDays { get; init; } public long OpeningBalance { get; init; } public string? Notes { get; init; } }
    private sealed class ProductRule { public bool TrackExpiry { get; init; } public bool AllowFraction { get; init; } }
    private sealed class PurchaseHeader { public long Id { get; init; } public long VendorId { get; init; } public DateOnly ReceivedDate { get; init; } public string Status { get; init; } = ""; public long OtherCharges { get; init; } public long? VendorBillTotal { get; init; } }
    private sealed class DraftHeader { public long Id { get; init; } public long VendorId { get; init; } public string? VendorInvoiceNumber { get; init; } public DateOnly? VendorInvoiceDate { get; init; } public DateOnly ReceivedDate { get; init; } public long? VendorBillTotal { get; init; } public long OtherCharges { get; init; } public string? Notes { get; init; } }
    private sealed class LineDefaultsRow { public bool TrackExpiry { get; init; } public bool PriceIncludesTax { get; init; } public int? ShelfLifeDays { get; init; } public int ExpiryAlertDays { get; init; } public long TaxRate { get; init; } public long? Mrp { get; init; } public long? SellingPrice { get; init; } public long? LastRate { get; init; } }
    private sealed class PurchaseLineRow { public long Id { get; init; } public long ProductId { get; init; } public string? BatchNumber { get; init; } public DateOnly? ManufacturingDate { get; init; } public DateOnly? ExpiryDate { get; init; } public long Quantity { get; init; } public long FreeQuantity { get; init; } public long Rate { get; init; } public long Discount { get; init; } public long TaxRate { get; init; } public long? Mrp { get; init; } public long SellingPrice { get; init; } public bool TrackExpiry { get; init; } public bool AllowFraction { get; init; } public PurchaseLineInput ToInput() => new(ProductId, BatchNumber, ManufacturingDate, ExpiryDate, GroceryErp.Core.Numerics.Quantity.FromScaled(Quantity), GroceryErp.Core.Numerics.Quantity.FromScaled(FreeQuantity), Money.FromScaled(Rate), Money.FromScaled(Discount), Percent.FromScaled(TaxRate), Mrp is null ? null : Money.FromScaled(Mrp.Value), Money.FromScaled(SellingPrice)); }
    private sealed class CancelLotRow { public long LotId { get; init; } public long ProductId { get; init; } public long Received { get; init; } public long OnHand { get; init; } public long UnitCost { get; init; } public long PurchaseLineId { get; init; } public long MovementCount { get; init; } }
    private sealed class PostedRow { public long Id { get; init; } public string DocumentNumber { get; init; } = ""; public long Subtotal { get; init; } public long Discount { get; init; } public long Tax { get; init; } public long OtherCharges { get; init; } public long RoundOff { get; init; } public long GrandTotal { get; init; } public long? VendorBillTotal { get; init; } }
    private sealed class LedgerRow { public DateOnly Date { get; init; } public string Kind { get; init; } = ""; public string? Reference { get; init; } public long Debit { get; init; } public long Credit { get; init; } public long SortId { get; init; } }
    private sealed class VendorPurchaseChoiceRow { public long Id { get; init; } public string DocumentNumber { get; init; } = ""; public string? InvoiceNumber { get; init; } }
    private sealed class VendorLotRow { public string ProductName { get; init; } = ""; public string LotCode { get; init; } = ""; public string? BatchNumber { get; init; } public DateOnly ReceivedDate { get; init; } public DateOnly? ExpiryDate { get; init; } public long OnHand { get; init; } }
    private sealed class VendorReturnLotRow
    {
        public long LotId { get; init; }
        public long ProductId { get; init; }
        public string ProductName { get; init; } = "";
        public string LotCode { get; init; } = "";
        public long Received { get; init; }
        public long OnHand { get; init; }
        public long Returned { get; init; }
        public long OriginalBeforeTax { get; init; }
        public long OriginalTax { get; init; }
        public long CreditedBeforeTax { get; init; }
        public long CreditedTax { get; init; }
        public long UnitCost { get; init; }
        public bool AllowFraction { get; init; }
        public VendorReturnLot ToModel() => new(LotId, ProductId, ProductName, LotCode,
            Quantity.FromScaled(Received), Quantity.FromScaled(OnHand),
            Quantity.FromScaled(Math.Min(OnHand, Received - Returned)),
            Money.FromScaled(OriginalBeforeTax), Money.FromScaled(OriginalTax),
            Money.FromScaled(CreditedBeforeTax), Money.FromScaled(CreditedTax),
            Money.FromScaled(UnitCost), AllowFraction, Quantity.FromScaled(Returned));
    }
    private sealed class VendorReturnPostedRow { public long Id { get; init; } public string DocumentNumber { get; init; } = ""; public long CreditTotal { get; init; } public long CostTotal { get; init; } }
    private sealed class PurchaseListRow { public long Id { get; init; } public string? DocumentNumber { get; init; } public string VendorName { get; init; } = ""; public string? VendorInvoiceNumber { get; init; } public DateOnly ReceivedDate { get; init; } public string Status { get; init; } = ""; public long GrandTotal { get; init; } public long? VendorBillTotal { get; init; } public PurchaseListItem ToModel() => new(Id, DocumentNumber, VendorName, VendorInvoiceNumber, ReceivedDate, Status, Money.FromScaled(GrandTotal), VendorBillTotal is null ? null : Money.FromScaled(VendorBillTotal.Value)); }
}
