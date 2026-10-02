using System.Globalization;
using System.IO;
using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Localization;
using GroceryErp.Printing;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

public partial class ProductLabelWindow : Window
{
    private readonly ProductDetails _details;
    private readonly int _width;
    private readonly int _height;

    public ProductLabelWindow(ProductDetails details, string printerName, string labelSize)
    {
        ArgumentNullException.ThrowIfNull(details);
        _details = details;
        (_width, _height) = ParseSize(labelSize);
        InitializeComponent();
        ProductNameText.Text = details.Product.Name;
        LocalNameText.Text = details.Product.NameLocal;
        BarcodeText.Text = details.Product.Barcodes.FirstOrDefault()?.Barcode ?? string.Empty;
        if (BarcodeText.Text.Length > 0) BarcodeImage.Source = BarcodePreview.Create(BarcodeText.Text);
        PriceText.Text = $"₹{details.Product.DefaultSellingPrice?.Value.ToString("0.00", CultureInfo.InvariantCulture) ?? "—"}";
        MrpText.Text = details.Product.DefaultMrp is null ? string.Empty : $"MRP {details.Product.DefaultMrp.Value.Value.ToString("0.00", CultureInfo.InvariantCulture)}";
        PrinterBox.Text = printerName;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[] command = BuildCommand();
            var dialog = new SaveFileDialog
            {
                Filter = "TSPL print file (*.prn)|*.prn|All files (*.*)|*.*",
                FileName = $"{_details.Product.Sku}-label.prn",
                AddExtension = true,
                DefaultExt = ".prn",
            };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllBytes(dialog.FileName, command);
            StatusText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void Print_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            PrintButton.IsEnabled = false;
            byte[] command = BuildCommand();
            string printer = PrinterBox.Text;
            await Task.Run(() => RawPrinter.Send(printer, command, $"{_details.Product.Sku} labels"));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            PrintButton.IsEnabled = true;
        }
    }

    private byte[] BuildCommand()
    {
        ProductBarcodeInput barcode = _details.Product.Barcodes.FirstOrDefault()
            ?? throw new InvalidOperationException(TranslationSource.Instance["Catalog_LabelBarcodeRequired"]);
        if (!int.TryParse(CopiesBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int copies) || copies is < 1 or > 999)
            throw new InvalidOperationException(TranslationSource.Instance["Catalog_LabelCopiesRange"]);
        decimal price = _details.Product.DefaultSellingPrice?.Value
            ?? throw new InvalidOperationException(TranslationSource.Instance["Catalog_LabelPriceRequired"]);
        return TsplLabelBuilder.Build(new(
            _details.Product.Name, _details.Product.NameLocal, barcode.Barcode, price,
            _details.Product.DefaultMrp?.Value, _width, _height, copies));
    }

    private static (int Width, int Height) ParseSize(string value)
    {
        string[] parts = value.Split(['x', 'X', '×'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int width) &&
               int.TryParse(parts[1], CultureInfo.InvariantCulture, out int height)
            ? (width, height)
            : (60, 40);
    }
}
