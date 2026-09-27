using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public partial class ProductImportWindow : Window
{
    private readonly ProductExcelImporter _importer;
    private ProductImportPreview? _preview;

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
            ImportButton.IsEnabled = false;
            StatusText.Text = TranslationSource.Instance["Catalog_Analyzing"];
            _preview = await _importer.DryRunAsync(dialog.FileName);
            PreviewGrid.ItemsSource = _preview.Rows;
            StatusText.Text = TranslationSource.Instance.Format("Catalog_ImportPreview", _preview.ValidCount, _preview.ErrorCount);
            ImportButton.IsEnabled = _preview.ValidCount > 0;
        }
        catch (Exception exception)
        {
            _preview = null;
            PreviewGrid.ItemsSource = null;
            StatusText.Text = exception.Message;
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
            StatusText.Text = exception.Message;
        }
    }

    private async void Import_OnClick(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        try
        {
            ImportButton.IsEnabled = false;
            ProductImportSummary summary = await _importer.ImportAsync(_preview, OperationGuard.NewId());
            MessageBox.Show(
                TranslationSource.Instance.Format("Catalog_ImportComplete", summary.Created, summary.Updated, summary.Skipped),
                TranslationSource.Instance["Catalog_ImportTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            ImportButton.IsEnabled = true;
        }
    }
}
