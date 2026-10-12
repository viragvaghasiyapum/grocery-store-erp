using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using GroceryErp.App.Presentation;
using GroceryErp.Assets;
using GroceryErp.Catalog;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Expenses;
using GroceryErp.Inventory;
using GroceryErp.Inventory.Contracts;
using GroceryErp.Localization;
using GroceryErp.Purchasing;
using GroceryErp.Reports;
using GroceryErp.Sales;
using GroceryErp.Settings;
using GroceryErp.UI.Common.Modularity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace GroceryErp.App;

// WPF owns Application lifetime; OnExit releases the mutex on every shutdown path.
#pragma warning disable CA1001
public partial class App : Application
{
    private IHost? _host;
    private bool _databaseReady;
    private bool _skipCloseBackup;
    private Task? _maintenanceTask;
    private CancellationTokenSource? _maintenanceCancellation;
    private Task? _shutdownTask;
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            ConfigureCultures();
            _instanceMutex = new Mutex(initiallyOwned: false, @"Local\GroceryErp.SingleInstance");
            try { _ownsInstanceMutex = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstanceMutex = true; }
            if (!_ownsInstanceMutex)
            {
                MessageBox.Show(TranslationSource.Instance["Startup_AlreadyRunning"],
                    TranslationSource.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Information);
                await RequestShutdownAsync();
                return;
            }
            // The packaged UI runner supplies an isolated temporary store without changing
            // the machine's real bootstrap file or operational database.
            string? testRoot = Environment.GetEnvironmentVariable("GROCERY_ERP_TEST_DATA_ROOT");
            if (testRoot is not null)
            {
                string expectedPrefix = Path.Combine(Path.GetTempPath(), "grocery-erp-packaged-ui-");
                if (!Path.GetFullPath(testRoot).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The packaged UI test data root must be temporary.");
            }
            if (testRoot is null) DataRootRelocator.ApplyPending();
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(e.Args);
            var paths = new AppPaths(testRoot is null ? SettingsBootstrap.Load() : new SettingsBootstrap(testRoot));
            string logDirectory = paths.LogsDir;

            builder.Services.AddSerilog((_, configuration) => configuration
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(logDirectory, "grocery-erp-.log"),
                    formatProvider: CultureInfo.InvariantCulture,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30));
            builder.Services.AddSingleton(TranslationSource.Instance);
            builder.Services.AddSingleton<DisplayFormatter>();
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<PerformanceRecorder>();
            builder.Services.AddHostedService(static provider => provider.GetRequiredService<PerformanceRecorder>());
            builder.Services.AddSingleton<SqliteConnectionFactory>();
            builder.Services.AddSingleton<DbExecutor>();
            builder.Services.AddSingleton<SettingsService>();
            builder.Services.AddSingleton<ISettings>(static provider => provider.GetRequiredService<SettingsService>());
            builder.Services.AddSingleton<IBusinessClockSettings>(static provider => provider.GetRequiredService<SettingsService>());
            builder.Services.AddSingleton<IClock, SystemClock>();
            builder.Services.AddSingleton<PasswordHasher>();
            builder.Services.AddSingleton<AuthenticationService>();
            builder.Services.AddSingleton<FirstRunService>();
            builder.Services.AddSingleton<BackupService>();
            builder.Services.AddSingleton<IMigrationBackup>(static provider => provider.GetRequiredService<BackupService>());
            builder.Services.AddSingleton<IAuditLog, AuditLog>();
            builder.Services.AddSingleton<DocNumberService>();
            builder.Services.AddSingleton<PeriodGuard>();
            builder.Services.AddSingleton<MigrationRunner>();
            builder.Services.AddSingleton<MaintenanceService>();
            builder.Services.AddSingleton<StartupPipeline>();
            builder.Services.AddSingleton<ShutdownPipeline>();
            IReadOnlyList<IModule> modules = CreateModules();
            foreach (IModule module in modules)
            {
                module.Register(builder.Services);
                TranslationSource.Instance.Register(module.Key, module.Strings);
            }
            builder.Services.AddSingleton(modules);
            builder.Services.AddSingleton<ReportNavigationState>();
            builder.Services.AddSingleton<UsbUpdateService>();
            builder.Services.AddSingleton<ReleaseNotesService>();
            builder.Services.AddSingleton<ShellViewModel>();
            builder.Services.AddSingleton<ShellWindow>();

