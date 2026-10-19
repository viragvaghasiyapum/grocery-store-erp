using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.App.Presentation;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;

namespace GroceryErp.Phase2StockReview;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            bool smoke = args.Length > 2 && args[^1] == "--smoke";
            string[] selection = smoke ? args[1..^1] : args[1..];
            if (args.Length < 2 || selection.Length is < 1 or > 100 ||
                !selection.All(static value => long.TryParse(value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out long id) && id > 0))
                throw new ArgumentException("Usage: GroceryErp.Phase2StockReview <phase2-data-root> <product-id> [product-id ...]");
            string root = Path.GetFullPath(args[0]);
            if (!Path.GetFileName(root).Contains("phase2", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only a disposable phase2 data root may be opened.");
            long[] productIds = selection.Select(static value => long.Parse(value,
                CultureInfo.InvariantCulture)).Distinct().ToArray();
            if (productIds.Length > 100) throw new ArgumentException("Select at most 100 products.");

            var paths = new AppPaths(new SettingsBootstrap(root));
            if (!File.Exists(paths.DatabaseFile)) throw new FileNotFoundException("Database is missing.", paths.DatabaseFile);
            DapperSetup.Register();
            var factory = new SqliteConnectionFactory(paths);
            try
            {
                using (var check = factory.Open())
                {
                    using var version = check.CreateCommand();
                    version.CommandText = "PRAGMA user_version";
                    if (Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture) != 30)
                        throw new InvalidOperationException("This launcher requires the isolated schema-30 sandbox.");
                    using var marker = check.CreateCommand();
                    marker.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='direct_stock_sandbox'";
                    if (Convert.ToInt64(marker.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                        throw new InvalidOperationException("The direct-stock sandbox marker is missing.");
                }
                using var database = new DbExecutor(factory);
                using var settings = new SettingsService(database, paths, new AuditLog());
                settings.LoadAsync().GetAwaiter().GetResult();
                TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo(settings.Current.UiLanguage);

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/GroceryErp.UI.Common;component/Themes/Tokens.xaml"),
                });
                var compact = new Style(typeof(DataGrid), (Style)app.Resources["CatalogGridBase"]);
                compact.Setters.Add(new Setter(DataGrid.RowHeightProperty, 40d));
                compact.Setters.Add(new Setter(DataGrid.ColumnHeaderHeightProperty, 32d));
                compact.Setters.Add(new Setter(ScrollViewer.CanContentScrollProperty, true));
                app.Resources.Add("CompactTableGrid", compact);

                var counts = new DirectStockCountService(database, new DirectStockEngine(),
                    new PeriodGuard(settings));
                if (smoke)
                {
                    DirectCountLotPage page = counts.GetPageAsync(productIds).GetAwaiter().GetResult();
                    if (page.Items.Count == 0) throw new InvalidOperationException("Selected products have no lots.");
                    var probe = new DirectCountReviewWindow(counts, productIds,
                        new SystemClock(settings), loadOnShow: false);
                    probe.Show();
                    probe.UpdateLayout();
                    probe.Close();
                    return 0;
                }
                var window = new DirectCountReviewWindow(counts, productIds, new SystemClock(settings));
                return app.Run(window);
            }
            finally { factory.ClearPool(); }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Phase 2 stock review", MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }
}
