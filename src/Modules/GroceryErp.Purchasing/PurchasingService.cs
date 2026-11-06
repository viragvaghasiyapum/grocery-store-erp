using System.Data;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Validation;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Purchasing;

public sealed partial class PurchasingService(
    DbExecutor database,
    ILotQuantities stock,
    DocNumberService numbers,
    PeriodGuard periods,
    ISettings settings,
    IAuditLog audit)
{
    private static readonly HashSet<string> PaymentModes = ["CASH", "UPI", "BANK", "CHEQUE", "OTHER"];
    public const int MaxPurchaseLines = 200;
    public const int MaxPaymentTermsDays = 3650;

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

    public Task<PurchaseInvoice> GetInvoiceAsync(long purchaseId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection =>
        {
            PurchaseInvoiceHeaderRow? header = connection.QuerySingleOrDefault<PurchaseInvoiceHeaderRow>("""
                SELECT p.id AS Id,p.doc_no AS DocumentNumber,
                       COALESCE(p.vendor_name_snapshot,v.name,'Legacy unknown') AS VendorName,
                       p.vendor_invoice_no AS VendorInvoiceNumber,
                       p.vendor_invoice_date AS VendorInvoiceDate,p.received_date AS ReceivedDate,
                       p.status AS Status,p.tax_mode AS TaxMode,p.notes AS Notes,p.subtotal AS Subtotal,
                       p.discount_total AS Discount,p.tax_total AS Tax,
                       p.other_charges AS OtherCharges,p.round_off AS RoundOff,
                       p.grand_total AS GrandTotal,p.vendor_bill_total AS VendorBillTotal
                FROM purchase p LEFT JOIN vendor v ON v.id=p.vendor_id WHERE p.id=@purchaseId
                """, new { purchaseId });
            if (header is null) throw new DomainException(ErrorCode.NotFound, "purchase", purchaseId);
            IReadOnlyList<PurchaseInvoiceLine> lines = connection.Query<PurchaseInvoiceLineRow>("""
                SELECT pl.line_no AS LineNumber,pl.line_kind AS LineKind,
                       pl.expense_category_name_snapshot AS ExpenseCategoryName,
                       COALESCE(pl.product_sku_snapshot,'Legacy unknown') AS Sku,
                       COALESCE(pl.product_name_snapshot,'Legacy unknown') AS ProductName,
                       pl.batch_no AS BatchNumber,pl.mfg_date AS ManufacturingDate,
                       pl.expiry_date AS ExpiryDate,pl.qty AS Quantity,
                       pl.free_qty AS FreeQuantity,pl.unit_cost AS Rate,
                       pl.discount AS Discount,pl.tax_rate_pct AS TaxRate,
                       pl.tax_amount AS TaxAmount,pl.line_total AS LineTotal,
                       pl.landed_unit_cost AS LandedUnitCost,pl.mrp AS Mrp,
                       pl.selling_price AS SellingPrice,pl.tax_label_snapshot AS TaxLabel,
                       pl.landed_unit_tax AS UnitTax,pl.landed_unit_cost_incl_tax AS UnitCostInclTax,
                       pl.snapshot_provenance AS SnapshotProvenance,
                       pl.legacy_unknown_fields AS LegacyUnknownFields
                FROM purchase_line pl
                WHERE pl.purchase_id=@purchaseId ORDER BY pl.line_no
                """, new { purchaseId }).Select(static row => row.ToModel()).ToList();
            return new PurchaseInvoice(header.Id, header.DocumentNumber, header.VendorName,
                header.VendorInvoiceNumber, header.VendorInvoiceDate, header.ReceivedDate,
                header.Status, header.Notes,
                new PurchaseTotals(Money.FromScaled(header.Subtotal), Money.FromScaled(header.Discount),
                    Money.FromScaled(header.Tax), Money.FromScaled(header.OtherCharges),
                    Money.FromScaled(header.RoundOff), Money.FromScaled(header.GrandTotal),
                    header.VendorBillTotal is null ? null : Money.FromScaled(header.VendorBillTotal.Value)),
                lines)
            { TaxMode = header.TaxMode };
        }, cancellationToken);

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

    public Task UpdateVendorAsync(long id, VendorInput input, bool? isActive = null, CancellationToken cancellationToken = default)
    {
        ValidVendor vendor = ValidateVendor(input);
        if (id <= 0) throw new DomainException(ErrorCode.Validation, nameof(id));
        return database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute("""
                UPDATE vendor SET name=@Name,contact_person=@ContactPerson,phone=@Phone,alt_phone=@AlternatePhone,
                    email=@Email,address=@Address,city=@City,state=@State,pincode=@Pincode,gstin=@Gstin,pan=@Pan,
                    upi_id=@UpiId,bank_details=@BankDetails,payment_terms_days=@PaymentTermsDays,
                    opening_balance=@OpeningBalance,notes=@Notes,is_active=COALESCE(@isActive,is_active) WHERE id=@id
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
                isActive = isActive is null ? (int?)null : isActive.Value ? 1 : 0,
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
            // A rate this draft already uses stays allowed after it is made inactive; a new
            // choice must be an active rate.
            HashSet<long> chosenBefore = [];
            if (purchaseId is null)
            {
                id = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO purchase(vendor_id,vendor_invoice_no,vendor_invoice_date,received_date,vendor_bill_total,
                                         other_charges,notes,rates_include_tax)
                    VALUES (@VendorId,@invoice,@invoiceDate,@receivedDate,@bill,@charges,@notes,@ratesIncludeTax) RETURNING id
                    """, HeaderParameters(input), transaction.Transaction);
            }
            else
            {
                id = purchaseId.Value;
                int changed = transaction.Connection.Execute("""
                    UPDATE purchase SET vendor_id=@VendorId,vendor_invoice_no=@invoice,vendor_invoice_date=@invoiceDate,
                        received_date=@receivedDate,vendor_bill_total=@bill,other_charges=@charges,notes=@notes,
                        rates_include_tax=@ratesIncludeTax
                    WHERE id=@id AND status='DRAFT'
                    """, Merge(HeaderParameters(input), id), transaction.Transaction);
                if (changed == 0) throw PurchaseMissingOrFinalized(transaction, id);
                chosenBefore = transaction.Connection.Query<long>("""
                    SELECT DISTINCT tax_rate_id FROM purchase_line WHERE purchase_id=@id AND tax_rate_id IS NOT NULL
                    """, new { id }, transaction.Transaction).ToHashSet();
                transaction.Connection.Execute("DELETE FROM purchase_line WHERE purchase_id=@id", new { id }, transaction.Transaction);
            }

            InsertDraftLines(transaction, id, input, chosenBefore);
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
                       vendor_bill_total AS VendorBillTotal,other_charges AS OtherCharges,notes AS Notes,
                       rates_include_tax AS RatesIncludeTax
                FROM purchase WHERE id=@purchaseId AND status='DRAFT'
                """, new { purchaseId });
            if (header is null) throw new DomainException(ErrorCode.NotFound, "purchase draft", purchaseId);
            // A draft typed including tax shows the rates and discounts exactly as they were typed. A
            // line saved before it had a tax rate takes the only active rate with its percent, or
            // none, and then the operator chooses one before saving.
            IReadOnlyList<PurchaseLineInput> lines = connection.Query<PurchaseLineRow>("""
                SELECT pl.id AS Id,pl.product_id AS ProductId,pl.line_kind AS LineKind,
                       pl.expense_category_id AS ExpenseCategoryId,pl.batch_no AS BatchNumber,
                       pl.mfg_date AS ManufacturingDate,pl.expiry_date AS ExpiryDate,pl.qty AS Quantity,
                       pl.free_qty AS FreeQuantity,
                       COALESCE(pl.entered_rate,pl.unit_cost) AS Rate,COALESCE(pl.entered_discount,pl.discount) AS Discount,
                       pl.tax_rate_pct AS TaxRate,pl.mrp AS Mrp,pl.selling_price AS SellingPrice,
                       COALESCE(pl.tax_rate_id,(SELECT CASE WHEN COUNT(*)=1 THEN MIN(t.id) END FROM tax_rate t
                           WHERE t.rate_pct=pl.tax_rate_pct AND t.is_active=1)) AS TaxRateId
                FROM purchase_line pl WHERE pl.purchase_id=@purchaseId ORDER BY pl.line_no
                """, new { purchaseId }).Select(static row => row.ToInput()).ToList();
            return new PurchaseDraft(header.Id, new PurchaseDraftInput(header.VendorId, header.VendorInvoiceNumber,
                header.VendorInvoiceDate, header.ReceivedDate,
                header.VendorBillTotal is null ? null : Money.FromScaled(header.VendorBillTotal.Value),
                Money.FromScaled(header.OtherCharges), header.Notes, lines)
            { RatesIncludeTax = header.RatesIncludeTax });
        }, cancellationToken);

    /// <summary>
    /// Gets whether the vendor's most recent purchase (draft, posted or void) was typed with rates
    /// including GST, so the next bill from that vendor starts the same way. False for a new vendor.
    /// </summary>
    public Task<bool> GetLastRatesIncludeTaxAsync(long vendorId, CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => connection.ExecuteScalar<bool?>("""
            SELECT rates_include_tax FROM purchase WHERE vendor_id=@vendorId ORDER BY id DESC LIMIT 1
            """, new { vendorId }) ?? false, cancellationToken);

    public Task<PurchaseLineDefaults> GetLineDefaultsAsync(long vendorId, long productId, DateOnly receivedDate,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        // The short-expiry warning uses the dashboard's alert window (ExpiryAlert), parent category included.
        LineDefaultsRow? row = connection.QuerySingleOrDefault<LineDefaultsRow>(LineDefaultsSql,
            new { vendorId, productId });
        if (row is null) throw new DomainException(ErrorCode.NotFound, "product", productId);
        return new PurchaseLineDefaults(row.LastRate is null ? null : Money.FromScaled(row.LastRate.Value),
            row.TaxRateId, Percent.FromScaled(row.TaxRate), row.Mrp is null ? null : Money.FromScaled(row.Mrp.Value),
            row.SellingPrice is null ? null : Money.FromScaled(row.SellingPrice.Value),
            row.TrackExpiry && row.ShelfLifeDays is int shelfLife ? receivedDate.AddDays(shelfLife) : null,
            row.ExpiryAlertDays);
    }, cancellationToken);

    private static readonly string LineDefaultsSql = $"""
            SELECT p.track_expiry AS TrackExpiry,
                   p.default_shelf_life_days AS ShelfLifeDays,
                   {ExpiryAlert.DaysSql("p")} AS ExpiryAlertDays,
                   t.id AS TaxRateId,COALESCE(t.rate_pct,0) AS TaxRate,
                   COALESCE((SELECT l.mrp FROM product_lot l WHERE l.product_id=p.id ORDER BY l.id DESC LIMIT 1),
                            p.default_mrp) AS Mrp,
                   COALESCE((SELECT l.selling_price FROM product_lot l WHERE l.product_id=p.id ORDER BY l.id DESC LIMIT 1),
                            p.default_selling_price) AS SellingPrice,
                   (SELECT pl.unit_cost FROM purchase_line pl JOIN purchase po ON po.id=pl.purchase_id
                    WHERE pl.product_id=p.id AND po.vendor_id=@vendorId AND po.status='POSTED'
                    ORDER BY po.received_date DESC,pl.id DESC LIMIT 1) AS LastRate
            FROM product p
            LEFT JOIN tax_rate t ON t.id=p.tax_rate_id
            WHERE p.id=@productId AND p.is_active=1
            """;

    public async Task<PostedPurchase> PostAsync(long purchaseId, string operationId, CancellationToken cancellationToken = default)
    {
        return await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.PurchasePost);
            if (replay is not null)
            {
                long existingId = replay.RefId ?? purchaseId;
                return LoadPosted(transaction, existingId) with { WasReplay = true };
            }

            PurchaseHeader? header = transaction.Connection.QuerySingleOrDefault<PurchaseHeader>("""
                SELECT id AS Id,vendor_id AS VendorId,received_date AS ReceivedDate,status AS Status,
                       other_charges AS OtherCharges,vendor_bill_total AS VendorBillTotal,
                       rates_include_tax AS RatesIncludeTax
                FROM purchase WHERE id=@purchaseId
                """, new { purchaseId }, transaction.Transaction);
            if (header is null) throw new DomainException(ErrorCode.NotFound, "purchase", purchaseId);
            if (header.Status != "DRAFT") throw new DomainException(ErrorCode.PurchaseFinalized, purchaseId);
            periods.EnsureOpen(transaction, header.ReceivedDate);
            List<PurchaseLineRow> rows = transaction.Connection.Query<PurchaseLineRow>("""
                SELECT pl.id AS Id,pl.product_id AS ProductId,pl.line_kind AS LineKind,
                       pl.expense_category_id AS ExpenseCategoryId,p.product_class AS ProductClass,
                       pl.batch_no AS BatchNumber,pl.mfg_date AS ManufacturingDate,
                       pl.expiry_date AS ExpiryDate,pl.qty AS Quantity,pl.free_qty AS FreeQuantity,
                       pl.unit_cost AS Rate,pl.discount AS Discount,pl.tax_rate_pct AS TaxRate,
                       pl.tax_rate_id AS TaxRateId,pl.entered_rate AS EnteredRate,
                       pl.entered_discount AS EnteredDiscount,
                       pl.mrp AS Mrp,pl.selling_price AS SellingPrice,
                       p.track_expiry AS TrackExpiry,u.allow_fraction AS AllowFraction
                FROM purchase_line pl JOIN product p ON p.id=pl.product_id JOIN uom u ON u.id=p.uom_id
                WHERE pl.purchase_id=@purchaseId ORDER BY pl.line_no
                """, new { purchaseId }, transaction.Transaction).ToList();
            if (rows.Count == 0) throw new DomainException(ErrorCode.EmptyBill);

            // Each rate's percent and name are read again, so a draft is posted at its rates as they
            // are now; figures typed including tax are converted again at that percent.
            var resolved = new List<(PurchaseLineInput Line, TaxRateSnapshot Rate)>(rows.Count);
            foreach (PurchaseLineRow row in rows)
            {
                TaxRateSnapshot rate = PostingRate(transaction, row.TaxRateId, row.TaxRate);
                PurchaseLineInput line = row.ToInput() with { TaxRateId = rate.Id };
                if (header.RatesIncludeTax && row.EnteredRate is { } enteredRate)
                    line = (line with
                    {
                        Rate = Money.FromScaled(enteredRate),
                        Discount = Money.FromScaled(row.EnteredDiscount ?? 0),
                    }).ExcludingTax(rate.Rate);
                ValidateLine(line, row.TrackExpiry, row.AllowFraction, header.ReceivedDate);
                if ((row.LineKind == "INTERNAL") != (row.ProductClass == "INTERNAL") && row.LineKind != "EXPENSE")
                    throw new DomainException(ErrorCode.PurchaseLineTypeMismatch);
                resolved.Add((line, rate));
            }
            Money otherCharges = Money.FromScaled(header.OtherCharges);
            PricedPurchaseLine[] priced = PriceLines(resolved, otherCharges);
            Money subtotal = priced.Aggregate(Money.Zero, static (sum, line) =>
                sum + line.Line.Rate * line.Line.Quantity);
            Money discounts = priced.Aggregate(Money.Zero, static (sum, line) => sum + line.Line.Discount);
            Money taxTotal = priced.Aggregate(Money.Zero, static (sum, line) => sum + line.Tax);
            Money beforeRound = subtotal - discounts + taxTotal + otherCharges;
            Money roundOff = MoneyMath.RoundOff(beforeRound, Money.FromScaled(settings.Current.BillRoundTo));
            Money grandTotal = beforeRound + roundOff;
            string docNo = numbers.Next(transaction, DocType.Purchase, header.ReceivedDate);
            var lotIds = new List<long>(rows.Count);

            for (int index = 0; index < rows.Count; index++)
            {
                PurchaseLineRow row = rows[index];
                PricedPurchaseLine line = priced[index];
                transaction.Connection.Execute("""
                    UPDATE purchase_line SET unit_cost=@rate,discount=@discount,tax_rate_id=@taxRateId,
                        tax_rate_pct=@taxRatePct,tax_label_snapshot=@taxLabel,
                        tax_amount=@tax,line_total=@lineTotal,landed_unit_cost=@landed,
                        landed_unit_tax=@unitTax,landed_unit_cost_incl_tax=@unitCostInclTax,
                        product_name_snapshot=p.name,product_sku_snapshot=p.sku,
                        product_name_local_snapshot=p.name_local,uom_snapshot=u.code,
                        product_class_snapshot=p.product_class,
                        tax_mode_snapshot=@taxMode,snapshot_provenance='AT_POST',
                        expense_category_name_snapshot=(SELECT name FROM expense_category
                            WHERE id=purchase_line.expense_category_id),
                        legacy_unknown_fields=NULL
                    FROM product p JOIN uom u ON u.id=p.uom_id
                    WHERE purchase_line.id=@id AND p.id=@productId
                    """, new
                {
                    id = row.Id,
                    productId = row.ProductId,
                    rate = line.Line.Rate.Scaled,
                    discount = line.Line.Discount.Scaled,
                    taxRateId = line.Rate.Id,
                    taxRatePct = line.Rate.Rate.Scaled,
                    taxLabel = line.Rate.Label,
                    tax = line.Tax.Scaled,
                    lineTotal = (line.Taxable + line.Tax).Scaled,
                    landed = line.Unit.Booked.Scaled,
                    unitTax = line.Unit.Tax.Scaled,
                    unitCostInclTax = line.Unit.InclTax.Scaled,
                    taxMode = settings.Current.TaxMode,
                }, transaction.Transaction);
                if (row.LineKind == "EXPENSE")
                {
                    // The line's exact amount: the rounded piece price times the pieces can lose paise.
                    long amount = line.Booked.Scaled;
                    if (amount <= 0) throw new DomainException(ErrorCode.ExpenseLineAmountRequired);
                    int expenseInserted = transaction.Connection.Execute("""
                        INSERT INTO expense(expense_date,category_id,amount,mode,paid_to,reference,notes,purchase_line_id)
                        SELECT @date,@categoryId,@amount,'OTHER',
                               (SELECT name FROM vendor WHERE id=@vendorId),@docNo,
                               'Purchase expense line',@lineId
                        WHERE EXISTS(SELECT 1 FROM expense_category WHERE id=@categoryId AND is_active=1)
                        """, new
                    {
                        date = Format(header.ReceivedDate),
                        categoryId = row.ExpenseCategoryId,
                        amount,
                        vendorId = header.VendorId,
                        docNo,
                        lineId = row.Id
                    }, transaction.Transaction);
                    if (expenseInserted != 1)
                        throw new DomainException(ErrorCode.ExpenseCategoryInactive);
                    continue;
                }
                string lotCode = numbers.Next(transaction, DocType.Lot, header.ReceivedDate);
                Quantity received = Quantity.FromScaled(row.Quantity) + Quantity.FromScaled(row.FreeQuantity);
                // The lot keeps the line's tax label and per-piece prices.
                long lotId = transaction.Connection.ExecuteScalar<long>("""
                    INSERT INTO product_lot(product_id,lot_code,batch_no,purchase_line_id,vendor_id,received_date,mfg_date,
                                            expiry_date,unit_cost,mrp,selling_price,qty_received,
                                            tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,
                                            unit_tax,unit_cost_incl_tax)
                    VALUES (@ProductId,@lotCode,@BatchNumber,@lineId,@vendorId,@receivedDate,@ManufacturingDate,
                            @ExpiryDate,@cost,@Mrp,@SellingPrice,@received,
                            @taxRateId,@taxLabel,@taxRatePct,@unitTax,@unitCostInclTax) RETURNING id
                    """, new LotTax(line.Rate, line.Unit).AddTo(new DynamicParameters(new
                {
                    row.ProductId,
                    lotCode,
                    row.BatchNumber,
                    lineId = row.Id,
                    vendorId = header.VendorId,
                    receivedDate = Format(header.ReceivedDate),
                    row.ManufacturingDate,
                    row.ExpiryDate,
                    cost = line.Unit.Booked.Scaled,
                    row.Mrp,
                    row.SellingPrice,
                    received = received.Scaled,
                })), transaction.Transaction);
                transaction.Connection.Execute("""
                    UPDATE purchase_line SET lot_code_snapshot=@lotCode,lot_id_snapshot=@lotId WHERE id=@id
                    """, new { lotCode, lotId, id = row.Id }, transaction.Transaction);
                stock.Change(transaction, new(header.ReceivedDate, row.ProductId, lotId, LotChangeKind.Purchase, received));
                lotIds.Add(lotId);
            }
            transaction.Connection.Execute("""
                UPDATE purchase SET status='POSTED',doc_no=@docNo,
                    vendor_name_snapshot=(SELECT name FROM vendor WHERE id=vendor_id),
                    subtotal=@subtotal,discount_total=@discounts,
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
    }

    public async Task CancelAsync(long purchaseId, DateOnly cancellationDate, string operationId, CancellationToken cancellationToken = default)
    {
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
            periods.EnsureOpen(transaction, cancellationDate);
            // A void treats the purchase as a mistake and takes it out of its own date's reports,
            // so that date must still be open; a locked year's purchase takes a vendor return.
            periods.EnsureOpen(transaction, header.ReceivedDate);
            List<CancelLotRow> lots = VoidLots(transaction.Connection, purchaseId, transaction.Transaction);
            if (!IsUntouched(transaction.Connection, purchaseId, lots, transaction.Transaction))
                throw new DomainException(ErrorCode.CancelNotAllowed, purchaseId);
            ApplyVoid(transaction, purchaseId, cancellationDate, lots);
            OperationGuard.Complete(transaction, operationId, "purchase", purchaseId);
        }, cancellationToken);
    }

    // "Untouched" means no posted document ever used the lot, even if its
    // quantity is back to the received amount. Legacy ledger touches carried
    // forward as these documents or as stock corrections.
    private static List<CancelLotRow> VoidLots(IDbConnection connection, long purchaseId, IDbTransaction? transaction = null) =>
        connection.Query<CancelLotRow>("""
            SELECT l.id AS LotId,l.product_id AS ProductId,l.qty_received AS Received,l.qty_on_hand AS OnHand,
                   l.unit_cost AS UnitCost,pl.id AS PurchaseLineId,
                   EXISTS(SELECT 1 FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
                          WHERE sl.lot_id=l.id AND s.status IN ('COMPLETED','VOID'))
                   OR EXISTS(SELECT 1 FROM damage WHERE lot_id=l.id)
                   OR EXISTS(SELECT 1 FROM vendor_return_line WHERE lot_id=l.id)
                   OR EXISTS(SELECT 1 FROM internal_use_record WHERE lot_id_snapshot=l.id)
                   OR EXISTS(SELECT 1 FROM physical_count_result WHERE source_lot_id=l.id)
                   OR EXISTS(SELECT 1 FROM stock_correction WHERE lot_id=l.id) AS Touched
            FROM product_lot l JOIN purchase_line pl ON pl.id=l.purchase_line_id
            WHERE pl.purchase_id=@purchaseId
            """, new { purchaseId }, transaction).ToList();

    private static bool IsUntouched(IDbConnection connection, long purchaseId, List<CancelLotRow> lots,
        IDbTransaction? transaction = null)
    {
        CancelLineCountRow lineCounts = connection.QuerySingle<CancelLineCountRow>("""
            SELECT COUNT(*) AS AllLines,
                   COALESCE(SUM(CASE WHEN line_kind<>'EXPENSE' THEN 1 ELSE 0 END),0) AS StockLines
            FROM purchase_line WHERE purchase_id=@purchaseId
            """, new { purchaseId }, transaction);
        return lineCounts.AllLines != 0 && lineCounts.StockLines == lots.Count &&
            !lots.Any(static lot => lot.OnHand != lot.Received || lot.Touched);
    }

    private void ApplyVoid(DbTx transaction, long purchaseId, DateOnly cancellationDate, List<CancelLotRow> lots)
    {
        foreach (CancelLotRow lot in lots)
        {
            stock.Change(transaction, new(cancellationDate, lot.ProductId, lot.LotId, LotChangeKind.PurchaseVoid,
                Quantity.FromScaled(-lot.Received)));
            transaction.Connection.Execute("UPDATE product_lot SET is_active=0 WHERE id=@LotId", lot, transaction.Transaction);
        }
        transaction.Connection.Execute("""
            INSERT INTO purchase_expense_reversal(expense_id,purchase_id,reversal_date,amount)
            SELECT e.id,@purchaseId,@date,e.amount FROM expense e
            JOIN purchase_line pl ON pl.id=e.purchase_line_id
            WHERE pl.purchase_id=@purchaseId
            """, new { purchaseId, date = Format(cancellationDate) }, transaction.Transaction);
        transaction.Connection.Execute("UPDATE purchase SET status='CANCELLED',cancelled_date=@date WHERE id=@purchaseId",
            new { purchaseId, date = Format(cancellationDate) }, transaction.Transaction);
        audit.Write(transaction, "purchase", purchaseId, "CANCEL", new { lots = lots.Count });
    }

    public Task<long> RecordPaymentAsync(VendorPaymentInput input, string operationId, CancellationToken cancellationToken = default)
    {
        ValidatePayment(input);
        return database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.VendorPayment);
            if (replay is not null) return replay.RefId ?? 0;
            periods.EnsureOpen(transaction, input.Date);
            EnsureVendorExists(transaction, input.VendorId);
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

    public Task<VendorLedgerPage> GetVendorLedgerPageAsync(long vendorId, DateOnly? beforeDate = null,
        string? beforeKind = null, long? beforeId = null, int limit = 100, string? filterKind = null,
        CancellationToken cancellationToken = default)
    {
        if (vendorId <= 0 || limit is < 1 or > 200 ||
            (beforeDate is null) != (beforeKind is null) || (beforeDate is null) != (beforeId is null) ||
            beforeKind is not null and not ("PURCHASE" or "PURCHASE_VOID" or "PAYMENT" or "VENDOR_RETURN" or "VENDOR_RETURN_CANCEL") ||
            filterKind is not null and not ("PURCHASE" or "PURCHASE_VOID" or "PAYMENT" or "VENDOR_RETURN" or "VENDOR_RETURN_CANCEL"))
            throw new DomainException(ErrorCode.Validation, "Invalid vendor ledger page.");
        return database.ReadAsync(connection =>
        {
            long opening = connection.QuerySingleOrDefault<long?>(
                "SELECT opening_balance FROM vendor WHERE id=@vendorId", new { vendorId })
                ?? throw new DomainException(ErrorCode.NotFound, "vendor", vendorId);
            List<LedgerRow> rows = connection.Query<LedgerRow>("""
                SELECT * FROM (
                    SELECT received_date AS Date,'PURCHASE' AS Kind,doc_no AS Reference,
                           grand_total AS Debit,0 AS Credit,id AS SortId
                    FROM purchase WHERE vendor_id=@vendorId AND
                        (status='POSTED' OR cancelled_date IS NOT NULL)
                        AND (@filterKind IS NULL OR @filterKind='PURCHASE')
                    UNION ALL
                    SELECT cancelled_date,'PURCHASE_VOID',doc_no,0,grand_total,id
                    FROM purchase WHERE vendor_id=@vendorId AND cancelled_date IS NOT NULL
                        AND (@filterKind IS NULL OR @filterKind='PURCHASE_VOID')
                    UNION ALL
                    SELECT pay_date,'PAYMENT',reference,0,amount,id
                    FROM vendor_payment WHERE vendor_id=@vendorId
                        AND (@filterKind IS NULL OR @filterKind='PAYMENT')
                    UNION ALL
                    SELECT return_date,'VENDOR_RETURN',doc_no,0,credit_total,id
                    FROM vendor_return WHERE vendor_id=@vendorId
                        AND (@filterKind IS NULL OR @filterKind='VENDOR_RETURN')
                    UNION ALL
                    SELECT c.cancellation_date,'VENDOR_RETURN_CANCEL',vr.doc_no,vr.credit_total,0,vr.id
                    FROM vendor_return_cancellation c JOIN vendor_return vr ON vr.id=c.vendor_return_id
                    WHERE vr.vendor_id=@vendorId
                        AND (@filterKind IS NULL OR @filterKind='VENDOR_RETURN_CANCEL')
                )
                WHERE @beforeDate IS NULL OR Date<@beforeDate
                   OR (Date=@beforeDate AND (Kind<@beforeKind OR (Kind=@beforeKind AND SortId<@beforeId)))
                ORDER BY Date DESC,Kind DESC,SortId DESC LIMIT @take
                """, new { vendorId, beforeDate, beforeKind, beforeId, filterKind, take = limit + 1 }).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            if (rows.Count == 0)
                return new VendorLedgerPage([], false, null, null, null);
            LedgerRow first = rows[0];
            long firstBalance = opening + connection.ExecuteScalar<long>("""
                SELECT COALESCE((SELECT SUM(grand_total) FROM purchase
                    WHERE vendor_id=@vendorId AND (status='POSTED' OR cancelled_date IS NOT NULL) AND
                        (received_date<@date OR (received_date=@date AND
                         ('PURCHASE'<@kind OR ('PURCHASE'=@kind AND id<=@id))))),0)
                     - COALESCE((SELECT SUM(grand_total) FROM purchase
                    WHERE vendor_id=@vendorId AND cancelled_date IS NOT NULL AND
                        (cancelled_date<@date OR (cancelled_date=@date AND
                         ('PURCHASE_VOID'<@kind OR ('PURCHASE_VOID'=@kind AND id<=@id))))),0)
                     - COALESCE((SELECT SUM(amount) FROM vendor_payment
                    WHERE vendor_id=@vendorId AND
                        (pay_date<@date OR (pay_date=@date AND
                         ('PAYMENT'<@kind OR ('PAYMENT'=@kind AND id<=@id))))),0)
                     - COALESCE((SELECT SUM(credit_total) FROM vendor_return
                    WHERE vendor_id=@vendorId AND
                        (return_date<@date OR (return_date=@date AND
                         ('VENDOR_RETURN'<@kind OR ('VENDOR_RETURN'=@kind AND id<=@id))))),0)
                     + COALESCE((SELECT SUM(vr.credit_total) FROM vendor_return_cancellation c
                    JOIN vendor_return vr ON vr.id=c.vendor_return_id
                    WHERE vr.vendor_id=@vendorId AND
                        (c.cancellation_date<@date OR (c.cancellation_date=@date AND
                         ('VENDOR_RETURN_CANCEL'<@kind OR ('VENDOR_RETURN_CANCEL'=@kind AND vr.id<=@id))))),0)
                """, new { vendorId, date = first.Date, kind = first.Kind, id = first.SortId });
            var items = new List<VendorLedgerItem>(rows.Count);
            long balance = firstBalance;
            foreach (LedgerRow row in rows)
            {
                items.Add(new(row.Date, row.Kind, row.Reference, Money.FromScaled(row.Debit),
                    Money.FromScaled(row.Credit), Money.FromScaled(balance)));
                balance -= row.Debit - row.Credit;
            }
            LedgerRow last = rows[^1];
            return new VendorLedgerPage(items, hasMore, last.Date, last.Kind, last.SortId);
        }, cancellationToken);
    }

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

    public Task<VendorReturnHistoryPage> GetVendorReturnHistoryPageAsync(long purchaseId,
        long? beforeId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (purchaseId <= 0 || beforeId < 1 || limit is < 1 or > 100)
            throw new DomainException(ErrorCode.Validation, "Invalid return history page.");
        return database.ReadAsync(connection =>
        {
            List<VendorReturnHistoryRow> rows = connection.Query<VendorReturnHistoryRow>("""
                SELECT vr.id AS Id,vr.doc_no AS DocumentNumber,vr.return_date AS Date,
                       vr.credit_total AS CreditTotal,vr.cost_total AS CostTotal,
                       c.cancellation_date AS CancellationDate
                FROM vendor_return vr LEFT JOIN vendor_return_cancellation c
                    ON c.vendor_return_id=vr.id
                WHERE vr.purchase_id=@purchaseId AND (@beforeId IS NULL OR vr.id<@beforeId)
                ORDER BY vr.id DESC LIMIT @take
                """, new { purchaseId, beforeId, take = limit + 1 }).ToList();
            bool more = rows.Count > limit;
            if (more) rows.RemoveAt(limit);
            return new VendorReturnHistoryPage(rows.Select(static row => row.ToModel()).ToArray(),
                more, rows.LastOrDefault()?.Id);
        }, cancellationToken);
    }

    public async Task<PostedVendorReturn> PostVendorReturnAsync(VendorReturnInput input, string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        string creditNumber = Required(input.VendorCreditNumber, 120, nameof(input.VendorCreditNumber));
        string reason = Required(input.Reason, 500, nameof(input.Reason));
        if (input.Lines.Count == 0 || input.Lines.Select(static line => line.LotId).Distinct().Count() != input.Lines.Count)
            throw new DomainException(ErrorCode.Validation, "Select distinct lots to return.");
        return await database.WriteAsync(transaction =>
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
            periods.EnsureOpen(transaction, input.Date);
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
                    throw new DomainException(ErrorCode.VendorReturnExceedsLot, line.LotId);
                beforeTax += line.CreditBeforeTax;
                tax += line.TaxCredit;
                cost += Money.FromScaled(lot.UnitCost) * line.Quantity;
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
                        credit_before_tax,tax_credit,cost_value,purchase_line_id_snapshot,
                        product_name_snapshot,product_sku_snapshot,product_name_local_snapshot,
                        uom_snapshot,product_class_snapshot,lot_code_snapshot,batch_no_snapshot,
                        expiry_date_snapshot,unit_cost_snapshot,tax_mode_snapshot,
                        snapshot_provenance,legacy_unknown_fields,tax_label_snapshot)
                    SELECT @returnId,l.product_id,l.id,@qty,@beforeTax,@tax,@cost,
                        l.purchase_line_id,pl.product_name_snapshot,pl.product_sku_snapshot,
                        pl.product_name_local_snapshot,pl.uom_snapshot,pl.product_class_snapshot,
                        l.lot_code,l.batch_no,l.expiry_date,pl.landed_unit_cost,@taxMode,
                        'AT_POST',NULL,pl.tax_label_snapshot
                    FROM product_lot l JOIN purchase_line pl ON pl.id=l.purchase_line_id
                    WHERE l.id=@lotId
                    """, new DynamicParameters(new
                {
                    returnId,
                    productId = lot.ProductId,
                    lotId = line.LotId,
                    qty = line.Quantity.Scaled,
                    beforeTax = line.CreditBeforeTax.Scaled,
                    tax = line.TaxCredit.Scaled,
                    cost = lineCost.Scaled,
                    taxMode = purchase.TaxMode ?? settings.Current.TaxMode,
                }), transaction.Transaction);
                stock.Change(transaction, new(input.Date, lot.ProductId, lot.LotId, LotChangeKind.VendorReturn,
                    Quantity.FromScaled(-line.Quantity.Scaled)));
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
    }

    public Task CancelVendorReturnAsync(long returnId, DateOnly date, string reason,
        string operationId, CancellationToken cancellationToken = default) =>
        CancelVendorReturnAsync(returnId, date, reason, operationId,
            new Dictionary<long, RestoreProductChoice>(), cancellationToken);

    // The units go back into the lot they left. If that lot was deleted at zero, they start a new
    // direct lot instead: of the same product or, when it was deleted too, of the product the
    // operator chose for that return line.
    public async Task CancelVendorReturnAsync(long returnId, DateOnly date, string reason, string operationId,
        IReadOnlyDictionary<long, RestoreProductChoice> restores, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restores);
        string cleanReason = Required(reason, 500, nameof(reason));
        await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.VendorReturnCancel);
            if (replay is not null) return;
            var source = transaction.Connection.QuerySingleOrDefault<(long Id, string DocumentNumber)>(
                "SELECT id AS Id,doc_no AS DocumentNumber FROM vendor_return WHERE id=@returnId",
                new { returnId }, transaction.Transaction);
            if (source.Id == 0) throw new DomainException(ErrorCode.NotFound, "vendor return", returnId);
            if (transaction.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM vendor_return_cancellation WHERE vendor_return_id=@returnId",
                new { returnId }, transaction.Transaction) != 0)
            {
                OperationGuard.Complete(transaction, operationId, "vendor_return", returnId);
                return;
            }
            periods.EnsureOpen(transaction, date);
            var lines = transaction.Connection.Query<(long Id, long LotId, long ProductId, long Quantity, bool LotExists)>("""
                SELECT vrl.id AS Id,vrl.lot_id AS LotId,vrl.product_id AS ProductId,vrl.qty AS Quantity,
                       EXISTS(SELECT 1 FROM product_lot l WHERE l.id=vrl.lot_id AND l.product_id=vrl.product_id) AS LotExists
                FROM vendor_return_line vrl WHERE vrl.vendor_return_id=@returnId
                """, new { returnId }, transaction.Transaction).ToList();
            var restoredLots = new List<long>();
            var restoredProducts = new List<object>();
            foreach (var line in lines)
            {
                if (line.LotExists)
                {
                    stock.Change(transaction, new(date, line.ProductId, line.LotId, LotChangeKind.VendorReturnCancel,
                        Quantity.FromScaled(line.Quantity)));
                    continue;
                }
                (long lotId, long productId, RestoredProduct? chosen) = RecreateReturnedLot(transaction, line.Id, date,
                    $"Vendor return {source.DocumentNumber} cancelled: {cleanReason}", restores.GetValueOrDefault(line.Id));
                restoredLots.Add(lotId);
                if (chosen is not null)
                    restoredProducts.Add(new { returnLineId = line.Id, productId, createdProduct = chosen.Created });
            }
            transaction.Connection.Execute("""
                INSERT INTO vendor_return_cancellation(vendor_return_id,operation_id,cancellation_date,reason)
                VALUES (@returnId,@operationId,@date,@cleanReason)
                """, new { returnId, operationId, date = Format(date), cleanReason }, transaction.Transaction);
            audit.Write(transaction, "vendor_return", returnId, "CANCEL", new { cleanReason, restoredLots, restoredProducts });
            OperationGuard.Complete(transaction, operationId, "vendor_return", returnId);
        }, cancellationToken);
    }

    private const string ReturnedLotSql = """
        SELECT vrl.id AS ReturnLineId,vr.doc_no AS DocumentNumber,p.id AS ProductId,vr.vendor_id AS VendorId,
               vrl.qty AS Quantity,COALESCE(vrl.unit_cost_snapshot,pl.landed_unit_cost) AS UnitCost,
               vrl.batch_no_snapshot AS BatchNumber,pl.mfg_date AS ManufacturingDate,
               vrl.expiry_date_snapshot AS ExpiryDate,pl.selling_price AS SellingPrice,pl.mrp AS Mrp,
               pl.tax_rate_id AS TaxRateId,pl.tax_label_snapshot AS TaxLabel,pl.tax_rate_pct AS TaxRatePct,
               pl.landed_unit_cost AS LineUnitCost,pl.landed_unit_tax AS LineUnitTax,
               pl.landed_unit_cost_incl_tax AS LineUnitCostInclTax,
               COALESCE(vrl.product_name_snapshot,pl.product_name_snapshot) AS ProductName,
               COALESCE(vrl.product_name_local_snapshot,pl.product_name_local_snapshot) AS ProductNameLocal,
               COALESCE(vrl.product_sku_snapshot,pl.product_sku_snapshot) AS Sku,
               COALESCE(vrl.uom_snapshot,pl.uom_snapshot) AS UnitCode,
               COALESCE(vrl.product_class_snapshot,pl.product_class_snapshot,'SALEABLE') AS ProductClass
        FROM vendor_return_line vrl JOIN vendor_return vr ON vr.id=vrl.vendor_return_id
        JOIN purchase_line pl ON pl.id=vrl.purchase_line_id_snapshot
        LEFT JOIN product p ON p.id=vrl.product_id
        """;

    /// <summary>
    /// The saved details of each line of a vendor return whose lot and product were both deleted;
    /// cancelling the return needs a product for each of them.
    /// </summary>
    public Task<IReadOnlyList<RestoreDraft>> GetVendorReturnRestoreDraftsAsync(long returnId,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
        (IReadOnlyList<RestoreDraft>)connection.Query<ReturnedLotRow>(ReturnedLotSql + """
             WHERE vrl.vendor_return_id=@returnId AND p.id IS NULL
               AND NOT EXISTS(SELECT 1 FROM vendor_return_cancellation c WHERE c.vendor_return_id=vrl.vendor_return_id)
               AND NOT EXISTS(SELECT 1 FROM product_lot l WHERE l.id=vrl.lot_id AND l.product_id=vrl.product_id)
             ORDER BY vrl.id
            """, new { returnId }).Select(row => ReturnDraft(connection, null, row)).ToList(), cancellationToken);

    private static RestoreDraft ReturnDraft(IDbConnection connection, IDbTransaction? transaction, ReturnedLotRow row) =>
        new(row.ReturnLineId, row.DocumentNumber, row.ProductName ?? "", row.ProductNameLocal,
            RestoreProducts.SuggestSku(connection, transaction, row.Sku, $"V{row.ReturnLineId:X}"), null,
            connection.ExecuteScalar<long?>("SELECT id FROM uom WHERE code=@UnitCode COLLATE NOCASE", row, transaction),
            row.UnitCode, row.TaxRateId, null, null, null, row.ProductClass, row.BatchNumber, row.ExpiryDate,
            Quantity.FromScaled(row.Quantity), Money.FromScaled(row.UnitCost), Money.FromScaled(row.SellingPrice),
            row.Mrp is { } mrp ? Money.FromScaled(mrp) : null);

    // A new lot carries what the return took out: its unit cost, batch and expiry. The purchase line
    // gives the tax, mfg date and prices; it gives the tax per piece only when its cost is the same
    // (a line saved before tax snapshots derives it from that cost and the line's rate). The tax
    // stays the purchase's even when the units go into a product the operator chose.
    private (long LotId, long ProductId, RestoredProduct? Chosen) RecreateReturnedLot(DbTx transaction,
        long returnLineId, DateOnly date, string reason, RestoreProductChoice? restore)
    {
        ReturnedLotRow lot = transaction.Connection.QuerySingleOrDefault<ReturnedLotRow>(
            ReturnedLotSql + " WHERE vrl.id=@returnLineId", new { returnLineId }, transaction.Transaction)
            ?? throw new DomainException(ErrorCode.NotFound, "vendor return line", returnLineId);
        RestoredProduct? chosen = null;
        long productId;
        if (lot.ProductId is { } existing) productId = existing;
        else if (restore is null) throw new DomainException(ErrorCode.VendorReturnProductDeleted);
        else
        {
            chosen = RestoreProducts.Resolve(transaction, ReturnDraft(transaction.Connection, transaction.Transaction, lot),
                restore);
            productId = chosen.ProductId;
        }
        var rate = new TaxRateSnapshot(lot.TaxRateId, lot.TaxLabel, Percent.FromScaled(lot.TaxRatePct));
        LotTax tax = lot.LineUnitCost == lot.UnitCost && lot.LineUnitTax is { } unitTax &&
            lot.LineUnitCostInclTax is { } inclTax
            ? new LotTax(rate, new PurchaseUnitCost(Money.FromScaled(inclTax - unitTax), Money.FromScaled(unitTax),
                Money.FromScaled(inclTax), Money.FromScaled(lot.UnitCost)))
            : LotTaxes.FromBooked(transaction, rate, Money.FromScaled(lot.UnitCost));
        long lotId = transaction.Connection.ExecuteScalar<long>("""
            INSERT INTO product_lot(product_id,lot_code,batch_no,vendor_id,received_date,mfg_date,expiry_date,
                unit_cost,mrp,selling_price,qty_received,direct_reason,
                tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,unit_tax,unit_cost_incl_tax)
            VALUES (@productId,@lotCode,@BatchNumber,@VendorId,@date,@ManufacturingDate,@ExpiryDate,
                @UnitCost,@Mrp,@SellingPrice,@Quantity,@reason,
                @taxRateId,@taxLabel,@taxRatePct,@unitTax,@unitCostInclTax)
            RETURNING id
            """, tax.AddTo(new DynamicParameters(new
        {
            productId,
            lotCode = numbers.Next(transaction, DocType.Lot, date),
            lot.BatchNumber,
            lot.VendorId,
            date = Format(date),
            lot.ManufacturingDate,
            ExpiryDate = chosen is null ? lot.ExpiryDate : chosen.ExpiryDate,
            lot.UnitCost,
            Mrp = chosen is null ? lot.Mrp : chosen.Mrp?.Scaled,
            SellingPrice = chosen is null ? lot.SellingPrice : chosen.SellingPrice.Scaled,
            lot.Quantity,
            reason,
        })), transaction.Transaction);
        stock.Change(transaction, new(date, productId, lotId, LotChangeKind.DirectEntry, Quantity.FromScaled(lot.Quantity)));
        return (lotId, productId, chosen);
    }

    public async Task<long> ConsumeInternalAsync(long lotId, Quantity quantity, DateOnly date,
        string? reason, string operationId, CancellationToken cancellationToken = default)
    {
        if (lotId <= 0 || quantity <= Quantity.Zero)
            throw new DomainException(ErrorCode.Validation, "Choose an internal-use lot and positive quantity.");
        long recordId = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.InternalUse);
            if (replay?.RefId is long previous) return previous;
            periods.EnsureOpen(transaction, date);
            var lot = transaction.Connection.QuerySingleOrDefault<InternalLotRow>("""
                SELECT l.id AS LotId,l.product_id AS ProductId,l.qty_on_hand AS OnHand,
                       l.unit_cost AS UnitCost,l.lot_code AS LotCode,l.batch_no AS BatchNumber,
                       l.expiry_date AS ExpiryDate,p.sku AS Sku,p.name AS ProductName,
                       u.code AS Uom,u.allow_fraction AS AllowFraction
                FROM product_lot l JOIN product p ON p.id=l.product_id JOIN uom u ON u.id=p.uom_id
                WHERE l.id=@lotId AND p.product_class='INTERNAL' AND l.is_active=1
                """, new { lotId }, transaction.Transaction)
                ?? throw new DomainException(ErrorCode.NotFound, "internal-use lot", lotId);
            if (quantity.Scaled > lot.OnHand) throw new DomainException(ErrorCode.InsufficientStock, lotId);
            if (!lot.AllowFraction && !quantity.IsWhole)
                throw new DomainException(ErrorCode.FractionalQtyNotAllowed);
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO internal_use_record(product_id_snapshot,lot_id_snapshot,product_sku_snapshot,
                    product_name_snapshot,uom_snapshot,lot_code_snapshot,batch_no_snapshot,
                    expiry_date_snapshot,quantity,unit_cost,used_date,reason)
                VALUES (@ProductId,@LotId,@Sku,@ProductName,@Uom,@LotCode,@BatchNumber,
                    @ExpiryDate,@quantity,@UnitCost,@date,@reason) RETURNING id
                """, new
            {
                lot.ProductId,
                lot.LotId,
                lot.Sku,
                lot.ProductName,
                lot.Uom,
                lot.LotCode,
                lot.BatchNumber,
                lot.ExpiryDate,
                quantity = quantity.Scaled,
                lot.UnitCost,
                date = Format(date),
                reason = Optional(reason, 500)
            }, transaction.Transaction);
            stock.Change(transaction, new(date, lot.ProductId, lotId, LotChangeKind.InternalUse,
                Quantity.FromScaled(-quantity.Scaled)));
            audit.Write(transaction, "internal_use_record", id, "CONSUME", new { lotId, quantity = quantity.Scaled });
            OperationGuard.Complete(transaction, operationId, "internal_use_record", id);
            return id;
        }, cancellationToken);
        return recordId;
    }

    private static VendorReturnLotRow[] LoadVendorReturnLots(IDbConnection connection, long purchaseId,
        IDbTransaction? transaction = null) => connection.Query<VendorReturnLotRow>("""
            SELECT l.id AS LotId,l.product_id AS ProductId,p.name AS ProductName,l.lot_code AS LotCode,
                   l.qty_received AS Received,l.qty_on_hand AS OnHand,pl.landed_unit_cost AS UnitCost,
                   pl.line_total-pl.tax_amount AS OriginalBeforeTax,pl.tax_amount AS OriginalTax,
                   COALESCE((SELECT SUM(vrl.qty) FROM vendor_return_line vrl
                     LEFT JOIN vendor_return_cancellation c ON c.vendor_return_id=vrl.vendor_return_id
                     WHERE vrl.lot_id=l.id AND c.vendor_return_id IS NULL),0) AS Returned,
                   COALESCE((SELECT SUM(vrl.credit_before_tax) FROM vendor_return_line vrl
                     LEFT JOIN vendor_return_cancellation c ON c.vendor_return_id=vrl.vendor_return_id
                     WHERE vrl.lot_id=l.id AND c.vendor_return_id IS NULL),0) AS CreditedBeforeTax,
                   COALESCE((SELECT SUM(vrl.tax_credit) FROM vendor_return_line vrl
                     LEFT JOIN vendor_return_cancellation c ON c.vendor_return_id=vrl.vendor_return_id
                     WHERE vrl.lot_id=l.id AND c.vendor_return_id IS NULL),0) AS CreditedTax,
                   u.allow_fraction AS AllowFraction
            FROM purchase_line pl JOIN product_lot l ON l.purchase_line_id=pl.id
            JOIN product p ON p.id=l.product_id JOIN uom u ON u.id=p.uom_id
            WHERE pl.purchase_id=@purchaseId
            ORDER BY pl.line_no
            """, new { purchaseId }, transaction).ToArray();

    public Task<VendorPurchasePage> GetVendorPurchasePageAsync(long vendorId, string? query = null,
        DateOnly? beforeDate = null, long? beforeId = null, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (vendorId <= 0 || (beforeDate is null) != (beforeId is null) || limit is < 1 or > 200)
            throw new DomainException(ErrorCode.Validation, "Invalid vendor purchase page.");
        string term = (query ?? string.Empty).Trim();
        if (term.Length > 120) throw new DomainException(ErrorCode.Validation, "Search term is too long.");
        string pattern = "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return database.ReadAsync(connection =>
        {
            List<VendorPurchaseChoiceRow> rows = connection.Query<VendorPurchaseChoiceRow>("""
                SELECT id AS Id,doc_no AS DocumentNumber,vendor_invoice_no AS InvoiceNumber,
                       received_date AS ReceivedDate
                FROM purchase WHERE vendor_id=@vendorId AND status='POSTED'
                  AND (@term='' OR doc_no LIKE @pattern ESCAPE '\' OR vendor_invoice_no LIKE @pattern ESCAPE '\')
                  AND (@beforeDate IS NULL OR received_date<@beforeDate
                       OR (received_date=@beforeDate AND id<@beforeId))
                ORDER BY received_date DESC,id DESC LIMIT @take
                """, new { vendorId, term, pattern, beforeDate, beforeId, take = limit + 1 }).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            VendorPurchaseChoiceRow? last = rows.LastOrDefault();
            return new VendorPurchasePage(rows.Select(static row => new VendorPurchaseChoice(row.Id,
                string.IsNullOrWhiteSpace(row.InvoiceNumber) ? row.DocumentNumber :
                    $"{row.DocumentNumber} · {row.InvoiceNumber}")).ToList(),
                hasMore, last?.ReceivedDate, last?.Id);
        }, cancellationToken);
    }

    public Task<VendorLotPage> GetVendorLotPageAsync(long vendorId, DateOnly? beforeDate = null,
        long? beforeId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (vendorId <= 0 || (beforeDate is null) != (beforeId is null) || limit is < 1 or > 200)
            throw new DomainException(ErrorCode.Validation, "Invalid vendor lot page.");
        return database.ReadAsync(connection =>
        {
            List<VendorLotRow> rows = connection.Query<VendorLotRow>("""
                SELECT l.id AS Id,p.name AS ProductName,l.lot_code AS LotCode,
                       l.batch_no AS BatchNumber,l.received_date AS ReceivedDate,
                       l.expiry_date AS ExpiryDate,l.qty_on_hand AS OnHand
                FROM product_lot l JOIN product p ON p.id=l.product_id
                WHERE l.vendor_id=@vendorId AND (@beforeDate IS NULL OR l.received_date<@beforeDate
                      OR (l.received_date=@beforeDate AND l.id<@beforeId))
                ORDER BY l.received_date DESC,l.id DESC LIMIT @take
                """, new { vendorId, beforeDate, beforeId, take = limit + 1 }).ToList();
            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            VendorLotRow? last = rows.LastOrDefault();
            return new VendorLotPage(rows.Select(static row => new VendorLotItem(row.ProductName,
                row.LotCode, row.BatchNumber, row.ReceivedDate, row.ExpiryDate,
                Quantity.FromScaled(row.OnHand))).ToList(), hasMore, last?.ReceivedDate, last?.Id);
        }, cancellationToken);
    }

    // Lines typed including tax are validated as typed, then stored before tax with the typed
    // rate and discount kept beside them. The draft also saves each line's tax label and per-piece
    // prices, so a reopened or reviewed draft shows what posting will save.
    private void InsertDraftLines(DbTx transaction, long purchaseId, PurchaseDraftInput input,
        HashSet<long> chosenBefore)
    {
        var resolved = new List<(PurchaseLineInput Line, TaxRateSnapshot Rate)>(input.Lines.Count);
        foreach (PurchaseLineInput entered in input.Lines)
        {
            ProductRule rule = transaction.Connection.QuerySingleOrDefault<ProductRule>("""
                SELECT p.track_expiry AS TrackExpiry,u.allow_fraction AS AllowFraction,
                       p.product_class AS ProductClass
                FROM product p JOIN uom u ON u.id=p.uom_id WHERE p.id=@ProductId AND p.is_active=1
                """, entered, transaction.Transaction) ?? throw new DomainException(ErrorCode.NotFound, "product", entered.ProductId);
            ValidateLine(entered, rule.TrackExpiry, rule.AllowFraction, default);
            if (entered.TaxRateId is not { } taxRateId)
                throw new DomainException(ErrorCode.TaxRateRequired);
            TaxRateSnapshot rate = LotTaxes.Rate(transaction, taxRateId, activeOnly: !chosenBefore.Contains(taxRateId));
            PurchaseLineInput line = input.RatesIncludeTax ? entered.ExcludingTax(rate.Rate) : entered;
            if (line.LineKind != "EXPENSE" &&
                ((line.LineKind == "INTERNAL") != (rule.ProductClass == "INTERNAL")))
                throw new DomainException(ErrorCode.PurchaseLineTypeMismatch);
            if (line.LineKind == "EXPENSE" && transaction.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM expense_category WHERE id=@id AND is_active=1",
                new { id = line.ExpenseCategoryId }, transaction.Transaction) != 1)
                throw new DomainException(ErrorCode.ExpenseCategoryInactive);
            resolved.Add((line, rate));
        }
        PricedPurchaseLine[] priced = PriceLines(resolved, input.OtherCharges);
        for (int index = 0; index < priced.Length; index++)
        {
            PricedPurchaseLine line = priced[index];
            PurchaseLineInput entered = input.Lines[index];
            transaction.Connection.Execute("""
                INSERT INTO purchase_line(purchase_id,line_no,product_id,line_kind,expense_category_id,
                                          batch_no,mfg_date,expiry_date,qty,free_qty,
                                          unit_cost,discount,tax_rate_pct,tax_amount,line_total,landed_unit_cost,mrp,selling_price,
                                          entered_rate,entered_discount,tax_rate_id,tax_label_snapshot,
                                          landed_unit_tax,landed_unit_cost_incl_tax)
                VALUES (@purchaseId,@lineNo,@ProductId,@LineKind,@ExpenseCategoryId,
                        @BatchNumber,@ManufacturingDate,@ExpiryDate,@Quantity,@FreeQuantity,
                        @Rate,@Discount,@TaxRate,@tax,@lineTotal,@landed,@Mrp,@SellingPrice,
                        @enteredRate,@enteredDiscount,@taxRateId,@taxLabel,
                        @unitTax,@unitCostInclTax)
                """, new
            {
                purchaseId,
                lineNo = index + 1,
                line.Line.ProductId,
                line.Line.LineKind,
                line.Line.ExpenseCategoryId,
                BatchNumber = Optional(line.Line.BatchNumber, 120),
                line.Line.ManufacturingDate,
                line.Line.ExpiryDate,
                Quantity = line.Line.Quantity.Scaled,
                FreeQuantity = line.Line.FreeQuantity.Scaled,
                Rate = line.Line.Rate.Scaled,
                Discount = line.Line.Discount.Scaled,
                TaxRate = line.Rate.Rate.Scaled,
                tax = line.Tax.Scaled,
                lineTotal = (line.Taxable + line.Tax).Scaled,
                landed = line.Unit.Booked.Scaled,
                Mrp = line.Line.Mrp?.Scaled,
                SellingPrice = line.Line.SellingPrice.Scaled,
                enteredRate = input.RatesIncludeTax ? entered.Rate.Scaled : (long?)null,
                enteredDiscount = input.RatesIncludeTax ? entered.Discount.Scaled : (long?)null,
                taxRateId = line.Rate.Id,
                taxLabel = line.Rate.Label,
                unitTax = line.Unit.Tax.Scaled,
                unitCostInclTax = line.Unit.InclTax.Scaled,
            }, transaction.Transaction);
        }
    }

    // One line as saved: its before-tax figures, its rate, tax on the goods, its per-piece prices
    // with its share of other charges, and the exact amount the whole line books.
    private sealed record PricedPurchaseLine(PurchaseLineInput Line, TaxRateSnapshot Rate, Money Taxable,
        Money Tax, PurchaseUnitCost Unit, Money Booked);

    private PricedPurchaseLine[] PriceLines(List<(PurchaseLineInput Line, TaxRateSnapshot Rate)> lines,
        Money otherCharges)
    {
        var taxable = new Money[lines.Count];
        var taxes = new Money[lines.Count];
        var costLines = new List<PurchaseCostLine>(lines.Count);
        for (int index = 0; index < lines.Count; index++)
        {
            PurchaseLineInput line = lines[index].Line;
            taxable[index] = line.Rate * line.Quantity - line.Discount;
            taxes[index] = Tax.FromExclusive(taxable[index], lines[index].Rate.Rate).Tax;
            costLines.Add(new(line.Quantity, line.FreeQuantity, line.Rate, line.Discount, taxes[index]));
        }
        IReadOnlyList<PurchaseLineCost> costs = LandedCost.ComputeLines(costLines, otherCharges,
            !string.Equals(settings.Current.TaxMode, "REGULAR", StringComparison.Ordinal));
        return lines.Select((line, index) => new PricedPurchaseLine(line.Line, line.Rate, taxable[index],
            taxes[index], costs[index].Unit, costs[index].Booked)).ToArray();
    }

    // A line saved before it had a tax rate takes the only active rate with its percent; otherwise
    // the operator must choose one in the purchase form before posting.
    private static TaxRateSnapshot PostingRate(DbTx transaction, long? taxRateId, long savedPercent)
    {
        if (taxRateId is { } id) return LotTaxes.Rate(transaction, id);
        long? matched = transaction.Connection.ExecuteScalar<long?>("""
            SELECT CASE WHEN COUNT(*)=1 THEN MIN(id) END FROM tax_rate WHERE rate_pct=@savedPercent AND is_active=1
            """, new { savedPercent }, transaction.Transaction);
        return matched is { } match ? LotTaxes.Rate(transaction, match)
            : throw new DomainException(ErrorCode.TaxRateRequired);
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
        if (input.Lines.Count > MaxPurchaseLines) throw new DomainException(ErrorCode.PurchaseTooManyLines, MaxPurchaseLines);
        if (input.VendorId <= 0 || input.ReceivedDate == default || input.OtherCharges < Money.Zero ||
            input.VendorBillTotal < Money.Zero || input.Lines.Count == 0)
            throw new DomainException(ErrorCode.Validation, "Invalid purchase draft.");
        if (input.VendorInvoiceDate is not null && input.VendorInvoiceDate > input.ReceivedDate)
            throw new DomainException(ErrorCode.InvalidDate);
    }

    private static void ValidateLine(PurchaseLineInput line, bool trackExpiry, bool allowFraction, DateOnly receivedDate)
    {
        if (line.ProductId <= 0 || line.LineKind is not ("PRODUCT" or "INTERNAL" or "EXPENSE") ||
            (line.LineKind == "EXPENSE") != (line.ExpenseCategoryId is not null) ||
            line.Quantity <= Quantity.Zero || line.FreeQuantity < Quantity.Zero || line.Rate < Money.Zero ||
            line.Discount < Money.Zero || line.SellingPrice < Money.Zero)
            throw new DomainException(ErrorCode.Validation, "Invalid purchase line.");
        if (line.Discount > line.Rate * line.Quantity) throw new DomainException(ErrorCode.DiscountExceedsLine, line.ProductId);
        if (!allowFraction && (!line.Quantity.IsWhole || !line.FreeQuantity.IsWhole))
            throw new DomainException(ErrorCode.FractionalQtyNotAllowed);
        if (line.LineKind != "EXPENSE" && trackExpiry && line.ExpiryDate is null)
            throw new DomainException(ErrorCode.ExpiryRequired, line.ProductId);
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
        if (gstin is not null && !IndianIds.IsGstin(gstin)) throw new DomainException(ErrorCode.InvalidGstin);
        if (pan is null && gstin is not null) pan = gstin.Substring(2, 10);
        if (pan is not null && !IndianIds.IsPan(pan)) throw new DomainException(ErrorCode.InvalidPan);
        if (pincode is not null && !IndianIds.IsPincode(pincode)) throw new DomainException(ErrorCode.Validation, "Pincode");
        if (input.PaymentTermsDays < 0 || input.PaymentTermsDays > MaxPaymentTermsDays || input.OpeningBalance < Money.Zero)
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

    private static void EnsureVendorExists(DbTx transaction, long vendorId)
    {
        if (transaction.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM vendor WHERE id=@vendorId",
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
        ratesIncludeTax = input.RatesIncludeTax ? 1 : 0,
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
    private sealed class ProductRule { public bool TrackExpiry { get; init; } public bool AllowFraction { get; init; } public string ProductClass { get; init; } = "SALEABLE"; }
    private sealed class PurchaseHeader { public long Id { get; init; } public long VendorId { get; init; } public DateOnly ReceivedDate { get; init; } public string Status { get; init; } = ""; public long OtherCharges { get; init; } public long? VendorBillTotal { get; init; } public bool RatesIncludeTax { get; init; } }
    private sealed class DraftHeader { public long Id { get; init; } public long VendorId { get; init; } public string? VendorInvoiceNumber { get; init; } public DateOnly? VendorInvoiceDate { get; init; } public DateOnly ReceivedDate { get; init; } public long? VendorBillTotal { get; init; } public long OtherCharges { get; init; } public string? Notes { get; init; } public bool RatesIncludeTax { get; init; } }
    private sealed class LineDefaultsRow { public bool TrackExpiry { get; init; } public int? ShelfLifeDays { get; init; } public int ExpiryAlertDays { get; init; } public long? TaxRateId { get; init; } public long TaxRate { get; init; } public long? Mrp { get; init; } public long? SellingPrice { get; init; } public long? LastRate { get; init; } }
    private sealed class PurchaseLineRow { public long Id { get; init; } public long ProductId { get; init; } public string LineKind { get; init; } = "PRODUCT"; public long? ExpenseCategoryId { get; init; } public string ProductClass { get; init; } = "SALEABLE"; public string? BatchNumber { get; init; } public DateOnly? ManufacturingDate { get; init; } public DateOnly? ExpiryDate { get; init; } public long Quantity { get; init; } public long FreeQuantity { get; init; } public long Rate { get; init; } public long Discount { get; init; } public long TaxRate { get; init; } public long? TaxRateId { get; init; } public long? EnteredRate { get; init; } public long? EnteredDiscount { get; init; } public long? Mrp { get; init; } public long SellingPrice { get; init; } public bool TrackExpiry { get; init; } public bool AllowFraction { get; init; } public PurchaseLineInput ToInput() => new(ProductId, BatchNumber, ManufacturingDate, ExpiryDate, GroceryErp.Core.Numerics.Quantity.FromScaled(Quantity), GroceryErp.Core.Numerics.Quantity.FromScaled(FreeQuantity), Money.FromScaled(Rate), Money.FromScaled(Discount), TaxRateId, Mrp is null ? null : Money.FromScaled(Mrp.Value), Money.FromScaled(SellingPrice)) { LineKind = LineKind, ExpenseCategoryId = ExpenseCategoryId }; }
    private sealed class CancelLotRow { public long LotId { get; init; } public long ProductId { get; init; } public long Received { get; init; } public long OnHand { get; init; } public long UnitCost { get; init; } public long PurchaseLineId { get; init; } public bool Touched { get; init; } }
    private sealed class CancelLineCountRow { public long AllLines { get; init; } public long StockLines { get; init; } }
    private sealed class PostedRow { public long Id { get; init; } public string DocumentNumber { get; init; } = ""; public long Subtotal { get; init; } public long Discount { get; init; } public long Tax { get; init; } public long OtherCharges { get; init; } public long RoundOff { get; init; } public long GrandTotal { get; init; } public long? VendorBillTotal { get; init; } }
    private sealed class LedgerRow { public DateOnly Date { get; init; } public string Kind { get; init; } = ""; public string? Reference { get; init; } public long Debit { get; init; } public long Credit { get; init; } public long SortId { get; init; } }
    private sealed class VendorPurchaseChoiceRow { public long Id { get; init; } public string DocumentNumber { get; init; } = ""; public string? InvoiceNumber { get; init; } public DateOnly ReceivedDate { get; init; } }
    private sealed class VendorLotRow { public long Id { get; init; } public string ProductName { get; init; } = ""; public string LotCode { get; init; } = ""; public string? BatchNumber { get; init; } public DateOnly ReceivedDate { get; init; } public DateOnly? ExpiryDate { get; init; } public long OnHand { get; init; } }
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
    private sealed class ReturnedLotRow
    {
        public long ReturnLineId { get; init; }
        public string DocumentNumber { get; init; } = "";
        public long? ProductId { get; init; }
        public long VendorId { get; init; }
        public long Quantity { get; init; }
        public long UnitCost { get; init; }
        public string? BatchNumber { get; init; }
        public string? ManufacturingDate { get; init; }
        public DateOnly? ExpiryDate { get; init; }
        public string? ProductName { get; init; }
        public string? ProductNameLocal { get; init; }
        public string? Sku { get; init; }
        public string? UnitCode { get; init; }
        public string ProductClass { get; init; } = "SALEABLE";
        public long SellingPrice { get; init; }
        public long? Mrp { get; init; }
        public long? TaxRateId { get; init; }
        public string? TaxLabel { get; init; }
        public long TaxRatePct { get; init; }
        public long LineUnitCost { get; init; }
        public long? LineUnitTax { get; init; }
        public long? LineUnitCostInclTax { get; init; }
    }
    private sealed class VendorReturnPostedRow { public long Id { get; init; } public string DocumentNumber { get; init; } = ""; public long CreditTotal { get; init; } public long CostTotal { get; init; } }
    private sealed class VendorReturnHistoryRow { public long Id { get; init; } public string DocumentNumber { get; init; } = ""; public DateOnly Date { get; init; } public long CreditTotal { get; init; } public long CostTotal { get; init; } public DateOnly? CancellationDate { get; init; } public VendorReturnHistoryItem ToModel() => new(Id, DocumentNumber, Date, Money.FromScaled(CreditTotal), Money.FromScaled(CostTotal), CancellationDate is not null, CancellationDate); }
    private sealed class InternalLotRow { public long LotId { get; init; } public long ProductId { get; init; } public long OnHand { get; init; } public long UnitCost { get; init; } public string LotCode { get; init; } = ""; public string? BatchNumber { get; init; } public DateOnly? ExpiryDate { get; init; } public string Sku { get; init; } = ""; public string ProductName { get; init; } = ""; public string Uom { get; init; } = ""; public bool AllowFraction { get; init; } }
    private sealed class PurchaseListRow { public long Id { get; init; } public string? DocumentNumber { get; init; } public string VendorName { get; init; } = ""; public string? VendorInvoiceNumber { get; init; } public DateOnly ReceivedDate { get; init; } public string Status { get; init; } = ""; public long GrandTotal { get; init; } public long? VendorBillTotal { get; init; } public PurchaseListItem ToModel() => new(Id, DocumentNumber, VendorName, VendorInvoiceNumber, ReceivedDate, Status, Money.FromScaled(GrandTotal), VendorBillTotal is null ? null : Money.FromScaled(VendorBillTotal.Value)); }
    private sealed class PurchaseInvoiceHeaderRow
    {
        public long Id { get; init; }
        public string? DocumentNumber { get; init; }
        public string VendorName { get; init; } = "";
        public string? VendorInvoiceNumber { get; init; }
        public DateOnly? VendorInvoiceDate { get; init; }
        public DateOnly ReceivedDate { get; init; }
        public string Status { get; init; } = "";
        public string? TaxMode { get; init; }
        public string? Notes { get; init; }
        public long Subtotal { get; init; }
        public long Discount { get; init; }
        public long Tax { get; init; }
        public long OtherCharges { get; init; }
        public long RoundOff { get; init; }
        public long GrandTotal { get; init; }
        public long? VendorBillTotal { get; init; }
    }
    private sealed class PurchaseInvoiceLineRow
    {
        public int LineNumber { get; init; }
        public string LineKind { get; init; } = "PRODUCT";
        public string? ExpenseCategoryName { get; init; }
        public string Sku { get; init; } = "";
        public string ProductName { get; init; } = "";
        public string? BatchNumber { get; init; }
        public DateOnly? ManufacturingDate { get; init; }
        public DateOnly? ExpiryDate { get; init; }
        public long Quantity { get; init; }
        public long FreeQuantity { get; init; }
        public long Rate { get; init; }
        public long Discount { get; init; }
        public long TaxRate { get; init; }
        public long TaxAmount { get; init; }
        public long LineTotal { get; init; }
        public long LandedUnitCost { get; init; }
        public long? Mrp { get; init; }
        public long SellingPrice { get; init; }
        public string? TaxLabel { get; init; }
        public long? UnitTax { get; init; }
        public long? UnitCostInclTax { get; init; }
        public string? SnapshotProvenance { get; init; }
        public string? LegacyUnknownFields { get; init; }
        public PurchaseInvoiceLine ToModel() => new(LineNumber, Sku, ProductName, BatchNumber,
            ManufacturingDate, ExpiryDate, GroceryErp.Core.Numerics.Quantity.FromScaled(Quantity),
            GroceryErp.Core.Numerics.Quantity.FromScaled(FreeQuantity),
            Money.FromScaled(Rate), Money.FromScaled(Discount), Percent.FromScaled(TaxRate),
            Money.FromScaled(TaxAmount), Money.FromScaled(LineTotal), Money.FromScaled(LandedUnitCost),
            Mrp is null ? null : Money.FromScaled(Mrp.Value), Money.FromScaled(SellingPrice))
        {
            LineKind = LineKind,
            ExpenseCategoryName = ExpenseCategoryName,
            SnapshotProvenance = SnapshotProvenance,
            LegacyUnknownFields = LegacyUnknownFields,
            TaxLabel = TaxLabel,
            UnitTax = UnitTax is null ? null : Money.FromScaled(UnitTax.Value),
            UnitCostInclTax = UnitCostInclTax is null ? null : Money.FromScaled(UnitCostInclTax.Value),
        };
    }
}
