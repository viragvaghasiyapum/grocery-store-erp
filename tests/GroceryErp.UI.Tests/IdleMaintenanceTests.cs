using GroceryErp.Core.Errors;
using Shouldly;
using Xunit;

namespace GroceryErp.UI.Tests;

// Idle maintenance runs each step once per session: a failed weekly check no longer holds
// back due depreciation or a year's close.
public sealed class IdleMaintenanceTests
{
    [Fact]
    public async Task EveryStepRuns_WhenAnEarlierOneFails_AndTheFailuresAreReportedAfterwards()
    {
        var ran = new List<string>();
        AggregateException failure = await Should.ThrowAsync<AggregateException>(() =>
            App.App.RunMaintenanceWhenIdleAsync(static _ => true,
            [
                _ => throw new DomainException(ErrorCode.BackupInvalid, "P&L day differs"),
                _ => Record(ran, "depreciation"),
                _ => Record(ran, "annual close"),
            ], TestContext.Current.CancellationToken));

        ran.ShouldBe(["depreciation", "annual close"]);
        failure.InnerExceptions.Single().ShouldBeOfType<DomainException>().Code.ShouldBe(ErrorCode.BackupInvalid);
        App.App.LotDifferencesOnly(failure).ShouldBeNull();
    }

    [Fact]
    public async Task AnInterruptedStep_ResumesAtTheNextIdleWindow_WithoutRepeatingFinishedSteps()
    {
        var ran = new List<string>();
        bool interrupted = false;
        await App.App.RunMaintenanceWhenIdleAsync(static _ => true,
        [
            _ => Record(ran, "depreciation"),
            _ =>
            {
                if (interrupted) return Record(ran, "annual close");
                interrupted = true;
                throw new OperationCanceledException("A customer came back.");
            },
            _ => Record(ran, "weekly check"),
        ], TestContext.Current.CancellationToken);

        ran.ShouldBe(["depreciation", "annual close", "weekly check"]);
    }

    [Fact]
    public void TheLotWarning_NamesTheLots_OnlyWhenNothingElseFailed()
    {
        var lots = new DomainException(ErrorCode.LotsDifferFromDocuments, 3);

        App.App.LotDifferencesOnly(new AggregateException(new AggregateException(lots))).ShouldBe(3);
        App.App.LotDifferencesOnly(new AggregateException(lots, new IOException("Disk full"))).ShouldBeNull();
    }

    private static Task Record(List<string> ran, string step)
    {
        ran.Add(step);
        return Task.CompletedTask;
    }
}
