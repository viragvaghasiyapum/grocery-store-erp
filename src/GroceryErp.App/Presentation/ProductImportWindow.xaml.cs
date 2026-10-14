using System.Windows;
using System.Windows.Threading;
using GroceryErp.Catalog;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public partial class ProductImportWindow : Window
{
    private readonly ProductExcelImporter _importer;
    private ProductImportPreview? _preview;
    private bool _importing;

    public ProductImportWindow(ProductExcelImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        _importer = importer;
        InitializeComponent();
    }

    private async void Browse_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        PathBox.Text = dialog.FileName;
        try
        {
            BrowseButton.IsEnabled = false;
            _preview = null;
            PreviewGrid.ItemsSource = null;
            UpdateImportButton();
            StatusText.Text = TranslationSource.Instance["Catalog_Analyzing"];
            _preview = await _importer.DryRunAsync(dialog.FileName);
            PreviewGrid.ItemsSource = _preview.Rows;
            StatusText.Text = TranslationSource.Instance.Format("Catalog_ImportPreview", _preview.ValidCount, _preview.ErrorCount);
        }
        catch (Exception exception)
        {
            _preview = null;
            PreviewGrid.ItemsSource = null;
            StatusText.Text = UserError.Message(exception);
        }
        finally
        {
            BrowseButton.IsEnabled = true;
            UpdateImportButton();
        }
    }

    private void Template_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = "product-import-template.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ProductExcelImporter.WriteTemplate(dialog.FileName);
            StatusText.Text = TranslationSource.Instance["Catalog_TemplateSaved"];
        }
        catch (Exception exception)
        {
            StatusText.Text = UserError.Message(exception);
        }
    }

    private void ExportErrors_OnClick(object sender, RoutedEventArgs e)
    {
        if (_preview is null || _preview.ErrorCount == 0) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = "product-import-errors.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ProductExcelImporter.WriteErrors(dialog.FileName, _preview);
            StatusText.Text = TranslationSource.Instance["Catalog_ErrorsSaved"];
        }
        catch (Exception exception) { StatusText.Text = UserError.Message(exception); }
    }

    private void PreviewGrid_OnCurrentCellChanged(object sender, EventArgs e) => UpdateImportButton();

    private void PreviewSelection_OnChanged(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateImportButton, DispatcherPriority.Background);

    private void UpdateImportButton()
    {
        if (ImportButton is null) return;
        ImportButton.IsEnabled = !_importing && _preview?.Rows.Any(static row => row.IsSelected && row.CanImport) == true;
    }

    private async void Import_OnClick(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        try
        {
            _importing = true;
            ImportButton.IsEnabled = false;
            PreviewGrid.IsEnabled = false;
            BrowseButton.IsEnabled = false;
            PreviewGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            HashSet<int> selected = _preview.Rows.Where(static row => row.IsSelected && row.CanImport)
                .Select(static row => row.RowNumber).ToHashSet();
            if (selected.Count == 0)
            {
                StatusText.Text = TranslationSource.Instance["Catalog_SelectForImport"];
                return;
            }
            ProductImportRow[] selectedRows = _preview.Rows.Where(row => row.CanImport && selected.Contains(row.RowNumber)).ToArray();
            string operationId = ImportJobs.OperationIdFor("PRODUCT", selectedRows);
            ProductImportSummary summary = await _importer.ImportAsync(_preview, operationId, selected);
            MessageBox.Show(
                TranslationSource.Instance.Format("Catalog_ImportComplete", summary.Created, summary.Updated, summary.Skipped),
                TranslationSource.Instance["Catalog_ImportTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = UserError.Message(exception);
        }
        finally
        {
            _importing = false;
            PreviewGrid.IsEnabled = true;
            BrowseButton.IsEnabled = true;
            UpdateImportButton();
        }
    }
}
