using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;

namespace GroceryErp.Inventory;

public sealed record DirectStockMismatch(long LotId, long Actual, long Expected);
public sealed record DirectCountReview(long LotId, long ExpectedVersion, long OldQuantity,
    long CountedQuantity, DateOnly BusinessDate, string Reason, string OperationId,
    string DamageCategory = "OTHER");

public interface IDirectStockEngine
{
    void ReceivePurchaseLot(DbTx tx, long lotId, long expectedVersion, string operationId);
    void CreateDirectLot(DbTx tx, long lotId, long expectedVersion, string operationId);
    void CompleteSale(DbTx tx, long saleId, IReadOnlyDictionary<long, long> expectedVersions, string operationId);
    void VoidSale(DbTx tx, long saleId, IReadOnlyDictionary<long, long> expectedVersions, string operationId);
    void CustomerReturn(DbTx tx, long returnId, IReadOnlyDictionary<long, long> expectedVersions, string operationId);
    void Damage(DbTx tx, long damageId, long expectedVersion, string operationId);
    void ConsumeInternal(DbTx tx, long recordId, long expectedVersion, string operationId);
    void ReturnToVendor(DbTx tx, long returnId, IReadOnlyDictionary<long, long> expectedVersions, string operationId);
    void CancelVendorReturn(DbTx tx, long returnId, IReadOnlyDictionary<long, long> expectedVersions,
        string reason, string operationId);
    void VoidPurchase(DbTx tx, long purchaseId, IReadOnlyDictionary<long, long> expectedVersions, string operationId);
    long ReviewCount(DbTx tx, long lotId, long expectedVersion, long oldQuantity,
        long countedQuantity, DateOnly businessDate, string reason, string operationId,
        string damageCategory = "OTHER");
    IReadOnlyList<long> ReviewCountBatch(DbTx tx, IReadOnlyList<DirectCountReview> reviews);
    DirectStockMismatch? CheckLot(DbTx tx, long lotId);
    IEnumerable<DirectStockMismatch> CheckAll(DbTx tx, int pageSize = 1000);
}

// All methods run inside the caller's document transaction. They are deliberately
// unavailable on the live ledger schema; a later cutover ports every caller at once.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance methods implement the stock engine contract for future document writers.")]
public sealed class DirectStockEngine : IDirectStockEngine
{
    public void ReceivePurchaseLot(DbTx tx, long lotId, long expectedVersion, string operationId) =>
        ApplyOrigin(tx, lotId, expectedVersion, operationId, OpType.DirectPurchaseLot, "PURCHASE_LINE");

    public void CreateDirectLot(DbTx tx, long lotId, long expectedVersion, string operationId) =>
        ApplyOrigin(tx, lotId, expectedVersion, operationId, OpType.DirectOriginLot, "DIRECT");

    public void CompleteSale(DbTx tx, long saleId, IReadOnlyDictionary<long, long> expectedVersions, string operationId)
    {
        ArgumentNullException.ThrowIfNull(expectedVersions);
        if (!Begin(tx, operationId, OpType.DirectSaleComplete, "DIRECT_SALE", saleId)) return;
        string date = tx.Connection.ExecuteScalar<string?>(
            "SELECT sale_date FROM sale WHERE id=@saleId AND status='COMPLETED'", new { saleId }, tx.Transaction)
            ?? throw new DomainException(ErrorCode.Validation, "Sale must be completed in this transaction.");
        var lines = tx.Connection.Query<SourceLine>("""
            SELECT sl.lot_id AS LotId,SUM(sl.qty) AS Quantity,l.quantity_version AS Version,
                   l.expiry_date AS Expiry,l.is_active AS IsActive,l.product_id AS ProductId
            FROM sale_line sl JOIN product_lot l ON l.id=sl.lot_id
            WHERE sl.sale_id=@saleId
            GROUP BY sl.lot_id
            ORDER BY l.expiry_date IS NULL,l.expiry_date,l.received_date,l.id,sl.id
            """, new { saleId }, tx.Transaction).ToList();
        if (lines.Count == 0) throw new DomainException(ErrorCode.EmptyBill);
        foreach (SourceLine line in lines)
        {
            if (line.IsActive != 1 || (line.Expiry is not null && string.CompareOrdinal(line.Expiry, date) < 0))
                throw new DomainException(ErrorCode.ExpiredLot, line.LotId);
            long? first = tx.Connection.ExecuteScalar<long?>("""
                SELECT id FROM product_lot
                WHERE product_id=@ProductId AND is_active=1 AND qty_on_hand>0
                  AND (expiry_date IS NULL OR expiry_date>=@date)
                ORDER BY expiry_date IS NULL,expiry_date,received_date,id LIMIT 1
                """, new { line.ProductId, date }, tx.Transaction);
            if (first != line.LotId) throw new DomainException(ErrorCode.Validation, "Sale allocation is not FEFO.");
            if (!expectedVersions.TryGetValue(line.LotId, out long version))
                throw new DomainException(ErrorCode.StockCountReviewChanged, line.LotId);
            Change(tx, line.LotId, -line.Quantity, version, requireSaleable: true, date);
        }
        CheckAffected(tx, lines.Select(static line => line.LotId));
        End(tx, operationId, "DIRECT_SALE", saleId);
    }

