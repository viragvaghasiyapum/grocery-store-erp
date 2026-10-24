using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Data.Services;

/// <summary>Outcome of one row in a bulk batch. <see cref="ReasonKey"/> names a localized resource.</summary>
public sealed record BulkItemResult(long Id, BulkItemOutcome Outcome, string? ReasonKey = null);

public enum BulkItemOutcome
{
    /// <summary>The row was changed by this batch.</summary>
    Changed,
    /// <summary>The row was rechecked inside the batch and was no longer eligible.</summary>
    NotEligible,
    /// <summary>The row no longer exists.</summary>
    Missing,
    /// <summary>The batch had already committed under the same operation ID.</summary>
    AlreadyDone,
}

/// <summary>
/// Bounded, resumable bulk work. A large selection is split into batches of at most
/// <see cref="MaxBatchSize"/> rows; each batch is one transaction that claims an operation ID
/// derived from the run's root ID and batch number, so a retried or resumed batch replays
/// instead of applying twice, and every committed batch stands on its own.
/// </summary>
public static class BulkBatch
{
    public const int MaxBatchSize = 200;

    public static string OperationId(string rootOperationId, int batchNumber)
    {
        ArgumentNullException.ThrowIfNull(rootOperationId);
        if (rootOperationId.Length != 32 || rootOperationId.Any(static c => !char.IsAsciiHexDigitLower(c)))
            throw new DomainException(ErrorCode.Validation, "Operation ID must be 32 lowercase hexadecimal characters.");
        ArgumentOutOfRangeException.ThrowIfNegative(batchNumber);
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(
            rootOperationId + ":" + batchNumber.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexStringLower(hash)[..32];
    }

    public static long[] ValidateIds(IReadOnlyCollection<long> ids, int maximum = MaxBatchSize)
    {
        ArgumentNullException.ThrowIfNull(ids);
        long[] distinct = ids.Where(static id => id > 0).Distinct().ToArray();
        if (distinct.Length == 0 || distinct.Length > maximum)
            throw new DomainException(ErrorCode.Validation,
                $"A bulk batch must contain 1 to {maximum.ToString(CultureInfo.InvariantCulture)} rows.");
        return distinct;
    }

    /// <summary>Claims the batch; returns the replay result when it already committed.</summary>
    public static IReadOnlyList<BulkItemResult>? Claim(DbTx transaction, string batchOperationId,
        IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (OperationGuard.Claim(transaction, batchOperationId, OpType.BulkBatch) is null) return null;
        return ids.Select(static id => new BulkItemResult(id, BulkItemOutcome.AlreadyDone)).ToArray();
    }

    public static IReadOnlyList<BulkItemResult> Complete(DbTx transaction, string batchOperationId,
        IReadOnlyList<BulkItemResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        OperationGuard.Complete(transaction, batchOperationId, "bulk_batch",
            results.Count(static result => result.Outcome == BulkItemOutcome.Changed));
        return results;
    }
}
