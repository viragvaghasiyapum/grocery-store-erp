using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using GroceryErp.App.Presentation;
using GroceryErp.Core.Time;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Security;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace GroceryErp.App;

public partial class App : Application
{
    private IHost? _host;
    private bool _databaseReady;

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
            builder.Services.AddSingleton<MigrationRunner>();
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
                MessageBox.Show("An unexpected error occurred. Details were written to the log.", "Grocery ERP", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            SqliteConnectionFactory factory = _host.Services.GetRequiredService<SqliteConnectionFactory>();
            DatabaseCapabilities.Verify(factory);
            _host.Services.GetRequiredService<MigrationRunner>().Run();
            _host.Services.GetRequiredService<BackupService>().FinishPendingRestore();
            SettingsService settings = _host.Services.GetRequiredService<SettingsService>();
            await settings.LoadAsync();
            TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo(
                await ReadLanguageAsync(_host.Services.GetRequiredService<DbExecutor>()));
            _databaseReady = true;

            AuthenticationService authentication = _host.Services.GetRequiredService<AuthenticationService>();
            if (!await authentication.HasUserAsync())
            {
                var setup = new SetupWindow(
                    _host.Services.GetRequiredService<FirstRunService>(), paths,
                    _host.Services.GetRequiredService<BackupService>(),
                    settings);
                if (setup.ShowDialog() != true) { Shutdown(); return; }
                await settings.LoadAsync();
            }

            var login = new LoginWindow(authentication);
            if (login.ShowDialog() != true) { Shutdown(); return; }

            ShellWindow shell = _host.Services.GetRequiredService<ShellWindow>();
            MainWindow = shell;
            shell.Show();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application startup failed");
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                if (_databaseReady)
                {
                    try { _host.Services.GetRequiredService<BackupService>().Create("close"); }
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

    private static Task<string> ReadLanguageAsync(DbExecutor database) => database.ReadAsync(connection =>
        Dapper.SqlMapper.ExecuteScalar<string>(connection, "SELECT value FROM setting WHERE key = 'ui.language'") ?? "en-IN");

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
}
