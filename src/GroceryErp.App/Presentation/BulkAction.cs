using GroceryErp.Data.Services;

namespace GroceryErp.App.Presentation;

/// <summary>One row the server resolved for a bulk action, with its eligibility before anything changes.</summary>
internal sealed record BulkCandidate(long Id, string Label, bool Eligible, string? SkipReasonKey = null);

/// <summary>The server-resolved rows of a bulk action and a localized description of its effect.</summary>
internal sealed record BulkPlan(IReadOnlyList<BulkCandidate> Candidates, string? Effect = null)
{
    public int EligibleCount => Candidates.Count(static candidate => candidate.Eligible);
}

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

    public required Func<CancellationToken, Task<BulkPlan>> Plan { get; init; }

    public required Func<IReadOnlyList<long>, string, CancellationToken, Task<IReadOnlyList<BulkItemResult>>> Run { get; init; }
}
