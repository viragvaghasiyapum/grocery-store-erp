using GroceryErp.App.Presentation;
using Shouldly;
using Xunit;

namespace GroceryErp.UI.Tests;

public sealed class BulkBatchingTests
{
    [Fact]
    public void Batches_FollowTheBatchSize_WhenTheActionHasNoWeightBudget()
    {
        BulkAction action = Action(batchSize: 2, budget: null);

        action.Batches(Candidates(0, 900, 900, 900, 0)).ShouldBe([[1L, 2L], [3L, 4L], [5L]]);
    }

    // A category change of products with long sale histories stops each batch at the line budget,
    // and a product heavier than the whole budget runs alone.
    [Fact]
    public void Batches_StopAtTheWeightBudget_AndAHeavyRowRunsAlone()
    {
        BulkAction action = Action(batchSize: 50, budget: 2000);

        action.Batches(Candidates(600, 600, 600, 600, 5500, 0, 0, 1999, 1)).ShouldBe(
            [[1L, 2L, 3L], [4L], [5L], [6L, 7L, 8L, 9L]]);
    }

    [Fact]
    public void Batches_StillCapTheRowCount_WhenRowsWeighNothing()
    {
        BulkAction action = Action(batchSize: 3, budget: 2000);

        action.Batches(Candidates(0, 0, 0, 0, 0, 0, 0)).Select(static batch => batch.Length).ShouldBe([3, 3, 1]);
    }

    private static BulkCandidate[] Candidates(params long[] weights) =>
        [.. weights.Select(static (weight, index) => new BulkCandidate(index + 1, $"Row {index + 1}", true, Weight: weight))];

    private static BulkAction Action(int batchSize, long? budget) => new()
    {
        Title = "Set category",
        RunLabel = "Apply",
        SelectionSummary = "Selected",
        BatchSize = batchSize,
        WeightBudget = budget,
        Plan = static _ => Task.FromResult(new BulkPlan([])),
        Run = static (_, _, _) => Task.FromResult<IReadOnlyList<GroceryErp.Data.Services.BulkItemResult>>([]),
    };
}