    public void VoidSale(DbTx tx, long saleId, IReadOnlyDictionary<long, long> expectedVersions, string operationId)
    {
        if (!Begin(tx, operationId, OpType.DirectSaleVoid, "DIRECT_SALE_VOID", saleId)) return;
        Require(tx, "SELECT COUNT(*) FROM sale WHERE id=@id AND status='VOID'", saleId);
        if (tx.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sale_return WHERE sale_id=@saleId", new { saleId }, tx.Transaction) != 0)
            throw new DomainException(ErrorCode.VoidNotAllowed, saleId);
        var lines = Lines(tx, "SELECT lot_id AS LotId,SUM(qty) AS Quantity FROM sale_line WHERE sale_id=@id GROUP BY lot_id", saleId).ToList();
        foreach (SourceLine line in lines)
            Change(tx, line.LotId, line.Quantity, VersionOf(expectedVersions, line.LotId));
        CheckAffected(tx, lines.Select(static line => line.LotId));
        End(tx, operationId, "DIRECT_SALE_VOID", saleId);
    }

    public void CustomerReturn(DbTx tx, long returnId, IReadOnlyDictionary<long, long> expectedVersions, string operationId)
    {
        if (!Begin(tx, operationId, OpType.DirectCustomerReturn, "DIRECT_RETURN", returnId)) return;
        Require(tx, """
            SELECT COUNT(*) FROM sale_return r JOIN sale s ON s.id=r.sale_id
            WHERE r.id=@id AND s.status='COMPLETED'
            """, returnId);
        if (tx.Connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
            WHERE rl.sale_return_id=@returnId AND
              (SELECT COALESCE(SUM(all_rl.qty),0) FROM sale_return_line all_rl
               WHERE all_rl.sale_line_id=sl.id)>sl.qty
            """, new { returnId }, tx.Transaction) != 0)
            throw new DomainException(ErrorCode.ReturnQtyExceedsSold, returnId);
        var lines = Lines(tx, """
            SELECT sl.lot_id AS LotId,SUM(rl.qty) AS Quantity
            FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
            WHERE rl.sale_return_id=@id
            GROUP BY sl.lot_id
            """, returnId).ToList();
        foreach (SourceLine line in lines)
            Change(tx, line.LotId, line.Quantity, VersionOf(expectedVersions, line.LotId));
        CheckAffected(tx, lines.Select(static line => line.LotId));
        End(tx, operationId, "DIRECT_RETURN", returnId);
    }

    public void Damage(DbTx tx, long damageId, long expectedVersion, string operationId)
    {
        if (!Begin(tx, operationId, OpType.DirectDamage, "DIRECT_DAMAGE", damageId)) return;
        SourceLine row = One(tx, "SELECT lot_id AS LotId,qty AS Quantity FROM damage WHERE id=@id", damageId);
        Change(tx, row.LotId, -row.Quantity, expectedVersion);
        CheckAffected(tx, [row.LotId]);
        End(tx, operationId, "DIRECT_DAMAGE", damageId);
    }

    public void ConsumeInternal(DbTx tx, long recordId, long expectedVersion, string operationId)
    {
        if (!Begin(tx, operationId, OpType.DirectInternalUse, "DIRECT_INTERNAL", recordId)) return;
        SourceLine row = One(tx, """
            SELECT i.lot_id_snapshot AS LotId,i.quantity AS Quantity
            FROM internal_use_record i JOIN product_lot l ON l.id=i.lot_id_snapshot
            JOIN product p ON p.id=l.product_id
            WHERE i.id=@id AND i.product_id_snapshot=l.product_id AND p.product_class='INTERNAL'
            """, recordId);
        Change(tx, row.LotId, -row.Quantity, expectedVersion);
        CheckAffected(tx, [row.LotId]);
        End(tx, operationId, "DIRECT_INTERNAL", recordId);
    }

    public void ReturnToVendor(DbTx tx, long returnId, IReadOnlyDictionary<long, long> expectedVersions, string operationId)
    {
        if (!Begin(tx, operationId, OpType.DirectVendorReturn, "DIRECT_VENDOR_RETURN", returnId)) return;
        Require(tx, """
            SELECT COUNT(*) FROM vendor_return vr JOIN purchase p ON p.id=vr.purchase_id
            WHERE vr.id=@id AND p.status='POSTED'
            """, returnId);
        if (tx.Connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM vendor_return_line vl JOIN vendor_return vr ON vr.id=vl.vendor_return_id
            JOIN product_lot l ON l.id=vl.lot_id JOIN purchase_line pl ON pl.id=l.purchase_line_id
            WHERE vr.id=@returnId AND (pl.purchase_id<>vr.purchase_id OR vl.product_id<>l.product_id)
            """, new { returnId }, tx.Transaction) != 0)
            throw new DomainException(ErrorCode.LotProductMismatch, returnId);
        var lines = Lines(tx,
            "SELECT lot_id AS LotId,SUM(qty) AS Quantity FROM vendor_return_line WHERE vendor_return_id=@id GROUP BY lot_id", returnId).ToList();
        foreach (SourceLine line in lines)
            Change(tx, line.LotId, -line.Quantity, VersionOf(expectedVersions, line.LotId));
        CheckAffected(tx, lines.Select(static line => line.LotId));
        End(tx, operationId, "DIRECT_VENDOR_RETURN", returnId);
    }

