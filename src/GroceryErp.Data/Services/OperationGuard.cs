using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Data.Services;

public enum OpType
{
    SaleComplete, SaleVoid, SaleReturn, PurchasePost, PurchaseCancel, VendorPayment, VendorReturn,
    Damage, DamageBulk, DamageCorrection, Expense, SalaryRun, SalaryCorrection, StockCountPost, StockAdjustment, PriceChange,
    AssetDispose, OpeningStockImport, ProductImport,
    DirectPurchaseLot, DirectOriginLot, DirectSaleComplete, DirectSaleVoid,
    DirectCustomerReturn, DirectDamage, DirectInternalUse, DirectVendorReturn,
    DirectVendorReturnCancel, DirectCountReview,
    DirectPurchaseVoid,
}

public sealed class OperationRef
{
    public string? RefType { get; init; }
    public long? RefId { get; init; }
}

public static class OperationGuard
{
    public static OperationRef? Claim(DbTx transaction, string operationId, OpType type)
    {
        ValidateId(operationId);
        int inserted = transaction.Connection.Execute("""
            INSERT INTO operation(op_id, op_type) VALUES (@operationId, @type)
            ON CONFLICT(op_id) DO NOTHING
            """, new { operationId, type = ToCode(type) }, transaction.Transaction);
        if (inserted == 1) return null;

        var existing = transaction.Connection.QuerySingle<OperationRow>("""
            SELECT op_type AS OpType, ref_type AS RefType, ref_id AS RefId
            FROM operation WHERE op_id = @operationId
            """, new { operationId }, transaction.Transaction);
        if (!string.Equals(existing.OpType, ToCode(type), StringComparison.Ordinal))
            throw new DomainException(ErrorCode.Validation, "Operation ID was reused for a different command.");
        return new() { RefType = existing.RefType, RefId = existing.RefId };
    }

    public static void Complete(DbTx transaction, string operationId, string referenceType, long referenceId)
    {
        int changed = transaction.Connection.Execute("""
            UPDATE operation SET ref_type = @referenceType, ref_id = @referenceId
            WHERE op_id = @operationId
            """, new { operationId, referenceType, referenceId }, transaction.Transaction);
        if (changed != 1) throw new InvalidOperationException("The operation was not claimed in this transaction.");
    }

    public static int Purge(DbTx transaction, DateTime cutoff) => transaction.Connection.Execute(
        "DELETE FROM operation WHERE created_at < @cutoff",
        new { cutoff = cutoff.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) },
        transaction.Transaction);

    public static string NewId() => Guid.NewGuid().ToString("N");

    private static void ValidateId(string id)
    {
        if (id.Length != 32 || id.Any(static character => !char.IsAsciiHexDigitLower(character)))
            throw new DomainException(ErrorCode.Validation, "Operation ID must be 32 lowercase hexadecimal characters.");
    }

    private static string ToCode(OpType type)
    {
        string name = type.ToString();
        return string.Concat(name.SelectMany(static (character, index) =>
            index > 0 && char.IsUpper(character) ? new[] { '_', character } : new[] { character })).ToUpperInvariant();
    }

    private sealed class OperationRow
    {
        public string OpType { get; init; } = "";
        public string? RefType { get; init; }
        public long? RefId { get; init; }
    }
}
