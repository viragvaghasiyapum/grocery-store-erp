using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dapper;
using GroceryErp.App.Presentation;
using GroceryErp.Assets;
using GroceryErp.Catalog;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using GroceryErp.Printing;
using GroceryErp.Purchasing;
using GroceryErp.Reports;
using GroceryErp.Sales;
using Shouldly;
using Xunit;

namespace GroceryErp.UI.Tests;

public sealed class PhaseZeroSmokeTests
{
    [Fact]
    public void EscPos_RasterSplitsIntoBoundedBands_AndTextModeUsesAscii()
    {
        byte[] bits = Enumerable.Repeat((byte)0xAA, 48 * 300).ToArray();
        byte[] command = new EscPos().Init().Raster(bits, 48, 300).Cut().ToArray();
        int offset = 2;
        int[] expectedRows = [128, 128, 44];
        foreach (int rows in expectedRows)
        {
            command[offset..(offset + 4)].ShouldBe([0x1D, 0x76, 0x30, 0x00]);
            (command[offset + 6] | command[offset + 7] << 8).ShouldBe(rows);
            offset += 8 + 48 * rows;
        }
        command[offset..].ShouldBe([0x1D, 0x56, 0x42, 0x00]);

        var receipt = new ReceiptData("Store", "Road", "9999999999", "", "REGULAR", "S-1",
            new DateTime(2026, 10, 8, 12, 0, 0), null, [], Money.Zero, Money.Zero,
            Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, [], Money.Zero,
            "gu-IN", "Thanks", false);
        byte[] text = ReceiptPrinter.RenderText(receipt, 384);
        System.Text.Encoding.ASCII.GetString(text).ShouldContain("Rs 0.000");
        text.ShouldContain((byte)0x1B);
    }

