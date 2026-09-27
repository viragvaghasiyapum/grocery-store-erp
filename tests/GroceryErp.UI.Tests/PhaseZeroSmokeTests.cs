using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GroceryErp.App.Presentation;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
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
                setup.Close();

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
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
    }

    private sealed class TestSettings : ISettings
    {
        public AppSettings Current { get; } = new("Test Store", 4, 0, null, 15, 30, "");
    }
}

