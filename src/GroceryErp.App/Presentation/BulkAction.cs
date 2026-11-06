using GroceryErp.Data.Services;

namespace GroceryErp.App.Presentation;

/// <summary>One row the server resolved for a bulk action, with its eligibility before anything changes.</summary>
/// <param name="Weight">The row's share of a batch's work when the action sets a <see cref="BulkAction.WeightBudget"/>.</param>
internal sealed record BulkCandidate(long Id, string Label, bool Eligible, string? SkipReasonKey = null, long Weight = 0);

/// <summary>The server-resolved rows of a bulk action and a localized description of its effect.</summary>
internal sealed record BulkPlan(IReadOnlyList<BulkCandidate> Candidates, string? Effect = null);

/// <summary>
/// Describes a reviewed bulk action: how to resolve and preview the selection on the server and
/// how to apply one bounded batch. <see cref="BulkActionWindow"/> runs it with progress,
/// cancellation between batches and a partial-completion report.
/// </summary>
internal sealed class BulkAction
{
    public required string Title { get; init; }

    public required string RunLabel { get; init; }

    /// <summary>"12 selected" or "All 1,234 matching" plus the applied filters.</summary>
    public required string SelectionSummary { get; init; }

    public bool Destructive { get; init; }

    /// <summary>
    /// Rows per transaction. Each batch holds the single writer, so a cashier's bill waits for it;
    /// 50 lot or product changes measured 40-80 ms on the full store, against 160-300 ms for 200.
    /// </summary>
    public int BatchSize { get; init; } = DefaultBatchSize;

    internal const int DefaultBatchSize = 50;

    /// <summary>
    /// When set, a batch also stops before its rows' total <see cref="BulkCandidate.Weight"/> passes
    /// this budget, for an action whose cost per row varies widely; a heavier row runs alone.
    /// </summary>
    public long? WeightBudget { get; init; }

    public required Func<CancellationToken, Task<BulkPlan>> Plan { get; init; }

    public required Func<IReadOnlyList<long>, string, CancellationToken, Task<IReadOnlyList<BulkItemResult>>> Run { get; init; }

    /// <summary>The eligible rows in review order, cut into batches by size and weight.</summary>
    internal long[][] Batches(IEnumerable<BulkCandidate> eligible)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        int size = Math.Clamp(BatchSize, 1, BulkBatch.MaxBatchSize);
        var batches = new List<long[]>();
        var current = new List<long>(size);
        long weight = 0;
        foreach (BulkCandidate candidate in eligible)
        {
            if (current.Count == size || (current.Count > 0 && weight + candidate.Weight > WeightBudget))
            {
                batches.Add([.. current]);
                current.Clear();
                weight = 0;
            }
            current.Add(candidate.Id);
            weight += candidate.Weight;
        }
        if (current.Count > 0) batches.Add([.. current]);
        return [.. batches];
    }
}
