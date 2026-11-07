using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using GroceryErp.App.Presentation;
using GroceryErp.Assets;
using GroceryErp.Catalog;
using GroceryErp.Core.Errors;
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

    private void TableGrid_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid) TableColumnLayouts.Attach(grid);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ScrollWheelRouting.Register();
        ScreenHelp.Register();
        TableColumnLayouts.Register();
        void RecordStartup(string phase) => PerformanceMetrics.UiStartupStageMilliseconds.Record(
            Stopwatch.GetElapsedTime(Program.LaunchTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("phase", phase));

        try
        {
            // Every window before the settings load (the upgrade window, startup errors) draws in
            // software; the store's choice applies before setup and sign-in.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
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
                if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_ACCEPTANCE") == "1")
                    _skipCloseBackup = true; // The isolated acceptance setup already verified its backup.
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
            builder.Services.AddSingleton<AnnualCloseService>();
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
            builder.Services.AddSingleton<PageStateStore>();
            builder.Services.AddSingleton<UsbUpdateService>();
            builder.Services.AddSingleton<ReleaseNotesService>();
            builder.Services.AddSingleton<ShellViewModel>();
            builder.Services.AddSingleton<ShellWindow>();

            _host = builder.Build();
            await _host.StartAsync();
            RecordStartup("host-started");

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
            backups.FinishPendingCompaction();
            // An existing store that needs an upgrade is shown with its time and space estimate
            // first; nothing changes until the owner confirms, and it then runs off the UI thread.
            MigrationRunner migrations = _host.Services.GetRequiredService<MigrationRunner>();
            if (migrations.Plan() is { } plan)
            {
                if (plan.UiLanguage is "en-IN" or "gu-IN")
                    TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo(plan.UiLanguage);
                var upgrade = new UpgradeWindow(plan, progress => Task.Run(() => migrations.Run(progress)),
                    paths.DefaultBackupDir, paths.LogsDir);
                upgrade.Loaded += (_, _) => RecordStartup("upgrade-loaded");
                if (upgrade.ShowDialog() != true) { await RequestShutdownAsync(); return; }
                RecordStartup("upgrade-done");
            }
            StartupState startup = await _host.Services.GetRequiredService<StartupPipeline>().RunAsync();
            // The compact store has opened again, so the original it replaced can go.
            backups.PurgeCompactedOriginals();
            RecordStartup("database-ready");
            TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo(startup.Settings.UiLanguage);
            if (startup.Settings.HardwareRendering) RenderOptions.ProcessRenderMode = RenderMode.Default;
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
            login.Loaded += (_, _) => RecordStartup("login-loaded");
            if (login.ShowDialog() != true) { await RequestShutdownAsync(); return; }
            long signInAccepted = Stopwatch.GetTimestamp();

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
            // Nothing else uses the store between sign-in and the shell, so compaction runs here.
            else if (await CompactIfRequestedAsync(backups, settings))
                signInAccepted = Stopwatch.GetTimestamp();

            MaintenanceService maintenance = _host.Services.GetRequiredService<MaintenanceService>();
            StartupHealth health = startup.Health;
            if (health.FreeDiskWarning)
            {
                Log.Warning("Available disk space is below 1 GB");
                MessageBox.Show(TranslationSource.Instance["Startup_LowDisk"], TranslationSource.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
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
            shell.Loaded += (_, _) =>
            {
                RecordStartup("shell-loaded");
                PerformanceMetrics.UiStartupStageMilliseconds.Record(
                    Stopwatch.GetElapsedTime(signInAccepted).TotalMilliseconds,
                    new KeyValuePair<string, object?>("phase", "sign-in-to-shell"));
            };
            MainWindow = shell;
            shell.Closed += async (_, _) => await RequestShutdownAsync();
            shell.Show();
            if (pendingPrintReview)
                MessageBox.Show(TranslationSource.Instance["Startup_PrintReview"],
                    TranslationSource.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            ReleaseNotesService releaseNotesService = _host.Services.GetRequiredService<ReleaseNotesService>();
            if (releaseNotesService.GetUnseen(TranslationSource.Instance.UiCulture) is { } releaseNotes)
            {
                // A message box cannot scroll, so long notes would run off a 1366×768 screen.
                new ReleaseNotesWindow(releaseNotes.Text) { Owner = shell }.ShowDialog();
                await releaseNotesService.MarkSeenAsync(releaseNotes.Version);
            }
            _maintenanceCancellation = new CancellationTokenSource();
            AssetService assets = _host.Services.GetRequiredService<AssetService>();
            AnnualCloseService annualClose = _host.Services.GetRequiredService<AnnualCloseService>();
            _maintenanceTask = RunMaintenanceWhenIdleAsync(shell.IsIdleFor,
            [
                token => assets.PostDueDepreciationAsync(token),
                token => annualClose.EnsureFinalizedAsync(token),
                token => maintenance.RunAfterShellAsync(token),
            ], _maintenanceCancellation.Token);
            _ = _maintenanceTask.ContinueWith(
                task =>
                {
                    if (task.Exception is not { } failure) return;
                    Log.Error(failure, "Background startup maintenance failed");
                    shell.ShowMaintenanceWarning(LotDifferencesOnly(failure));
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
                    exception is Microsoft.Data.Sqlite.SqliteException or IOException or DomainException
                        ? UserError.Message(exception)
                        : TranslationSource.Instance["Startup_Failed"],
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

    private static int _culturesConfigured;

    /// <summary>
    /// Sets the calling UI thread's cultures. Runs synchronously before the dispatcher starts:
    /// a culture set inside the asynchronous startup is undone when startup first awaits, and a
    /// DatePicker then shows the machine's format (10/24/2026 on an en-US PC) instead of 24-10-2026.
    /// </summary>
    internal static void ConfigureCultures()
    {
        CultureInfo formatCulture = TranslationSource.FormatCulture;
        CultureInfo.DefaultThreadCurrentCulture = formatCulture;
        CultureInfo.CurrentCulture = PickerCulture;
        if (Interlocked.Exchange(ref _culturesConfigured, 1) == 0)
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(formatCulture.IetfLanguageTag)));
        TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("en-IN");
    }

    // DatePicker formats with the thread culture; this is en-IN with the dd-MM-yyyy dates the app
    // prints and accepts everywhere else.
    private static readonly CultureInfo PickerCulture = CreatePickerCulture();

    private static CultureInfo CreatePickerCulture()
    {
        var culture = (CultureInfo)TranslationSource.FormatCulture.Clone();
        culture.DateTimeFormat.ShortDatePattern = "dd-MM-yyyy";
        culture.DateTimeFormat.DateSeparator = "-";
        return CultureInfo.ReadOnly(culture);
    }

    internal IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("The application host is not ready.");

    // Pages hosted by UI tests have no host; optional session helpers are then skipped.
    internal IServiceProvider? ServicesOrNull => _host?.Services;

    // The business date and time from the app clock. Windows hosted without the app (UI tests)
    // have no clock service and use the machine clock.
    internal static DateOnly BusinessDate =>
        (Current as App)?.ServicesOrNull?.GetService<IClock>()?.BusinessDate ?? DateOnly.FromDateTime(MachineNow);

    internal static DateTime ClockNow => (Current as App)?.ServicesOrNull?.GetService<IClock>()?.Now ?? MachineNow;

#pragma warning disable RS0030 // The one fallback for windows hosted without the app clock.
    private static DateTime MachineNow => DateTime.Now;
#pragma warning restore RS0030

    internal static IReadOnlyList<IModule> CreateModules() =>
    [
        new SalesModule(typeof(BillingPage), typeof(SalesHistoryPage)),
        new PurchasingModule(typeof(PurchasesPage), typeof(VendorsPage)),
        new InventoryModule(),
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

    // Each step runs once per session, whatever another step found: a failed weekly check
    // never holds back due depreciation or a year's close. Failures are reported together
    // after the last step.
    internal static async Task RunMaintenanceWhenIdleAsync(Func<TimeSpan, bool> isIdleFor,
        IReadOnlyList<Func<CancellationToken, Task>> steps, CancellationToken cancellationToken)
    {
        TimeSpan idleWindow = TimeSpan.FromMinutes(10);
        List<Exception> failures = [];
        int next = 0;
        while (next < steps.Count)
        {
            while (!isIdleFor(idleWindow))
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            using var workCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task activityMonitor = MonitorActivityAsync();
            try
            {
                for (; next < steps.Count; next++)
                    try { await steps[next](workCancellation.Token); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failures.Add(exception);
                    }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A customer returned during a large integrity read. The interrupted step
                // resumes at the next idle window; finished steps are not repeated.
            }
            finally
            {
                workCancellation.Cancel();
                try { await activityMonitor; }
                catch (OperationCanceledException) { }
            }

            async Task MonitorActivityAsync()
            {
                while (!workCancellation.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), workCancellation.Token);
                    if (!isIdleFor(idleWindow)) workCancellation.Cancel();
                }
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    // When lots differing from their documents is all maintenance found, the warning names
    // them and opens their list; anything else gets the general safety-check warning.
    internal static int? LotDifferencesOnly(AggregateException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ReadOnlyCollection<Exception> inner = failure.Flatten().InnerExceptions;
        return inner.Count > 0 && inner.All(static exception =>
                exception is DomainException { Code: ErrorCode.LotsDifferFromDocuments })
            ? Convert.ToInt32(((DomainException)inner[0]).Arguments[0], CultureInfo.InvariantCulture)
            : null;
    }

    internal void SkipCloseBackup() => _skipCloseBackup = true;

    // Settings key recording that the one-time compaction offer was shown for this store.
    private const string CompactionOfferedKey = "maintenance.compact_offered";

    /// <summary>
    /// Runs a compaction requested from Settings, or offers one once when the store has
    /// much unused space (as after the stock ledger was removed). Returns whether one ran.
    /// </summary>
    private static async Task<bool> CompactIfRequestedAsync(BackupService backups, SettingsService settings)
    {
        if (!backups.HasCompactionRequest)
        {
            if (settings.Current.Values.GetValueOrDefault(CompactionOfferedKey) == "1") return false;
            CompactionEstimate estimate = await Task.Run(backups.EstimateCompaction);
            if (!estimate.IsWorthwhile || !estimate.HasSpace) return false;
            await settings.SaveAsync(new Dictionary<string, string> { [CompactionOfferedKey] = "1" });
            if (MessageBox.Show(TranslationSource.Instance.Format("Startup_CompactOffer",
                        CompactionWindow.FormatBytes(estimate.ReclaimableBytes),
                        CompactionWindow.FormatBytes(estimate.DatabaseBytes)),
                    TranslationSource.Instance["App_Title"], MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return false;
        }
        new CompactionWindow(backups).ShowDialog();
        return true;
    }
}
#pragma warning restore CA1001
