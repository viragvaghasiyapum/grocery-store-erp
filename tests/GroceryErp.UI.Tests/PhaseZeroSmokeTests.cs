using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dapper;
using GroceryErp.App.Presentation;
using GroceryErp.Catalog;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using GroceryErp.Reports;
using Shouldly;
using Xunit;

namespace GroceryErp.UI.Tests;

public sealed class PhaseZeroSmokeTests
{
    [Fact]
    public async Task A4Report_PaginatesOnStaThread()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                var output = new ReportOutputService(new TestSettings());
                IReadOnlyList<ReportRow> rows = Enumerable.Range(1, 60)
                    .Select(index => new ReportRow(new Dictionary<string, object?> { ["Name"] = $"Item {index}", ["Amount"] = index * 1000L }))
                    .ToList();
                var report = new TabularReport("A4 test", null,
                    [new("Name", "Name", ReportValueKind.Text), new("Amount", "Amount", ReportValueKind.Money)],
                    rows, new Dictionary<string, decimal> { ["Amount"] = 1830m });

                output.CreateA4(report, new DateTime(2026, 9, 27, 12, 0, 0)).Pages.Count.ShouldBe(3);
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shell_LoadsAndSwitchesToGujarati()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-ui-{Guid.NewGuid():N}");
            try
            {
                GroceryErp.App.App application = new();
                application.InitializeComponent();
                TranslationSource translations = TranslationSource.Instance;
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                DisplayFormatter formatter = new();
                ShellViewModel viewModel = new(translations, formatter);
                ShellWindow shell = new(viewModel);

                shell.Show();
                new WindowInteropHelper(shell).Handle.ShouldNotBe(IntPtr.Zero);
                ((Button)shell.FindName("LockButton")).Content.ShouldBe("Lock");

                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                translations["Demo_Title"].ShouldBe("તમારી દુકાન, વ્યવસાય માટે તૈયાર");
                viewModel.SampleAmount.ShouldBe("₹1,23,45,678.900");
                viewModel.SampleAmount.ShouldNotContain("૧");
                ((Button)shell.FindName("LockButton")).Content.ShouldBe("લૉક");

                var paths = new AppPaths(new SettingsBootstrap(root));
                var factory = new SqliteConnectionFactory(paths);
                using var database = new DbExecutor(factory);
                new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
                using var settings = new SettingsService(database, paths, new AuditLog());
                settings.LoadAsync().GetAwaiter().GetResult();
                var setup = new SetupWindow(
                    new FirstRunService(database, new PasswordHasher()),
                    paths,
                    new BackupService(paths, factory, new GroceryErp.Core.Time.SystemClock(settings), settings),
                    settings);

                setup.Show();
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                var steps = (TabControl)setup.FindName("Steps");
                ((TabItem)steps.Items[1]).Header.ShouldBe("2  દુકાન");
                ((Button)setup.FindName("BackButton")).Content.ShouldBe("પાછળ");
                steps.SelectedIndex = 4;
                steps.SelectedIndex.ShouldBe(0);
                setup.Close();

                var summary = new BackupSummary(123, 45, "S2627-000045", "2026-09-27");
                var manifest = new BackupManifest(1, "GroceryErp", "1.0.0", 5,
                    "2026-09-28T08:00:00", "manual", "TEST-PC",
                    new BackupDbInfo("grocery.db", 100, "abc", "ok"), summary);
                var restore = new RestoreWindow(new CurrentDatabaseInfo(5, summary), manifest);
                restore.Show();
                ((Button)restore.FindName("RestoreButton")).IsEnabled.ShouldBeFalse();
                var backupSummary = (TextBlock)restore.FindName("BackupSummary");
                backupSummary.Text.ShouldContain("S2627-000045");
                backupSummary.Text.ShouldContain("વસ્તુઓ");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                backupSummary.Text.ShouldContain("Products");
                restore.Close();

                var catalog = new CatalogService(database, new AuditLog(), settings);
                using (var catalogConnection = factory.Open())
                {
                    long categoryId = catalogConnection.ExecuteScalar<long>(
                        "INSERT INTO category(name,name_local,expiry_alert_days) VALUES ('Staples','મૂળભૂત વસ્તુઓ',30) RETURNING id");
                    catalogConnection.Execute("INSERT INTO category(name,name_local,parent_id) VALUES ('Salt','મીઠું',@categoryId)", new { categoryId });
                    long unitId = catalogConnection.ExecuteScalar<long>("SELECT id FROM uom WHERE code='PCS'");
                    long taxId = catalogConnection.ExecuteScalar<long>("SELECT id FROM tax_rate WHERE rate_pct=5000");
                    catalogConnection.Execute("""
                        INSERT INTO product(sku,name,name_local,search_terms,category_id,uom_id,hsn_code,
                            tax_rate_id,default_selling_price,default_mrp,reorder_level,expiry_alert_days)
                        VALUES ('SALT001','Tata Salt 1 kg','ટાટા મીઠું 1 kg','salt',@categoryId,@unitId,'1001',
                            @taxId,27500,30000,5000,7)
                        """, new { categoryId, unitId, taxId });
                }
                var products = new ProductsPage(catalog, settings,
                    new ProductExcelImporter(database, new AuditLog(), settings), loadOnShow: false);
                ((DataGrid)products.FindName("ProductsGrid")).ItemsSource = new[]
                {
                    new ProductListItem { Id = 1, Sku = "SALT001", Name = "Tata Salt 1 kg",
                        NameLocal = "ટાટા મીઠું 1 kg", Category = "Staples", StockScaled = 5000,
                        SellingPriceScaled = 27500, MrpScaled = 30000, IsActive = true },
                };
                var productsWindow = new Window { Content = products, Width = 1080, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                productsWindow.Show();
                ((DataGrid)products.FindName("ProductsGrid")).SelectedIndex = 0;
                ((Button)products.FindName("EditButton")).IsEnabled.ShouldBeTrue();
                ((Button)products.FindName("LabelsButton")).IsEnabled.ShouldBeTrue();
                ((Button)products.FindName("RestoreButton")).IsEnabled.ShouldBeFalse();
                ((TextBlock)products.FindName("StatusText")).Text = translations.Format("Catalog_Count", 1);
                Capture(productsWindow, "products.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                ((Button)products.FindName("EditButton")).Content.ShouldBe("ફેરફાર");
                Capture(productsWindow, "products.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                productsWindow.Close();

                var inventory = new InventoryService(database, new StockLedger(settings),
                    new DocNumberService(settings), new PeriodGuard(settings), new AuditLog());
                var stock = new ProductStockWindow(inventory,
                    new ProductListItem { Id = 1, Sku = "SALT001", Name = "Tata Salt 1 kg" },
                    showLedger: false, loadOnShow: false);
                stock.Show();
                ((DataGrid)stock.FindName("LotsGrid")).ItemsSource = new[]
                {
                    new LotItem(1, 1, "Tata Salt 1 kg", "L000001", "B2401",
                        new DateOnly(2026, 9, 1), new DateOnly(2027, 9, 1),
                        new Money(20), new Money(30), new Money(27.5m), new Quantity(10), new Quantity(5), true),
                };
                Capture(stock, "product-lots.en.png");
                ((DataGrid)stock.FindName("LedgerGrid")).ItemsSource = new[]
                {
                    new LedgerItem(1, new DateTime(2026, 9, 1), new DateOnly(2026, 9, 1), "OPENING",
                        new Quantity(10), new Money(20), "opening_import", null, "L000001", new Quantity(10), "Opening stock"),
                };
                ((TabControl)stock.FindName("StockTabs")).SelectedIndex = 1;
                Capture(stock, "product-ledger.en.png");
                stock.Close();

                var batch = new LabelBatchWindow(catalog, inventory, settings, loadOnShow: false);
                batch.Rows.Add(new LabelBatchRow("Tata Salt 1 kg", "ટાટા મીઠું 1 kg", "Lot L000001",
                    "2000000000015", 27.5m, 30m, "10", true));
                batch.Show();
                ((DataGrid)batch.FindName("LabelsGrid")).SelectedIndex = 0;
                Capture(batch, "label-batch.en.png");
                batch.Close();

                var unknown = new UnknownBarcodeWindow(catalog, inventory, "4006381333931", new DateOnly(2026, 9, 29));
                unknown.Show();
                Capture(unknown, "unknown-barcode.en.png");
                ((DataGrid)unknown.FindName("ProductsGrid")).ItemsSource = new[]
                {
                    new ProductListItem { Id = 1, Sku = "SALT001", Name = "Tata Salt 1 kg", Category = "Staples", StockScaled = 5000 },
                };
                ((TabControl)unknown.FindName("OptionsTabs")).SelectedIndex = 1;
                Capture(unknown, "unknown-barcode-attach.en.png");
                unknown.Close();

                var quick = new QuickProductWindow(catalog, "4006381333931", loadOnShow: false);
                ((TextBox)quick.FindName("SkuBox")).Text = "P000002";
                ((TextBox)quick.FindName("NameBox")).Text = "New soap 100 g";
                ((ComboBox)quick.FindName("CategoryBox")).ItemsSource = new[] { new MasterItem { Id = 1, Name = "Staples" } };
                ((ComboBox)quick.FindName("UnitBox")).ItemsSource = new[] { new MasterItem { Id = 1, Code = "PCS" } };
                ((ComboBox)quick.FindName("TaxBox")).ItemsSource = new[] { new TaxRateItem(1, "GST 5%", 5, true) };
                ((ComboBox)quick.FindName("CategoryBox")).SelectedIndex = 0;
                ((ComboBox)quick.FindName("UnitBox")).SelectedIndex = 0;
                ((ComboBox)quick.FindName("TaxBox")).SelectedIndex = 0;
                ((TextBox)quick.FindName("PriceBox")).Text = "27.50";
                ((TextBox)quick.FindName("MrpBox")).Text = "30.00";
                ((Button)quick.FindName("SaveButton")).IsEnabled = true;
                quick.Show();
                Capture(quick, "quick-product.en.png");
                quick.Close();

                var masters = new CatalogMastersWindow(catalog, loadOnShow: false) { Width = 720, Height = 560 };
                masters.Show();
                var categoryTree = (TreeView)masters.FindName("CategoryTree");
                categoryTree.ItemsSource = new[]
                {
                    new { Item = new MasterItem { Id = 1, Name = "Staples", NameLocal = "મૂળભૂત વસ્તુઓ" },
                        Children = new[] { new { Item = new MasterItem { Id = 2, Name = "Salt", NameLocal = "મીઠું" },
                            Children = Array.Empty<object>() } } },
                };
                categoryTree.UpdateLayout();
                ((TreeViewItem)categoryTree.ItemContainerGenerator.ContainerFromIndex(0)).IsExpanded = true;
                categoryTree.UpdateLayout();
                Capture(masters, "category-tree.en.png");
                masters.Close();

                var editor = new ProductEditorWindow(catalog) { Width = 760, Height = 720 };
                editor.Show();
                Capture(editor, "product-editor.en.png");
                editor.Close();

                var label = new ProductLabelWindow(new ProductDetails(1,
                    new ProductInput("SALT001", "Tata Salt 1 kg", "ટાટા મીઠું 1 kg", "salt",
                        1, null, 1, "1001", 1, true, false, null, true, null, 7,
                        new Quantity(5), new Money(27.5m), new Money(30m),
                        [new ProductBarcodeInput("4006381333931", Quantity.One, "EAN13", true)]), true),
                    "Test printer", "60x40");
                label.Show();
                ((System.Windows.Controls.Image)label.FindName("BarcodeImage")).Source.ShouldNotBeNull();
                Capture(label, "product-label.en.png");
                label.Close();

                shell.Close();
                application.Shutdown();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
    }

    private static void Capture(Window window, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("GROCERY_PHASE2_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        var surface = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext context = background.RenderOpen())
        {
            var area = new Rect(0, 0, width, height);
            context.DrawRectangle(Brushes.White, null, area);
        }
        bitmap.Render(background);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, fileName));
        encoder.Save(output);
    }


    private sealed class TestSettings : ISettings
    {
        public AppSettings Current { get; } = new("Test Store", 4, 0, null, 15, 30, "");
    }
}

