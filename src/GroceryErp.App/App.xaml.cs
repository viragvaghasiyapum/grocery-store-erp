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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace GroceryErp.App;

public partial class App : Application
{
    private IHost? _host;
    private bool _databaseReady;
    private Task? _maintenanceTask;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            ConfigureCultures();
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(e.Args);
            var paths = new AppPaths(SettingsBootstrap.Load());
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
            builder.Services.AddSingleton<CatalogService>();
            builder.Services.AddSingleton<ProductExcelImporter>();
            builder.Services.AddSingleton<GroceryErp.Catalog.Barcodes.ScanResolver>();
            builder.Services.AddSingleton<StockLedger>();
            builder.Services.AddSingleton<IStockLedger>(static provider => provider.GetRequiredService<StockLedger>());
            builder.Services.AddSingleton<InventoryService>();
            builder.Services.AddSingleton<OpeningStockExcelImporter>();
            builder.Services.AddSingleton<PurchasingService>();
            builder.Services.AddSingleton<SalesService>();
            builder.Services.AddSingleton<ReceiptPrinter>();
            builder.Services.AddSingleton<MoneyOutService>();
            builder.Services.AddSingleton<AssetService>();
            builder.Services.AddSingleton<ReportService>();
            builder.Services.AddSingleton<ReportOutputService>();
            builder.Services.AddSingleton<ReportNavigationState>();
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
                if (setup.ShowDialog() != true) { Shutdown(); return; }
                await settings.LoadAsync();
            }

            var login = new LoginWindow(authentication);
            if (login.ShowDialog() != true) { Shutdown(); return; }

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
                    Shutdown();
                    return;
                }
                else
                {
                    Shutdown();
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
                    Shutdown();
                    return;
                }
                await maintenance.OverrideClockWarningAsync(health.LastSaleDate);
            }

            ShellWindow shell = _host.Services.GetRequiredService<ShellWindow>();
            MainWindow = shell;
            shell.Show();
            _maintenanceTask = Task.WhenAll(
                maintenance.RunAfterShellAsync(),
                _host.Services.GetRequiredService<AssetService>().PostDueDepreciationAsync());
            _ = _maintenanceTask.ContinueWith(
                task => { if (task.IsFaulted) Log.Error(task.Exception, "Background startup maintenance failed"); },
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
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                if (_maintenanceTask is not null)
                {
                    try { await _maintenanceTask; }
                    catch (OperationCanceledException) { }
                    catch (Exception exception) { Log.Error(exception, "Background startup maintenance failed during shutdown"); }
                }
                if (_databaseReady)
                {
                    try { await _host.Services.GetRequiredService<ShutdownPipeline>().RunAsync(); }
                    catch (Exception exception) { Log.Error(exception, "Close backup failed"); }
                }
                await _host.StopAsync();
                _host.Dispose();
            }

            await Log.CloseAndFlushAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Application shutdown failed");
        }
        finally
        {
            base.OnExit(e);
        }
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
}
