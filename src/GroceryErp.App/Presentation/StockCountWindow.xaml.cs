using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

public partial class StockCountWindow : Window
{
    private readonly InventoryService _inventory;
    private long _countId;
    public StockCountWindow(InventoryService inventory)
    {
        InitializeComponent();
        _inventory = inventory;
        Loaded += LoadAsync;
    }
    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            DateOnly date = ((App)Application.Current).Services.GetRequiredService<IClock>().BusinessDate;
            _countId = await _inventory.CreateStockCountAsync(date, null, null);
            StockCountDetails count = await _inventory.GetStockCountAsync(_countId);
            CountGrid.ItemsSource = count.Lines.Select(static line => new CountEditLine(line)).ToList();
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }
    private async void Post_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CountGrid.CommitEdit();
            foreach (CountEditLine line in CountGrid.ItemsSource.Cast<CountEditLine>())
                await _inventory.SetCountedQuantityAsync(_countId, line.LotId, new Quantity(decimal.Parse(line.Counted, CultureInfo.InvariantCulture)));
            await _inventory.PostStockCountAsync(_countId, OperationGuard.NewId());
            DialogResult = true;
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }

    private void Print_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        var document = new FlowDocument { FontFamily = new FontFamily("Nirmala UI"), FontSize = 11, PagePadding = new Thickness(36) };
        document.Blocks.Add(new Paragraph(new Run(TranslationSource.Instance["Stock_Count"])) { FontSize = 18, FontWeight = FontWeights.SemiBold });
        var table = new Table();
        table.Columns.Add(new TableColumn { Width = new GridLength(260) });
        table.Columns.Add(new TableColumn { Width = new GridLength(100) });
        table.Columns.Add(new TableColumn { Width = new GridLength(100) });
        table.Columns.Add(new TableColumn { Width = new GridLength(120) });
        var group = new TableRowGroup();
        group.Rows.Add(Row(TranslationSource.Instance["Catalog_Name"], TranslationSource.Instance["Stock_Expected"],
            TranslationSource.Instance["Stock_Counted"], TranslationSource.Instance["Purchasing_Notes"]));
        foreach (CountEditLine line in CountGrid.ItemsSource.Cast<CountEditLine>())
            group.Rows.Add(Row($"{line.ProductName} / {line.LotCode}", line.Expected.ToString(), "", ""));
        table.RowGroups.Add(group);
        document.Blocks.Add(table);
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "Stock count sheet");
    }

    private static TableRow Row(params string[] values)
    {
        var row = new TableRow();
        foreach (string value in values)
            row.Cells.Add(new TableCell(new Paragraph(new Run(value))) { Padding = new Thickness(4), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) });
        return row;
    }
    private async void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        try { if (_countId > 0) await _inventory.CancelStockCountAsync(_countId); }
        finally { DialogResult = false; }
    }
    private sealed class CountEditLine(StockCountLine line)
    {
        public long LotId { get; } = line.LotId;
        public string ProductName { get; } = line.ProductName;
        public string LotCode { get; } = line.LotCode;
        public Quantity Expected { get; } = line.Expected;
        public string Counted { get; set; } = line.Expected.Value.ToString("0.###", CultureInfo.InvariantCulture);
        public Quantity Difference => new(decimal.TryParse(Counted, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) ? value - Expected.Value : 0);
    }
}
