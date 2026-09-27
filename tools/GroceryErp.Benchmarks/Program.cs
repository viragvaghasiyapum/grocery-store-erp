using System.Diagnostics;
using System.Globalization;
using ClosedXML.Excel;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;

const int ProductCount = 20_000;
string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-benchmark-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var paths = new AppPaths(new SettingsBootstrap(root));
    var factory = new SqliteConnectionFactory(paths);
    using var database = new DbExecutor(factory);
    new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
    using var settings = new SettingsService(database, paths, new AuditLog());
    await settings.LoadAsync();
    var audit = new AuditLog();
    var importer = new ProductExcelImporter(database, audit, settings);
    var catalog = new CatalogService(database, audit, settings);
    string workbookPath = Path.Combine(root, "benchmark-products.xlsx");
    ProductExcelImporter.WriteTemplate(workbookPath);
    using (var workbook = new XLWorkbook(workbookPath))
    {
        IXLWorksheet sheet = workbook.Worksheet(1);
        IEnumerable<object[]> rows = Enumerable.Range(1, ProductCount).Select(static index => new object[]
        {
            $"P{index:D6}", $"Benchmark Product {index:D5}", string.Empty, $"alias {index:D5}",
            $"Category {index % 20:D2}", string.Empty, $"Brand {index % 50:D2}", "PCS", "1001", 5,
            Ean.Internal("21", index), 50m + index % 20, 100m, index % 5, 180, "N", string.Empty, "Y",
        });
        sheet.Cell(2, 1).InsertData(rows);
        workbook.Save();
    }

    var importTimer = Stopwatch.StartNew();
    ProductImportPreview preview = await importer.DryRunAsync(workbookPath);
    ProductImportSummary summary = await importer.ImportAsync(preview, OperationGuard.NewId());
    importTimer.Stop();

    await catalog.SearchAsync(new ProductSearchFilter("Benchmark Product 19999", Limit: 30));
    var searchSamples = new List<double>(25);
    for (int index = 0; index < 25; index++)
    {
        var searchTimer = Stopwatch.StartNew();
        await catalog.SearchAsync(new ProductSearchFilter($"alias {10_000 + index:D5}", Limit: 30));
        searchTimer.Stop();
        searchSamples.Add(searchTimer.Elapsed.TotalMilliseconds);
    }

    double averageSearch = searchSamples.Average();
    double p95Search = searchSamples.Order().ElementAt((int)Math.Ceiling(searchSamples.Count * 0.95) - 1);
    Console.WriteLine($"Products: {summary.Created.ToString(CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Import: {importTimer.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s");
    Console.WriteLine($"Search average: {averageSearch.ToString("0.000", CultureInfo.InvariantCulture)} ms");
    Console.WriteLine($"Search p95: {p95Search.ToString("0.000", CultureInfo.InvariantCulture)} ms");
    bool passed = summary.Created == ProductCount && preview.ErrorCount == 0 &&
                  importTimer.Elapsed < TimeSpan.FromMinutes(1) && p95Search < 30;
    Console.WriteLine(passed ? "PASS" : "FAIL");
    if (!passed) Environment.ExitCode = 1;
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

