using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
using GroceryErp.Purchasing;
using GroceryErp.Reports;
using GroceryErp.Sales;
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
                wideDocument.DocumentPaginator.PageSize.Width.ShouldBeGreaterThan(wideDocument.DocumentPaginator.PageSize.Height);
                CaptureA4(wideDocument.Pages[0].Child, "report-a4-band1.en.png");
                CaptureA4(wideDocument.Pages[1].Child, "report-a4-band2.en.png");
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
                Capture(settingsHost, "settings-top.en.png");
                ((ScrollViewer)settingsPage.Content).ScrollToEnd();
                Capture(settingsHost, "settings-bottom.en.png");
                ((ScrollViewer)settingsPage.Content).ScrollToTop();
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(settingsHost, "settings-top.gu.png");
                settingsHost.Close();
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");

                var expiryAlert = new ExpiryAlertWindow(3);
                expiryAlert.Show();
                Capture(expiryAlert, "expiry-alert.en.png");
                expiryAlert.Close();
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                expiryAlert = new ExpiryAlertWindow(3);
                expiryAlert.Show();
                Capture(expiryAlert, "expiry-alert.gu.png");
                expiryAlert.Close();
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");

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
                        new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 9),
                        new Money(20), new Money(30), new Money(27.5m), new Quantity(10), new Quantity(5), true, "CRITICAL"),
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

                var selectedLot = ((IEnumerable<LotItem>)((DataGrid)stock.FindName("LotsGrid")).ItemsSource).First();
                var adjustment = new StockAdjustmentWindow(inventory, selectedLot);
                adjustment.Show();
                Capture(adjustment, "stock-adjustment.en.png");
                adjustment.Close();
                var priceChange = new LotPriceWindow(inventory, selectedLot);
                priceChange.Show();
                Capture(priceChange, "lot-price.en.png");
                priceChange.Close();

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

                var bulkEdit = new CatalogBulkEditWindow(catalog, new long[] { 1 });
                bulkEdit.Show();
                Capture(bulkEdit, "catalog-bulk-edit.en.png");
                bulkEdit.Close();

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

                var purchasing = new PurchasingService(database, new StockLedger(settings),
                    new DocNumberService(settings), new PeriodGuard(settings), settings, new AuditLog());
                var vendorEditor = new VendorEditorWindow(purchasing);
                vendorEditor.Show();
                Capture(vendorEditor, "vendor-editor.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(vendorEditor, "vendor-editor.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                ((ScrollViewer)((Grid)vendorEditor.Content).Children[0]).ScrollToEnd();
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
                ((ScrollViewer)((Grid)paymentForm).Children[0]).ScrollToEnd();
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
                ((DataGridComboBoxColumn)((DataGrid)purchaseEntry.FindName("LinesGrid")).Columns[0]).ItemsSource = new[]
                {
                    new ProductListItem { Id = 1, Name = "Tata Salt 1 kg" },
                };
                ((TextBox)purchaseEntry.FindName("ChargesBox")).Text = "૧૦૦";
                ((TextBox)purchaseEntry.FindName("BillTotalBox")).Text = "૨૭૨૫";
                ((TextBlock)purchaseEntry.FindName("DifferenceText")).Text.ShouldBe("0.000");
                ((TextBox)purchaseEntry.FindName("ChargesBox")).Text = "100";
                ((TextBox)purchaseEntry.FindName("BillTotalBox")).Text = "2725";
                purchaseEntry.Show();
                Capture(purchaseEntry, "purchase-entry.en.png");
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
                purchaseEntry.Close();

                var countWindow = new StockCountWindow(inventory, loadOnShow: false);
                ((DataGrid)countWindow.FindName("CountGrid")).ItemsSource = new object[]
                {
                    new { ProductName = "Tata Salt 1 kg", LotCode = "L2627-000014", Expected = new Quantity(11),
                        Counted = "10", Movement = Quantity.Zero, Difference = new Quantity(-1), DifferenceValue = new Money(-250) },
                    new { ProductName = "Wheat flour 5 kg", LotCode = "L2627-000015", Expected = new Quantity(8),
                        Counted = "", Movement = Quantity.Zero, Difference = (Quantity?)null, DifferenceValue = (Money?)null },
                };
                ((TextBlock)countWindow.FindName("ProgressText")).Text = "1 of 2 lots counted";
                countWindow.Show();
                Capture(countWindow, "stock-count.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(countWindow, "stock-count.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                countWindow.Close();

                var countScope = new StockCountScopeWindow(catalog, loadOnShow: false);
                ((ListBox)countScope.FindName("CategoriesBox")).ItemsSource = new[]
                {
                    new MasterItem { Id = 1, Name = "Staples" },
                    new MasterItem { Id = 2, Name = "Dairy" },
                    new MasterItem { Id = 3, Name = "Household" },
                };
                countScope.Show();
                Capture(countScope, "stock-count-scope.en.png");
                countScope.Close();

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
                ((DataGrid)purchasesPage.FindName("VendorsGrid")).ItemsSource = new[]
                {
                    new VendorItem(1, "Shree Wholesale Traders", "9999999999", "24ABCDE1234F1Z5", new Money(2325), true),
                };
                var purchasesHost = new Window { Content = purchasesPage, Width = 1080, Height = 700 };
                purchasesHost.Show();
                Capture(purchasesHost, "purchases-page.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(purchasesHost, "purchases-page.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                purchasesHost.Close();

                var vendorHistory = new VendorLedgerWindow(purchasing, 1, "Shree Wholesale Traders", loadOnShow: false);
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
                vendorHistory.Show();
                Capture(vendorHistory, "vendor-history.en.png");
                vendorHistory.Close();

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

                var stockPage = new StockPage(inventory, loadOnShow: false);
                ((DataGrid)stockPage.FindName("OverviewGrid")).ItemsSource = new[]
                {
                    new StockOverviewItem(1, "SALT001", "Tata Salt 1 kg", new Quantity(11),
                        new Money(2750), new Quantity(5), false),
                };
                ((DataGrid)stockPage.FindName("LotsGrid")).ItemsSource = new[]
                {
                    new LotItem(1, 1, "Tata Salt 1 kg", "L2627-000014", "SALT-2409",
                        new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 9),
                        new Money(250), new Money(350), new Money(300), new Quantity(11), new Quantity(11), true, "CRITICAL"),
                };
                var stockHost = new Window { Content = stockPage, Width = 1080, Height = 700 };
                stockHost.Show();
                Capture(stockHost, "stock-page.en.png");
                ((TabControl)stockPage.FindName("StockTabs")).SelectedIndex = 1;
                Capture(stockHost, "stock-lots.en.png");
                ((DataGrid)stockPage.FindName("LedgerGrid")).ItemsSource = new[]
                {
                    new LedgerItem(1, new DateTime(2026, 9, 28), new DateOnly(2026, 9, 28), "PURCHASE",
                        new Quantity(11), new Money(250), "purchase_line", 1, "L2627-000014",
                        new Quantity(11), "Shree Wholesale Traders"),
                };
                ((TabControl)stockPage.FindName("StockTabs")).SelectedIndex = 2;
                ((ComboBox)stockPage.FindName("LedgerTypeBox")).ItemsSource = new List<string> { "All movements", "PURCHASE" };
                ((ComboBox)stockPage.FindName("LedgerTypeBox")).SelectedIndex = 0;
                Capture(stockHost, "stock-ledger.en.png");
                stockHost.Close();

                var source = new StockSourceWindow(new StockSourceDocument("Purchase", "P2627-000051",
                    new DateOnly(2026, 9, 28), "Shree Wholesale Traders", "POSTED", "SW-1042",
                    [new("Tata Salt 1 kg", "L2627-000014", new Quantity(11), new Money(2600))]));
                source.Show();
                Capture(source, "stock-source.en.png");
                source.Close();

                IClock phase5Clock = new SystemClock(settings);
                var moneyOutService = new MoneyOutService(database, new StockLedger(settings),
                    new DocNumberService(settings), new PeriodGuard(settings), new AuditLog());
                var moneyOutPage = new MoneyOutPage(moneyOutService, inventory, phase5Clock, loadOnShow: false);
                var moneyOutHost = new Window { Content = moneyOutPage, Width = 1120, Height = 700 };
                ((ListBox)moneyOutPage.FindName("ExpiredList")).ItemsSource = new[]
                {
                    new ExpiryLot(1, 1, "SALT001", "Tata Salt 1 kg", "ટાટા મીઠું", "L2627-000014",
                        "SALT-2409", new DateOnly(2026, 9, 26), -2, new Quantity(11), new Money(220), "EXPIRED", 1, "Shree Wholesale"),
                };
                ((DataGrid)moneyOutPage.FindName("DamageGrid")).ItemsSource = new[]
                {
                    new DamageItem(1, "D2627-000021", new DateOnly(2026, 9, 27), "Tata Salt 1 kg", "L2627-000014",
                        new Quantity(2), new Money(20), new Money(40), Money.Zero, "BROKEN", "Handling"),
                };
                ((ComboBox)moneyOutPage.FindName("ExpenseCategory")).ItemsSource = new[]
                {
                    new ExpenseCategoryItem(1, "Electricity", "વીજળી", true),
                };
                ((ComboBox)moneyOutPage.FindName("ExpenseCategory")).SelectedIndex = 0;
                ((ComboBox)moneyOutPage.FindName("CategoryMaster")).ItemsSource = new[]
                {
                    new ExpenseCategoryItem(1, "Electricity", "વીજળી", true),
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
                ((DataGrid)moneyOutPage.FindName("SalaryGrid")).ItemsSource = new[]
                {
                    new { EmployeeId = 1, BaseSalary = "12000.000", LeaveDeduction = "500.000", Overtime = "250.000", Bonus = "1000.000" },
                };
                moneyOutHost.Show();
                Capture(moneyOutHost, "expiry.en.png");
                ((TabControl)moneyOutPage.FindName("MoneyOutTabs")).SelectedIndex = 1;
                Capture(moneyOutHost, "damages.en.png");
                ((TabControl)moneyOutPage.FindName("MoneyOutTabs")).SelectedIndex = 2;
                Capture(moneyOutHost, "expenses.en.png");
                ((TabControl)moneyOutPage.FindName("MoneyOutTabs")).SelectedIndex = 3;
                Capture(moneyOutHost, "payroll.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(moneyOutHost, "payroll.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                moneyOutHost.Close();

                var assetService = new AssetService(database, settings, new PeriodGuard(settings), new AuditLog(), phase5Clock);
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
                Capture(assetsHost, "assets.en.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                Capture(assetsHost, "assets.gu.png");
                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                assetsHost.Close();

                IClock posClock = new SystemClock(settings);
                var sales = new SalesService(database, new StockLedger(settings), new DocNumberService(settings),
                    new PeriodGuard(settings), settings, new AuditLog(), posClock);
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
                ((TextBlock)history.FindName("DetailHeading")).Text = "S2627-000041 · Cash ₹109.000";
                var historyHost = new Window { Content = history, Width = 1120, Height = 720 };
                historyHost.Show();
                Capture(historyHost, "sales-history.en.png");
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("gu-IN");
                ((TextBlock)history.FindName("DetailHeading")).Text = "S2627-000041 · રોકડ ₹109.000";
                Capture(historyHost, "sales-history.gu.png");
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                historyHost.Close();

                var returning = new SaleActionWindow(true, [new ReturnableSaleLine(lines[0], Quantity.One),
                    new ReturnableSaleLine(lines[1], new Quantity(1.25m))]);
                returning.Show();
                Capture(returning, "return.en.png");
                returning.Close();

                var voidDialog = new SaleActionWindow(false);
                voidDialog.Title.ShouldBe("Void");
                voidDialog.Show();
                Capture(voidDialog, "void.en.png");
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
                    ((ScrollViewer)reportsPage.FindName("PnlPanel")).Visibility = Visibility.Visible;
                    ((DataGrid)reportsPage.FindName("ReportGrid")).Visibility = Visibility.Collapsed;
                    Capture(reportsHost, "reports-pnl.en.png");
                    reportsHost.Width = 740;
                    Capture(reportsHost, "reports-pnl.narrow.en.png");
                    reportsHost.Width = 1120;
                    var reportPicker = (ComboBox)reportsPage.FindName("ReportPicker");
                    reportPicker.SelectedIndex = 1;
                    PopulateReportCapture(reportsPage, translations);
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
                    Capture(dashboardHost, "dashboard.en.png");
                    dashboardHost.Width = 740;
                    PopulateDashboardCapture(dashboardPage, translations);
                    Capture(dashboardHost, "dashboard.narrow.en.png");
                    dashboardHost.Close();
                }

                translations.UiCulture = CultureInfo.GetCultureInfo("en-IN");
                Capture(shell, "shell.en.png");
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
        string? phase7Directory = Environment.GetEnvironmentVariable("GROCERY_PHASE7_SCREENSHOT_DIR");
        if (phase7Directory is not null && fileName is not ("settings-top.en.png" or "settings-bottom.en.png" or
            "settings-top.gu.png" or "restore.en.png" or "restore.gu.png")) return;
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
        if (phase6Directory is null && phase3Directory is not null && fileName is not ("purchase-entry.en.png" or "purchase-entry-warnings.en.png" or "purchases-page.en.png" or
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
        int bytesWide = command[4] | command[5] << 8;
        int height = command[6] | command[7] << 8;
        bytesWide.ShouldBe(48);
        height.ShouldBeGreaterThan(100);
        string? directory = Environment.GetEnvironmentVariable("GROCERY_PHASE4_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        byte[] pixels = new byte[384 * height];
        for (int row = 0; row < height; row++)
            for (int column = 0; column < 384; column++)
                pixels[row * 384 + column] = (command[8 + row * bytesWide + column / 8] & (0x80 >> column % 8)) == 0
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
        ((ScrollViewer)page.FindName("PnlPanel")).Visibility = Visibility.Collapsed;
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
        public AppSettings Current { get; } = new("Test Store", 4, 0, null, 15, 30, "");
    }
}