    [Fact]
    public async Task A4Report_PaginatesOnStaThread()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string name) => stages.Enqueue($"{name}:{elapsed.Elapsed.TotalMilliseconds:0}ms");
        Thread thread = new(() =>
        {
            try
            {
                Stage("started");
                var output = new ReportOutputService(new TestSettings());
                IReadOnlyList<ReportRow> rows = Enumerable.Range(1, 60)
                    .Select(index => new ReportRow(new Dictionary<string, object?> { ["Name"] = $"Item {index}", ["Amount"] = index * 1000L }))
                    .ToList();
                var report = new TabularReport("A4 test", null,
                    [new("Name", "Name", ReportValueKind.Text), new("Amount", "Amount", ReportValueKind.Money)],
                    rows, new Dictionary<string, decimal> { ["Amount"] = 1830m });

                output.CreateA4(report, new DateTime(2026, 9, 27, 12, 0, 0)).Pages.Count.ShouldBe(2);
                Stage("basic-pages");
                var oversized = report with
                {
                    Rows = Enumerable.Range(1, 5_000)
                        .Select(index => new ReportRow(new Dictionary<string, object?>
                        {
                            ["Name"] = $"Item {index}",
                            ["Amount"] = index * 1000L,
                        })).ToArray(),
                };
                FixedDocument capped = output.CreateA4(oversized, new DateTime(2026, 9, 27, 12, 0, 0));
                capped.Pages.Count.ShouldBeLessThan(10);
                capped.Pages[0].Child.Children.OfType<TextBlock>()
                    .ShouldContain(block => block.Text.Contains("first 100 rows", StringComparison.Ordinal));
                Stage("capped-pages");
                var wrappedRows = Enumerable.Range(1, 60)
                    .Select(index => new ReportRow(new Dictionary<string, object?>
                    {
                        ["Name"] = $"ઉત્પાદન {index} " + string.Concat(Enumerable.Repeat(
                            "લાંબું ગુજરાતી વર્ણન અને વધુ વિગતો ", 8)),
                        ["Amount"] = index * 1000L,
                    })).ToList();
                var wrappedReport = report with { Rows = wrappedRows };
                output.CreateA4(wrappedReport, new DateTime(2026, 9, 27, 12, 0, 0)).Pages.Count
                    .ShouldBeGreaterThan(2);
                Stage("wrapped-pages");
                string longValue = string.Concat(Enumerable.Repeat(
                    "લાંબું ગુજરાતી વર્ણન અને વિગત જે અહેવાલમાં ઘણા પાનાં સુધી ચાલુ રહે છે. ", 400));
                var tallReport = new TabularReport("Long cell", null,
                    [new("Name", "Name", ReportValueKind.Text)],
                    [new(new Dictionary<string, object?> { ["Name"] = longValue })],
                    new Dictionary<string, decimal>());
                var tallDocument = output.CreateA4(tallReport, new DateTime(2026, 9, 27, 12, 0, 0));
                tallDocument.Pages.Count.ShouldBeGreaterThan(1);
                Stage("tall-pages");
                var continuation = tallDocument.Pages[1].Child.Children.OfType<Border>()
                    .Select(border => border.Child).OfType<TextBlock>()
                    .Single(block => block.Text == longValue);
                continuation.Margin.Top.ShouldBeLessThan(0);
                FixedPage continuationPage = tallDocument.Pages[1].Child;
                continuationPage.Measure(new Size(continuationPage.Width, continuationPage.Height));
                continuationPage.Arrange(new Rect(0, 0, continuationPage.Width, continuationPage.Height));
                continuationPage.UpdateLayout();
                int bitmapWidth = (int)Math.Ceiling(continuationPage.Width);
                int bitmapHeight = (int)Math.Ceiling(continuationPage.Height);
                var continuationBitmap = new RenderTargetBitmap(bitmapWidth, bitmapHeight,
                    96, 96, PixelFormats.Pbgra32);
                continuationBitmap.Render(continuationPage);
                byte[] pixels = new byte[bitmapWidth * bitmapHeight * 4];
                continuationBitmap.CopyPixels(pixels, bitmapWidth * 4, 0);
                int darkBodyPixels = 0;
                for (int y = 170; y < 350; y++)
                    for (int x = 100; x < 500; x++)
                        if (pixels[(y * bitmapWidth + x) * 4] < 128) darkBodyPixels++;
                darkBodyPixels.ShouldBeGreaterThan(100);
                Stage("bitmap-render");
                var wideColumns = new[]
                {
                    new ReportColumn("Item", "Item", ReportValueKind.Text),
                    new ReportColumn("Date", "Date", ReportValueKind.Date),
                    new ReportColumn("Sku", "SKU", ReportValueKind.Text),
                    new ReportColumn("Category", "Category", ReportValueKind.Text),
                    new ReportColumn("Quantity", "Quantity", ReportValueKind.Quantity),
                    new ReportColumn("Cost", "Cost", ReportValueKind.Money),
                    new ReportColumn("Tax", "Tax", ReportValueKind.Money),
                    new ReportColumn("Value", "Value", ReportValueKind.Money),
                    new ReportColumn("Vendor", "Vendor", ReportValueKind.Text),
                    new ReportColumn("Note", "Note", ReportValueKind.Text),
                };
                var wideReport = new TabularReport("Stock value by lot", null, wideColumns,
                    [new(new Dictionary<string, object?>
                    {
                        ["Item"] = "Tata Salt 1 kg", ["Date"] = "2026-09-27", ["Sku"] = "SALT001",
                        ["Category"] = "Staples", ["Quantity"] = 3000L, ["Cost"] = 60000L,
                        ["Tax"] = 3000L, ["Value"] = 90000L, ["Vendor"] = "Shree Traders", ["Note"] = "Batch A",
                    })], new Dictionary<string, decimal> { ["Value"] = 90m });
                var wideDocument = output.CreateA4(wideReport, new DateTime(2026, 9, 27, 12, 0, 0));
                wideDocument.Pages.Count.ShouldBe(2);
                Stage("wide-pages");
                wideDocument.DocumentPaginator.PageSize.Width.ShouldBeGreaterThan(wideDocument.DocumentPaginator.PageSize.Height);
                CaptureA4(wideDocument.Pages[0].Child, "report-a4-band1.en.png");
                CaptureA4(wideDocument.Pages[1].Child, "report-a4-band2.en.png");

                DrawingVisual label = DriverLabelPrinter.Render(new TsplLabel(
                    "Tata Salt 1 kg", "મીઠું", "8901234567890", 28m, 30m, 50, 25), 189, 94);
                var labelBitmap = new RenderTargetBitmap(189, 94, 96, 96, PixelFormats.Pbgra32);
                labelBitmap.Render(label);
                byte[] labelPixels = new byte[189 * 94 * 4];
                labelBitmap.CopyPixels(labelPixels, 189 * 4, 0);
                labelPixels.Where((_, index) => index % 4 == 0).Any(static value => value < 128).ShouldBeTrue();
                Stage("complete");
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("A4 pagination stalled at " + string.Join(" -> ", stages), exception);
        }
        Console.WriteLine("A4 pagination stages: " + string.Join(" -> ", stages));
    }

    [Fact]
    public async Task Shell_LoadsAndSwitchesToGujarati()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-ui-{Guid.NewGuid():N}");
            string? previousTestRoot = Environment.GetEnvironmentVariable("GROCERY_ERP_TEST_DATA_ROOT");
            Environment.SetEnvironmentVariable("GROCERY_ERP_TEST_DATA_ROOT", root);
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "table-columns.json"),
                    """{"GroceryErp.App.Presentation.ProductsPage/ProductsGrid/0:DataGridTextColumn:OldTemplate|1:DataGridTextColumn:Sku|2:DataGridTextColumn:OldName|3:DataGridTextColumn:Category|4:DataGridTextColumn:Stock|5:DataGridTextColumn:SellingPrice|6:DataGridTextColumn:Mrp|7:DataGridTextColumn:IsActive":[80,120,200,150,90,90,90,80]}""");
                GroceryErp.App.App application = new();
                application.InitializeComponent();
                // WPF queues Application startup on the dispatcher. This test hosts pages
                // only, so that startup must never run: it would open the real data store.
                ConsumeApplicationStartup();
                // The cultures Program.Main sets before the app starts, so dates show as they do in the app.
                GroceryErp.App.App.ConfigureCultures();
                Type layouts = typeof(GroceryErp.App.App).Assembly.GetType(
                    "GroceryErp.App.Presentation.TableColumnLayouts")!;
                layouts.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
                Environment.SetEnvironmentVariable("GROCERY_ERP_TEST_DATA_ROOT", previousTestRoot);
                TranslationSource translations = TranslationSource.Instance;
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                // Runs first, while no other window has queued work on this dispatcher.
                VerifyPhaseSixScreens(translations);
                VerifyPhaseSevenScreens(translations);
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
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                var welcome = new DemoPage { DataContext = viewModel };
                var welcomeHost = new Window { Content = welcome, Width = 960, Height = 640 };
                welcomeHost.Show();
                Capture(welcomeHost, "welcome.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(welcomeHost, "welcome.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                welcomeHost.Close();
                var login = new LoginWindow(new AuthenticationService(database, new PasswordHasher(),
                    new SystemClock(settings), new AuditLog()));
                login.Show();
                Capture(login, "login.en.png");
                login.Close();
                VerifyWheelScrolling();
                var setup = new SetupWindow(
                    new FirstRunService(database, new PasswordHasher()),
                    paths,
                    new BackupService(paths, factory, new GroceryErp.Core.Time.SystemClock(settings), settings),
                    settings);

                setup.Show();
                Capture(setup, "onboarding-1-language.en.png");
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(setup, "onboarding-1-language.gu.png");
                var steps = (TabControl)setup.FindName("Steps");
                ((TabItem)steps.Items[1]).Header.ShouldBe("2  દુકાન");
                ((Button)setup.FindName("BackButton")).Content.ShouldBe("પાછળ");
                steps.SelectedIndex = 4;
                steps.SelectedIndex.ShouldBe(0);
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                var setupNext = (Button)setup.FindName("NextButton");
                for (int step = 1; step < steps.Items.Count; step++)
                {
                    if (step == 2) ((TextBox)setup.FindName("StoreName")).Text = "Test grocery";
                    if (step == 3)
                    {
                        ((PasswordBox)setup.FindName("Password")).Password = "samplepass123";
                        ((PasswordBox)setup.FindName("ConfirmPassword")).Password = "samplepass123";
                    }
                    setupNext.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    steps.SelectedIndex.ShouldBe(step);
                    Capture(setup, $"onboarding-{step + 1}.en.png");
                }
                setup.Close();
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");

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
                Capture(restore, "restore.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(restore, "restore.gu.png");
                restore.Close();

                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                var settingsPage = new SettingsPage(
                    new BackupService(paths, factory, new SystemClock(settings), settings), settings,
                    new AuthenticationService(database, new PasswordHasher(), new SystemClock(settings), new AuditLog()),
                    new SystemClock(settings), paths);
                var settingsHost = new Window { Content = settingsPage, Width = 1000, Height = 720 };
                settingsHost.Show();
                VerifyAccessibleNames(settingsHost);
                Capture(settingsHost, "settings-top.en.png");
                ((ScrollViewer)settingsPage.Content).ScrollToEnd();
                Capture(settingsHost, "settings-bottom.en.png");
                ((ScrollViewer)settingsPage.Content).ScrollToTop();
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(settingsHost, "settings-top.gu.png");
                settingsHost.Close();
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");

                var compaction = new CompactionWindow(
                    new BackupService(paths, factory, new SystemClock(settings), settings), runOnLoad: false);
                compaction.Show();
                compaction.ShowProgress(new CompactionProgress(CompactionStage.Copying, 0.42));
                ((TextBlock)compaction.FindName("StageText")).Text.ShouldBe(translations["Settings_CompactStageCopying"]);
                ((TextBlock)compaction.FindName("DetailText")).Text.ShouldContain("42");
                Capture(compaction, "compaction.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                compaction.ShowProgress(new CompactionProgress(CompactionStage.Verifying, null));
                ((TextBlock)compaction.FindName("StageText")).Text.ShouldBe(translations["Settings_CompactStageVerifying"]);
                Capture(compaction, "compaction.gu.png");
                compaction.Close();
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");

                // The upgrade confirmation states the estimate before the store changes, blocks when
                // the volume is short of space, and says how to recover after a failure.
                string upgradeBackups = Path.Combine(root, "backups");
                var upgradePlan = new MigrationPlan(28, MigrationRunner.LatestVersion, 2_928_615_424, 40L * 1024 * 1024 * 1024,
                    6_400_000_000, TimeSpan.FromSeconds(401), KeepsLedgerArchive: true, UiLanguage: "en-IN");
                var upgrade = new UpgradeWindow(upgradePlan, static _ => Task.CompletedTask, upgradeBackups, Path.Combine(root, "logs"));
                upgrade.Show();
                ((Button)upgrade.FindName("UpgradeButton")).IsEnabled.ShouldBeTrue();
                ((TextBlock)upgrade.FindName("EstimateText")).Text.ShouldContain("7 minutes");
                ((TextBlock)upgrade.FindName("ContextText")).Text.ShouldContain(MigrationRunner.LatestVersion.ToString(CultureInfo.InvariantCulture));
                ((FrameworkElement)upgrade.FindName("ArchiveText")).Visibility.ShouldBe(Visibility.Visible);
                CaptureRevision(upgrade, "upgrade.en.png");
                upgrade.ShowProgress(new MigrationProgress(MigrationStage.Upgrade, 3, 9));
                ((TextBlock)upgrade.FindName("StageText")).Text.ShouldBe(translations.Format("Upgrade_StageUpgrade", 3, 9));
                ((ProgressBar)upgrade.FindName("Progress")).Value.ShouldBe(200d / 9, 0.001);
                CaptureRevision(upgrade, "upgrade-progress.en.png");
                upgrade.ShowFailure(new GroceryErp.Core.Errors.DomainException(GroceryErp.Core.Errors.ErrorCode.DiskFull));
                ((FrameworkElement)upgrade.FindName("FailurePanel")).Visibility.ShouldBe(Visibility.Visible);
                ((FrameworkElement)upgrade.FindName("UpgradeButton")).Visibility.ShouldBe(Visibility.Collapsed);
                ((TextBlock)upgrade.FindName("RecoveryText")).Text.ShouldContain(upgradeBackups);
                CaptureRevision(upgrade, "upgrade-failed.en.png");
                upgrade.Close();
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                var blockedUpgrade = new UpgradeWindow(upgradePlan with { FreeBytes = 1_000_000_000 }, static _ => Task.CompletedTask,
                    upgradeBackups, Path.Combine(root, "logs"));
                blockedUpgrade.Show();
                ((Button)blockedUpgrade.FindName("UpgradeButton")).IsEnabled.ShouldBeFalse();
                ((FrameworkElement)blockedUpgrade.FindName("SpaceWarning")).Visibility.ShouldBe(Visibility.Visible);
                ((TextBlock)blockedUpgrade.FindName("EstimateText")).Text.ShouldContain("7 મિનિટ");
                CaptureRevision(blockedUpgrade, "upgrade-no-space.gu.png");
                blockedUpgrade.Close();
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");

                var catalog = new CatalogService(database, new AuditLog(), settings, new SystemClock(settings));
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
                Math.Abs(((DataGrid)products.FindName("ProductsGrid")).Columns[1].ActualWidth - 120)
                    .ShouldBeLessThan(1);
                ((DataGrid)products.FindName("ProductsGrid")).SelectedIndex = 0;
                ((FrameworkElement)products.FindName("MoreFiltersPanel")).Visibility.ShouldBe(Visibility.Collapsed);
                ((FrameworkElement)products.FindName("Selection")).Visibility.ShouldBe(Visibility.Collapsed);
                ((FrameworkElement)products.FindName("Details")).Visibility.ShouldBe(Visibility.Collapsed);
                ((PagerBar)products.FindName("Pager")).Status = translations.Format("Catalog_Count", 1);
                VerifyAccessibleNames(productsWindow);
                Capture(productsWindow, "products.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                ((ToggleButton)products.FindName("MoreFiltersToggle")).Content.ShouldBe(translations["Common_MoreFilters"]);
                ((ToggleButton)products.FindName("MoreFiltersToggle")).Content.ShouldNotBe("More filters");
                Capture(productsWindow, "products.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                VerifyStickyProductTable(productsWindow, products);
                ((DataGrid)products.FindName("ProductsGrid")).Columns[1].Width =
                    new DataGridLength(176, DataGridLengthUnitType.Pixel);
                ((DataGrid)products.FindName("ProductsGrid")).Columns[2].DisplayIndex = 1;
                productsWindow.UpdateLayout();
                ((DataGrid)products.FindName("ProductsGrid")).RaiseEvent(
                    new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                File.Exists(Path.Combine(root, "table-columns.json")).ShouldBeTrue();
                File.ReadAllText(Path.Combine(root, "table-columns.json"))
                    .ShouldContain("\"GroceryErp.App.Presentation.ProductsPage/ProductsGrid\":{");
                // A later layout pass must keep the width and order chosen by the user.
                productsWindow.UpdateLayout();
                var productGrid = (DataGrid)products.FindName("ProductsGrid");
                Math.Abs(productGrid.Columns[1].ActualWidth - 176).ShouldBeLessThan(1);
                productGrid.Columns[2].DisplayIndex.ShouldBe(1);
                var firstProduct = (ProductListItem)productGrid.Items[0];
                var secondProduct = new ProductListItem { Id = 2, Sku = "SALT002", Name = "Other salt", IsActive = true };
                productGrid.ItemsSource = new[] { firstProduct, secondProduct };
                // Checked rows show the selection bar with the exact count; the header box
                // follows the visible page, and picks survive moving to another page.
                var selectionBar = (SelectionBar)products.FindName("Selection");
                var checkProduct = typeof(ProductsPage).GetMethod("ProductCheck_OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                checkProduct.Invoke(products, [new CheckBox { DataContext = firstProduct, IsChecked = true }, new RoutedEventArgs()]);
                checkProduct.Invoke(products, [new CheckBox { DataContext = secondProduct, IsChecked = true }, new RoutedEventArgs()]);
                selectionBar.Visibility.ShouldBe(Visibility.Visible);
                TextOf(selectionBar).ShouldContain(translations.Format("Selection_Count", 2));
                var pageSelection = new CheckBox();
                typeof(ProductsPage).GetMethod("PageSelectionCheck_OnLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(products, [pageSelection, new RoutedEventArgs()]);
                pageSelection.IsChecked.ShouldBe(true);
                var updateSelection = typeof(ProductsPage).GetMethod("UpdateSelection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                productGrid.ItemsSource = new[] { new ProductListItem { Id = 3, Name = "Next page", IsActive = true } };
                updateSelection.Invoke(products, null);
                pageSelection.IsChecked.ShouldBe(false);
                TextOf(selectionBar).ShouldContain(translations.Format("Selection_Count", 2));
                productGrid.ItemsSource = new[] { firstProduct, secondProduct };
                updateSelection.Invoke(products, null);
                pageSelection.IsChecked.ShouldBe(true);
                Capture(productsWindow, "products-selection.en.png");
                typeof(ProductsPage).GetMethod("PageSelectionCheck_OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(products, [pageSelection, new RoutedEventArgs()]);
                pageSelection.IsChecked.ShouldBe(false);
                selectionBar.Visibility.ShouldBe(Visibility.Collapsed);
                typeof(ProductsPage).GetMethod("PageSelectionCheck_OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(products, [pageSelection, new RoutedEventArgs()]);
                pageSelection.IsChecked.ShouldBe(true);
                productsWindow.Close();
                var savedLayouts = (System.Collections.IDictionary)layouts.GetField("Saved",
                    BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                savedLayouts.Clear();
                foreach (System.Collections.DictionaryEntry entry in (System.Collections.IDictionary)layouts.GetMethod("Load",
                    BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!)
                    savedLayouts.Add(entry.Key, entry.Value);
                var restoredProducts = new ProductsPage(catalog, settings,
                    new ProductExcelImporter(database, new AuditLog(), settings), loadOnShow: false);
                var restoredHost = new Window { Content = restoredProducts, Width = 1080, Height = 700 };
                restoredHost.Show();
                restoredHost.UpdateLayout();
                var restoredGrid = (DataGrid)restoredProducts.FindName("ProductsGrid");
                restoredGrid.IsLoaded.ShouldBeTrue();
                restoredGrid.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, restoredGrid));
                double restoredWidth = restoredGrid.Columns[1].ActualWidth;
                Math.Abs(restoredWidth - 176).ShouldBeLessThan(1);
                restoredGrid.Columns[2].DisplayIndex.ShouldBe(1);
                restoredHost.Close();

                var inventory = new InventoryService(database, new LotQuantities(settings),
                    new DocNumberService(settings), new PeriodGuard(settings, new SystemClock(settings)), new AuditLog());
                var selectedLot = new LotItem(1, 1, "Tata Salt 1 kg", "L000001", "B2401",
                    new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 9),
                    new Money(20), new Money(30), new Money(27.5m), new Quantity(10), new Quantity(5), true, "CRITICAL");
                var lotsView = new ProductLotsView(inventory, 1, isInternal: false, loadOnShow: false);
                ((DataGrid)lotsView.FindName("LotsGrid")).ItemsSource = new[] { selectedLot };
                var lotsHost = new Window { Content = lotsView, Width = 760, Height = 520 };
                lotsHost.Show();
                ((FrameworkElement)lotsView.FindName("MoreFiltersPanel")).Visibility.ShouldBe(Visibility.Collapsed);
                ((FrameworkElement)lotsView.FindName("ConsumeButton")).Visibility.ShouldBe(Visibility.Collapsed);
                Capture(lotsHost, "product-lots.en.png");
                lotsHost.Close();

                var priceChange = new LotPriceWindow(inventory, selectedLot);
                priceChange.Show();
                Capture(priceChange, "lot-price.en.png");
                priceChange.Close();

                var batch = new LabelBatchWindow(catalog, inventory, settings, loadOnShow: false);
                var productLabel = new LabelBatchRow("Tata Salt 1 kg", "ટાટા મીઠું 1 kg", "Product",
                    "2000000000015", 27.5m, 30m, "1", true);
                var secondProductLabel = new LabelBatchRow("Second salt", null, "Product",
                    "2000000000022", 30m, 35m, "1", true);
                var firstLotLabel = new LabelBatchRow("Tata Salt 1 kg", "ટાટા મીઠું 1 kg", "Lot L000001",
                    "2000000000015", 27.5m, 30m, "1", false, isLot: true);
                var secondLotLabel = new LabelBatchRow("Second salt", null, "Lot L000002",
                    "2000000000022", 30m, 35m, "1", false, isLot: true);
                var nextLotLabel = new LabelBatchRow("Tata Salt 1 kg", "ટાટા મીઠું 1 kg", "Lot L000003",
                    "2000000000039", 28m, 30m, "1", false, isLot: true);
                batch.Rows.Add(productLabel);
                batch.Rows.Add(secondProductLabel);
                var productRows = (Dictionary<long, LabelBatchRow>)typeof(LabelBatchWindow)
                    .GetField("_productRows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(batch)!;
                productRows.Add(1, productLabel);
                productRows.Add(2, secondProductLabel);
                var lotRows = (Dictionary<long, LabelBatchRow>)typeof(LabelBatchWindow)
                    .GetField("_lotRows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(batch)!;
                lotRows.Add(1, firstLotLabel);
                lotRows.Add(2, secondLotLabel);
                lotRows.Add(3, nextLotLabel);
                var lotPages = (List<Dictionary<long, LotPage>>)typeof(LabelBatchWindow)
                    .GetField("_lotPages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(batch)!;
                var firstLot = new LotItem(1, 1, "Tata Salt 1 kg", "L000001", null,
                    new DateOnly(2026, 10, 1), null, new Money(20), new Money(30), new Money(27.5m),
                    Quantity.One, Quantity.One, true);
                var secondLot = firstLot with { Id = 2, ProductId = 2, ProductName = "Second salt", LotCode = "L000002" };
                var nextLot = firstLot with { Id = 3, LotCode = "L000003" };
                lotPages.Add(new Dictionary<long, LotPage>
                {
                    [1] = new LotPage([firstLot], true, 1),
                    [2] = new LotPage([secondLot], false, 2),
                });
                lotPages.Add(new Dictionary<long, LotPage>
                {
                    [1] = new LotPage([nextLot], false, 3),
                    [2] = new LotPage([secondLot], false, 2),
                });
                batch.Show();
                typeof(LabelBatchWindow).GetMethod("ShowLotPage", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(batch, [0]);
                ((DataGrid)batch.FindName("LabelsGrid")).SelectedIndex = 0;
                var productLabelCheck = typeof(LabelBatchWindow).GetMethod("ProductLabelCheck_OnClick",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                productLabelCheck.Invoke(batch, [new CheckBox { DataContext = productLabel, IsChecked = false }, new RoutedEventArgs()]);
                ((TextBlock)batch.FindName("PreviewName")).Text.ShouldBe(secondProductLabel.Name);
                productLabelCheck.Invoke(batch, [new CheckBox { DataContext = secondProductLabel, IsChecked = false }, new RoutedEventArgs()]);
                ((TextBlock)batch.FindName("PreviewName")).Text.ShouldBeEmpty();
                ((Button)batch.FindName("PrintButton")).IsEnabled.ShouldBeFalse();
                typeof(LabelBatchWindow).GetMethod("LotCheck_OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(batch, [new CheckBox { DataContext = firstLotLabel, IsChecked = true }, new RoutedEventArgs()]);
                ((TextBlock)batch.FindName("PreviewBarcodeText")).Text.ShouldBe(firstLotLabel.Barcode);
                ((Button)batch.FindName("PrintButton")).IsEnabled.ShouldBeTrue();
                productLabelCheck.Invoke(batch, [new CheckBox { DataContext = productLabel, IsChecked = true }, new RoutedEventArgs()]);
                productLabelCheck.Invoke(batch, [new CheckBox { DataContext = secondProductLabel, IsChecked = true }, new RoutedEventArgs()]);
                firstLotLabel.IsSelected = false;
                typeof(LabelBatchWindow).GetMethod("UpdatePreview", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(batch, [productLabel]);
                var pageSizeMenu = (MenuItem)batch.FindName("PageSizeMenu");
                pageSizeMenu.IsSubmenuOpen = true;
                batch.UpdateLayout();
                pageSizeMenu.IsSubmenuOpen.ShouldBeTrue();
                pageSizeMenu.IsSubmenuOpen = false;
                productLabel.ToggleLots();
                secondProductLabel.ToggleLots();
                batch.Rows.Count.ShouldBe(2);
                batch.Rows[0].Lots.Count.ShouldBe(1);
                batch.Rows[1].Lots.Count.ShouldBe(1);
                ((Button)batch.FindName("NextLotsButton")).IsEnabled.ShouldBeTrue();
                var labelGrid = (DataGrid)batch.FindName("LabelsGrid");
                var labelRow = FindVisualChild<DataGridRow>(labelGrid);
                labelRow.ShouldNotBeNull();
                labelRow.ActualHeight.ShouldBeGreaterThan(0);
                labelRow.IsVisible.ShouldBeTrue();
                labelRow.TranslatePoint(new Point(), labelGrid).Y.ShouldBeGreaterThan(40);
                labelRow.TranslatePoint(new Point(), labelGrid).Y.ShouldBeLessThan(labelGrid.ActualHeight);
                Capture(batch, "label-batch.en.png");
                firstLotLabel.IsSelected = true;
                firstLotLabel.CopiesText = "7";
                typeof(LabelBatchWindow).GetMethod("NextLots_OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(batch, [batch, new RoutedEventArgs()]);
                batch.Rows[0].Lots[0].ShouldBeSameAs(nextLotLabel);
                batch.Rows[1].Lots[0].ShouldBeSameAs(secondLotLabel);
                ((Button)batch.FindName("PreviousLotsButton")).IsEnabled.ShouldBeTrue();
                ((IReadOnlyList<TsplLabel>)typeof(LabelBatchWindow).GetMethod("BuildLabels", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(batch, null)!).Count.ShouldBe(3);
                typeof(LabelBatchWindow).GetMethod("PreviousLots_OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(batch, [batch, new RoutedEventArgs()]);
                batch.Rows[0].Lots[0].ShouldBeSameAs(firstLotLabel);
                firstLotLabel.CopiesText.ShouldBe("7");
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
                // One form with grouped sections; a new product can add its first lot.
                var productForm = (ProductForm)editor.FindName("Form");
                ((TextBox)productForm.FindName("PriceBox")).IsVisible.ShouldBeTrue();
                ((FrameworkElement)productForm.FindName("FirstLotSection")).Visibility.ShouldBe(Visibility.Visible);
                editor.Close();

                var productImport = new ProductImportWindow(new ProductExcelImporter(database, new AuditLog(), settings));
                productImport.Show();
                Capture(productImport, "product-import.en.png");
                productImport.Width = 760;
                Capture(productImport, "product-import.narrow.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(productImport, "product-import.narrow.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                productImport.Close();

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

                var purchasing = new PurchasingService(database, new LotQuantities(settings),
                    new DocNumberService(settings), new PeriodGuard(settings, new SystemClock(settings)), settings, new AuditLog());
                var vendorEditor = new VendorEditorWindow(purchasing);
                vendorEditor.Show();
                Capture(vendorEditor, "vendor-editor.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(vendorEditor, "vendor-editor.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                ((ScrollViewer)vendorEditor.FindName("Scroller")).ScrollToEnd();
                Capture(vendorEditor, "vendor-editor-bottom.en.png");
                vendorEditor.Close();
                var vendorPayment = new VendorPaymentWindow(purchasing);
                var paymentForm = vendorPayment.Content;
                vendorPayment.Content = null;
                var paymentPreview = new Window { Content = paymentForm, Width = 470, Height = 600 };
                paymentPreview.Show();
                Capture(paymentPreview, "vendor-payment.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(paymentPreview, "vendor-payment.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                paymentPreview.Height = 510;
                ((Grid)paymentForm).Children.OfType<ScrollViewer>().Single().ScrollToEnd();
                Capture(paymentPreview, "vendor-payment.narrow.en.png");
                paymentPreview.Close();
                var purchaseEntry = new PurchaseEntryWindow(purchasing, loadOnShow: false);
                ((ComboBox)purchaseEntry.FindName("VendorBox")).ItemsSource = new[]
                {
                    new VendorItem(1, "Shree Wholesale Traders", "9999999999", null, Money.Zero, true),
                };
                ((ComboBox)purchaseEntry.FindName("VendorBox")).SelectedIndex = 0;
                ((TextBox)purchaseEntry.FindName("InvoiceBox")).Text = "SW-1042";
                ((DatePicker)purchaseEntry.FindName("InvoiceDateBox")).SelectedDate = new DateTime(2026, 9, 27);
                ((DatePicker)purchaseEntry.FindName("ReceivedDateBox")).SelectedDate = new DateTime(2026, 9, 28);
                var purchaseLine = new PurchaseEntryWindow.PurchaseEditLine
                {
                    ProductId = 1,
                    ProductName = "Tata Salt 1 kg",
                    Batch = "SALT-2409",
                    Expiry = "2027-09-28",
                    Quantity = 10,
                    FreeQuantity = 1,
                    Rate = 250,
                    TaxRate = 5,
                    Mrp = 350,
                    SellingPrice = 300,
                };
                purchaseEntry.Lines.Add(purchaseLine);
                ((TextBox)purchaseEntry.FindName("ChargesBox")).Text = "૧૦૦";
                ((TextBox)purchaseEntry.FindName("BillTotalBox")).Text = "૨૭૨૫";
                ((TextBlock)purchaseEntry.FindName("DifferenceText")).Text.ShouldBe("0.000");
                ((TextBox)purchaseEntry.FindName("ChargesBox")).Text = "100";
                ((TextBox)purchaseEntry.FindName("BillTotalBox")).Text = "2725";
                purchaseEntry.Show();
                // The line type is a visible list, the expense category column appears only for an
                // Expense line, and each line has its own remove button.
                var entryGrid = (DataGrid)purchaseEntry.FindName("LinesGrid");
                var expenseColumn = (DataGridColumn)purchaseEntry.FindName("ExpenseCategoryColumn");
                purchaseEntry.UpdateLayout();
                Descendants<ComboBox>(entryGrid).First().SelectedValue.ShouldBe("PRODUCT");
                expenseColumn.Visibility.ShouldBe(Visibility.Collapsed);
                purchaseLine.LineKind = "EXPENSE";
                expenseColumn.Visibility.ShouldBe(Visibility.Visible);
                purchaseLine.LineKind = "PRODUCT";
                expenseColumn.Visibility.ShouldBe(Visibility.Collapsed);
                VerifyAccessibleNames(purchaseEntry);
                Capture(purchaseEntry, "purchase-entry.en.png");
                // Typed including GST, 10 × 250 plus 100 charges is the 2,600 the bill prints.
                var ratesIncludeTax = (CheckBox)purchaseEntry.FindName("RatesIncludeTaxBox");
                ratesIncludeTax.IsChecked = true;
                ((TextBlock)purchaseEntry.FindName("SubtotalText")).Text.ShouldBe("2,380.950");
                ((TextBlock)purchaseEntry.FindName("ComputedText")).Text.ShouldBe("2,600.000");
                ((DataGridColumn)purchaseEntry.FindName("RateColumn")).Header.ShouldBe("Rate incl. GST");
                Capture(purchaseEntry, "purchase-entry-incl-gst.en.png");
                ratesIncludeTax.IsChecked = false;
                ((TextBlock)purchaseEntry.FindName("ComputedText")).Text.ShouldBe("2,725.000");
                var extraLine = new PurchaseEntryWindow.PurchaseEditLine { ProductId = 2, ProductName = "Sugar 1 kg", Rate = 40 };
                purchaseEntry.Lines.Add(extraLine);
                ((TextBlock)purchaseEntry.FindName("ComputedText")).Text.ShouldBe("2,765.000");
                purchaseEntry.UpdateLayout();
                Descendants<Button>(entryGrid)
                    .Where(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Remove line")
                    .ElementAt(1).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                purchaseEntry.Lines.Single().ShouldBeSameAs(purchaseLine);
                ((TextBlock)purchaseEntry.FindName("ComputedText")).Text.ShouldBe("2,725.000");
                purchaseLine.ApplyMetadata(new PurchaseLineDefaults(new Money(200), new Percent(5),
                    new Money(350), new Money(200), null, 30, true));
                purchaseLine.Expiry = "2026-10-01";
                purchaseLine.SellingPrice = 200;
                ((DataGrid)purchaseEntry.FindName("LinesGrid")).Items.Refresh();
                ((TextBox)purchaseEntry.FindName("ChargesBox")).Text = "101";
                purchaseLine.MarginNegative.ShouldBeTrue();
                ((TextBlock)purchaseEntry.FindName("WarningText")).Text.ShouldContain("short expiry");
                ((TextBlock)purchaseEntry.FindName("WarningText")).Text.ShouldContain("selling below landed cost");
                ((TextBlock)purchaseEntry.FindName("WarningText")).Text.ShouldContain("rate differs over 10%");
                Capture(purchaseEntry, "purchase-entry-warnings.en.png");
                // Enter on a search match adds it as a line; an internal-use product starts as Internal use.
                var choices = (ListBox)((ProductChoices)purchaseEntry.FindName("ProductChoices")).FindName("List");
                choices.ItemsSource = new[] { new ProductListItem { Id = 3, Name = "Packing twine", ProductClass = "INTERNAL" } };
                choices.SelectedIndex = 0;
                // Raised through the dispatcher, as input is, so the window's async work resumes on this thread.
                purchaseEntry.Dispatcher.Invoke(() => choices.RaiseEvent(
                    new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(purchaseEntry), 0, Key.Enter)
                    { RoutedEvent = Keyboard.KeyDownEvent }));
                purchaseEntry.Lines.Count.ShouldBe(2);
                purchaseEntry.Lines[1].ProductName.ShouldBe("Packing twine");
                purchaseEntry.Lines[1].LineKind.ShouldBe("INTERNAL");
                purchaseEntry.Close();

                var directCount = new DirectCountReviewWindow(
                    new DirectStockCountService(database, new DirectStockEngine(), new PeriodGuard(settings, new SystemClock(settings))),
                    [1], new SystemClock(settings), loadOnShow: false);
                ((DataGrid)directCount.FindName("CountGrid")).ItemsSource = new object[]
                {
                    new { ProductName = "Tata Salt 1 kg", LotCode = "L2627-000014",
                        Expected = 11m, Counted = "10", Difference = -1m, DifferenceValue = -250m },
                    new { ProductName = "Wheat flour 5 kg", LotCode = "L2627-000015",
                        Expected = 8m, Counted = "8", Difference = 0m, DifferenceValue = 0m },
                };
                ((TextBox)directCount.FindName("ReasonBox")).Text = "Shelf count";
                ((TextBlock)directCount.FindName("PageText")).Text = translations.Format("Stock_CountPage", 1, 2);
                ((TextBlock)directCount.FindName("StatusText")).Text = translations.Format("Stock_ReviewSummary", 1, "-250.000");
                ((Button)directCount.FindName("PostButton")).IsEnabled = true;
                directCount.Show();
                Capture(directCount, "direct-count-review.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                ((TextBlock)directCount.FindName("PageText")).Text = translations.Format("Stock_CountPage", 1, 2);
                ((TextBlock)directCount.FindName("StatusText")).Text = translations.Format("Stock_ReviewSummary", 1, "-250.000");
                Capture(directCount, "direct-count-review.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                directCount.Close();

                var openingPreview = new OpeningStockPreviewWindow(new OpeningStockImportPreview(
                [
                    new OpeningStockImportRow(2, "SALT001",
                        new OpeningStockLine(1, new Quantity(11), new Money(250), new Money(300),
                            new Money(350), new DateOnly(2027, 9, 28), "SALT-2409", new DateOnly(2026, 9, 28)), []),
                    new OpeningStockImportRow(3, "UNKNOWN", null, ["SKU was not found or is archived."]),
                ]));
                openingPreview.Show();
                ((Button)openingPreview.FindName("ImportButton")).IsEnabled.ShouldBeFalse();
                Capture(openingPreview, "opening-stock-preview.en.png");
                openingPreview.Close();

                var purchasesPage = new PurchasesPage(purchasing, loadOnShow: false);
                ((DataGrid)purchasesPage.FindName("PurchasesGrid")).ItemsSource = new[]
                {
                    new PurchaseListItem(1, "P2627-000051", "Shree Wholesale Traders", "SW-1042",
                        new DateOnly(2026, 9, 28), "POSTED", new Money(2725), new Money(2725)),
                    new PurchaseListItem(2, null, "Amul Distribution", "AD-729",
                        new DateOnly(2026, 9, 29), "DRAFT", Money.Zero, new Money(1850)),
                };
                var purchasesHost = new Window { Content = purchasesPage, Width = 1080, Height = 700 };
                purchasesHost.Show();
                ((DatePicker)purchasesPage.FindName("FromBox")).IsVisible.ShouldBeTrue();
                VerifyAccessibleNames(purchasesHost);
                Capture(purchasesHost, "purchases-page.en.png");
                var moreFilters = (ToggleButton)purchasesPage.FindName("MoreFiltersToggle");
                ((FrameworkElement)purchasesPage.FindName("ReturnableFilter")).IsVisible.ShouldBeFalse();
                moreFilters.IsChecked = true;
                moreFilters.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                purchasesHost.UpdateLayout();
                ((FrameworkElement)purchasesPage.FindName("ReturnableFilter")).IsVisible.ShouldBeTrue();
                Capture(purchasesHost, "purchases-filters.en.png");
                moreFilters.IsChecked = false;
                moreFilters.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(purchasesHost, "purchases-page.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                var purchaseGrid = (DataGrid)purchasesPage.FindName("PurchasesGrid");
                purchaseGrid.Columns[2].Width = new DataGridLength(300, DataGridLengthUnitType.Pixel);
                purchaseGrid.Columns[2].DisplayIndex = 1;
                purchasesHost.Close();
                var reopenedPurchases = new PurchasesPage(purchasing, loadOnShow: false);
                var reopenedPurchasesHost = new Window { Content = reopenedPurchases, Width = 1080, Height = 700 };
                reopenedPurchasesHost.Show();
                var reopenedPurchaseGrid = (DataGrid)reopenedPurchases.FindName("PurchasesGrid");
                Math.Abs(reopenedPurchaseGrid.Columns[2].ActualWidth - 300).ShouldBeLessThan(1);
                reopenedPurchaseGrid.Columns[2].DisplayIndex.ShouldBe(1);
                reopenedPurchasesHost.Close();

                var vendorHistory = new VendorLedgerView();
                ((DataGrid)vendorHistory.FindName("PurchasesGrid")).ItemsSource = new[]
                {
                    new VendorLedgerItem(new DateOnly(2026, 9, 28), "PURCHASE", "P2627-000051",
                        new Money(2725), Money.Zero, new Money(2825)),
                };
                ((DataGrid)vendorHistory.FindName("LotsGrid")).ItemsSource = new[]
                {
                    new VendorLotItem("Tata Salt 1 kg", "L2627-000014", "SALT-2409",
                        new DateOnly(2026, 9, 28), new DateOnly(2027, 9, 28), new Quantity(11)),
                };
                var vendorHistoryHost = new Window { Content = vendorHistory, Width = 900, Height = 620 };
                vendorHistoryHost.Show();
                Capture(vendorHistoryHost, "vendor-history.en.png");
                vendorHistoryHost.Close();

                var vendorReturn = new VendorReturnWindow(purchasing, 1, loadOnShow: false,
                    clock: new SystemClock(settings));
                ((TextBox)vendorReturn.FindName("CreditNumberBox")).Text = "CN-2026-07";
                ((TextBox)vendorReturn.FindName("ReasonBox")).Text = "Short dated stock";
                ((DataGrid)vendorReturn.FindName("ReturnGrid")).ItemsSource = new[]
                {
                    new { ProductName = "Tata Salt 1 kg", LotCode = "L2627-000014", OnHand = "11",
                        Returnable = "11", QuantityText = "2", BeforeTaxText = "476.190", TaxText = "23.810" },
                };
                ((TextBlock)vendorReturn.FindName("TotalsText")).Text = "1 lot · credit ₹500.000";
                vendorReturn.Show();
                Capture(vendorReturn, "vendor-return.en.png");
                vendorReturn.Close();

                var vendorsPage = new VendorsPage(purchasing, loadOnShow: false);
                ((DataGrid)vendorsPage.FindName("VendorsGrid")).ItemsSource = new[]
                {
                    new VendorItem(1, "Shree Wholesale Traders", "9999999999", "24ABCDE1234F1Z5", new Money(2325), true),
                    new VendorItem(2, "Amul Distribution", "8888888888", null, Money.Zero, false),
                };
                var vendorsHost = new Window { Content = vendorsPage, Width = 1080, Height = 700 };
                vendorsHost.Show();
                ((DataGrid)vendorsPage.FindName("VendorsGrid")).SelectedIndex = 0;
                ((PagerBar)vendorsPage.FindName("Pager")).Status = translations.Format("Purchasing_VendorCount", 2);
                ((FrameworkElement)vendorsPage.FindName("Selection")).Visibility.ShouldBe(Visibility.Collapsed);
                VerifyAccessibleNames(vendorsHost);
                Capture(vendorsHost, "vendors-page.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(vendorsHost, "vendors-page.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                vendorsHost.Close();

                IClock phase5Clock = new SystemClock(settings);
                var moneyOutService = new MoneyOutService(database, new LotQuantities(settings),
                    new DocNumberService(settings), new PeriodGuard(settings, new SystemClock(settings)), new AuditLog());
                var moneyOutPage = new MoneyOutPage(moneyOutService, inventory, phase5Clock, loadOnShow: false);
                var moneyOutHost = new Window { Content = moneyOutPage, Width = 1120, Height = 700 };
                ((DataGrid)moneyOutPage.FindName("DamageGrid")).ItemsSource = new[]
                {
                    new DamageItem(1, "D2627-000021", new DateOnly(2026, 9, 27), "Tata Salt 1 kg", "L2627-000014",
                        new Quantity(2), new Money(20), new Money(40), Money.Zero, "BROKEN", "Handling"),
                };
                ((DataGrid)moneyOutPage.FindName("ExpenseGrid")).ItemsSource = new[]
                {
                    new ExpenseItem(1, new DateOnly(2026, 9, 27), 1, "Electricity", new Money(1500), "UPI", "Power Co", "TX-1", null),
                };
                ((DataGrid)moneyOutPage.FindName("EmployeeGrid")).ItemsSource = new[]
                {
                    new EmployeeItem(1, "Asha Patel", "9999999999", "Cashier", new DateOnly(2026, 4, 1), null,
                        new Money(12000), true, null),
                };
                moneyOutHost.Show();
                // Damage, Expenses, Employees and posted payments are separate tabs; each shows
                // only its own actions above the tabs.
                var moneyOutTabs = (TabControl)moneyOutPage.FindName("MoneyOutTabs");
                moneyOutTabs.Items.Count.ShouldBe(4);
                string[] actionPanels = ["DamageActions", "ExpenseActions", "EmployeeActions", "SalaryActions"];
                string[] tabCaptures = ["damages.en.png", "expenses.en.png", "employees.en.png", "payroll.en.png"];
                for (int tab = 0; tab < actionPanels.Length; tab++)
                {
                    moneyOutTabs.SelectedIndex = tab;
                    for (int panel = 0; panel < actionPanels.Length; panel++)
                        ((FrameworkElement)moneyOutPage.FindName(actionPanels[panel])).Visibility
                            .ShouldBe(panel == tab ? Visibility.Visible : Visibility.Collapsed);
                    Capture(moneyOutHost, tabCaptures[tab]);
                }
                ((FrameworkElement)moneyOutPage.FindName("SalaryDetails")).Visibility.ShouldBe(Visibility.Collapsed);
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(moneyOutHost, "payroll.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                moneyOutTabs.SelectedIndex = 2;
                ((Button)moneyOutPage.FindName("EditEmployeeButton")).IsEnabled.ShouldBeFalse();
                moneyOutTabs.SelectedIndex = 0;
                VerifyStickyMoneyOutTable(moneyOutHost, moneyOutPage);
                moneyOutHost.Close();

                var assetService = new AssetService(database, settings, new PeriodGuard(settings, new SystemClock(settings)), new AuditLog(), phase5Clock);
                var assetsPage = new AssetsPage(assetService, phase5Clock, purchasing, loadOnShow: false);
                ((DataGrid)assetsPage.FindName("AssetGrid")).ItemsSource = new[]
                {
                    new AssetItem(1, "Visi-cooler 300 L", "Equipment", new DateOnly(2026, 4, 1),
                        new DateOnly(2026, 4, 1), new Money(45000), null, null, DepreciationMethod.Wdv,
                        new Percent(15), Money.Zero, null, null, new Money(1128.083m), new Money(43871.917m), null),
                };
                ((DataGrid)assetsPage.FindName("ScheduleGrid")).ItemsSource = new[]
                {
                    new DepreciationEntry(1, 1, new YearMonth(2026, 4), new Money(45000), new Money(554.795m),
                        new Money(44445.205m), new DateTime(2026, 5, 1)),
                };
                var assetsHost = new Window { Content = assetsPage, Width = 1120, Height = 700 };
                assetsHost.Show();
                // Add, edit and dispose open their own dialogs; the table keeps the page.
                ((FrameworkElement)assetsPage.FindName("Timeline")).Visibility.ShouldBe(Visibility.Collapsed);
                ((Button)assetsPage.FindName("EditAssetButton")).IsEnabled.ShouldBeFalse();
                // The timeline opens from the title row for the selected asset, not a button in every row.
                ((Button)assetsPage.FindName("TimelineButton")).IsEnabled.ShouldBeFalse();
                VerifyAccessibleNames(assetsHost);
                Capture(assetsHost, "assets.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(assetsHost, "assets.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                var assetGrid = (DataGrid)assetsPage.FindName("AssetGrid");
                assetGrid.AutoGenerateColumns.ShouldBeFalse();
                assetGrid.CanUserResizeColumns.ShouldBeTrue();
                assetGrid.CanUserReorderColumns.ShouldBeTrue();
                assetGrid.Columns.Count.ShouldBe(7);
                assetGrid.Columns[1].Width = new DataGridLength(260, DataGridLengthUnitType.Pixel);
                assetGrid.Columns[2].DisplayIndex = 1;
                assetsHost.Close();
                savedLayouts.Clear();
                foreach (System.Collections.DictionaryEntry entry in (System.Collections.IDictionary)layouts.GetMethod("Load",
                    BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!)
                    savedLayouts.Add(entry.Key, entry.Value);
                var reopenedAssets = new AssetsPage(assetService, phase5Clock, purchasing, loadOnShow: false);
                var reopenedAssetsHost = new Window { Content = reopenedAssets, Width = 1120, Height = 700 };
                reopenedAssetsHost.Show();
                var reopenedAssetGrid = (DataGrid)reopenedAssets.FindName("AssetGrid");
                Math.Abs(reopenedAssetGrid.Columns[1].ActualWidth - 260).ShouldBeLessThan(1);
                reopenedAssetGrid.Columns[2].DisplayIndex.ShouldBe(1);
                reopenedAssetsHost.Close();

                IClock posClock = new SystemClock(settings);
                var sales = new SalesService(database, new LotQuantities(settings), new DocNumberService(settings),
                    new PeriodGuard(settings, new SystemClock(settings)), settings, new AuditLog(), posClock);
                var printer = new ReceiptPrinter(settings);
                SaleLine[] lines =
                [
                    new(1, 1, 1, 1, "Tata Salt 1 kg", "ટાટા મીઠું 1 કિલો", "SALT-2409",
                        new DateOnly(2027, 9, 28), new Quantity(2), new Money(28), new Money(30),
                        Money.Zero, new Percent(5), new Money(53.333m), new Money(2.667m),
                        new Money(56), new Money(20), new Money(40), "4006381333931"),
                    new(2, 2, 2, 2, "Loose sugar", "ખાંડ", "SUG-2410",
                        new DateOnly(2027, 8, 12), new Quantity(1.25m), new Money(44), null,
                        new Money(2), new Percent(5), new Money(50.476m), new Money(2.524m),
                        new Money(53), new Money(28), new Money(35), null),
                ];
                var billing = new BillingPage(sales, new ScanResolver(database), catalog, inventory, printer, loadOnShow: false);
                ((DataGrid)billing.FindName("BillGrid")).ItemsSource = lines;
                ((DataGrid)billing.FindName("BillGrid")).SelectedIndex = 0;
                ((TextBlock)billing.FindName("GrossText")).Text = "₹111.000";
                ((TextBlock)billing.FindName("DiscountText")).Text = "₹2.000";
                ((TextBlock)billing.FindName("TaxText")).Text = "₹5.191";
                ((TextBlock)billing.FindName("RoundOffText")).Text = "₹0.000";
                ((TextBlock)billing.FindName("TotalText")).Text = "₹109.000";
                ((TextBlock)billing.FindName("SavingsText")).Text = "You saved ₹4.000";
                ((TextBlock)billing.FindName("ItemCountText")).Text = "2";
                ((TextBlock)billing.FindName("CustomerText")).Text = "Mrs. Shah";
                ((TextBlock)billing.FindName("StatusText")).Text = TranslationSource.Instance["Sales_Status_Draft"];
                ((TextBlock)billing.FindName("LastScanText")).Text = "Last scan: Tata Salt 1 kg · on hand 11.000 · MRP ₹30.000";
                ((TextBlock)billing.FindName("PrinterStatusText")).Text = "Receipt printer ready";
                ((ItemsControl)billing.FindName("HeldChips")).ItemsSource = new[]
                {
                    new { Label = "Held #41 · Mrs. Shah" }, new { Label = "Held #42 · Pickup" },
                };
                var billingHost = new Window { Content = billing, Width = 1120, Height = 680 };
                billingHost.Show();
                VerifyAccessibleNames(billingHost);
                Capture(billingHost, "billing.en.png");
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                ((TextBlock)billing.FindName("SavingsText")).Text = TranslationSource.Instance.Format("Sales_Savings", "4.000");
                ((TextBlock)billing.FindName("StatusText")).Text = TranslationSource.Instance["Sales_Status_Draft"];
                ((TextBlock)billing.FindName("LastScanText")).Text = TranslationSource.Instance.Format(
                    "Sales_LastScan", "Tata Salt 1 kg", "11.000", "30.000");
                ((TextBlock)billing.FindName("PrinterStatusText")).Text = TranslationSource.Instance["Sales_PrinterReady"];
                Capture(billingHost, "billing.gu.png");
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                billingHost.Close();

                var payment = new SplitPaymentWindow(new Money(109));
                ((TextBox)payment.FindName("CashBox")).Text = "120";
                payment.Show();
                ((TextBlock)payment.FindName("BalanceText")).Text.ShouldContain("11.000");
                Capture(payment, "payment.en.png");
                payment.Close();
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                var guPayment = new SplitPaymentWindow(new Money(109));
                ((TextBox)guPayment.FindName("CashBox")).Text = "120";
                guPayment.Show();
                Capture(guPayment, "payment.gu.png");
                guPayment.Close();
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("en-IN");

                var saved = new SavedBillsWindow([
                    new HeldSale(41, new DateTime(2026, 10, 3, 10, 30, 0), "Mrs. Shah", 3, new Money(231)),
                    new HeldSale(42, new DateTime(2026, 10, 3, 11, 15, 0), "Pickup", 2, new Money(109)),
                ]);
                saved.Show();
                Capture(saved, "saved-bills.en.png");
                saved.Close();

                var heldReview = new HeldBillReviewWindow(new HeldBillReview(41,
                    [new(1, "Tata Salt 1 kg", new Quantity(2), new Money(28), new Money(30),
                        new Money(30), new Money(32), new Money(20), new Quantity(11), true,
                        new DateOnly(2027, 9, 28))]), posClock);
                heldReview.Show();
                Capture(heldReview, "held-review.en.png");
                heldReview.Close();

                var discount = new DiscountWindow(new Money(100), new Money(5));
                discount.Show();
                ((ComboBox)discount.FindName("ModeBox")).SelectedIndex = 1;
                Capture(discount, "discount-percent.en.png");
                discount.Close();

                var history = new SalesHistoryPage(sales, printer, posClock, loadOnShow: false);
                ((DataGrid)history.FindName("HistoryGrid")).ItemsSource = new[]
                {
                    new SaleHistoryItem(1, "S2627-000041", new DateOnly(2026, 10, 3), SaleStatus.Completed,
                        2, new Money(109), "CASH", new DateTime(2026, 10, 3, 11, 15, 0), null),
                    new SaleHistoryItem(2, "S2627-000040", new DateOnly(2026, 10, 3), SaleStatus.Void,
                        1, new Money(59), "UPI", new DateTime(2026, 10, 3, 10, 20, 0), "Mistake"),
                };
                ((DataGrid)history.FindName("DetailGrid")).ItemsSource = lines;
                ((DataGrid)history.FindName("HistoryGrid")).ItemsSource = new[]
                {
                    new SaleHistoryItem(1, "S2627-000041", new DateOnly(2026, 10, 3), SaleStatus.Completed,
                        2, new Money(109), "CASH", new DateTime(2026, 10, 3, 11, 15, 0), null),
                    new SaleHistoryItem(3, "S2627-000039", new DateOnly(2026, 10, 2), SaleStatus.PartiallyRefunded,
                        3, new Money(240), "UPI", new DateTime(2026, 10, 2, 18, 5, 0), null),
                    new SaleHistoryItem(4, "S2627-000038", new DateOnly(2026, 10, 2), SaleStatus.Refunded,
                        1, new Money(30), "CASH", new DateTime(2026, 10, 2, 17, 45, 0), null),
                    new SaleHistoryItem(2, "S2627-000040", new DateOnly(2026, 10, 3), SaleStatus.Void,
                        1, new Money(59), "UPI", new DateTime(2026, 10, 3, 10, 20, 0), "Mistake"),
                };
                var historyHost = new Window { Content = history, Width = 1120, Height = 720 };
                historyHost.Show();
                // Date pickers show the app's dd-MM-yyyy dates whatever the PC's region, and fit them.
                var fromPicker = (DatePicker)history.FindName("FromPicker");
                fromPicker.SelectedDate = new DateTime(2026, 10, 24);
                historyHost.UpdateLayout();
                fromPicker.Text.ShouldBe("24-10-2026");
                var pickerText = FindVisualChild<System.Windows.Controls.Primitives.DatePickerTextBox>(fromPicker)!;
                pickerText.ExtentWidth.ShouldBeLessThanOrEqualTo(pickerText.ViewportWidth + 0.5);
                VerifyAccessibleNames(historyHost);
                Capture(historyHost, "sales-history.en.png");
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(historyHost, "sales-history.gu.png");
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                historyHost.Close();

                ReturnableSaleLine[] returnable =
                [
                    new(lines[0], Quantity.One) { Refundable = new Money(56), RemainingRefund = new Money(28),
                        OriginalLotAvailable = true },
                    // The lot that would take this line back has expired since the sale.
                    new(lines[1] with { ExpiryDate = new DateOnly(2026, 10, 10) }, new Quantity(1.25m))
                        { Refundable = new Money(53), RemainingRefund = new Money(53), ResaleExpired = true },
                ];
                var returning = new SaleActionWindow(true, returnable, "Bill S2627-000041 · 03-10-2026 · ₹109.00");
                ((TextBox)returning.FindName("ReasonBox")).Text = "Customer changed mind";
                returning.Show();
                ((DataGrid)returning.FindName("ReturnGrid")).UpdateLayout();
                Capture(returning, "return.en.png");
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(returning, "return.gu.png");
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                returning.Close();

                var recreation = new ReturnRecreationWindow(new ReturnRecreationDraft(lines[0].Id, "SALT-1",
                    "Tata Salt 1 kg", "ટાટા મીઠું", null, null, null, null, null, new Percent(5), null, true,
                    null, false, "SALEABLE", null, null, new Money(20), new Money(30), new Money(32),
                    ["category", "unit", "tax rate", "tax mode", "HSN", "expiry tracking"]),
                    new ReturnRecreationOptions([new(1, "Staples")], [new(1, "PCS — Pieces")],
                        [new(1, "GST 5%", 5000)]));
                recreation.Show();
                Capture(recreation, "return-recreation.en.png");
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                recreation.Close();
                var guRecreation = new ReturnRecreationWindow(new ReturnRecreationDraft(lines[0].Id, "SALT-1",
                    "Tata Salt 1 kg", "ટાટા મીઠું", null, null, null, null, null, new Percent(5), null, true,
                    null, false, "SALEABLE", null, null, new Money(20), new Money(30), new Money(32),
                    ["category", "tax mode"]),
                    new ReturnRecreationOptions([new(1, "Staples")], [new(1, "PCS — Pieces")],
                        [new(1, "GST 5%", 5000)]));
                guRecreation.Show();
                Capture(guRecreation, "return-recreation.gu.png");
                guRecreation.Close();
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("en-IN");

                var voidDialog = new SaleActionWindow(false, context: "Bill S2627-000041 · 03-10-2026 · ₹109.00");
                voidDialog.Title.ShouldBe("Void");
                voidDialog.Show();
                Capture(voidDialog, "void.en.png");
                // A failed submit marks the field inline, lists it in the summary and keeps the dialog open.
                ((Button)voidDialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                ((FormField)voidDialog.FindName("ReasonField")).HasError.ShouldBeTrue();
                ((FormErrorSummary)voidDialog.FindName("Summary")).HasMessage.ShouldBeTrue();
                voidDialog.IsVisible.ShouldBeTrue();
                voidDialog.Close();

                var quantityDialog = new QuantityWindow(new Quantity(2), allowZero: true);
                quantityDialog.Show();
                Capture(quantityDialog, "quantity.en.png");
                quantityDialog.Close();

                var dayEnd = new DayEndWindow(new DayEndSummary(new DateOnly(2026, 10, 3), 12,
                    new Money(3720), new Money(120), new Money(3428.571m), new Money(171.429m),
                    Money.Zero, [new(PaymentMode.Cash, new Money(2800)),
                        new(PaymentMode.Upi, new Money(800))], 1, new Money(59), 2,
                    new Money(180), new Money(50), new Money(2460), new Money(80),
                    new Money(210), new Money(3600)), printer);
                dayEnd.Show();
                Capture(dayEnd, "day-end.en.png");
                dayEnd.Close();

                var lotPicker = new LotPickerWindow([
                    new LotItem(1, 1, "Tata Salt 1 kg", "L2627-000014", "SALT-2409",
                        new DateOnly(2026, 9, 28), new DateOnly(2027, 9, 28),
                        new Money(20), new Money(30), new Money(28), new Quantity(50), new Quantity(46), true),
                    new LotItem(2, 1, "Tata Salt 1 kg", "L2627-000021", "SALT-2410",
                        new DateOnly(2026, 10, 1), new DateOnly(2027, 10, 1),
                        new Money(21), new Money(32), new Money(29), new Quantity(40), new Quantity(36), true),
                ], 1);
                lotPicker.Show();
                Capture(lotPicker, "lot-picker.en.png");
                lotPicker.Close();

                var hold = new HoldBillWindow();
                ((TextBox)hold.FindName("LabelBox")).Text = "Mrs. Shah";
                hold.Show();
                Capture(hold, "hold-bill.en.png");
                hold.Close();

                var customerWindow = new CustomerWindow(sales, null, loadOnShow: false);
                ((DataGrid)customerWindow.FindName("CustomersGrid")).ItemsSource = new[]
                {
                    new CustomerItem(1, "Mrs. Shah", "9999999999", null),
                    new CustomerItem(2, "Shree Retail Traders", "8888888888", "24ABCDE1234F1Z5"),
                };
                customerWindow.Show();
                Capture(customerWindow, "customer.en.png");
                ((Button)customerWindow.FindName("CreateButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                ((FormField)customerWindow.FindName("NameField")).HasError.ShouldBeTrue();
                customerWindow.IsVisible.ShouldBeTrue();
                customerWindow.Close();

                ReceiptLine[] receiptLines =
                [
                    new("Tata Salt 1 kg", "ટાટા મીઠું 1 કિલો", new Quantity(2), new Money(28),
                        new Money(30), Money.Zero, new Money(53.333m), new Money(2.667m),
                        new Money(56), new Percent(5)),
                ];
                var saleReceipt = new ReceiptData("Hare Krushna", "Main Road, Ahmedabad", "9999999999",
                    "24ABCDE1234F1Z5", "REGULAR", "S2627-000041", new DateTime(2026, 10, 3, 11, 15, 0),
                    null, receiptLines, new Money(56), Money.Zero, new Money(53.333m), new Money(2.667m),
                    Money.Zero, new Money(56), new Money(4), [new(PaymentMode.Cash, new Money(60))],
                    new Money(4), "en-IN", "Thank you! Visit again.", false);
                CaptureReceipt(saleReceipt, "sale-receipt.en.png");
                CaptureReceipt(saleReceipt with { PrintLanguage = "gu-IN", Footer = "ફરી મળવા આવજો." },
                    "sale-receipt.gu.png");
                var returnReceipt = saleReceipt with
                {
                    TaxMode = "RETURN",
                    InvoiceNumber = "R2627-000007",
                    PrintLanguage = "gu-IN",
                    Payments = [],
                    GrandTotal = new Money(28),
                    Taxable = new Money(26.667m),
                    Tax = new Money(1.333m),
                    Gross = new Money(28),
                    Savings = Money.Zero,
                    Change = Money.Zero,
                    OriginalInvoiceNumber = "S2627-000041",
                    ReturnReason = "Opened package",
                    RefundMethod = "CASH",
                    Lines = [receiptLines[0] with { Quantity = Quantity.One, Taxable = new Money(26.667m),
                        Tax = new Money(1.333m), Total = new Money(28) }],
                };
                CaptureReceipt(returnReceipt, "return-receipt.gu.png");

                if (Environment.GetEnvironmentVariable("GROCERY_PHASE6_SCREENSHOT_DIR") is not null)
                {
                    using (var reportConnection = factory.Open())
                        reportConnection.Execute("""
                            INSERT INTO product_lot(product_id,lot_code,received_date,unit_cost,selling_price,qty_received,qty_on_hand)
                            VALUES (1,'REPORT-LOT','2026-10-01',20000,30000,10000,10000);
                            INSERT INTO sale(sale_date,status) VALUES ('2026-10-04','DRAFT'),('2026-10-05','DRAFT');
                            INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,
                                taxable_amount,tax_amount,line_total,unit_cost,cost_amount)
                            VALUES (1,1,1,1,'Tata Salt 1 kg',1000,30000,30000,0,30000,20000,20000),
                                   (2,1,1,1,'Tata Salt 1 kg',2000,30000,60000,0,60000,20000,40000);
                            UPDATE sale SET invoice_no='RPT-'||id,status='COMPLETED',line_count=1,
                                taxable_total=(SELECT SUM(taxable_amount) FROM sale_line WHERE sale_id=sale.id),
                                grand_total=(SELECT SUM(line_total) FROM sale_line WHERE sale_id=sale.id),
                                cost_total=(SELECT SUM(cost_amount) FROM sale_line WHERE sale_id=sale.id)
                            WHERE id IN (1,2);
                            INSERT INTO sale_payment(sale_id,mode,amount) VALUES (1,'CASH',30000),(2,'UPI',60000);
                            """);
                    var reportClock = new ReportClock();
                    var reportService = new ReportService(database, settings, reportClock, translations);
                    var reportOutput = new ReportOutputService(settings, translations);
                    var reportNavigation = new ReportNavigationState();
                    var reportsPage = new ReportsPage(reportService, reportOutput, reportClock,
                        new DisplayFormatter(), reportNavigation, loadOnShow: false);
                    var reportsHost = new Window { Content = reportsPage, Width = 1120, Height = 700 };
                    reportsHost.Show();
                    ((DataGrid)reportsPage.FindName("PnlGrid")).ItemsSource = new[]
                    {
                        new { Label = "Sales (net of GST)", Amount = "90.000" },
                        new { Label = "Cost of goods sold", Amount = "60.000" },
                        new { Label = "Gross profit", Amount = "30.000" },
                    };
                    ((TextBlock)reportsPage.FindName("PnlTotals")).Text = "Gross profit ₹30.000 (33.333%) · Net profit ₹30.000 (33.333%)";
                    ((FrameworkElement)reportsPage.FindName("PnlPanel")).Visibility = Visibility.Visible;
                    ((DataGrid)reportsPage.FindName("ReportGrid")).Visibility = Visibility.Collapsed;
                    Capture(reportsHost, "reports-pnl.en.png");
                    reportsHost.Width = 740;
                    Capture(reportsHost, "reports-pnl.narrow.en.png");
                    reportsHost.Width = 1120;
                    var reportPicker = (ComboBox)reportsPage.FindName("ReportPicker");
                    reportPicker.SelectedIndex = 1;
                    PopulateReportCapture(reportsPage, translations);
                    VerifyAccessibleNames(reportsHost);
                    Capture(reportsHost, "reports-table.en.png");
                    translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                    reportsPage.RefreshLocalizedChoices();
                    reportPicker.SelectedItem!.ToString().ShouldBe(translations["Reports_Kind_SalesRegister"]);
                    PopulateReportCapture(reportsPage, translations);
                    Capture(reportsHost, "reports-table.gu.png");
                    reportPicker.SelectedIndex = 4;
                    reportsPage.ShowHourlyReport(HourlySample(translations));
                    ((TextBlock)reportsPage.FindName("StatusText")).Text = translations.Format("Reports_RowCount", 2);
                    ((TextBlock)reportsPage.FindName("TotalsText")).Text = translations["Reports_Column_Total"] + ": 90.000";
                    Capture(reportsHost, "reports-hourly.gu.png");
                    translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                    reportsPage.RefreshLocalizedChoices();
                    reportPicker.SelectedItem!.ToString().ShouldBe(translations["Reports_Kind_HourlySales"]);
                    reportsPage.ShowHourlyReport(HourlySample(translations));
                    ((TextBlock)reportsPage.FindName("StatusText")).Text = translations.Format("Reports_RowCount", 2);
                    ((TextBlock)reportsPage.FindName("TotalsText")).Text = translations["Reports_Column_Total"] + ": 90.000";
                    Capture(reportsHost, "reports-hourly.en.png");
                    reportsHost.Close();

                    translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                    var dashboardPage = new DashboardPage(reportService, reportNavigation, loadOnShow: false);
                    var dashboardHost = new Window { Content = dashboardPage, Width = 1120, Height = 700 };
                    dashboardHost.Show();
                    PopulateDashboardCapture(dashboardPage, translations);
                    Capture(dashboardHost, "dashboard.gu.png");
                    dashboardHost.Width = 740;
                    PopulateDashboardCapture(dashboardPage, translations);
                    Capture(dashboardHost, "dashboard.narrow.gu.png");
                    dashboardHost.Close();
                    translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                    dashboardPage = new DashboardPage(reportService, reportNavigation, loadOnShow: false);
                    dashboardHost = new Window { Content = dashboardPage, Width = 1120, Height = 700 };
                    dashboardHost.Show();
                    PopulateDashboardCapture(dashboardPage, translations);
                    VerifyAccessibleNames(dashboardHost);
                    Capture(dashboardHost, "dashboard.en.png");
                    dashboardHost.Width = 740;
                    PopulateDashboardCapture(dashboardPage, translations);
                    Capture(dashboardHost, "dashboard.narrow.en.png");
                    dashboardHost.Close();
                }

                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                Capture(shell, "shell.en.png");
                ((Border)shell.FindName("LockOverlay")).Visibility = Visibility.Visible;
                Capture(shell, "session-lock.en.png");
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
                Environment.SetEnvironmentVariable("GROCERY_ERP_TEST_DATA_ROOT", previousTestRoot);
                SqliteConnectionFactory.ClearPoolForDataRoot(root);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(
            TimeSpan.FromSeconds(120),
            TestContext.Current.CancellationToken);
    }

    // Revision Phase 6 screens on real services: money-out entries in a finalized and an
    // open year with their correction history, the correction dialog, asset term
    // previews and Open and Finalized annual P&L at the 1366x768 desktop size.
    private static void VerifyPhaseSixScreens(TranslationSource translations)
    {
        string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-ui-phase6-{Guid.NewGuid():N}");
        // Page handlers await services; their continuations must return to this dispatcher.
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            var paths = new AppPaths(new SettingsBootstrap(root));
            var factory = new SqliteConnectionFactory(paths);
            using var database = new DbExecutor(factory);
            new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
            using var settings = new SettingsService(database, paths, new AuditLog());
            Run(() => settings.LoadAsync());
            var clock = new MutableClock(new DateOnly(2026, 2, 20));
            var periods = new PeriodGuard(settings, clock);
            var ledger = new LotQuantities(settings);
            var numbers = new DocNumberService(settings);
            var moneyOut = new MoneyOutService(database, ledger, numbers, periods, new AuditLog());
            var inventory = new InventoryService(database, ledger, numbers, periods, new AuditLog());
            var purchasing = new PurchasingService(database, ledger, numbers, periods, settings, new AuditLog());
            var assets = new AssetService(database, settings, periods, new AuditLog(), clock);
            var reports = new ReportService(database, settings, clock, translations);
            long lot;
            using (var connection = factory.Open())
            {
                long category = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Staples') RETURNING id");
                long product = connection.ExecuteScalar<long>("""
                    INSERT INTO product(sku,name,name_local,category_id,uom_id,tax_rate_id,track_expiry,default_selling_price)
                    SELECT 'SALT001','Tata Salt 1 kg','ટાટા મીઠું 1 કિલો',@category,u.id,t.id,1,30000
                    FROM uom u,tax_rate t WHERE u.code='PCS' ORDER BY t.id LIMIT 1 RETURNING id
                    """, new { category });
                lot = connection.ExecuteScalar<long>("""
                    INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,unit_cost,selling_price,qty_received)
                    VALUES (@product,'L-0001','B-17','2026-01-15','2027-06-30',20000,30000,50000) RETURNING id
                    """, new { product });
                connection.Execute("UPDATE product_lot SET qty_on_hand=qty_received WHERE id=@lot", new { lot });
            }
            long electricity = Run(() => moneyOut.GetExpenseCategoriesAsync())[0].Id;
            Run(() => moneyOut.RecordExpenseAsync(new(new DateOnly(2026, 2, 10), electricity, new Money(1500), "UPI",
                "Power Co", "FEB-26", null), OperationGuard.NewId()));
            Run(() => moneyOut.RecordDamageAsync(new(lot, new DateOnly(2026, 2, 12), new Quantity(2), "BROKEN",
                Money.Zero, "Dropped carton"), OperationGuard.NewId()));
            Run(() => assets.SaveAssetAsync(null, new AssetInput("Visi-cooler 300 L", "Equipment", new DateOnly(2026, 1, 1),
                new DateOnly(2026, 1, 1), new Money(45000), null, DepreciationMethod.Wdv, new Percent(15), Money.Zero, null)));
            Run(() => assets.PostDueDepreciationAsync());

            clock.BusinessDate = new DateOnly(2026, 10, 23);
            Run(() => moneyOut.RecordExpenseAsync(new(new DateOnly(2026, 10, 20), electricity, new Money(1800), "UPI",
                "Power Co", "OCT-26", null), OperationGuard.NewId()));
            long employee = Run(() => moneyOut.SaveEmployeeAsync(null, new("Asha Patel", "9999999999", "Cashier",
                new DateOnly(2026, 4, 1), null, new Money(12000), true, null)));
            Run(() => moneyOut.PostSalaryRunAsync(new(new YearMonth(2026, 9), new DateOnly(2026, 10, 5), "BANK",
                [new(employee, new Money(12000), Money.Zero, Money.Zero, new Money(500), "September")]), OperationGuard.NewId()));
            Run(() => assets.PostDueDepreciationAsync()).CurrentDatedPeriods.Count.ShouldBe(2);
            ExpenseItem february = Run(() => moneyOut.GetExpensesAsync(new DateOnly(2026, 2, 10), new DateOnly(2026, 2, 10))).Single();
            Run(() => moneyOut.CorrectExpenseAsync(february, new ExpenseInput(february.Date, february.CategoryId, new Money(1650),
                february.Mode, february.PaidTo, february.Reference, "Late fee"),
                "Bill had a late fee", OperationGuard.NewId()));
            Run(() => new AnnualCloseService(database, periods, clock).EnsureFinalizedAsync()).ShouldBe(1);

            var moneyOutPage = new MoneyOutPage(moneyOut, inventory, clock);
            // Lists open on the last three months; widen them to reach the finalized year.
            ((DatePicker)moneyOutPage.FindName("DamageFromBox")).SelectedDate = new DateTime(2026, 1, 1);
            ((DatePicker)moneyOutPage.FindName("ExpenseFromBox")).SelectedDate = new DateTime(2026, 1, 1);
            var moneyOutHost = new Window { Content = moneyOutPage, Width = ShellPageWidth, Height = ShellPageHeight, Language = AppLanguage };
            moneyOutHost.Show();
            var damageGrid = (DataGrid)moneyOutPage.FindName("DamageGrid");
            PumpUntil(() => damageGrid.Items.Count == 1);
            damageGrid.SelectedIndex = 0;
            var damageDetails = (CorrectionDetailsPanel)moneyOutPage.FindName("DamageDetails");
            PumpUntil(() => damageDetails.Visibility == Visibility.Visible);
            TextOf(damageDetails).ShouldContain("FY 2025-26 is finalized");
            VerifyAccessibleNames(moneyOutHost);
            CaptureRevision(moneyOutHost, "damage-details.en.png");
            // The tabs are the page header: the active tab's actions share their line.
            var damageActions = (FrameworkElement)moneyOutPage.FindName("DamageActions");
            var damageTab = (FrameworkElement)((TabControl)moneyOutPage.FindName("MoneyOutTabs")).Items[0];
            double tabTop = damageTab.TranslatePoint(new Point(), moneyOutHost).Y;
            damageActions.TranslatePoint(new Point(), moneyOutHost).Y.ShouldBeGreaterThanOrEqualTo(tabTop - 1);
            damageActions.TranslatePoint(new Point(0, damageActions.ActualHeight), moneyOutHost).Y
                .ShouldBeLessThanOrEqualTo(tabTop + damageTab.ActualHeight + 1);

            ((TabControl)moneyOutPage.FindName("MoneyOutTabs")).SelectedIndex = 1;
            var expenseGrid = (DataGrid)moneyOutPage.FindName("ExpenseGrid");
            PumpUntil(() => expenseGrid.Items.Count == 2);
            var expenseDetails = (CorrectionDetailsPanel)moneyOutPage.FindName("ExpenseDetails");
            expenseGrid.SelectedItem = expenseGrid.Items.Cast<ExpenseItem>().Single(item => item.Date.Month == 2);
            PumpUntil(() => expenseDetails.Visibility == Visibility.Visible && TextOf(expenseDetails).Contains("Bill had a late fee"));
            TextOf(expenseDetails).ShouldContain("posts a correction dated 23-10-2026");
            CaptureRevision(moneyOutHost, "expense-details-finalized.en.png");
            expenseGrid.SelectedItem = expenseGrid.Items.Cast<ExpenseItem>().Single(item => item.Date.Month == 10);
            PumpUntil(() => TextOf(expenseDetails).Contains("Both annual periods of 20-10-2026 are open"));
            CaptureRevision(moneyOutHost, "expense-details-open.en.png");
            translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
            // The open details panel re-renders in the new language.
            TextOf(expenseDetails).ShouldContain(translations.Format("MoneyOut_TreatmentOpen", "20-10-2026"));
            TextOf(expenseDetails).ShouldNotContain("Both annual periods");
            CaptureRevision(moneyOutHost, "expense-details-open.gu.png");
            translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");

            ExpenseItem corrected = expenseGrid.Items.Cast<ExpenseItem>().Single(item => item.Date.Month == 2);
            CorrectionDating dating = Run(() => moneyOut.PreviewCorrectionAsync(corrected.Date));
            var correction = new MoneyOutCorrectionWindow(moneyOut, corrected, Run(() => moneyOut.GetExpenseCategoriesAsync()),
                dating, voidEntry: false);
            correction.Language = AppLanguage;
            correction.Show();
            TextOf(correction).ShouldContain("posts a correction dated 23-10-2026");
            CaptureRevision(correction, "expense-correction.en.png");
            correction.Close();
            var voidWindow = new MoneyOutCorrectionWindow(moneyOut, corrected, Run(() => moneyOut.GetExpenseCategoriesAsync()),
                dating, voidEntry: true);
            voidWindow.Language = AppLanguage;
            voidWindow.Show();
            CaptureRevision(voidWindow, "expense-void.en.png");
            voidWindow.Close();

            ((TextBox)moneyOutPage.FindName("SalaryFromMonth")).Text = "2026-09";
            ((TextBox)moneyOutPage.FindName("SalaryToMonth")).Text = "2026-09";
            ((TabControl)moneyOutPage.FindName("MoneyOutTabs")).SelectedIndex = 3;
            var salaryGrid = (DataGrid)moneyOutPage.FindName("SalaryRegisterGrid");
            PumpUntil(() => salaryGrid.Items.Count == 2);
            salaryGrid.SelectedIndex = 0;
            var salaryDetails = (CorrectionDetailsPanel)moneyOutPage.FindName("SalaryDetails");
            PumpUntil(() => salaryDetails.Visibility == Visibility.Visible);
            TextOf(salaryDetails).ShouldContain("Both annual periods of 30-09-2026 are open");
            CaptureRevision(moneyOutHost, "payroll-register.en.png");
            moneyOutHost.Close();

            var assetsPage = new AssetsPage(assets, clock, purchasing);
            var assetsHost = new Window { Content = assetsPage, Width = ShellPageWidth, Height = ShellPageHeight, Language = AppLanguage };
            assetsHost.Show();
            var assetGrid = (DataGrid)assetsPage.FindName("AssetGrid");
            PumpUntil(() => assetGrid.Items.Count == 1);
            assetGrid.SelectedIndex = 0;
            ((Button)assetsPage.FindName("EditAssetButton")).IsEnabled.ShouldBeTrue();
            assetsHost.Close();
            // Editing terms previews the recalculated months before saving; blocked changes cannot save.
            var assetForm = new AssetFormWindow(assets, clock, [], (AssetItem)assetGrid.Items[0]) { Language = AppLanguage };
            assetForm.Show();
            ((TextBox)assetForm.FindName("AssetRate")).Text = "20";
            var assetPreview = (TextBlock)assetForm.FindName("AssetFinancialLockText");
            PumpUntil(() => assetPreview.Text.Contains("New terms apply from 2026-04", StringComparison.Ordinal));
            ((FrameworkElement)assetForm.FindName("EffectiveField")).Visibility.ShouldBe(Visibility.Visible);
            CaptureRevision(assetForm, "asset-terms-preview.en.png");
            ((TextBox)assetForm.FindName("AssetRate")).Text = "15";
            ((TextBox)assetForm.FindName("AssetCost")).Text = "46000";
            PumpUntil(() => assetPreview.Text.Contains("Cost and in-use date are fixed", StringComparison.Ordinal));
            ((Button)assetForm.FindName("SaveAssetButton")).IsEnabled.ShouldBeFalse();
            CaptureRevision(assetForm, "asset-terms-blocked.en.png");
            assetForm.Close();

            var reportsPage = new ReportsPage(reports, new ReportOutputService(settings, translations), clock,
                new DisplayFormatter(), new ReportNavigationState());
            var reportsHost = new Window { Content = reportsPage, Width = ShellPageWidth, Height = ShellPageHeight, Language = AppLanguage };
            reportsHost.Show();
            var status = (TextBlock)reportsPage.FindName("PnlStatusText");
            PumpUntil(() => status.Text.StartsWith("Open · editable through 07-01-2027", StringComparison.Ordinal));
            ((ComboBox)reportsPage.FindName("ScopePicker")).SelectedIndex = 2;
            var periodPicker = (ComboBox)reportsPage.FindName("PnlPeriodPicker");
            PumpUntil(() => periodPicker.Items.Count == 2);
            periodPicker.SelectedIndex = 1;
            PumpUntil(() => status.Text.StartsWith("Finalized · FY 2025-26", StringComparison.Ordinal));
            var pnlGrid = (DataGrid)reportsPage.FindName("PnlGrid");
            pnlGrid.Items.Count.ShouldBeGreaterThan(5);
            VerifyAccessibleNames(reportsHost);
            CaptureRevision(reportsHost, "pnl-fiscal-finalized.en.png");
            translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
            PumpUntil(() => status.Text.StartsWith("અંતિમ", StringComparison.Ordinal) || !status.Text.StartsWith("Finalized", StringComparison.Ordinal));
            CaptureRevision(reportsHost, "pnl-fiscal-finalized.gu.png");
            translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
            ((ComboBox)reportsPage.FindName("ScopePicker")).SelectedIndex = 1;
            PumpUntil(() => status.Text.StartsWith("Open · editable through 07-01-2027", StringComparison.Ordinal));
            ((CheckBox)reportsPage.FindName("MonthsCheck")).IsChecked = true;
            ((CheckBox)reportsPage.FindName("MonthsCheck")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => ((DataGrid)reportsPage.FindName("ReportGrid")).Visibility == Visibility.Visible);
            CaptureRevision(reportsHost, "pnl-calendar-open-months.en.png");
            reportsHost.Close();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            SqliteConnectionFactory.ClearPoolForDataRoot(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Revision Phase 7 screens on real services at the 1366x768 desktop size: Products pages
    // sized to the table, exact SKU first, server filters, an all-matching selection reviewed
    // and run in bounded batches, the product panel building Lots only when opened, and the
    // Purchases and Vendors tables and entry forms.
    private static void VerifyPhaseSevenScreens(TranslationSource translations)
    {
        string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-ui-phase7-{Guid.NewGuid():N}");
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            var paths = new AppPaths(new SettingsBootstrap(root));
            var factory = new SqliteConnectionFactory(paths);
            using var database = new DbExecutor(factory);
            new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
            using var settings = new SettingsService(database, paths, new AuditLog());
            Run(() => settings.LoadAsync());
            var clock = new MutableClock(new DateOnly(2026, 10, 23));
            var periods = new PeriodGuard(settings, clock);
            var ledger = new LotQuantities(settings);
            var numbers = new DocNumberService(settings);
            var catalog = new CatalogService(database, new AuditLog(), settings, clock);
            var inventory = new InventoryService(database, ledger, numbers, periods, new AuditLog());
            var purchasing = new PurchasingService(database, ledger, numbers, periods, settings, new AuditLog());
            var moneyOut = new MoneyOutService(database, ledger, numbers, periods, new AuditLog());
            long staples, household, salt;
            using (var connection = factory.Open())
            {
                staples = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Staples') RETURNING id");
                household = connection.ExecuteScalar<long>("INSERT INTO category(name) VALUES ('Household') RETURNING id");
                long AddProduct(string sku, string name, string productClass, long stock, string? expiry = "2099-12-31")
                {
                    long id = connection.ExecuteScalar<long>("""
                        INSERT INTO product(sku,name,category_id,uom_id,tax_rate_id,track_expiry,default_selling_price,product_class)
                        SELECT @sku,@name,@staples,u.id,t.id,1,30000,@productClass
                        FROM uom u,tax_rate t WHERE u.code='PCS' ORDER BY t.id LIMIT 1 RETURNING id
                        """, new { sku, name, staples, productClass });
                    if (stock <= 0) return id;
                    long lot = connection.ExecuteScalar<long>("""
                        INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,unit_cost,selling_price,qty_received)
                        VALUES (@id,'L-'||@sku,'B-'||@sku,'2026-09-01',@expiry,20000,30000,@stock) RETURNING id
                        """, new { id, sku, expiry, stock });
                    connection.Execute("UPDATE product_lot SET qty_on_hand=qty_received WHERE id=@lot", new { lot });
                    return id;
                }
                // Sixty products, every third without stock, one with an expired lot.
                for (int index = 1; index <= 60; index++)
                    AddProduct($"P{index:000}", $"Product {index:00}", "SALEABLE", index % 3 == 0 ? 0 : 10_000,
                        index == 1 ? "2026-09-15" : "2099-12-31");
                // The exact SKU leads even though another product's SKU contains it and sorts first.
                AddProduct("SALT0011", "Black salt 500 g", "SALEABLE", 5_000);
                salt = AddProduct("SALT001", "Tata Salt 1 kg", "SALEABLE", 11_000);
                AddProduct("CLEAN01", "Shop floor cleaner", "INTERNAL", 2_000);
            }
            long vendor = Run(() => purchasing.CreateVendorAsync(new("Shree Wholesale Traders", null, "9999999999", null, null,
                null, "Ahmedabad", "Gujarat", "380001", null, null, null, null, 30, Money.Zero, null)));
            long draft = Run(() => purchasing.SaveDraftAsync(null, new PurchaseDraftInput(vendor, "SW-1042",
                new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20), null, Money.Zero, null,
                [new(salt, "SALT-2410", null, new DateOnly(2030, 10, 1), new Quantity(10), Quantity.Zero,
                    new Money(250), Money.Zero, new Percent(5), new Money(350), new Money(300))])));
            PostedPurchase posted = Run(() => purchasing.PostAsync(draft, OperationGuard.NewId()));

            var products = new ProductsPage(catalog, settings, new ProductExcelImporter(database, new AuditLog(), settings));
            var productsHost = new Window { Content = products, Width = ShellPageWidth, Height = ShellPageHeight, Language = AppLanguage };
            productsHost.Show();
            var grid = (DataGrid)products.FindName("ProductsGrid");
            var pager = (PagerBar)products.FindName("Pager");
            Button next = pager.NextButton;
            Button previous = pager.PreviousButton;
            PumpUntil(() => grid.Items.Count > 0 && next.IsEnabled);
            // A page is the rows that fit the table, so the table never scrolls inside.
            int fitted = grid.Items.Count;
            fitted.ShouldBeInRange(5, 40);
            productsHost.UpdateLayout();
            FindVisualChild<ScrollViewer>(grid)!.ScrollableHeight.ShouldBeLessThan(1);
            var first = (ProductListItem)grid.Items[0];
            next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => grid.Items.Count > 0 && ((ProductListItem)grid.Items[0]).Id != first.Id && previous.IsEnabled);
            pager.PageText.Text.ShouldBe(translations.Format("Pager_Page", 2));
            previous.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => grid.Items.Count == fitted && ((ProductListItem)grid.Items[0]).Id == first.Id);
            ShouldBeOneLine((FrameworkElement)products.FindName("FilterBar"));
            VerifyAccessibleNames(productsHost);
            CaptureRevision(productsHost, "products.en.png");

            // An exact SKU leads the result, and a search shows at most eight rows.
            var search = (TextBox)products.FindName("SearchBox");
            search.Text = "SALT001";
            PumpUntil(() => grid.Items.Count == 2 && ((ProductListItem)grid.Items[0]).Sku == "SALT001");
            ((ProductListItem)grid.Items[1]).Sku.ShouldBe("SALT0011");
            search.Text = "Product";
            PumpUntil(() => grid.Items.Count == 8 && next.IsEnabled);
            CaptureRevision(productsHost, "products-search.en.png");
            search.Text = string.Empty;
            PumpUntil(() => grid.Items.Count == fitted);

            // Filters run on the server; internal-use and stocked products are separate views.
            var classFilter = (ComboBox)products.FindName("ClassFilter");
            classFilter.SelectedIndex = 2;
            PumpUntil(() => grid.Items.Count == 1);
            ((ProductListItem)grid.Items[0]).ProductClass.ShouldBe("INTERNAL");
            classFilter.SelectedIndex = 1;
            ((ComboBox)products.FindName("ExpiryFilter")).SelectedIndex = 1;
            PumpUntil(() => grid.Items.Count == 1 && ((ProductListItem)grid.Items[0]).Sku == "P001");
            ((ProductListItem)grid.Items[0]).HasExpiredStock.ShouldBeTrue();
            ((ComboBox)products.FindName("ExpiryFilter")).SelectedIndex = 0;
            ((ComboBox)products.FindName("StockFilter")).SelectedIndex = 1;
            PumpUntil(() => grid.Items.Count == fitted && grid.Items.Cast<ProductListItem>().All(item => item.StockScaled > 0) && next.IsEnabled);

            // A checked page offers every matching row; the count comes from the server.
            var header = FindVisualChild<CheckBox>(FindVisualChild<DataGridColumnHeadersPresenter>(grid)!)!;
            header.IsChecked = true;
            header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var selection = (SelectionBar)products.FindName("Selection");
            selection.Visibility.ShouldBe(Visibility.Visible);
            TextOf(selection).ShouldContain(translations.Format("Selection_Count", fitted));
            Button allMatching = Descendants<Button>(selection)
                .Single(button => Equals(button.Content, translations["Selection_AllMatching"]));
            allMatching.Visibility.ShouldBe(Visibility.Visible);
            allMatching.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            // 40 stocked numbered products plus the two salts; the internal cleaner is filtered out.
            PumpUntil(() => TextOf(selection).Contains(translations.Format("Selection_AllMatchingCount", 42), StringComparison.Ordinal));
            CaptureRevision(productsHost, "products-all-matching.en.png");

            // The review re-resolves the rows on the server, then runs them in bounded batches.
            BulkAction categoryChange = products.CatalogBulk(new CatalogBulkCommand(CatalogBulkAction.Category, household),
                "Catalog_SetCategory");
            var bulk = new BulkActionWindow(new BulkAction
            {
                Title = categoryChange.Title,
                RunLabel = categoryChange.RunLabel,
                SelectionSummary = categoryChange.SelectionSummary,
                BatchSize = 10,
                Plan = categoryChange.Plan,
                Run = categoryChange.Run,
            })
            { Language = AppLanguage };
            bulk.Show();
            var runBulk = (Button)bulk.FindName("RunButton");
            PumpUntil(() => runBulk.IsEnabled);
            ((TextBlock)bulk.FindName("PlanText")).Text.ShouldBe(translations.Format("Bulk_PlanSummary", 42, 42, 0));
            ((TextBlock)bulk.FindName("SelectionText")).Text.ShouldContain(translations.Format("Selection_AllMatchingCount", 42));
            CaptureRevision(bulk, "bulk-review.en.png");
            runBulk.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => ((FrameworkElement)bulk.FindName("ResultPanel")).Visibility == Visibility.Visible, 15_000);
            ((TextBlock)bulk.FindName("ResultText")).Text.ShouldBe(translations.Format("Bulk_ResultSummary", 42, 0, 0, 0));
            ((TextBlock)bulk.FindName("ErrorText")).Text.ShouldBeEmpty();
            bulk.ChangedAny.ShouldBeTrue();
            CaptureRevision(bulk, "bulk-result.en.png");
            bulk.Close();
            using (var connection = factory.Open())
            {
                connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product WHERE category_id=@household", new { household }).ShouldBe(42);
                connection.ExecuteScalar<long>("SELECT COUNT(*) FROM operation WHERE op_type='BULK_BATCH'").ShouldBe(5);
                connection.ExecuteScalar<long>("SELECT category_id FROM product WHERE sku='CLEAN01'").ShouldBe(staples);
                connection.ExecuteScalar<long>("SELECT category_id FROM product WHERE sku='P003'").ShouldBe(staples);
            }

            // The product panel opens beside the table; Lots is built only when selected.
            ((Button)Descendants<Button>(selection).Single(button => Equals(button.Content, translations["Selection_Clear"])))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            selection.Visibility.ShouldBe(Visibility.Collapsed);
            var details = (ProductDetailsPanel)products.FindName("Details");
            details.Initialize(catalog, inventory, clock.BusinessDate, regularTaxMode: true);
            search.Text = "SALT001";
            PumpUntil(() => grid.Items.Count > 0 && ((ProductListItem)grid.Items[0]).Sku == "SALT001");
            grid.SelectedIndex = 0;
            Task opening = (Task)typeof(ProductsPage).GetMethod("OpenDetailsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(products, [grid.Items[0], false])!;
            PumpUntil(() => opening.IsCompleted);
            opening.GetAwaiter().GetResult();
            details.Visibility.ShouldBe(Visibility.Visible);
            details.ProductId.ShouldBe(salt);
            // The panel opens beside the list, which stays usable; the arrows step through its rows.
            productsHost.UpdateLayout();
            details.Panel.IsFullWidth.ShouldBeFalse();
            grid.ActualWidth.ShouldBeGreaterThan(300);
            details.TranslatePoint(new Point(), productsHost).X
                .ShouldBeGreaterThanOrEqualTo(grid.TranslatePoint(new Point(grid.ActualWidth, 0), productsHost).X - 1);
            details.Panel.PreviousButton.IsEnabled.ShouldBeFalse();
            details.Panel.NextButton.IsEnabled.ShouldBeTrue();
            details.Panel.NextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => details.ProductId != salt && grid.SelectedIndex == 1);
            details.Panel.PreviousButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => details.ProductId == salt && grid.SelectedIndex == 0);
            ((ContentControl)details.FindName("LotsHost")).Content.ShouldBeNull();
            // The panel has Basics and Lots only; the pre-upgrade ledger History tab is gone.
            ((TabControl)details.FindName("Tabs")).Items.Count.ShouldBe(2);
            details.FindName("HistoryHost").ShouldBeNull();
            var form = (ProductForm)details.FindName("Form");
            ((TextBox)form.FindName("NameBox")).Text.ShouldBe("Tata Salt 1 kg");
            ((FrameworkElement)form.FindName("FirstLotSection")).Visibility.ShouldBe(Visibility.Collapsed);
            // Beside the open panel the filters fold behind More filters and the row stays one line.
            productsHost.UpdateLayout();
            PumpUntil(() => ((Panel)products.FindName("MoreFiltersPanel")).Children.Contains((UIElement)products.FindName("ClassFilter")));
            ShouldBeOneLine((FrameworkElement)products.FindName("FilterBar"));
            CaptureRevision(productsHost, "product-basics.en.png");
            ((TabControl)details.FindName("Tabs")).SelectedItem = details.FindName("LotsTab");
            var lots = (ProductLotsView)((ContentControl)details.FindName("LotsHost")).Content;
            lots.ShouldNotBeNull();
            var lotsGrid = (DataGrid)lots.FindName("LotsGrid");
            // The opening lot and the posted purchase lot.
            PumpUntil(() => lotsGrid.Items.Count == 2);
            // The lot actions share the tab row, level with the headers, so the table gains that row.
            var tabActions = (ContentControl)details.FindName("TabActionsHost");
            var lotActions = (FrameworkElement)lots.FindName("ActionBar");
            var lotsTab = (TabItem)details.FindName("LotsTab");
            tabActions.Content.ShouldBeSameAs(lotActions);
            tabActions.Visibility.ShouldBe(Visibility.Visible);
            productsHost.UpdateLayout();
            Math.Abs(lotActions.TranslatePoint(new Point(0, 0), details).Y - lotsTab.TranslatePoint(new Point(0, 0), details).Y)
                .ShouldBeLessThan(1);
            lotActions.TranslatePoint(new Point(0, 0), details).X
                .ShouldBeGreaterThan(lotsTab.TranslatePoint(new Point(lotsTab.ActualWidth, 0), details).X);
            void ShouldFitTabRow()
            {
                lotActions.ActualHeight.ShouldBeLessThanOrEqualTo(lotsTab.ActualHeight + 0.5);
                ((FrameworkElement)lots.FindName("ActionButtons")).ActualWidth.ShouldBeLessThanOrEqualTo(lotActions.ActualWidth + 0.5);
            }
            ShouldFitTabRow();
            CaptureRevision(productsHost, "product-lots.en.png");
            TextBlock stockText = details.Panel.SummaryText;
            string englishStock = stockText.Text;
            translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
            // Text set in code follows the language too: the folded-filters toggle at once, the
            // panel header and the row count after their refresh.
            ((string)((ToggleButton)products.FindName("MoreFiltersToggle")).Content).ShouldStartWith(translations["Common_MoreFilters"]);
            PumpUntil(() => stockText.Text != englishStock &&
                ((PagerBar)products.FindName("Pager")).Status == translations.Format("Catalog_Count", grid.Items.Count));
            // Longer Gujarati labels fold the last actions behind More; each item still runs its button.
            productsHost.UpdateLayout();
            PumpUntil(() => ((FrameworkElement)lots.FindName("MoreActionsMenu")).Visibility == Visibility.Visible);
            productsHost.UpdateLayout();
            ShouldFitTabRow();
            var moreActions = (MenuItem)lots.FindName("MoreActions");
            moreActions.Items.Count.ShouldBeGreaterThan(0);
            var labelButton = (Button)lots.FindName("LabelButton");
            ((MenuItem)moreActions.Items[^1]).Header.ShouldBe(labelButton.Content);
            ((MenuItem)moreActions.Items[^1]).IsEnabled.ShouldBe(labelButton.IsEnabled);
            CaptureRevision(productsHost, "product-lots.gu.png");
            translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
            details.Panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(details)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.KeyDownEvent });
            details.Visibility.ShouldBe(Visibility.Collapsed);
            productsHost.Close();

            // Purchases: a document number finds its purchase; the toolbar shows the posted GRN.
            var purchases = new PurchasesPage(purchasing);
            var purchasesHost = new Window { Content = purchases, Width = ShellPageWidth, Height = ShellPageHeight, Language = AppLanguage };
            purchasesHost.Show();
            var purchaseGrid = (DataGrid)purchases.FindName("PurchasesGrid");
            PumpUntil(() => purchaseGrid.Items.Count == 1);
            ((TextBox)purchases.FindName("DocumentFilter")).Text = posted.DocumentNumber.ToLowerInvariant();
            PumpUntil(() => purchaseGrid.Items.Count == 1 &&
                ((PurchaseListItem)purchaseGrid.Items[0]).DocumentNumber == posted.DocumentNumber);
            ((TextBox)purchases.FindName("DocumentFilter")).Text = "NO-SUCH-BILL";
            PumpUntil(() => purchaseGrid.Items.Count == 0 &&
                ((FrameworkElement)purchases.FindName("EmptyText")).Visibility == Visibility.Visible);
            ((TextBox)purchases.FindName("DocumentFilter")).Text = string.Empty;
            PumpUntil(() => purchaseGrid.Items.Count == 1);
            purchaseGrid.SelectedIndex = 0;
            ((Button)purchases.FindName("OpenButton")).IsEnabled.ShouldBeTrue();
            ShouldBeOneLine((FrameworkElement)purchases.FindName("FilterBar"));
            CaptureRevision(purchasesHost, "purchases.en.png");
            // Opening a purchase shows it in the detail panel at full width; its actions move into the
            // panel, and the width switch puts the list back beside it, a choice the screen remembers.
            var invoice = (DetailPanel)purchases.FindName("Invoice");
            double[] listWidths = [.. purchaseGrid.Columns.Select(static column => column.ActualWidth)];
            ((Button)purchases.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => invoice.Visibility == Visibility.Visible);
            invoice.IsFullWidth.ShouldBeTrue();
            invoice.Title.ShouldContain(posted.DocumentNumber);
            invoice.Facts.ShouldContain("Shree Wholesale Traders");
            var invoiceView = (PurchaseInvoiceView)purchases.FindName("InvoiceView");
            ((DataGrid)invoiceView.FindName("LinesGrid")).Items.Count.ShouldBe(1);
            ((Button)purchases.FindName("OpenButton")).Visibility.ShouldBe(Visibility.Collapsed);
            ((Button)purchases.FindName("InvoiceVendorReturnButton")).Visibility.ShouldBe(Visibility.Visible);
            ((Button)purchases.FindName("InvoiceEditButton")).Visibility.ShouldBe(Visibility.Collapsed);
            // Full width covers the list without narrowing it.
            purchasesHost.UpdateLayout();
            purchaseGrid.IsVisible.ShouldBeFalse();
            VerifyAccessibleNames(purchasesHost);
            CaptureRevision(purchasesHost, "purchase-panel-full.en.png");
            invoice.WidthButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            purchasesHost.UpdateLayout();
            invoice.IsFullWidth.ShouldBeFalse();
            purchaseGrid.IsVisible.ShouldBeTrue();
            purchaseGrid.ActualWidth.ShouldBeGreaterThan(300);
            File.ReadAllText(TableColumnLayouts.PreferenceFile("detail-panels.json")).ShouldContain("\"Purchases\":false");
            CaptureRevision(purchasesHost, "purchase-panel-side.en.png");
            invoice.WidthButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            invoice.IsFullWidth.ShouldBeTrue();
            invoice.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(purchasesHost)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.KeyDownEvent });
            invoice.Visibility.ShouldBe(Visibility.Collapsed);
            ((Button)purchases.FindName("OpenButton")).Visibility.ShouldBe(Visibility.Visible);
            // The list's columns keep the widths they had before the panel opened.
            purchasesHost.UpdateLayout();
            for (int column = 0; column < listWidths.Length; column++)
                Math.Abs(purchaseGrid.Columns[column].ActualWidth - listWidths[column]).ShouldBeLessThan(1);
            purchasesHost.Close();

            var vendors = new VendorsPage(purchasing);
            var vendorsHost = new Window { Content = vendors, Width = ShellPageWidth, Height = ShellPageHeight, Language = AppLanguage };
            vendorsHost.Show();
            var vendorGrid = (DataGrid)vendors.FindName("VendorsGrid");
            PumpUntil(() => vendorGrid.Items.Count == 1);
            ((VendorItem)vendorGrid.Items[0]).BalanceDue.ShouldBe(posted.Totals.GrandTotal);
            // The select box fits its column whole, and the column widens like any other.
            vendorsHost.UpdateLayout();
            DataGridColumn selectColumn = vendorGrid.Columns[0];
            selectColumn.CanUserResize.ShouldBeTrue();
            CheckBox rowCheck = Descendants<CheckBox>(FindVisualChild<DataGridRow>(vendorGrid)!).First();
            (rowCheck.ActualWidth + 12).ShouldBeLessThanOrEqualTo(selectColumn.ActualWidth);
            // The row count and page arrows sit in the footer under the table.
            var vendorPager = (PagerBar)vendors.FindName("Pager");
            vendorPager.TranslatePoint(new Point(), vendorsHost).Y
                .ShouldBeGreaterThanOrEqualTo(vendorGrid.TranslatePoint(new Point(0, vendorGrid.ActualHeight), vendorsHost).Y - 1);
            CaptureRevision(vendorsHost, "vendors.en.png");
            // The ledger opens in the detail panel at full width, in place of the old ledger window.
            vendorGrid.SelectedIndex = 0;
            ((Button)vendors.FindName("LedgerButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var ledgerPanel = (DetailPanel)vendors.FindName("Ledger");
            var ledgerView = (VendorLedgerView)vendors.FindName("LedgerView");
            PumpUntil(() => ledgerPanel.Visibility == Visibility.Visible && ((DataGrid)ledgerView.FindName("PurchasesGrid")).Items.Count > 0);
            ledgerPanel.Title.ShouldBe("Shree Wholesale Traders");
            ledgerPanel.IsFullWidth.ShouldBeTrue();
            ((Button)vendors.FindName("EditButton")).Visibility.ShouldBe(Visibility.Collapsed);
            CaptureRevision(vendorsHost, "vendor-ledger-panel.en.png");
            ledgerPanel.CloseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ledgerPanel.Visibility.ShouldBe(Visibility.Collapsed);
            ((Button)vendors.FindName("EditButton")).Visibility.ShouldBe(Visibility.Visible);
            vendorsHost.Close();

            // Billing: a typed name lists its matches to pick from, rather than taking the first or
            // offering to save the name as a barcode; a single match joins the bill directly.
            var sales = new SalesService(database, ledger, numbers, periods, settings, new AuditLog(), clock);
            var billing = new BillingPage(sales, new ScanResolver(database), catalog, inventory, new ReceiptPrinter(settings),
                loadOnShow: false);
            var billingHost = new Window { Content = billing, Width = ShellPageWidth, Height = ShellPageHeight, Language = AppLanguage };
            billingHost.Show();
            MethodInfo addText = typeof(BillingPage).GetMethod("AddTextAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void TypeIntoBill(string text)
            {
                var typing = (Task)addText.Invoke(billing, [text, false])!;
                PumpUntil(() => typing.IsCompleted);
                typing.GetAwaiter().GetResult();
            }
            var billGrid = (DataGrid)billing.FindName("BillGrid");
            var billChoices = (ProductChoices)billing.FindName("ProductChoices");
            var billChoiceList = (ListBox)billChoices.FindName("List");
            TypeIntoBill("2*salt");
            billChoices.IsOpen.ShouldBeTrue();
            string.Join(",", billChoiceList.Items.Cast<ProductListItem>().Select(static item => item.Sku).Order(StringComparer.Ordinal))
                .ShouldBe("SALT001,SALT0011");
            billGrid.Items.Count.ShouldBe(0);
            CaptureRevision(billChoices, "billing-choices.en.png");
            billChoiceList.SelectedItem = billChoiceList.Items.Cast<ProductListItem>().Single(static item => item.Sku == "SALT001");
            billing.Dispatcher.Invoke(() => billChoiceList.RaiseEvent(
                new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(billingHost)!, 0, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent }));
            PumpUntil(() => billGrid.Items.Count == 1);
            billChoices.IsOpen.ShouldBeFalse();
            ((SaleLine)billGrid.Items[0]).ProductName.ShouldBe("Tata Salt 1 kg");
            // The quantity typed before * carries over to the picked product.
            ((SaleLine)billGrid.Items[0]).Quantity.ShouldBe(new Quantity(2));
            TypeIntoBill("black salt");
            PumpUntil(() => billGrid.Items.Count == 2);
            billChoices.IsOpen.ShouldBeFalse();
            TypeIntoBill("no such thing");
            billChoices.IsOpen.ShouldBeFalse();
            billGrid.Items.Count.ShouldBe(2);
            ((TextBlock)billing.FindName("StatusText")).Text.ShouldBe(translations["Purchasing_NoProduct"]);
            billingHost.Close();
            long billId = (long)typeof(BillingPage).GetField("_saleId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(billing)!;
            Money billTotal = Run(() => sales.GetAsync(billId)).Totals.GrandTotal;
            CompletedSale completedBill = Run(() => sales.CompleteAsync(new CompleteSaleCommand(billId,
                [new PaymentInput(PaymentMode.Cash, billTotal)], OperationGuard.NewId())));

            // Sales history: View bill items opens the bill beside the list, which stays usable.
            var salesHistory = new SalesHistoryPage(sales, new ReceiptPrinter(settings), clock);
            var salesHistoryHost = new Window { Content = salesHistory, Width = ShellPageWidth, Height = ShellPageHeight, Language = AppLanguage };
            salesHistoryHost.Show();
            var historyGrid = (DataGrid)salesHistory.FindName("HistoryGrid");
            PumpUntil(() => historyGrid.Items.Count == 1);
            historyGrid.SelectedIndex = 0;
            ((Button)salesHistory.FindName("ViewItemsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var billPanel = (DetailPanel)salesHistory.FindName("Detail");
            PumpUntil(() => billPanel.Visibility == Visibility.Visible && ((DataGrid)salesHistory.FindName("DetailGrid")).Items.Count == 2);
            billPanel.Title.ShouldContain(completedBill.InvoiceNumber);
            billPanel.IsFullWidth.ShouldBeFalse();
            salesHistoryHost.UpdateLayout();
            historyGrid.ActualWidth.ShouldBeGreaterThan(300);
            ((Button)salesHistory.FindName("ViewItemsButton")).Visibility.ShouldBe(Visibility.Collapsed);
            ((Button)salesHistory.FindName("DetailReprintButton")).IsEnabled.ShouldBeTrue();
            VerifyAccessibleNames(salesHistoryHost);
            CaptureRevision(salesHistoryHost, "sales-history-panel.en.png");
            billPanel.CloseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            billPanel.Visibility.ShouldBe(Visibility.Collapsed);
            ((Button)salesHistory.FindName("ViewItemsButton")).Visibility.ShouldBe(Visibility.Visible);
            salesHistoryHost.Close();

            // Forms mark required fields, list errors at the top and keep the typed values.
            var vendorForm = new VendorEditorWindow(purchasing) { Language = AppLanguage };
            vendorForm.Show();
            ((TextBox)vendorForm.FindName("GstinBox")).Text = "24ABC";
            ((Button)vendorForm.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ((FormErrorSummary)vendorForm.FindName("Summary")).HasMessage.ShouldBeTrue();
            ((FormField)vendorForm.FindName("NameField")).HasError.ShouldBeTrue();
            ((FormField)vendorForm.FindName("GstinField")).HasError.ShouldBeTrue();
            ((TextBox)vendorForm.FindName("GstinBox")).Text.ShouldBe("24ABC");
            CaptureRevision(vendorForm, "vendor-form-errors.en.png");
            vendorForm.Close();

            var expenseForm = new ExpenseFormWindow(moneyOut, clock, Run(() => moneyOut.GetExpenseCategoriesAsync())) { Language = AppLanguage };
            expenseForm.Show();
            var periodText = (TextBlock)expenseForm.FindName("PeriodText");
            PumpUntil(() => !string.IsNullOrEmpty(periodText.Text));
            CaptureRevision(expenseForm, "expense-form.en.png");
            expenseForm.Close();

            var damageForm = new LotDamageWindow(moneyOut, clock, inventory) { Language = AppLanguage };
            damageForm.Show();
            ((TextBox)damageForm.FindName("LotSearchBox")).Text = "SALT-2410";
            var lotChoice = (ComboBox)damageForm.FindName("LotBox");
            PumpUntil(() => lotChoice.Items.Count == 1 && lotChoice.SelectedIndex == 0);
            ((TextBlock)damageForm.FindName("LotText")).Text.ShouldContain("Tata Salt 1 kg");
            CaptureRevision(damageForm, "damage-form.en.png");
            damageForm.Close();

            // Every entry dialog opens on real services in the shared form layout.
            void Settle(int milliseconds)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                PumpUntil(() => watch.ElapsedMilliseconds >= milliseconds, milliseconds + 1000);
            }
            void CaptureForm(Window form, string fileName, bool fields = true)
            {
                form.Language = AppLanguage;
                form.Show();
                Settle(300);
                if (fields) Descendants<FormField>(form).ShouldNotBeEmpty(fileName);
                CaptureRevision(form, fileName);
                form.Close();
            }
            long helper = Run(() => moneyOut.SaveEmployeeAsync(null, new("Ravi Kumar", "9999999999", "Helper",
                new DateOnly(2026, 4, 1), null, new Money(9000), true, null)));
            IReadOnlyList<EmployeeItem> staff = Run(() => moneyOut.GetEmployeesAsync(new EmployeeFilter()));
            IReadOnlyList<ExpenseCategoryItem> expenseCategories = Run(() => moneyOut.GetExpenseCategoriesAsync());
            CaptureForm(new EmployeeFormWindow(moneyOut, clock), "employee-form.en.png");
            CaptureForm(new SalaryRunWindow(moneyOut, clock, staff.ToDictionary(static item => item.Id, static item => item.Name)),
                "salary-run.en.png");
            CaptureForm(new AdvanceFormWindow(moneyOut, clock, staff), "advance-form.en.png");
            CaptureForm(new ExpenseCategoriesWindow(moneyOut), "expense-categories.en.png");
            CaptureForm(new RepeatExpensesWindow(moneyOut, clock, expenseCategories), "repeat-expenses.en.png", fields: false);
            CaptureForm(new DirectLotWindow(inventory, salt, clock), "direct-lot.en.png");
            IReadOnlyList<LotItem> saltLots = Run(() => inventory.GetLotPageAsync(salt, limit: 10)).Items;
            CaptureForm(new LotBulkDamageWindow(moneyOut, clock, saltLots.Where(static lot => lot.OnHand > Quantity.Zero).ToArray()),
                "bulk-damage.en.png");
            var assets = new AssetService(database, settings, periods, new AuditLog(), clock);
            CaptureForm(new AssetFormWindow(assets, clock, []), "asset-form.en.png");
            Run(() => assets.SaveAssetAsync(null, new AssetInput("Display fridge", "Equipment", new DateOnly(2026, 4, 1),
                new DateOnly(2026, 4, 1), new Money(30000), null, DepreciationMethod.Slm, new Percent(10), Money.Zero, null)));
            AssetItem fridge = Run(() => assets.GetAssetPageAsync()).Items.Single();
            CaptureForm(new AssetDisposalWindow(assets, clock, fridge), "asset-disposal.en.png");
            helper.ShouldBeGreaterThan(0);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            SqliteConnectionFactory.ClearPoolForDataRoot(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    // Service work runs off this thread; the dispatcher keeps pumping so pages that
    // receive change messages can still marshal back to it.
    private static T Run<T>(Func<Task<T>> action)
    {
        Task<T> task = Task.Run(action);
        PumpUntil(() => task.IsCompleted, 30_000);
        return task.GetAwaiter().GetResult();
    }

    private static void Run(Func<Task> action)
    {
        Task task = Task.Run(action);
        PumpUntil(() => task.IsCompleted, 30_000);
        task.GetAwaiter().GetResult();
    }

    // A page's area in the shell at 1366x768: the window less its 220 px navigation pane, title
    // bar and header. Pages are captured and checked at this size, not the bare desktop size.
    private const double ShellPageWidth = 1146;
    private const double ShellPageHeight = 690;

    // A filter row that stays on one line keeps the table at its full height.
    private static void ShouldBeOneLine(FrameworkElement filterRow) => filterRow.ActualHeight.ShouldBeLessThan(46);

    // The app formats dates and numbers in this culture; the test skips its startup, so the
    // Phase 6 windows set it themselves.
    private static System.Windows.Markup.XmlLanguage AppLanguage =>
        System.Windows.Markup.XmlLanguage.GetLanguage(TranslationSource.FormatCulture.IetfLanguageTag);

    // Runs the queued Startup callback while WPF reports shutdown, so it returns without
    // calling App.OnStartup; later dispatcher pumping then runs only this test's work.
    private static void ConsumeApplicationStartup()
    {
        PropertyInfo shuttingDown = typeof(Application).GetProperty("IsShuttingDown",
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("WPF startup guard missing.");
        shuttingDown.SetValue(null, true);
        try
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        finally { shuttingDown.SetValue(null, false); }
    }

    // Lets queued async page work finish on this STA thread until the condition holds.
    private static void PumpUntil(Func<bool> condition, int timeoutMilliseconds = 5000,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        condition().ShouldBeTrue($"waited at line {line}");
    }

    private static string TextOf(DependencyObject root)
    {
        var text = new System.Text.StringBuilder();
        void Walk(DependencyObject node)
        {
            if (node is TextBlock block) text.Append(block.Text).Append('\n');
            if (node is ItemsControl items && items.ItemsSource is not null)
                foreach (object item in items.Items)
                    if (item is DependencyObject element) Walk(element);
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject element) Walk(element);
        }
        Walk(root);
        return text.ToString();
    }

    private static void CaptureRevision(Window window, string fileName)
    {
        window.UpdateLayout();
        CaptureRevision((Visual)window.Content, window.ActualWidth, window.ActualHeight, fileName);
    }

    // A popup draws in its own window, so a capture of the page leaves it out; this captures what it shows.
    private static void CaptureRevision(Popup popup, string fileName)
    {
        var content = (FrameworkElement)popup.Child;
        content.UpdateLayout();
        CaptureRevision(content, content.ActualWidth, content.ActualHeight, fileName);
    }

    private static void CaptureRevision(Visual surface, double actualWidth, double actualHeight, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("GROCERY_REVISION_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        int width = (int)Math.Ceiling(actualWidth);
        int height = (int)Math.Ceiling(actualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext context = background.RenderOpen())
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, fileName));
        encoder.Save(output);
    }

    private sealed class MutableClock(DateOnly date) : IClock
    {
        public DateOnly BusinessDate { get; set; } = date;
        public DateTime Now => BusinessDate.ToDateTime(new TimeOnly(12, 0));
    }

    private static void VerifyWheelScrolling()
    {
        Type routing = typeof(GroceryErp.App.App).Assembly.GetType(
            "GroceryErp.App.Presentation.ScrollWheelRouting")!;
        routing.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        var content = new StackPanel();
        for (int index = 0; index < 30; index++)
            content.Children.Add(new TextBlock { Text = $"Scrollable row {index}", Height = 40 });
        var viewer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var window = new Window { Content = viewer, Width = 320, Height = 220 };
        window.Show();
        window.UpdateLayout();
        viewer.ScrollableHeight.ShouldBeGreaterThan(0);
        for (int index = 0; index < 12; index++)
        {
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -10)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
            };
            content.Children[0].RaiseEvent(wheel);
            wheel.Handled.ShouldBeTrue();
        }
        viewer.UpdateLayout();
        viewer.VerticalOffset.ShouldBeGreaterThan(0);
        window.Close();

        var grid = new DataGrid
        {
            ItemsSource = Enumerable.Range(0, 100).Select(index => new { Name = $"Result {index}" }),
        };
        var gridWindow = new Window { Content = grid, Width = 320, Height = 220 };
        gridWindow.Show();
        gridWindow.UpdateLayout();
        var gridViewer = FindVisualChild<ScrollViewer>(grid)!;
        gridViewer.ScrollableHeight.ShouldBeGreaterThan(0);
        var firstRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        for (int index = 0; index < 12; index++)
        {
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -10)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
            };
            firstRow.RaiseEvent(wheel);
            wheel.Handled.ShouldBeTrue();
        }
        gridViewer.UpdateLayout();
        gridViewer.VerticalOffset.ShouldBeGreaterThan(0);
        gridWindow.Close();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    // The pager sits in the title row above the filters, and the table runs from the filters to
    // the bottom edge: only the rows scroll.
    private static void VerifyStickyProductTable(Window window, ProductsPage page)
    {
        ScrollViewer.GetCanContentScroll(page).ShouldBeFalse();
        var grid = (DataGrid)page.FindName("ProductsGrid");
        var rows = Enumerable.Range(1, 120).Select(index => new ProductListItem
        {
            Id = index,
            Sku = $"SKU{index:000}",
            Name = $"Product {index}",
        }).ToArray();
        grid.ItemsSource = rows;
        window.UpdateLayout();
        VerifyStickyTable(window, grid, (FrameworkElement)page.FindName("SearchBox"), (FrameworkElement)page.FindName("Pager"));
    }

    private static void VerifyStickyMoneyOutTable(Window window, MoneyOutPage page)
    {
        ScrollViewer.GetCanContentScroll(page).ShouldBeFalse();
        var grid = (DataGrid)page.FindName("DamageGrid");
        var originalItems = grid.ItemsSource;
        var damage = (DamageItem)grid.Items[0];
        var rows = Enumerable.Range(1, 120).Select(index => damage with { Id = index }).ToArray();
        grid.ItemsSource = rows;
        window.UpdateLayout();
        VerifyStickyTable(window, grid, (FrameworkElement)page.FindName("DamageSearch"), (FrameworkElement)page.FindName("DamagePager"));
        grid.ItemsSource = originalItems;
        window.UpdateLayout();
    }

    private static void VerifyStickyTable(Window window, DataGrid grid, FrameworkElement filters, FrameworkElement pager)
    {
        var header = FindVisualChild<DataGridColumnHeadersPresenter>(grid)!;
        double filtersY = filters.TranslatePoint(new Point(), window).Y;
        double pagerY = pager.TranslatePoint(new Point(), window).Y;
        double headerY = header.TranslatePoint(new Point(), window).Y;
        double gridBottom = grid.TranslatePoint(new Point(0, grid.ActualHeight), window).Y;
        // Filters above the table; the footer with the row count and page arrows under it, at the bottom edge.
        pager.IsVisible.ShouldBeTrue();
        filtersY.ShouldBeLessThan(headerY);
        pagerY.ShouldBeGreaterThanOrEqualTo(gridBottom - 1);
        (((FrameworkElement)window.Content).ActualHeight - (pagerY + pager.ActualHeight)).ShouldBeLessThan(16);
        pager.TranslatePoint(new Point(pager.ActualWidth, 0), window).X.ShouldBeLessThan(window.ActualWidth);
        grid.ScrollIntoView(grid.Items[^1]);
        grid.UpdateLayout();
        FindVisualChild<ScrollViewer>(grid)!.VerticalOffset.ShouldBeGreaterThan(0);
        Math.Abs(filters.TranslatePoint(new Point(), window).Y - filtersY).ShouldBeLessThan(1);
        Math.Abs(pager.TranslatePoint(new Point(), window).Y - pagerY).ShouldBeLessThan(1);
        Math.Abs(header.TranslatePoint(new Point(), window).Y - headerY).ShouldBeLessThan(1);
    }

    // Every visible button, text box, list box and date box a keyboard or screen-reader user can
    // reach announces a name: its text, or AutomationProperties.Name for an icon or input. A
    // control's own template parts and scroll bars are not checked.
    private static void VerifyAccessibleNames(Window window)
    {
        window.UpdateLayout();
        var unnamed = new List<string>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { IsVisible: false } or ScrollBar or DataGridColumnHeader) return;
            if (node is ButtonBase or TextBoxBase or ComboBox or DatePicker or PasswordBox)
            {
                var control = (FrameworkElement)node;
                string? name = UIElementAutomationPeer.CreatePeerForElement(control)?.GetName();
                if (string.IsNullOrWhiteSpace(name))
                    unnamed.Add($"{control.GetType().Name} '{control.Name}' in {VisualTreeHelper.GetParent(control)?.GetType().Name}");
                return;
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                Walk(VisualTreeHelper.GetChild(node, index));
        }
        Walk(window);
        unnamed.ShouldBeEmpty();
    }

    private static void Capture(Window window, string fileName)
    {
        string? phase7Directory = Environment.GetEnvironmentVariable("GROCERY_PHASE7_SCREENSHOT_DIR");
        if (phase7Directory is not null && fileName is not ("settings-top.en.png" or "settings-bottom.en.png" or
            "settings-top.gu.png" or "restore.en.png" or "restore.gu.png" or "compaction.en.png" or "compaction.gu.png")) return;
        string? phase6Directory = Environment.GetEnvironmentVariable("GROCERY_PHASE6_SCREENSHOT_DIR");
        if (phase7Directory is null && phase6Directory is not null && fileName is not ("dashboard.en.png" or "dashboard.gu.png" or
            "dashboard.narrow.en.png" or "dashboard.narrow.gu.png" or "reports-pnl.en.png" or "reports-pnl.narrow.en.png" or
            "reports-table.en.png" or "reports-table.gu.png" or "reports-hourly.en.png" or "reports-hourly.gu.png")) return;
        string? phase5Directory = Environment.GetEnvironmentVariable("GROCERY_PHASE5_SCREENSHOT_DIR");
        if (phase6Directory is null && phase5Directory is not null && fileName is not ("expiry.en.png" or "damages.en.png" or
            "expenses.en.png" or "payroll.en.png" or "payroll.gu.png" or "assets.en.png" or "assets.gu.png")) return;
        string? phase4Directory = Environment.GetEnvironmentVariable("GROCERY_PHASE4_SCREENSHOT_DIR");
        if (phase6Directory is null && phase4Directory is not null)
        {
            if (fileName is not ("billing.en.png" or "billing.gu.png" or "payment.en.png" or "payment.gu.png" or
                "saved-bills.en.png" or "sales-history.en.png" or "sales-history.gu.png" or "return.en.png" or
                "lot-picker.en.png" or "hold-bill.en.png" or "customer.en.png" or "held-review.en.png" or
                "discount-percent.en.png" or "day-end.en.png")) return;
        }
        string? phase3Directory = Environment.GetEnvironmentVariable("GROCERY_PHASE3_SCREENSHOT_DIR");
        if (phase6Directory is null && phase3Directory is not null && fileName is not ("purchase-entry.en.png" or "purchase-entry-warnings.en.png" or
            "purchase-entry-incl-gst.en.png" or "purchases-page.en.png" or
            "purchases-page.gu.png" or "stock-page.en.png" or "stock-lots.en.png" or "stock-ledger.en.png" or "stock-count.en.png" or "stock-count.gu.png" or
            "stock-count-scope.en.png" or "opening-stock-preview.en.png" or "vendor-history.en.png" or
            "product-lots.en.png" or
            "vendor-return.en.png" or "stock-source.en.png")) return;
        string? directory = phase7Directory ?? phase6Directory ?? phase5Directory ?? phase4Directory ?? phase3Directory ?? Environment.GetEnvironmentVariable("GROCERY_PHASE2_SCREENSHOT_DIR");
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

    private static void CaptureReceipt(ReceiptData receipt, string fileName)
    {
        byte[] command = ReceiptPrinter.Render(receipt, 384, bilingualNames: true);
        int offset = 2; // ESC @ initialization
        int bytesWide = 0;
        int height = 0;
        var raster = new List<byte>();
        while (offset + 8 <= command.Length && command[offset] == 0x1D && command[offset + 1] == 0x76)
        {
            int bandWidth = command[offset + 4] | command[offset + 5] << 8;
            int rows = command[offset + 6] | command[offset + 7] << 8;
            if (bytesWide == 0) bytesWide = bandWidth;
            bandWidth.ShouldBe(bytesWide);
            offset += 8;
            raster.AddRange(command.AsSpan(offset, bandWidth * rows).ToArray());
            offset += bandWidth * rows;
            height += rows;
        }
        bytesWide.ShouldBe(48);
        height.ShouldBeGreaterThan(100);
        string? directory = Environment.GetEnvironmentVariable("GROCERY_PHASE4_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        byte[] pixels = new byte[384 * height];
        for (int row = 0; row < height; row++)
            for (int column = 0; column < 384; column++)
                pixels[row * 384 + column] = (raster[row * bytesWide + column / 8] & (0x80 >> column % 8)) == 0
                    ? (byte)255 : (byte)0;
        var bitmap = BitmapSource.Create(384, height, 96, 96, PixelFormats.Gray8, null, pixels, 384);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, fileName));
        encoder.Save(output);
    }

    private static void PopulateReportCapture(ReportsPage page, TranslationSource translations)
    {
        var grid = (DataGrid)page.FindName("ReportGrid");
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridTextColumn { Header = translations["Reports_Column_Date"], Binding = new System.Windows.Data.Binding("Date"), Width = new DataGridLength(130) });
        grid.Columns.Add(new DataGridTextColumn { Header = translations["Reports_Column_Invoice"], Binding = new System.Windows.Data.Binding("Invoice"), Width = new DataGridLength(160) });
        grid.Columns.Add(new DataGridTextColumn { Header = translations["Reports_Column_Status"], Binding = new System.Windows.Data.Binding("Status"), Width = new DataGridLength(150) });
        grid.Columns.Add(new DataGridTextColumn { Header = translations["Reports_Column_Total"], Binding = new System.Windows.Data.Binding("Total"), Width = new DataGridLength(150) });
        grid.ItemsSource = new[]
        {
            new { Date = "04-10-2026", Invoice = "RPT-1", Status = translations["Reports_StatusCompleted"], Total = "30.000" },
            new { Date = "05-10-2026", Invoice = "RPT-2", Status = translations["Reports_StatusCompleted"], Total = "60.000" },
        };
        ((FrameworkElement)page.FindName("PnlPanel")).Visibility = Visibility.Collapsed;
        grid.Visibility = Visibility.Visible;
        ((TextBlock)page.FindName("StatusText")).Text = translations.Format("Reports_RowCount", 2);
        ((TextBlock)page.FindName("TotalsText")).Text = translations["Reports_Column_Total"] + ": 90.000";
    }

    private static void PopulateDashboardCapture(DashboardPage page, TranslationSource translations)
    {
        ((TextBlock)page.FindName("TodaySales")).Text = "₹60.000";
        ((TextBlock)page.FindName("TodayDetails")).Text = translations.Format("Dashboard_TodayDetails", 1, 60m, 33.333m);
        ((TextBlock)page.FindName("MonthSales")).Text = "₹90.000";
        ((TextBlock)page.FindName("MonthDetails")).Text = translations.Format("Dashboard_MonthDetails", 0m);
        ((TextBlock)page.FindName("StockValue")).Text = "₹200.000";
        ((TextBlock)page.FindName("Payables")).Text = "₹0.000";
        ((TextBlock)page.FindName("Attention")).Text = translations.Format("Dashboard_AttentionDetails", 0, 1, 2, 0);
        ((Button)page.FindName("ExpiredButton")).Content = translations.Format("Dashboard_ExpiredCount", 0);
        ((Button)page.FindName("CriticalButton")).Content = translations.Format("Dashboard_CriticalCount", 1);
        ((Button)page.FindName("SoonButton")).Content = translations.Format("Dashboard_SoonCount", 2);
        ((Button)page.FindName("LowStockButton")).Content = translations.Format("Dashboard_LowStockCount", 0);
        ((TextBlock)page.FindName("PaymentSplit")).Text = translations["Dashboard_Payments"] + " " +
            translations["Reports_PaymentCash"] + " ₹30.000 · " + translations["Reports_PaymentUpi"] + " ₹60.000";
        ((TextBlock)page.FindName("Backup")).Text = translations.Format("Dashboard_BackupDetails", "04-10-2026 21:00", "—");
        ((TextBlock)page.FindName("TrendDates")).Text = translations.Format("Dashboard_TrendDates", "06-09-2026", "05-10-2026");
        string product = translations.UiCulture.Name == "gu-IN" ? "ટાટા મીઠું 1 kg" : "Tata Salt 1 kg";
        ((DataGrid)page.FindName("RevenueGrid")).ItemsSource = new[] { new { Product = product, Quantity = "3.000", Revenue = "90.000" } };
        ((DataGrid)page.FindName("MarginGrid")).ItemsSource = new[] { new { Product = product, Quantity = "3.000", Margin = "30.000" } };
        var trend = (Canvas)page.FindName("TrendCanvas");
        page.UpdateLayout();
        trend.Children.Clear();
        double trendWidth = Math.Max(16, trend.ActualWidth);
        var line = new System.Windows.Shapes.Polyline { Stroke = Brushes.DodgerBlue, StrokeThickness = 2 };
        line.Points.Add(new Point(8, 135));
        line.Points.Add(new Point(trendWidth / 2, 100));
        line.Points.Add(new Point(trendWidth - 8, 36));
        trend.Children.Add(line);
    }

    private static TabularReport HourlySample(TranslationSource translations)
    {
        ReportRow Hour(string day, int hour, int bills, long revenue) => new(new Dictionary<string, object?>
        {
            ["Weekday"] = translations[$"Reports_Weekday_{day}"],
            ["Hour"] = hour,
            ["Bills"] = bills,
            ["Revenue"] = revenue,
        });
        return new TabularReport(translations["Reports_Kind_HourlySales"], null, [],
            [Hour("Mon", 9, 2, 30000), Hour("Tue", 14, 4, 90000), Hour("Sat", 18, 1, 50000)],
            new Dictionary<string, decimal>());
    }

    private sealed class ReportClock : IClock
    {
        public DateTime Now => new(2026, 10, 5, 12, 0, 0);
        public DateOnly BusinessDate => new(2026, 10, 5);
    }

    private static void CaptureA4(FixedPage page, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("GROCERY_PHASE6_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        page.Measure(new Size(page.Width, page.Height));
        page.Arrange(new Rect(0, 0, page.Width, page.Height));
        page.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(page.Width), (int)Math.Ceiling(page.Height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, fileName));
        encoder.Save(output);
    }


    private sealed class TestSettings : ISettings
    {
        public AppSettings Current { get; } = new("Test Store", 4, 0, 15, 30, "");
    }
}

