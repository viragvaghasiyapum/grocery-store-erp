using Shouldly;
using Xunit;

namespace GroceryErp.Architecture.Tests;

public sealed class LedgerRemovalTests
{
    // Schema 36 removed the stock ledger. Only the migration steps that run before it
    // (Phase 1 snapshots, the direct-stock cutover and the removal itself) may name it.
    private static readonly string[] MigrationSteps = ["PhaseOneMigration.cs", "DirectStockCutover.cs", "LedgerRemoval.cs"];

    private static readonly string[] RemovedNames =
        ["stock_movement", "StockLedger", "StockMovementInput", "direct_stock_live", "ProductLedgerView",
         "StockSourceWindow", "ExpiryAlertWindow", "v_product_stock"];

    [Fact]
    public void ProductionCode_NoLongerUsesTheStockLedgerOrItsScreens()
    {
        string source = Path.Combine(RepositoryRoot(), "src");
        string[] offenders = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(static file => file.EndsWith(".cs", StringComparison.Ordinal) ||
                                  file.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                  !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static file => !MigrationSteps.Contains(Path.GetFileName(file)))
            .Where(static file =>
            {
                string text = File.ReadAllText(file);
                return RemovedNames.Any(name => text.Contains(name, StringComparison.Ordinal));
            })
            .Select(file => Path.GetRelativePath(source, file))
            .ToArray();

        offenders.ShouldBeEmpty();
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "GroceryErp.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