            _host = builder.Build();
            await _host.StartAsync();

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Fatal(args.ExceptionObject as Exception, "Unhandled application-domain exception");
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error(args.Exception, "Unobserved task exception");
                args.SetObserved();
            };
            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error(args.Exception, "Unhandled UI exception");
                MessageBox.Show(
                    TranslationSource.Instance["App_UnexpectedError"],
                    TranslationSource.Instance["App_Title"],
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };

            SettingsService settings = _host.Services.GetRequiredService<SettingsService>();
            BackupService backups = _host.Services.GetRequiredService<BackupService>();
            backups.RecoverInterruptedRestoreBeforeStartup();
            StartupState startup = await _host.Services.GetRequiredService<StartupPipeline>().RunAsync();
            TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo(startup.Settings.UiLanguage);
            _databaseReady = true;

            AuthenticationService authentication = _host.Services.GetRequiredService<AuthenticationService>();
            if (!await authentication.HasUserAsync())
            {
                var setup = new SetupWindow(
                    _host.Services.GetRequiredService<FirstRunService>(), paths,
                    backups,
                    settings);
                if (setup.ShowDialog() != true) { await RequestShutdownAsync(); return; }
                await settings.LoadAsync();
                if (DataRootRelocator.HasPending(paths.Root))
                {
                    MessageBox.Show(TranslationSource.Instance["Settings_DataMoveRestart"],
                        TranslationSource.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Information);
                    await RequestShutdownAsync();
                    return;
                }
            }

            var login = new LoginWindow(authentication);
            if (login.ShowDialog() != true) { await RequestShutdownAsync(); return; }

            if (backups.HasPendingRestore)
            {
                MessageBoxResult restoreChoice = MessageBox.Show(
                    TranslationSource.Instance["Startup_RestorePending"],
                    TranslationSource.Instance["App_Title"], MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (restoreChoice == MessageBoxResult.Yes)
                {
                    backups.FinishPendingRestore();
                }
                else if (restoreChoice == MessageBoxResult.No)
                {
                    backups.UndoPendingRestore();
                    MessageBox.Show(TranslationSource.Instance["Startup_RestoreUndone"], TranslationSource.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Information);
                    await RequestShutdownAsync();
                    return;
                }
                else
                {
                    await RequestShutdownAsync();
                    return;
                }
            }

            MaintenanceService maintenance = _host.Services.GetRequiredService<MaintenanceService>();
            StartupHealth health = startup.Health;
            if (health.FreeDiskWarning)
            {
                Log.Warning("Available disk space is below 1 GB");
                MessageBox.Show(TranslationSource.Instance["Startup_LowDisk"], TranslationSource.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (!health.LotCacheHealthy)
            {
                Log.Error("Lot cache integrity check failed");
                MessageBoxResult rebuild = MessageBox.Show(
                    TranslationSource.Instance["Startup_LotCache"], TranslationSource.Instance["App_Title"],
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (rebuild == MessageBoxResult.Yes)
                {
                    int corrected = await _host.Services.GetRequiredService<InventoryService>().RebuildStockCacheAsync();
                    Log.Warning("Rebuilt cached quantities for {LotCount} lots", corrected);
                }
                else
                {
                    await RequestShutdownAsync(1);
                    return;
                }
            }
            if (health.LastSaleDate is not null &&
                DateOnly.TryParseExact(health.LastSaleDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly lastSale) &&
                _host.Services.GetRequiredService<IClock>().BusinessDate < lastSale)
            {
                MessageBoxResult choice = MessageBox.Show(
                    string.Format(CultureInfo.CurrentCulture, TranslationSource.Instance["Startup_ClockWarning"], lastSale),
                    TranslationSource.Instance["App_Title"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes)
                {
                    await RequestShutdownAsync();
                    return;
                }
                await maintenance.OverrideClockWarningAsync(health.LastSaleDate);
            }

            bool pendingPrintReview = _host.Services.GetRequiredService<ReceiptPrinter>().HasFailedJobs;
            ShellWindow shell = _host.Services.GetRequiredService<ShellWindow>();
            MainWindow = shell;
            shell.Closed += async (_, _) => await RequestShutdownAsync();
            shell.Show();
            if (pendingPrintReview)
                MessageBox.Show(TranslationSource.Instance["Startup_PrintReview"],
                    TranslationSource.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            ReleaseNotesService releaseNotesService = _host.Services.GetRequiredService<ReleaseNotesService>();
            if (releaseNotesService.GetUnseen(TranslationSource.Instance.UiCulture) is { } releaseNotes)
            {
                MessageBox.Show(releaseNotes.Markdown, TranslationSource.Instance["ReleaseNotes_Title"],
                    MessageBoxButton.OK, MessageBoxImage.Information);
                await releaseNotesService.MarkSeenAsync(releaseNotes.Version);
            }
            _maintenanceCancellation = new CancellationTokenSource();
            _maintenanceTask = RunMaintenanceWhenIdleAsync(shell, maintenance,
                _host.Services.GetRequiredService<AssetService>(), _maintenanceCancellation.Token);
            _ = _maintenanceTask.ContinueWith(
                task =>
                {
                    if (!task.IsFaulted) return;
                    Log.Error(task.Exception, "Background startup maintenance failed");
                    shell.ShowMaintenanceWarning();
                },
                TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application startup failed");
            if (_host?.Services.GetService<BackupService>() is { HasPendingRestore: true } backups)
            {
                MessageBoxResult undo = MessageBox.Show(
                    TranslationSource.Instance["Startup_RestoreFailed"],
                    TranslationSource.Instance["App_Title"],
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Error);
                if (undo == MessageBoxResult.Yes)
                {
                    try
                    {
                        backups.UndoPendingRestore();
                        MessageBox.Show(
                            TranslationSource.Instance["Startup_RestoreUndone"],
                            TranslationSource.Instance["App_Title"],
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    catch (Exception undoException)
                    {
                        Log.Fatal(undoException, "Failed to undo restore after startup failure");
                    }
                }
            }
            else
            {
                MessageBox.Show(
                    TranslationSource.Instance["Startup_Failed"],
                    TranslationSource.Instance["App_Title"],
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            await RequestShutdownAsync(1);
        }
    }

    internal Task RequestShutdownAsync(int exitCode = 0) =>
        _shutdownTask ??= CompleteShutdownAsync(exitCode);

    private async Task CompleteShutdownAsync(int exitCode)
    {
        try
        {
            if (_host is not null)
            {
                _maintenanceCancellation?.Cancel();
                if (_maintenanceTask is not null)
                {
                    try { await _maintenanceTask; }
                    catch (OperationCanceledException) { }
                    catch (Exception exception) { Log.Error(exception, "Background startup maintenance failed during shutdown"); }
                }
                if (_databaseReady && !_skipCloseBackup &&
                    _host.Services.GetRequiredService<BackupService>().HasPendingRestore == false)
                {
                    try { await _host.Services.GetRequiredService<ShutdownPipeline>().RunAsync(); }
                    catch (Exception exception) { Log.Error(exception, "Close backup failed"); }
                }
                await _host.StopAsync();
                _host.Dispose();
                _host = null;
            }

            await Log.CloseAndFlushAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Application shutdown failed");
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _maintenanceCancellation?.Dispose();
        _host?.Dispose();
        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
            _ownsInstanceMutex = false;
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureCultures()
    {
        CultureInfo formatCulture = TranslationSource.FormatCulture;
        CultureInfo.DefaultThreadCurrentCulture = formatCulture;
        CultureInfo.CurrentCulture = formatCulture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(formatCulture.IetfLanguageTag)));
        TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("en-IN");
    }

    internal IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("The application host is not ready.");

    internal static IReadOnlyList<IModule> CreateModules() =>
    [
        new SalesModule(typeof(BillingPage), typeof(SalesHistoryPage)),
        new PurchasingModule(typeof(PurchasesPage)),
        new InventoryModule(typeof(StockPage)),
        new ExpensesModule(typeof(MoneyOutPage)),
        new AssetsModule(typeof(AssetsPage)),
        new ReportsModule(typeof(ReportsPage)),
        new CatalogModule(typeof(ProductsPage)),
        new SettingsModule(typeof(SettingsPage)),
    ];

    internal async Task WaitForStartupMaintenanceAsync()
    {
        _maintenanceCancellation?.Cancel();
        if (_maintenanceTask is not null)
            try { await _maintenanceTask; }
            catch (OperationCanceledException) { }
    }

    private static async Task RunMaintenanceWhenIdleAsync(ShellWindow shell, MaintenanceService maintenance,
        AssetService assets, CancellationToken cancellationToken)
    {
        while (!shell.IsIdleFor(TimeSpan.FromMinutes(2)))
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await maintenance.RunAfterShellAsync(cancellationToken);
        await assets.PostDueDepreciationAsync(cancellationToken);
    }

    internal void SkipCloseBackup() => _skipCloseBackup = true;
}
#pragma warning restore CA1001