    public void CancelVendorReturn(DbTx tx, long returnId, IReadOnlyDictionary<long, long> expectedVersions,
        string reason, string operationId)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCode.Validation, "Cancellation reason is required.");
        if (!Begin(tx, operationId, OpType.DirectVendorReturnCancel, "DIRECT_VENDOR_CANCEL", returnId)) return;
        Require(tx, "SELECT COUNT(*) FROM vendor_return WHERE id=@id", returnId);
        int inserted = tx.Connection.Execute("""
            INSERT INTO vendor_return_cancellation(vendor_return_id,operation_id,reason)
            VALUES (@returnId,@operationId,@reason) ON CONFLICT(vendor_return_id) DO NOTHING
            """, new { returnId, operationId, reason }, tx.Transaction);
        if (inserted != 1) throw new DomainException(ErrorCode.CancelNotAllowed, returnId);
        var lines = Lines(tx,
            "SELECT lot_id AS LotId,SUM(qty) AS Quantity FROM vendor_return_line WHERE vendor_return_id=@id GROUP BY lot_id", returnId).ToList();
        foreach (SourceLine line in lines)
            Change(tx, line.LotId, line.Quantity, VersionOf(expectedVersions, line.LotId));
        CheckAffected(tx, lines.Select(static line => line.LotId));
        End(tx, operationId, "DIRECT_VENDOR_CANCEL", returnId);
    }

    public void VoidPurchase(DbTx tx, long purchaseId, IReadOnlyDictionary<long, long> expectedVersions, string operationId)
    {
        if (!Begin(tx, operationId, OpType.DirectPurchaseVoid, "DIRECT_PURCHASE_VOID", purchaseId)) return;
        Require(tx, "SELECT COUNT(*) FROM purchase WHERE id=@id AND status='CANCELLED'", purchaseId);
        var lots = tx.Connection.Query<SourceLine>("""
            SELECT l.id AS LotId,l.qty_received AS Quantity,l.qty_on_hand AS OnHand
            FROM product_lot l JOIN purchase_line pl ON pl.id=l.purchase_line_id
            WHERE pl.purchase_id=@purchaseId
            """, new { purchaseId }, tx.Transaction).ToList();
        if (lots.Count == 0 && tx.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM purchase_line WHERE purchase_id=@purchaseId AND line_kind='EXPENSE'",
                new { purchaseId }, tx.Transaction) == 0)
            throw new DomainException(ErrorCode.NotFound, purchaseId);
        foreach (SourceLine lot in lots)
        {
            long dependencies = tx.Connection.ExecuteScalar<long>("""
                SELECT (SELECT COUNT(*) FROM sale_line WHERE lot_id=@lotId)
                     + (SELECT COUNT(*) FROM damage WHERE lot_id=@lotId)
                     + (SELECT COUNT(*) FROM vendor_return_line WHERE lot_id=@lotId)
                     + (SELECT COUNT(*) FROM internal_use_record WHERE lot_id_snapshot=@lotId)
                     + (SELECT COUNT(*) FROM physical_count_result WHERE source_lot_id=@lotId)
                """, new { lotId = lot.LotId }, tx.Transaction);
            if (dependencies != 0 || lot.OnHand != lot.Quantity)
                throw new DomainException(ErrorCode.VoidNotAllowed, purchaseId);
        }
        foreach (SourceLine lot in lots)
            Change(tx, lot.LotId, -lot.Quantity, VersionOf(expectedVersions, lot.LotId));
        CheckAffected(tx, lots.Select(static lot => lot.LotId));
        End(tx, operationId, "DIRECT_PURCHASE_VOID", purchaseId);
    }

    // One reviewed physical count is one short transaction. A surplus starts a
    // distinct direct-origin lot; a deficit is a categorized damage document.
    public long ReviewCount(DbTx tx, long lotId, long expectedVersion, long oldQuantity,
        long countedQuantity, DateOnly businessDate, string reason, string operationId,
        string damageCategory = "OTHER")
    {
        if (countedQuantity < 0 || oldQuantity < 0 || string.IsNullOrWhiteSpace(reason) ||
            damageCategory is not ("EXPIRED" or "BROKEN" or "SPOILED" or "PEST" or "LOST" or "OTHER"))
            throw new DomainException(ErrorCode.Validation, "A count and reason are required.");
        if (tx.Connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='direct_stock_sandbox'",
            transaction: tx.Transaction) != 1)
            throw new InvalidOperationException("Direct stock is inactive on this database.");
        OperationRef? prior = OperationGuard.Claim(tx, operationId, OpType.DirectCountReview);
        if (prior is not null)
        {
            if (prior.RefType != "DIRECT_COUNT")
                throw new DomainException(ErrorCode.Validation, "Operation ID belongs to another document.");
            CountResult replay = tx.Connection.QuerySingleOrDefault<CountResult>("""
                SELECT id AS Id,COALESCE(source_id,0) AS SourceId,source_kind AS SourceKind,
                       source_lot_id AS SourceLotId,old_quantity AS OldQuantity,
                       counted_quantity AS CountedQuantity,reason AS Reason
                FROM physical_count_result WHERE id=@id AND operation_id=@operationId
                """, new { id = prior.RefId, operationId }, tx.Transaction)
                ?? throw new DomainException(ErrorCode.Validation, "Count operation has no result.");
            ValidateCountReplay(tx, replay, lotId, oldQuantity, countedQuantity, reason, damageCategory);
            return replay.SourceId;
        }
        var existing = tx.Connection.QuerySingleOrDefault<CountResult>("""
            SELECT id AS Id,COALESCE(source_id,0) AS SourceId,source_kind AS SourceKind,
                   source_lot_id AS SourceLotId,old_quantity AS OldQuantity,
                   counted_quantity AS CountedQuantity,reason AS Reason
            FROM physical_count_result WHERE operation_id=@operationId
            """, new { operationId }, tx.Transaction);
        if (existing is not null)
        {
            ValidateCountReplay(tx, existing, lotId, oldQuantity, countedQuantity, reason, damageCategory);
            OperationGuard.Complete(tx, operationId, "DIRECT_COUNT", existing.Id);
            return existing.SourceId;
        }
        var lot = tx.Connection.QuerySingleOrDefault<CountLot>("""
            SELECT id AS LotId,product_id AS ProductId,qty_on_hand AS OnHand,
                   quantity_version AS Version,unit_cost AS Cost,selling_price AS Price,
                   mrp AS Mrp,expiry_date AS Expiry,batch_no AS Batch
            FROM product_lot WHERE id=@lotId
            """, new { lotId }, tx.Transaction) ?? throw new DomainException(ErrorCode.NotFound, lotId);
        if (lot.Version != expectedVersion || lot.OnHand != oldQuantity)
            throw new DomainException(ErrorCode.StockCountReviewChanged, lotId);
        if (countedQuantity % 1000 != 0 && tx.Connection.ExecuteScalar<long>("""
            SELECT u.allow_fraction FROM product_lot l JOIN product p ON p.id=l.product_id
            JOIN uom u ON u.id=p.uom_id WHERE l.id=@lotId
            """, new { lotId }, tx.Transaction) == 0)
            throw new DomainException(ErrorCode.FractionalQtyNotAllowed, lotId);
        long resultId = tx.Connection.ExecuteScalar<long>("""
            INSERT INTO physical_count_result(operation_id,source_lot_id,old_quantity,
              counted_quantity,source_kind,reason)
            VALUES (@operationId,@lotId,@oldQuantity,@countedQuantity,'MATCH',@reason)
            RETURNING id
            """, new { operationId, lotId, oldQuantity, countedQuantity, reason }, tx.Transaction);
        string kind = "MATCH";
        long? sourceId = null;
        long delta = countedQuantity - oldQuantity;
        if (delta < 0)
        {
            sourceId = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO damage(damage_date,product_id,lot_id,qty,unit_cost,cost_value,reason,notes)
                VALUES (@date,@productId,@lotId,@quantity,@cost,
                        CAST(ROUND(@quantity*@cost/1000.0) AS INTEGER),@damageCategory,@reason)
                RETURNING id
                """, new { date = businessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), productId = lot.ProductId,
                    lotId, quantity = -delta, cost = lot.Cost, reason, damageCategory }, tx.Transaction);
            Change(tx, lotId, delta, expectedVersion);
            CheckAffected(tx, [lotId]);
            kind = "DAMAGE";
        }
        else if (delta > 0)
        {
            sourceId = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,
                  unit_cost,mrp,selling_price,qty_received,direct_reason)
                VALUES (@productId,@code,@batch,@date,@expiry,@cost,@mrp,@price,@delta,@reason)
                RETURNING id
                """, new { productId = lot.ProductId, code = "RC-" + operationId, batch = lot.Batch,
                    date = businessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), expiry = lot.Expiry, cost = lot.Cost,
                    mrp = lot.Mrp, price = lot.Price, delta, reason }, tx.Transaction);
            Change(tx, sourceId.Value, delta, 1);
            CheckAffected(tx, [lotId, sourceId.Value]);
            kind = "DIRECT_ORIGIN";
        }
        else CheckAffected(tx, [lotId]);
        tx.Connection.Execute("""
            UPDATE physical_count_result SET source_kind=@kind,source_id=@sourceId WHERE id=@resultId
            """, new { kind, sourceId, resultId }, tx.Transaction);
        OperationGuard.Complete(tx, operationId, "DIRECT_COUNT", resultId);
        return sourceId ?? 0;
    }

    public IReadOnlyList<long> ReviewCountBatch(DbTx tx, IReadOnlyList<DirectCountReview> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        if (reviews.Count is < 1 or > 200 || reviews.Select(static review => review.LotId).Distinct().Count() != reviews.Count)
            throw new DomainException(ErrorCode.Validation, "Select 1 to 200 distinct lots per count batch.");
        var sources = new List<long>(reviews.Count);
        foreach (DirectCountReview review in reviews)
            sources.Add(ReviewCount(tx, review.LotId, review.ExpectedVersion, review.OldQuantity,
                review.CountedQuantity, review.BusinessDate, review.Reason, review.OperationId,
                review.DamageCategory));
        return sources;
    }

    public DirectStockMismatch? CheckLot(DbTx tx, long lotId)
    {
        var row = tx.Connection.QuerySingleOrDefault<BalanceRow>(BalanceSql + " WHERE l.id=@lotId", new { lotId }, tx.Transaction);
        if (row is null) throw new DomainException(ErrorCode.NotFound, lotId);
        return row.Actual == row.Expected ? null : new(row.LotId, row.Actual, row.Expected);
    }

    public IEnumerable<DirectStockMismatch> CheckAll(DbTx tx, int pageSize = 1000)
    {
        if (pageSize is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        long after = 0;
        while (true)
        {
            var rows = tx.Connection.Query<BalanceRow>("""
                SELECT l.id AS LotId,l.qty_on_hand AS Actual,
                  COALESCE(CASE l.origin_kind
                    WHEN 'PURCHASE_LINE' THEN CASE WHEN p.status='POSTED' THEN pl.qty+pl.free_qty ELSE 0 END
                    WHEN 'DIRECT' THEN d.quantity ELSE l.qty_received END,0) AS Expected
                FROM product_lot l
                LEFT JOIN purchase_line pl ON pl.id=l.origin_ref_id AND l.origin_kind='PURCHASE_LINE'
                LEFT JOIN purchase p ON p.id=pl.purchase_id
                LEFT JOIN direct_stock_origin d ON d.id=l.origin_ref_id AND l.origin_kind='DIRECT'
                WHERE l.id>@after ORDER BY l.id LIMIT @pageSize
                """, new { after, pageSize }, tx.Transaction).ToList();
            if (rows.Count == 0) yield break;
            long last = rows[^1].LotId;
            var balances = rows.ToDictionary(static row => row.LotId);
            foreach (string sql in PageDeltaSql)
                foreach (DeltaRow delta in tx.Connection.Query<DeltaRow>(sql, new { after, last }, tx.Transaction))
                    balances[delta.LotId].Expected += delta.Delta;
            foreach (BalanceRow row in rows)
                if (row.Actual != row.Expected) yield return new(row.LotId, row.Actual, row.Expected);
            after = last;
        }
    }

    private void ApplyOrigin(DbTx tx, long lotId, long expectedVersion, string operationId, OpType type, string kind)
    {
        if (!Begin(tx, operationId, type, "DIRECT_ORIGIN", lotId)) return;
        var row = tx.Connection.QuerySingleOrDefault<OriginRow>("""
            SELECT l.qty_received AS Quantity,l.qty_on_hand AS OnHand,l.origin_kind AS Kind,
                   l.origin_ref_id AS RefId,l.purchase_line_id AS PurchaseLineId,
                   l.received_date AS Received,l.expiry_date AS Expiry
            FROM product_lot l WHERE l.id=@lotId
            """, new { lotId }, tx.Transaction) ?? throw new DomainException(ErrorCode.NotFound, lotId);
        if (row.Kind != kind || row.OnHand != 0 || row.Quantity <= 0 || row.RefId is null)
            throw new DomainException(ErrorCode.Validation, "Invalid or already received lot.");
        if (row.Expiry is not null && string.CompareOrdinal(row.Expiry, row.Received) < 0)
            throw new DomainException(ErrorCode.InvalidDate, lotId);
        if (kind == "PURCHASE_LINE")
            Require(tx, """
                SELECT COUNT(*) FROM purchase_line pl JOIN purchase p ON p.id=pl.purchase_id
                WHERE pl.id=@id AND p.status='POSTED'
                """, row.RefId.Value);
        else
            Require(tx, "SELECT COUNT(*) FROM direct_stock_origin WHERE id=@id AND quantity>0", row.RefId.Value);
        long sourceQuantity = kind == "PURCHASE_LINE"
            ? tx.Connection.ExecuteScalar<long>(
                "SELECT qty+free_qty FROM purchase_line WHERE id=@id", new { id = row.RefId.Value }, tx.Transaction)
            : tx.Connection.ExecuteScalar<long>(
                "SELECT quantity FROM direct_stock_origin WHERE id=@id", new { id = row.RefId.Value }, tx.Transaction);
        if (sourceQuantity != row.Quantity)
            throw new DomainException(ErrorCode.Validation, "Lot quantity differs from its source document.");
        Change(tx, lotId, row.Quantity, expectedVersion);
        CheckAffected(tx, [lotId]);
        End(tx, operationId, "DIRECT_ORIGIN", lotId);
    }

    private static bool Begin(DbTx tx, string opId, OpType type, string refType, long refId)
    {
        if (refId <= 0) throw new DomainException(ErrorCode.Validation, "A positive document ID is required.");
        if (tx.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='direct_stock_sandbox'",
                transaction: tx.Transaction) != 1)
            throw new InvalidOperationException("Direct stock is inactive on this database.");
        OperationRef? prior = OperationGuard.Claim(tx, opId, type);
        if (prior is not null)
        {
            if (prior.RefType != refType || prior.RefId != refId)
                throw new DomainException(ErrorCode.Validation, "Operation ID belongs to another document.");
            return false;
        }
        string? claimedBy = tx.Connection.ExecuteScalar<string?>("""
            SELECT operation_id FROM direct_stock_claim
            WHERE source_kind=@refType AND source_id=@refId
            """, new { refType, refId }, tx.Transaction);
        if (claimedBy is not null)
        {
            if (claimedBy != opId)
                throw new DomainException(ErrorCode.Validation, "Document stock was already applied.");
            OperationGuard.Complete(tx, opId, refType, refId);
            return false;
        }
        return true;
    }

    private static void End(DbTx tx, string operationId, string type, long id)
    {
        tx.Connection.Execute("""
            INSERT INTO direct_stock_claim(source_kind,source_id,operation_id)
            VALUES (@type,@id,@operationId)
            """, new { type, id, operationId }, tx.Transaction);
        OperationGuard.Complete(tx, operationId, type, id);
    }

    private static void Change(DbTx tx, long lotId, long delta, long? version,
        bool requireSaleable = false, string? date = null)
    {
        if (delta == 0) throw new DomainException(ErrorCode.Validation, "Stock quantity must be nonzero.");
        int updated = tx.Connection.Execute("""
            UPDATE product_lot SET qty_on_hand=qty_on_hand+@delta
            WHERE id=@lotId AND qty_on_hand+@delta>=0
              AND (@version IS NULL OR quantity_version=@version)
              AND (@requireSaleable=0 OR (is_active=1 AND (expiry_date IS NULL OR expiry_date>=@date)))
            """, new { lotId, delta, version, requireSaleable, date }, tx.Transaction);
        if (updated != 1) throw new DomainException(version is not null ? ErrorCode.StockCountReviewChanged :
            delta < 0 ? ErrorCode.InsufficientStock : ErrorCode.Validation, lotId);
    }

    private static long VersionOf(IReadOnlyDictionary<long, long> versions, long lotId)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (!versions.TryGetValue(lotId, out long version) || version <= 0)
            throw new DomainException(ErrorCode.StockCountReviewChanged, lotId);
        return version;
    }

    private void CheckAffected(DbTx tx, IEnumerable<long> lotIds)
    {
        foreach (long lotId in lotIds.Distinct())
            if (CheckLot(tx, lotId) is { } mismatch)
                throw new DomainException(ErrorCode.Validation,
                    $"Lot {lotId} source balance {mismatch.Expected} differs from {mismatch.Actual}.");
    }

    private static void ValidateCountReplay(DbTx tx, CountResult result, long lotId, long oldQuantity,
        long countedQuantity, string reason, string damageCategory)
    {
        if (result.SourceLotId != lotId || result.OldQuantity != oldQuantity ||
            result.CountedQuantity != countedQuantity || result.Reason != reason)
            throw new DomainException(ErrorCode.Validation, "Operation ID belongs to another count.");
        if (result.SourceKind == "DAMAGE" && tx.Connection.ExecuteScalar<string>(
                "SELECT reason FROM damage WHERE id=@id", new { id = result.SourceId }, tx.Transaction)
            != damageCategory)
            throw new DomainException(ErrorCode.Validation, "Operation ID belongs to another damage category.");
    }

    private static void Require(DbTx tx, string sql, long id)
    {
        if (tx.Connection.ExecuteScalar<long>(sql, new { id }, tx.Transaction) != 1)
            throw new DomainException(ErrorCode.NotFound, id);
    }

    private static SourceLine One(DbTx tx, string sql, long id) =>
        tx.Connection.QuerySingleOrDefault<SourceLine>(sql, new { id }, tx.Transaction)
        ?? throw new DomainException(ErrorCode.NotFound, id);

    private static IEnumerable<SourceLine> Lines(DbTx tx, string sql, long id) =>
        tx.Connection.Query<SourceLine>(sql, new { id }, tx.Transaction);

    private const string BalanceSql = """
        SELECT l.id AS LotId,l.qty_on_hand AS Actual,
          COALESCE(CASE l.origin_kind
            WHEN 'PURCHASE_LINE' THEN (SELECT CASE WHEN p.status='POSTED' THEN pl.qty+pl.free_qty ELSE 0 END
              FROM purchase_line pl JOIN purchase p ON p.id=pl.purchase_id WHERE pl.id=l.origin_ref_id)
            WHEN 'DIRECT' THEN (SELECT quantity FROM direct_stock_origin WHERE id=l.origin_ref_id)
            ELSE l.qty_received END,0)
          - COALESCE((SELECT SUM(sl.qty) FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
              WHERE sl.lot_id=l.id AND s.status='COMPLETED'),0)
          + COALESCE((SELECT SUM(rl.qty) FROM sale_return_line rl JOIN sale_line sl ON sl.id=rl.sale_line_id
              WHERE sl.lot_id=l.id),0)
          - COALESCE((SELECT SUM(d.qty) FROM damage d WHERE d.lot_id=l.id),0)
          - COALESCE((SELECT SUM(vl.qty) FROM vendor_return_line vl
              LEFT JOIN vendor_return_cancellation c ON c.vendor_return_id=vl.vendor_return_id
              WHERE vl.lot_id=l.id AND c.vendor_return_id IS NULL),0)
          - COALESCE((SELECT SUM(i.quantity) FROM internal_use_record i WHERE i.lot_id_snapshot=l.id),0)
          + COALESCE((SELECT SUM(c.quantity_delta) FROM legacy_stock_correction c WHERE c.lot_id=l.id),0)
          AS Expected
        FROM product_lot l
        """;

    private static readonly string[] PageDeltaSql =
    [
        """
        SELECT sl.lot_id AS LotId,-SUM(sl.qty) AS Delta
        FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
        WHERE sl.lot_id>@after AND sl.lot_id<=@last AND s.status='COMPLETED'
        GROUP BY sl.lot_id
        """,
        """
        SELECT sl.lot_id AS LotId,SUM(rl.qty) AS Delta
        FROM sale_line sl JOIN sale_return_line rl ON rl.sale_line_id=sl.id
        WHERE sl.lot_id>@after AND sl.lot_id<=@last GROUP BY sl.lot_id
        """,
        """
        SELECT lot_id AS LotId,-SUM(qty) AS Delta FROM damage
        WHERE lot_id>@after AND lot_id<=@last GROUP BY lot_id
        """,
        """
        SELECT vl.lot_id AS LotId,-SUM(vl.qty) AS Delta
        FROM vendor_return_line vl
        LEFT JOIN vendor_return_cancellation c ON c.vendor_return_id=vl.vendor_return_id
        WHERE vl.lot_id>@after AND vl.lot_id<=@last AND c.vendor_return_id IS NULL
        GROUP BY vl.lot_id
        """,
        """
        SELECT lot_id_snapshot AS LotId,-SUM(quantity) AS Delta FROM internal_use_record
        WHERE lot_id_snapshot>@after AND lot_id_snapshot<=@last GROUP BY lot_id_snapshot
        """,
        """
        SELECT lot_id AS LotId,SUM(quantity_delta) AS Delta FROM legacy_stock_correction
        WHERE lot_id>@after AND lot_id<=@last GROUP BY lot_id
        """,
    ];

    private sealed class SourceLine
    {
        public long LotId { get; init; }
        public long Quantity { get; init; }
        public long Version { get; init; }
        public string? Expiry { get; init; }
        public int IsActive { get; init; }
        public long ProductId { get; init; }
        public long OnHand { get; init; }
    }
    private sealed class OriginRow
    {
        public long Quantity { get; init; }
        public long OnHand { get; init; }
        public string Kind { get; init; } = "";
        public long? RefId { get; init; }
        public long? PurchaseLineId { get; init; }
        public string Received { get; init; } = "";
        public string? Expiry { get; init; }
    }
    private sealed class BalanceRow
    {
        public long LotId { get; init; }
        public long Actual { get; init; }
        public long Expected { get; set; }
    }
    private sealed class DeltaRow
    {
        public long LotId { get; init; }
        public long Delta { get; init; }
    }
    private sealed class CountLot
    {
        public long LotId { get; init; }
        public long ProductId { get; init; }
        public long OnHand { get; init; }
        public long Version { get; init; }
        public long Cost { get; init; }
        public long Price { get; init; }
        public long? Mrp { get; init; }
        public string? Expiry { get; init; }
        public string? Batch { get; init; }
    }
    private sealed class CountResult
    {
        public long Id { get; init; }
        public long SourceId { get; init; }
        public string SourceKind { get; init; } = "";
        public long SourceLotId { get; init; }
        public long OldQuantity { get; init; }
        public long CountedQuantity { get; init; }
        public string Reason { get; init; } = "";
    }
}
