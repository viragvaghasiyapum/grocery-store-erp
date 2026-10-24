using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Microsoft.Data.Sqlite;
using Xunit;
using UiApplication = FlaUI.Core.Application;

namespace GroceryErp.UI.Tests;

public sealed class ScaleUiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public async Task LargeStore_ModuleNavigationRemainsResponsive()
    {
        string? root = Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_ROOT");
        string? executable = Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXE");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(executable))
        {
            Assert.Skip("Set GROCERY_SCALE_UI_ROOT and GROCERY_SCALE_UI_EXE for an isolated large-store UI run.");
            return;
        }
        string expectedPrefix = Path.Combine(Path.GetTempPath(), "grocery-erp-packaged-ui-");
        if (!Path.GetFullPath(root).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"GROCERY_SCALE_UI_ROOT must be under {expectedPrefix} to match the app's test-store guard.");
        if (!File.Exists(Path.Combine(root, "db", "grocery.db")))
            throw new FileNotFoundException("The isolated UI database is missing.", Path.Combine(root, "db", "grocery.db"));

        var records = new List<object>();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.Environment["GROCERY_ERP_TEST_DATA_ROOT"] = root;
        UiApplication? app = null;
        try
        {
            var launchTimer = Stopwatch.StartNew();
            app = UiApplication.Launch(start);
            using var automation = new UIA3Automation();
            Window login = await WaitWindowAsync(app, automation, "PasswordBox", TimeSpan.FromMinutes(3));
            double loginReadyMilliseconds = launchTimer.Elapsed.TotalMilliseconds;
            login.FindFirstDescendant(cf => cf.ByAutomationId("UsernameBox"))!.AsTextBox().Text = "owner";
            login.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox"))!.AsTextBox().Text = "scale-audit-2026";
            login.FindAllDescendants().First(element =>
                element.ControlType == FlaUI.Core.Definitions.ControlType.Button && element.Name == "Sign in").Click();
            Window shell;
            var signInTimer = Stopwatch.StartNew();
            try
            {
                shell = await WaitWindowAsync(app, automation, "BackupStatusButton",
                    Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPORT") == "1"
                        ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(3));
            }
            catch (TimeoutException)
            {
                File.WriteAllText(Path.Combine(root, "scale-audit-login-diagnostics.txt"),
                    string.Join(Environment.NewLine, app.GetAllTopLevelWindows(automation).Select(window =>
                        $"{window.Name}: {string.Join(" | ", window.FindAllDescendants().Take(100).Select(child => child.Name))}")));
                login.CaptureToFile(Path.Combine(root, "scale-audit-login-diagnostics.png"));
                throw;
            }
            File.WriteAllText(Path.Combine(root, "phase6-ui-startup.json"), JsonSerializer.Serialize(new
            {
                loginReadyMilliseconds,
                shellReadyAfterSignInMilliseconds = signInTimer.Elapsed.TotalMilliseconds,
            }, JsonOptions));
            foreach (Window other in app.GetAllTopLevelWindows(automation).Where(window =>
                         window.FindFirstDescendant(cf => cf.ByAutomationId("BackupStatusButton")) is null))
                other.FindFirstDescendant(cf => cf.ByName("OK"))?.Click();
            await Task.Delay(2000, TestContext.Current.CancellationToken);
            foreach (Window other in app.GetAllTopLevelWindows(automation).Where(window =>
                         window.FindFirstDescendant(cf => cf.ByAutomationId("BackupStatusButton")) is null))
                other.FindFirstDescendant(cf => cf.ByName("Later"))?.Click();

            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_PHASE5_UPDATE") == "1" ||
                Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_PHASE5_OUTPUT") == "1")
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    Window? review = app.GetAllTopLevelWindows(automation).FirstOrDefault(window =>
                        window.FindAllDescendants().Any(element =>
                            DescribeElement(element).Contains("Saved print jobs", StringComparison.Ordinal)));
                    if (review is null) break;
                    review.FindFirstDescendant(cf => cf.ByName("OK"))?.Click();
                    await Task.Delay(250, TestContext.Current.CancellationToken);
                }
            }

            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_PHASE5_UPDATE") == "1")
            {
                shell.FindFirstDescendant(cf => cf.ByAutomationId("UpdateButton"))!.Click();
                Window picker = await WaitWindowAsync(app, automation, "1152", TimeSpan.FromSeconds(20));
                picker.FindFirstDescendant(cf => cf.ByAutomationId("1152"))!.AsTextBox().Text =
                    Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_UPDATE_FEED")
                    ?? throw new InvalidOperationException("The Phase 5 update feed is missing.");
                picker.FindFirstDescendant(cf => cf.ByName("Select Folder"))!.Click();
                await Task.Delay(2000, TestContext.Current.CancellationToken);
                string installDirectory = Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!;
                string versionFile = Path.Combine(installDirectory, "current", "sq.version");
                string previousVersion = XDocument.Load(versionFile).Descendants()
                    .First(element => element.Name.LocalName == "version").Value;
                string nextVersion = Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_UPDATE_VERSION")
                    ?? throw new InvalidOperationException("The target update version is missing.");
                int backupsBefore = Directory.EnumerateFiles(Path.Combine(root, "backups"), "*pre-update.zip").Count();
                var updateTimer = Stopwatch.StartNew();
                (shell.FindFirstDescendant(cf => cf.ByName("Yes"))
                    ?? throw new InvalidOperationException("The update confirmation is missing.")).Click();
                await WaitUntilAsync(() =>
                {
                    try
                    {
                        return File.ReadAllText(versionFile).Contains($"<version>{nextVersion}</version>",
                        StringComparison.Ordinal);
                    }
                    catch (IOException) { return false; }
                }, TimeSpan.FromMinutes(7), 500);
                await WaitUntilAsync(() => Directory.EnumerateFiles(Path.Combine(root, "backups"),
                    "*pre-update.zip").Count() > backupsBefore, TimeSpan.FromMinutes(2));
                string backup = Directory.EnumerateFiles(Path.Combine(root, "backups"), "*pre-update.zip")
                    .OrderByDescending(File.GetLastWriteTimeUtc).First();
                Assert.True(new FileInfo(backup).Length > 100_000_000L);
                using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "db", "grocery.db")};Mode=ReadOnly"))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM sale_line";
                    Assert.True((long)command.ExecuteScalar()! > 5_000_000L);
                }
                File.WriteAllText(Path.Combine(root, "phase5-update-result.json"), JsonSerializer.Serialize(new
                {
                    from = previousVersion,
                    to = nextVersion,
                    updateMilliseconds = updateTimer.Elapsed.TotalMilliseconds,
                    backupBytes = new FileInfo(backup).Length,
                    backup = Path.GetFileName(backup),
                }, JsonOptions));
                return;
            }

            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_PHASE5_OUTPUT") == "1")
            {
                await AuditPhase5OutputAsync(app, automation, shell, root);
                shell.Close();
                await WaitUntilAsync(() => app.HasExited, TimeSpan.FromMinutes(3));
                return;
            }

            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_PHASE4") == "1")
            {
                await AuditPhase4UiAsync(app, automation, shell, root);
                shell.Close();
                await WaitUntilAsync(() => app.HasExited, TimeSpan.FromMinutes(3));
                return;
            }

            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_ACCEPTANCE") == "1")
            {
                await AuditPopulatedAcceptanceAsync(shell, root);
                shell.Close();
                await WaitUntilAsync(() => app.HasExited, TimeSpan.FromMinutes(7));
                return;
            }

            // Every Phase 7 page, then Products and Purchases again so repeated navigation shows
            // whether memory keeps rising.
            foreach ((string label, string control) in new[]
            {
                ("Products", "SearchBox"),
                ("Purchases", "PurchasesGrid"),
                ("Vendors", "VendorsGrid"),
                ("Assets", "AssetSearchBox"),
                ("Reports", "ReportPicker"),
                ("Sales history", "HistoryGrid"),
                ("Billing (POS)", "ScanBox"),
                ("Dashboard", "TodaySales"),
                ("Money out", "MoneyOutTabs"),
                ("Products", "SearchBox"),
                ("Purchases", "PurchasesGrid"),
                ("Money out", "MoneyOutTabs"),
            })
            {
                var timer = Stopwatch.StartNew();
                File.AppendAllText(Path.Combine(root, "scale-audit-ui-progress.txt"), $"Starting {label}{Environment.NewLine}");
                AutomationElement action = shell.FindFirstDescendant(cf => cf.ByName(label))
                    ?? throw new InvalidOperationException($"Missing navigation action: {label}");
                action.Click();
                try
                {
                    await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId(control)) is not null,
                        TimeSpan.FromSeconds(20));
                }
                catch (TimeoutException)
                {
                    try { shell.CaptureToFile(Path.Combine(root, "scale-audit-ui-timeout.png")); }
                    catch (Exception captureError)
                    {
                        File.AppendAllText(Path.Combine(root, "scale-audit-ui-progress.txt"),
                            $"Capture failed: {captureError.Message}{Environment.NewLine}");
                    }
                    string windows = string.Join(" | ", app.GetAllTopLevelWindows(automation).Select(static window => window.Name));
                    string children = string.Join(" | ", shell.FindAllChildren().Select(static child => child.Name));
                    File.AppendAllText(Path.Combine(root, "scale-audit-ui-progress.txt"),
                        $"Timeout {label}; windows={windows}; shell children={children}{Environment.NewLine}");
                    throw;
                }
                double controlFoundMilliseconds = timer.Elapsed.TotalMilliseconds;
                await Task.Delay(1500, TestContext.Current.CancellationToken);
                using Process? process = Process.GetProcessesByName("GroceryErp.App")
                    .OrderByDescending(static item => item.StartTime).FirstOrDefault();
                process?.Refresh();
                string gridId = label switch
                {
                    "Products" => "ProductsGrid",
                    "Purchases" => "PurchasesGrid",
                    "Vendors" => "VendorsGrid",
                    "Assets" => "AssetGrid",
                    "Reports" => "ReportGrid",
                    "Sales history" => "HistoryGrid",
                    "Billing (POS)" => "BillGrid",
                    "Dashboard" => "RevenueGrid",
                    "Money out" => "MoneyOutTabs",
                    _ => throw new InvalidOperationException($"Unknown page: {label}"),
                };
                int gridRowItems = shell.FindFirstDescendant(cf => cf.ByAutomationId(gridId))?
                    .FindAllDescendants().Count(element =>
                        element.ControlType == FlaUI.Core.Definitions.ControlType.DataItem) ?? 0;
                var record = new
                {
                    page = label,
                    controlFoundMilliseconds,
                    settledMilliseconds = timer.Elapsed.TotalMilliseconds,
                    workingSet = process?.WorkingSet64,
                    peakWorkingSet = process?.PeakWorkingSet64,
                    gridRowItems,
                    alive = !app.HasExited
                };
                records.Add(record);
                Console.WriteLine(JsonSerializer.Serialize(record));
                if (label is "Products" or "Reports" or "Dashboard")
                    shell.CaptureToFile(Path.Combine(root, "scale-audit-ui-" + label.ToLowerInvariant().Replace(' ', '-') + ".png"));
                Assert.False(app.HasExited, $"App exited on {label}");
                if (label == "Billing (POS)" &&
                    Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_SAVED_BILLS") == "1")
                {
                    // Populated fixtures can open the saved-bill picker on entry.
                    // Dismiss it before navigating to another shell page.
                    Window savedBills = await WaitWindowAsync(app, automation, "BillsGrid", TimeSpan.FromSeconds(15));
                    (savedBills.FindFirstDescendant(cf => cf.ByName("Cancel"))
                        ?? throw new InvalidOperationException("Saved-bill picker has no Cancel action.")).Click();
                    await WaitUntilAsync(() => app.GetAllTopLevelWindows(automation).All(window =>
                        window.FindFirstDescendant(cf => cf.ByAutomationId("BillsGrid")) is null),
                        TimeSpan.FromSeconds(10));
                }
            }
            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_DIALOGS") == "1")
                await AuditDialogsAsync(app, automation, shell, root);
            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPORT") == "1")
                await ExportFullHistoryAsync(app, automation, shell, root);
            shell.Close();
            await WaitUntilAsync(() => app.HasExited, TimeSpan.FromMinutes(3));
        }
        finally
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "scale-audit-ui.json"),
                JsonSerializer.Serialize(records, JsonOptions));
            if (app is { HasExited: false }) app.Kill();
            app?.Dispose();
            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_PHASE5_UPDATE") == "1" &&
                !string.IsNullOrWhiteSpace(executable))
            {
                string installedPath = Path.GetDirectoryName(executable)!;
                foreach (Process process in Process.GetProcessesByName("GroceryErp.App"))
                {
                    try
                    {
                        if (process.MainModule?.FileName.StartsWith(installedPath,
                            StringComparison.OrdinalIgnoreCase) == true) process.Kill();
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    finally { process.Dispose(); }
                }
            }
        }
    }

    private static string DescribeElement(AutomationElement element)
    {
        try { return $"{element.ControlType}:{element.AutomationId}:{element.Name}"; }
        catch { return element.ControlType.ToString(); }
    }

    private static async Task AuditPhase5OutputAsync(UiApplication app, UIA3Automation automation,
        Window shell, string root)
    {
        shell.FindFirstDescendant(cf => cf.ByName("Reports"))!.Click();
        await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("ReportPicker")) is not null,
            TimeSpan.FromSeconds(20), 100);
        shell.FindFirstDescendant(cf => cf.ByAutomationId("ReportPicker"))!.AsComboBox().Select("Sales register");
        SetDate(shell, "FromPicker", "2024-01-01");
        SetDate(shell, "ToPicker", "2026-09-26");
        ClickRefresh(shell);
        await WaitUntilAsync(() => GridHasRows(shell, "ReportGrid") &&
            ControlNameContains(shell, "StatusText", "500"), TimeSpan.FromSeconds(30), 100);

        string workbook = Path.Combine(root, $"phase5-ui-sales-{Guid.NewGuid():N}.xlsx");
        var excelTimer = Stopwatch.StartNew();
        shell.FindFirstDescendant(cf => cf.ByAutomationId("ExportExcelButton"))!.Click();
        await SaveAsAsync(automation, workbook);
        await WaitUntilAsync(() => File.Exists(workbook) &&
            shell.FindFirstDescendant(cf => cf.ByAutomationId("CancelExportButton"))?.IsEnabled == false,
            TimeSpan.FromMinutes(2), 100);
        double excelMilliseconds = excelTimer.Elapsed.TotalMilliseconds;
        using (ZipArchive archive = ZipFile.OpenRead(workbook))
            Assert.NotNull(archive.GetEntry("xl/worksheets/sheet1.xml"));

        AutomationElement cancelExport = shell.FindFirstDescendant(cf => cf.ByAutomationId("CancelExportButton"))!;
        AutomationElement moreFilters = shell.FindFirstDescendant(cf => cf.ByAutomationId("MoreFiltersExpander"))!;
        AutomationElement statusText = shell.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"))!;
        string csv = Path.Combine(root, $"phase5-ui-sales-full-{Guid.NewGuid():N}.csv");
        shell.FindFirstDescendant(cf => cf.ByAutomationId("FullCsvButton"))!.Click();
        await SaveAsAsync(automation, csv);
        await WaitUntilAsync(() => cancelExport.IsEnabled, TimeSpan.FromSeconds(20), 50);
        await WaitUntilAsync(() => cancelExport.IsEnabled && statusText.Name.Contains("Exported",
            StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10), 50);
        string progressBeforeCancel = statusText.Name;
        var responseTimes = new List<double>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!cancelExport.IsEnabled) break;
            var responseTimer = Stopwatch.StartNew();
            moreFilters.Click();
            Assert.NotNull(statusText.Name);
            responseTimes.Add(responseTimer.Elapsed.TotalMilliseconds);
        }
        Assert.NotEmpty(responseTimes);
        cancelExport.Click();
        await WaitUntilAsync(() => !cancelExport.IsEnabled, TimeSpan.FromSeconds(30), 100);
        File.WriteAllText(Path.Combine(root, "phase5-ui-csv-diagnostics.json"), JsonSerializer.Serialize(new
        {
            responseTimes,
            progressBeforeCancel,
            completedFile = File.Exists(csv),
            bytes = File.Exists(csv) ? new FileInfo(csv).Length : 0,
            status = statusText.Name,
        }, JsonOptions));
        Assert.False(File.Exists(csv));
        Assert.Empty(Directory.EnumerateFiles(root, Path.GetFileName(csv) + ".*.partial"));

        var previewTimer = Stopwatch.StartNew();
        shell.FindFirstDescendant(cf => cf.ByAutomationId("PreviewA4Button"))!.Click();
        var previewResponses = new List<double>();
        Window? preview = null;
        try
        {
            await WaitUntilAsync(() =>
        {
            var responseTimer = Stopwatch.StartNew();
            Assert.NotNull(statusText.Name);
            previewResponses.Add(responseTimer.Elapsed.TotalMilliseconds);
            preview = app.GetAllTopLevelWindows(automation).FirstOrDefault(window =>
                !window.Equals(shell) && window.Name.Contains("Sales register", StringComparison.OrdinalIgnoreCase));
            return preview is not null;
        }, TimeSpan.FromSeconds(30), 50);
        }
        catch (TimeoutException)
        {
            File.WriteAllText(Path.Combine(root, "phase5-ui-preview-diagnostics.json"), JsonSerializer.Serialize(new
            {
                status = statusText.Name,
                appWindows = app.GetAllTopLevelWindows(automation).Select(window => window.Name).ToArray(),
                desktopWindows = automation.GetDesktop().FindAllChildren()
                    .Where(element => element.ControlType == FlaUI.Core.Definitions.ControlType.Window)
                    .Select(SafeName).ToArray(),
            }, JsonOptions));
            throw;
        }
        double previewOpenMilliseconds = previewTimer.Elapsed.TotalMilliseconds;
        var navigationTimer = Stopwatch.StartNew();
        shell.Focus();
        shell.FindFirstDescendant(cf => cf.ByName("Dashboard"))!.Click();
        try
        {
            await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("TodaySales")) is not null,
            TimeSpan.FromSeconds(20), 100);
        }
        catch (TimeoutException)
        {
            File.WriteAllText(Path.Combine(root, "phase5-ui-navigation-diagnostics.json"), JsonSerializer.Serialize(new
            {
                preview = preview!.Name,
                shell = shell.Name,
                windows = app.GetAllTopLevelWindows(automation).Select(window => window.Name).ToArray(),
                status = statusText.Name,
            }, JsonOptions));
            preview!.Close();
            throw;
        }
        double navigationMilliseconds = navigationTimer.Elapsed.TotalMilliseconds;
        preview!.Close();
        Assert.True(responseTimes.Max() < 1000, "Report export blocked WPF input for at least one second.");
        Assert.True(previewResponses.Max() < 1000, "A4 preview blocked WPF input for at least one second.");
        File.WriteAllText(Path.Combine(root, "phase5-ui-output-result.json"), JsonSerializer.Serialize(new
        {
            excelMilliseconds,
            excelBytes = new FileInfo(workbook).Length,
            csvBusyInteractions = responseTimes.Count,
            csvMaxInputMilliseconds = responseTimes.Max(),
            csvProgress = progressBeforeCancel,
            previewOpenMilliseconds,
            previewMaxInputMilliseconds = previewResponses.Max(),
            navigationWithPreviewMilliseconds = navigationMilliseconds,
        }, JsonOptions));
    }

    private static async Task SaveAsAsync(UIA3Automation automation, string path)
    {
        await WaitUntilAsync(() => automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Save As")) is not null,
            TimeSpan.FromSeconds(20), 100);
        Keyboard.Type(path);
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
    }

    private static async Task AuditPopulatedAcceptanceAsync(Window shell, string root)
    {
        var records = new List<object>();
        async Task CaptureAsync(string page, string readyControl, Func<AutomationElement, bool> ready)
        {
            var timer = Stopwatch.StartNew();
            AutomationElement? navigation = shell.FindFirstDescendant(cf => cf.ByAutomationId("RootNavigation"));
            (navigation?.FindFirstDescendant(cf => cf.ByName(page)) ??
                shell.FindFirstDescendant(cf => cf.ByName(page)))!.Click();
            AutomationElement? control = null;
            try
            {
                await WaitUntilAsync(() =>
                {
                    control = shell.FindFirstDescendant(cf => cf.ByAutomationId(readyControl));
                    return control is not null && ready(control);
                }, TimeSpan.FromSeconds(30), 100);
            }
            catch (TimeoutException)
            {
                shell.CaptureToFile(Path.Combine(root,
                    "phase2-acceptance-" + page.ToLowerInvariant().Replace(' ', '-') + "-timeout.png"));
                throw;
            }
            records.Add(new
            {
                page,
                readyMilliseconds = timer.Elapsed.TotalMilliseconds,
                visibleDataItems = control!.FindAllDescendants().Count(element =>
                    element.ControlType == FlaUI.Core.Definitions.ControlType.DataItem)
            });
            shell.CaptureToFile(Path.Combine(root, "phase2-acceptance-" + page.ToLowerInvariant().Replace(' ', '-') + ".png"));
        }

        await CaptureAsync("Dashboard", "RevenueGrid", element =>
            element.FindAllDescendants().Any(item =>
                item.ControlType == FlaUI.Core.Definitions.ControlType.DataItem));
        await RepeatRefreshAsync(shell, 8, 900);
        await CaptureAsync("Reports", "PnlGrid", _ => true);
        shell.FindFirstDescendant(cf => cf.ByAutomationId("PeriodPicker"))!.AsComboBox().Select("Custom");
        SetDate(shell, "FromPicker", "2026-09-01");
        SetDate(shell, "ToPicker", "2026-09-26");
        var monthTimer = Stopwatch.StartNew();
        ClickRefresh(shell);
        try
        {
            await WaitUntilAsync(() => GridHasRows(shell, "PnlGrid") &&
                ControlNameContains(shell, "StatusText", "01-09-2026"), TimeSpan.FromSeconds(30), 100);
        }
        catch (TimeoutException)
        {
            File.WriteAllText(Path.Combine(root, "phase2-report-month-diagnostics.txt"),
                $"from={DateText(shell, "FromPicker")}; to={DateText(shell, "ToPicker")}; " +
                $"status={shell.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"))?.Name}; " +
                $"rows={GridRows(shell, "PnlGrid")}");
            shell.CaptureToFile(Path.Combine(root, "phase2-report-month-timeout.png"));
            throw;
        }
        records.Add(new
        {
            page = "P&L month",
            readyMilliseconds = monthTimer.Elapsed.TotalMilliseconds,
            visibleDataItems = GridRows(shell, "PnlGrid")
        });
        await RepeatRefreshAsync(shell, 8, 500);
        SetDate(shell, "FromPicker", "2026-04-01");
        var fiscalTimer = Stopwatch.StartNew();
        ClickRefresh(shell);
        await WaitUntilAsync(() => GridHasRows(shell, "PnlGrid") &&
            ControlNameContains(shell, "StatusText", "01-04-2026"), TimeSpan.FromSeconds(30), 100);
        records.Add(new
        {
            page = "P&L fiscal",
            readyMilliseconds = fiscalTimer.Elapsed.TotalMilliseconds,
            visibleDataItems = GridRows(shell, "PnlGrid")
        });
        await RepeatRefreshAsync(shell, 8, 500);
        await CaptureAsync("Sales history", "HistoryGrid", _ => true);
        SetDate(shell, "FromPicker", "2026-09-01");
        SetDate(shell, "ToPicker", "2026-09-26");
        var historyTimer = Stopwatch.StartNew();
        ClickRefresh(shell);
        await WaitUntilAsync(() => GridHasRows(shell, "HistoryGrid"), TimeSpan.FromSeconds(30), 100);
        records.Add(new
        {
            page = "Sales history first page",
            readyMilliseconds = historyTimer.Elapsed.TotalMilliseconds,
            visibleDataItems = GridRows(shell, "HistoryGrid")
        });
        await RepeatRefreshAsync(shell, 8, 500);
        await CaptureAsync("Products", "ProductsGrid", _ => true);
        AutomationElement search = shell.FindFirstDescendant(cf => cf.ByAutomationId("SearchBox"))!;
        for (int sample = 0; sample < 9; sample++)
        {
            search.AsTextBox().Text = "zzzz-no-such-product";
            await WaitUntilAsync(() => GridRows(shell, "ProductsGrid") == 0,
                TimeSpan.FromSeconds(30), 100);
            var searchTimer = Stopwatch.StartNew();
            search.AsTextBox().Text = "grocery item";
            await WaitUntilAsync(() => GridHasRows(shell, "ProductsGrid"),
                TimeSpan.FromSeconds(30), 20);
            records.Add(new
            {
                page = "Product search",
                sample,
                readyMilliseconds = searchTimer.Elapsed.TotalMilliseconds,
                visibleDataItems = GridRows(shell, "ProductsGrid")
            });
        }
        File.WriteAllText(Path.Combine(root, "phase2-acceptance-ui.json"),
            JsonSerializer.Serialize(records, JsonOptions));
    }

    private static int GridRows(Window shell, string id) =>
        shell.FindFirstDescendant(cf => cf.ByAutomationId(id))?.FindAllDescendants().Count(element =>
            element.ControlType == FlaUI.Core.Definitions.ControlType.DataItem) ?? 0;

    private static bool GridHasRows(Window shell, string id) => GridRows(shell, id) > 0;

    private static async Task AuditPhase4UiAsync(UiApplication app, UIA3Automation automation,
        Window shell, string root)
    {
        var records = new List<object>();
        static string Scalar(string root, string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "db", "grocery.db")};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar()?.ToString() ?? throw new InvalidDataException("The UI fixture has no matching row.");
        }
        string barcode = Scalar(root, """
            SELECT b.barcode FROM product_barcode b JOIN product_lot l ON l.product_id=b.product_id
            WHERE l.qty_on_hand>1000 AND l.is_active=1 AND l.expiry_date>=date('now')
              AND b.pack_qty=1000
            ORDER BY l.qty_on_hand DESC LIMIT 1
            """);
        string lotCode = Scalar(root, "SELECT lot_code FROM product_lot WHERE id=202500");

        shell.SetForeground();
        shell.FindFirstDescendant(cf => cf.ByName("Sales history"))!.Click();
        await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("HistoryGrid")) is not null,
            TimeSpan.FromSeconds(20));
        SetDate(shell, "FromPicker", "2026-01-01");
        ClickRefresh(shell);
        try { await WaitUntilAsync(() => GridHasRows(shell, "HistoryGrid"), TimeSpan.FromSeconds(30), 100); }
        catch (TimeoutException)
        {
            File.WriteAllText(Path.Combine(root, "phase4-ui-history-diagnostics.txt"),
                $"from={DateText(shell, "FromPicker")}; to={DateText(shell, "ToPicker")}; " +
                $"status={shell.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"))?.Name}; " +
                $"dbRows={Scalar(root, "SELECT COUNT(*) FROM sale WHERE sale_date BETWEEN '2026-01-01' AND date('now')")}");
            shell.CaptureToFile(Path.Combine(root, "phase4-ui-history-timeout.png"));
            throw;
        }
        var historyTimer = Stopwatch.StartNew();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            shell.SetForeground();
            await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("NextButton"))?.IsEnabled == true,
                TimeSpan.FromSeconds(20), 100);
            shell.FindFirstDescendant(cf => cf.ByAutomationId("NextButton"))!.AsButton().Invoke();
            await WaitUntilAsync(() => GridHasRows(shell, "HistoryGrid") &&
                shell.FindFirstDescendant(cf => cf.ByAutomationId("PreviousButton"))?.IsEnabled == true,
                TimeSpan.FromSeconds(20), 100);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            shell.SetForeground();
            shell.FindFirstDescendant(cf => cf.ByAutomationId("PreviousButton"))!.AsButton().Invoke();
            await WaitUntilAsync(() => GridHasRows(shell, "HistoryGrid") &&
                shell.FindFirstDescendant(cf => cf.ByAutomationId("PreviousButton"))?.IsEnabled == false,
                TimeSpan.FromSeconds(20), 100);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            shell.SetForeground();
            ClickRefresh(shell);
        }
        records.Add(new
        {
            journey = "history-next-previous-refresh",
            iterations = 8,
            milliseconds = historyTimer.Elapsed.TotalMilliseconds,
            firstPageRestored = shell.FindFirstDescendant(cf => cf.ByAutomationId("PreviousButton"))?.IsEnabled == false
        });
        File.WriteAllText(Path.Combine(root, "phase4-ui-local.json"), JsonSerializer.Serialize(records, JsonOptions));

        await Task.Delay(500, TestContext.Current.CancellationToken);
        shell.SetForeground();
        shell.FindFirstDescendant(cf => cf.ByName("Billing (POS)"))!.Click();
        try
        {
            await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("ScanBox")) is not null,
            TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            File.WriteAllText(Path.Combine(root, "phase4-ui-billing-diagnostics.txt"),
                string.Join(" | ", app.GetAllTopLevelWindows(automation).Select(window => window.Name + ": " +
                    string.Join(",", window.FindAllDescendants().Take(40).Select(SafeName)))));
            shell.CaptureToFile(Path.Combine(root, "phase4-ui-billing-timeout.png"));
            throw;
        }
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        foreach (Window saved in app.GetAllTopLevelWindows(automation).Where(window =>
                     window.FindFirstDescendant(cf => cf.ByAutomationId("BillsGrid")) is not null))
            saved.FindFirstDescendant(cf => cf.ByName("Cancel"))?.Click();
        AutomationElement scan = shell.FindFirstDescendant(cf => cf.ByAutomationId("ScanBox"))!;
        var scanTimer = Stopwatch.StartNew();
        for (int iteration = 0; iteration < 3; iteration++)
        {
            scan.AsTextBox().Text = barcode;
            scan.Focus();
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            if (iteration == 0) Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        }
        long DraftQuantity() => long.Parse(Scalar(root, """
            SELECT COALESCE(SUM(sl.qty),0) FROM sale_line sl
            WHERE sl.sale_id=(SELECT MAX(id) FROM sale WHERE status='DRAFT')
            """), System.Globalization.CultureInfo.InvariantCulture);
        await WaitUntilAsync(() => GridHasRows(shell, "BillGrid") && DraftQuantity() == 3000,
            TimeSpan.FromSeconds(30), 100);
        records.Add(new
        {
            journey = "keyboard-scan-burst-double-enter",
            milliseconds = scanTimer.Elapsed.TotalMilliseconds,
            visibleRows = GridRows(shell, "BillGrid"),
            quantityScaled = DraftQuantity()
        });
        scan.AsTextBox().Text = "9999999999999";
        scan.Focus();
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        Window unknown = await WaitWindowAsync(app, automation, "OptionsTabs", TimeSpan.FromSeconds(20));
        unknown.FindFirstDescendant(cf => cf.ByName("Cancel"))!.Click();
        await WaitUntilAsync(() => app.GetAllTopLevelWindows(automation).All(window =>
            window.FindFirstDescendant(cf => cf.ByAutomationId("OptionsTabs")) is null), TimeSpan.FromSeconds(20));
        records.Add(new { journey = "unknown-barcode-interruption", billRowsPreserved = GridHasRows(shell, "BillGrid") });
        shell.FindFirstDescendant(cf => cf.ByName("F7 Hold"))!.Click();
        Window hold = await WaitWindowAsync(app, automation, "LabelBox", TimeSpan.FromSeconds(20));
        hold.FindFirstDescendant(cf => cf.ByAutomationId("LabelBox"))!.AsTextBox().Text = "Phase 4 keyboard audit";
        hold.FindFirstDescendant(cf => cf.ByName("F7 Hold"))!.Click();
        await WaitUntilAsync(() => !GridHasRows(shell, "BillGrid"), TimeSpan.FromSeconds(20));
        records.Add(new { journey = "hold-after-scan", billCleared = !GridHasRows(shell, "BillGrid") });
        shell.FindFirstDescendant(cf => cf.ByName("F2 New bill"))!.Click();
        scan.AsTextBox().Text = barcode;
        scan.Focus();
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        await WaitUntilAsync(() => GridHasRows(shell, "BillGrid"), TimeSpan.FromSeconds(20));
        long beforePayment = long.Parse(Scalar(root, "SELECT COUNT(*) FROM sale WHERE status='COMPLETED'"),
            System.Globalization.CultureInfo.InvariantCulture);
        shell.FindFirstDescendant(cf => cf.ByName("F9 Cash"))!.Click();
        Window payment = await WaitWindowAsync(app, automation, "CashBox", TimeSpan.FromSeconds(20));
        payment.FindFirstDescendant(cf => cf.ByName("Complete bill"))!.Click();
        await WaitUntilAsync(() => long.Parse(Scalar(root,
            "SELECT COUNT(*) FROM sale WHERE status='COMPLETED'"),
            System.Globalization.CultureInfo.InvariantCulture) == beforePayment + 1,
            TimeSpan.FromSeconds(30), 100);
        records.Add(new { journey = "new-scan-pay", completedSaleDelta = 1 });
        File.WriteAllText(Path.Combine(root, "phase4-ui-local.json"), JsonSerializer.Serialize(records, JsonOptions));

        shell.SetForeground();
        shell.FindFirstDescendant(cf => cf.ByName("Stock"))!.Click();
        await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("StockTabs")) is not null,
            TimeSpan.FromSeconds(20));
        shell.FindFirstDescendant(cf => cf.ByName("New count"))!.Click();
        Window scope = await WaitWindowAsync(app, automation, "AllCategoriesBox", TimeSpan.FromSeconds(20));
        scope.SetForeground();
        scope.FindFirstDescendant(cf => cf.ByAutomationId("AllCategoriesBox"))!.AsCheckBox().IsChecked = true;
        await WaitUntilAsync(() => scope.FindFirstDescendant(cf => cf.ByAutomationId("AllCategoriesBox"))!
            .AsCheckBox().IsChecked == true, TimeSpan.FromSeconds(10), 100);
        scope.FindFirstDescendant(cf => cf.ByName("Create sheet"))!.AsButton().Invoke();
        Window count;
        try { count = await WaitWindowAsync(app, automation, "CountSearchBox", TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            File.WriteAllText(Path.Combine(root, "phase4-ui-count-diagnostics.txt"),
                $"scopeError={scope.FindFirstDescendant(cf => cf.ByAutomationId("ErrorText"))?.Name}; " +
                $"allCategories={scope.FindFirstDescendant(cf => cf.ByAutomationId("AllCategoriesBox"))?.AsCheckBox().IsChecked}; " +
                $"windows={string.Join(" | ", app.GetAllTopLevelWindows(automation).Select(window => window.Name))}");
            scope.CaptureToFile(Path.Combine(root, "phase4-ui-count-timeout.png"));
            throw;
        }
        await WaitUntilAsync(() => GridHasRows(count, "CountGrid"), TimeSpan.FromSeconds(30), 100);
        var countTimer = Stopwatch.StartNew();
        count.FindFirstDescendant(cf => cf.ByAutomationId("CountSearchBox"))!.AsTextBox().Text = lotCode;
        count.FindFirstDescendant(cf => cf.ByAutomationId("CountSearchBox"))!.Focus();
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        await WaitUntilAsync(() => count.FindFirstDescendant(cf => cf.ByAutomationId("CountSearchBox"))!
            .AsTextBox().Text.Length == 0, TimeSpan.FromSeconds(30), 100);
        records.Add(new
        {
            journey = "whole-count-search-to-edit",
            milliseconds = countTimer.Elapsed.TotalMilliseconds,
            total = count.FindFirstDescendant(cf => cf.ByAutomationId("ProgressText"))?.Name,
            visibleRows = GridRows(count, "CountGrid")
        });
        count.FindFirstDescendant(cf => cf.ByAutomationId("SaveButton"))!.Click();
        await WaitUntilAsync(() => app.GetAllTopLevelWindows(automation).All(window =>
            window.FindFirstDescendant(cf => cf.ByAutomationId("CountSearchBox")) is null), TimeSpan.FromSeconds(20));
        File.WriteAllText(Path.Combine(root, "phase4-ui-local.json"), JsonSerializer.Serialize(records, JsonOptions));
    }

    private static bool ControlNameContains(Window shell, string id, string value) =>
        shell.FindFirstDescendant(cf => cf.ByAutomationId(id))?.Name.Contains(value,
            StringComparison.OrdinalIgnoreCase) == true;

    private static string? DateText(Window shell, string picker) =>
        shell.FindFirstDescendant(cf => cf.ByAutomationId(picker))?
            .FindFirstDescendant(cf => cf.ByAutomationId("PART_TextBox"))?.AsTextBox().Text;

    private static void SetDate(Window shell, string picker, string value)
    {
        DateTime date = DateTime.ParseExact(value, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
        shell.FindFirstDescendant(cf => cf.ByAutomationId(picker))!
            .FindFirstDescendant(cf => cf.ByAutomationId("PART_TextBox"))!
            .AsTextBox().Text = date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
    }

    private static void ClickRefresh(Window shell) =>
        (shell.FindAllDescendants().FirstOrDefault(element =>
            element.ControlType == FlaUI.Core.Definitions.ControlType.Button && element.Name == "Refresh")
        ?? throw new InvalidOperationException("The page has no Refresh button.")).Click();

    private static async Task RepeatRefreshAsync(Window shell, int count, int settleMilliseconds)
    {
        for (int sample = 0; sample < count; sample++)
        {
            ClickRefresh(shell);
            await Task.Delay(settleMilliseconds, TestContext.Current.CancellationToken);
        }
    }

    private static async Task AuditDialogsAsync(UiApplication app, UIA3Automation automation,
        Window shell, string root)
    {
        var records = new List<object>();
        foreach ((string page, string action, string control) in new[]
        {
            ("Stock", "ResumeCountButton", "DraftGrid"),
            ("Purchases", "PaymentButton", "PurchaseSearchBox"),
        })
        {
            shell.FindFirstDescendant(cf => cf.ByName(page))!.Click();
            await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId(action)) is not null,
                TimeSpan.FromSeconds(20));
            var timer = Stopwatch.StartNew();
            shell.FindFirstDescendant(cf => cf.ByAutomationId(action))!.Click();
            Window dialog = await WaitWindowAsync(app, automation, control, TimeSpan.FromSeconds(20));
            AutomationElement list = dialog.FindFirstDescendant(cf =>
                cf.ByAutomationId(control == "DraftGrid" ? "DraftGrid" : "PurchaseBox"))!;
            int visibleDataItems = list.FindAllDescendants().Count(element =>
                element.ControlType == FlaUI.Core.Definitions.ControlType.DataItem);
            records.Add(new
            {
                page,
                control,
                readyMilliseconds = timer.Elapsed.TotalMilliseconds,
                visibleDataItems
            });
            File.WriteAllText(Path.Combine(root, "scale-audit-dialogs.json"),
                JsonSerializer.Serialize(records, JsonOptions));
            AutomationElement cancel = dialog.FindFirstDescendant(cf => cf.ByName("Cancel"))
                ?? throw new InvalidOperationException($"The {control} dialog has no Cancel action.");
            cancel.Click();
            await WaitUntilAsync(() => app.GetAllTopLevelWindows(automation).All(window =>
                window.FindFirstDescendant(cf => cf.ByAutomationId(control)) is null), TimeSpan.FromSeconds(20));
        }
        File.WriteAllText(Path.Combine(root, "scale-audit-dialogs.json"),
            JsonSerializer.Serialize(records, JsonOptions));
    }

    private static async Task ExportFullHistoryAsync(UiApplication app, UIA3Automation automation,
        Window shell, string root)
    {
        shell.FindFirstDescendant(cf => cf.ByName("Sales history"))!.Click();
        await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("ExportButton")) is not null,
            TimeSpan.FromSeconds(20));
        string? dateText = Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPORT_DATE");
        foreach ((string picker, string? selected) in new[]
        {
            ("FromPicker", Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPORT_FROM_DATE") ?? dateText),
            ("ToPicker", Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPORT_TO_DATE") ?? dateText),
        })
        {
            if (selected is null) continue;
            DateTime date = DateTime.ParseExact(selected, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture);
            AutomationElement input = shell.FindFirstDescendant(cf => cf.ByAutomationId(picker))!
                .FindFirstDescendant(cf => cf.ByAutomationId("PART_TextBox"))!;
            input.AsTextBox().Text = date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
        }
        string path = Path.Combine(root, "phase1-five-million-history-" + Guid.NewGuid().ToString("N") + ".csv");
        var timer = Stopwatch.StartNew();
        shell.FindFirstDescendant(cf => cf.ByAutomationId("ExportButton"))!.Click();
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Window? save = null;
        try
        {
            await WaitUntilAsync(() =>
            {
                save = automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Save As"))?.AsWindow();
                return save is not null;
            }, TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            File.WriteAllText(Path.Combine(root, "scale-audit-export-dialogs.txt"),
                string.Join(Environment.NewLine, automation.GetDesktop().FindAllChildren()
                    .Where(element => element.ControlType == FlaUI.Core.Definitions.ControlType.Window)
                    .Take(20).Select(window => $"{SafeName(window)}: {string.Join(" | ", window.FindAllDescendants().Take(100).Select(SafeName))}")));
            shell.CaptureToFile(Path.Combine(root, "scale-audit-export-dialogs.png"));
            throw;
        }
        Keyboard.Type(path);
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        await WaitUntilAsync(() => File.Exists(path), TimeSpan.FromMinutes(15));
        timer.Stop();
        long count = File.ReadLines(path).LongCount() - 1;
        long expected = long.Parse(Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPECT_EXPORT_ROWS") ?? "5000000",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, count);
        if (expected == 5_000_000)
        {
            string firstRow = File.ReadLines(path).Skip(1).First();
            Assert.Contains("\"PHASE1-5000000\"", firstRow, StringComparison.Ordinal);
            Assert.EndsWith(",1.00", firstRow, StringComparison.Ordinal);
        }
        Assert.False(app.HasExited);
        using Process? process = Process.GetProcessesByName("GroceryErp.App")
            .OrderByDescending(static item => item.StartTime).FirstOrDefault();
        process?.Refresh();
        File.WriteAllText(Path.Combine(root, "scale-audit-history-export.json"),
            JsonSerializer.Serialize(new
            {
                rows = count,
                elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                bytes = new FileInfo(path).Length,
                peakWorkingSet = process?.PeakWorkingSet64,
                alive = !app.HasExited
            }, JsonOptions));
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name; }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException) { return "<unavailable>"; }
    }

    private static async Task<Window> WaitWindowAsync(UiApplication app, UIA3Automation automation,
        string automationId, TimeSpan timeout)
    {
        Window? found = null;
        await WaitUntilAsync(() =>
        {
            try
            {
                found = app.GetAllTopLevelWindows(automation).FirstOrDefault(window =>
                    window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null);
                return found is not null;
            }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x80131505))
            {
                return false;
            }
        }, timeout, 25);
        return found!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, int pollMilliseconds = 1000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > timeout) throw new TimeoutException($"UI did not reach expected state within {timeout}.");
            await Task.Delay(pollMilliseconds, TestContext.Current.CancellationToken);
        }
    }
}
