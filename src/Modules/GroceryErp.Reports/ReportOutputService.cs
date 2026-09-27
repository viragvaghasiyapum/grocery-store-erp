using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using ClosedXML.Excel;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;

namespace GroceryErp.Reports;

public sealed class ReportOutputService(ISettings settings)
{
    private const string NumberFormat = "#,##0.000";
    private const double PageWidth = 793.7;
    private const double PageHeight = 1122.5;
    private const double Margin = 45.35;

    public void ExportExcel(TabularReport report, string path)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var workbook = new XLWorkbook();
        workbook.Properties.Author = settings.Current.StoreName;
        IXLWorksheet sheet = workbook.Worksheets.Add(SafeSheetName(report.Title));
        sheet.Cell(1, 1).Value = report.Title;
        sheet.Range(1, 1, 1, Math.Max(1, report.Columns.Count)).Merge().Style.Font.SetBold().Font.SetFontSize(16);
        int headerRow = report.Period is null ? 3 : 4;
        if (report.Period is { } period)
            sheet.Cell(2, 1).Value = $"{period.From:dd-MM-yyyy} to {period.To:dd-MM-yyyy}";
        for (int column = 0; column < report.Columns.Count; column++)
        {
            ReportColumn definition = report.Columns[column];
            IXLCell cell = sheet.Cell(headerRow, column + 1);
            cell.Value = definition.Heading;
            cell.Style.Font.SetBold();
            cell.Style.Fill.SetBackgroundColor(XLColor.LightGray);
        }
        for (int row = 0; row < report.Rows.Count; row++)
            for (int column = 0; column < report.Columns.Count; column++)
                WriteCell(sheet.Cell(headerRow + row + 1, column + 1), report.Columns[column],
                    report.Rows[row].Values.GetValueOrDefault(report.Columns[column].Key));
        int totalsRow = headerRow + report.Rows.Count + 1;
        if (report.Totals.Count > 0)
        {
            sheet.Cell(totalsRow, 1).Value = "Total";
            sheet.Cell(totalsRow, 1).Style.Font.SetBold();
            for (int column = 0; column < report.Columns.Count; column++)
                if (report.Totals.TryGetValue(report.Columns[column].Key, out decimal total))
                {
                    sheet.Cell(totalsRow, column + 1).Value = total;
                    sheet.Cell(totalsRow, column + 1).Style.NumberFormat.Format = NumberFormat;
                    sheet.Cell(totalsRow, column + 1).Style.Font.SetBold();
                }
        }
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns().AdjustToContents(1, Math.Min(headerRow + report.Rows.Count, 500));
        foreach (IXLColumn column in sheet.ColumnsUsed()) if (column.Width > 45) column.Width = 45;
        sheet.Range(headerRow, 1, headerRow + report.Rows.Count, report.Columns.Count).SetAutoFilter();
        workbook.SaveAs(path);
    }

    public void ExportPnlExcel(PnlStatement statement, string path)
    {
        var rows = new List<ReportRow>();
        AddSection(rows, "Income", statement.Income);
        rows.Add(PnlRow("Net sales", statement.NetSales));
        AddSection(rows, "Cost of goods sold", statement.CostOfGoodsSold);
        rows.Add(PnlRow("Gross profit", statement.GrossProfit));
        AddSection(rows, "Operating expenses", statement.Expenses);
        rows.Add(PnlRow("Total expenses", statement.TotalExpenses));
        AddSection(rows, "Other income", statement.OtherIncome);
        rows.Add(PnlRow("Net profit", statement.NetProfit));
        var report = new TabularReport("Profit and loss", statement.Period,
            [new("Label", "Particulars", ReportValueKind.Text), new("Amount", "Amount", ReportValueKind.Money)],
            rows, new Dictionary<string, decimal> { ["Amount"] = statement.NetProfit.Value });
        ExportExcel(report, path);
    }

    public FixedDocument CreateA4(TabularReport report, DateTime printedAt)
    {
        ArgumentNullException.ThrowIfNull(report);
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(PageWidth, PageHeight);
        int rowsPerPage = 27;
        int renderedRows = report.Rows.Count + (report.Totals.Count > 0 ? 1 : 0);
        int pages = Math.Max(1, (renderedRows + rowsPerPage - 1) / rowsPerPage);
        for (int pageNumber = 0; pageNumber < pages; pageNumber++)
        {
            var page = new FixedPage { Width = PageWidth, Height = PageHeight, Background = Brushes.White };
            NumberSubstitution.SetCultureSource(page, NumberCultureSource.Override);
            NumberSubstitution.SetCultureOverride(page, CultureInfo.GetCultureInfo("en-IN"));
            NumberSubstitution.SetSubstitution(page, NumberSubstitutionMethod.European);
            AddText(page, settings.Current.StoreName, Margin, 24, 18, FontWeights.Bold, PageWidth - 2 * Margin);
            AddText(page, report.Title, Margin, 52, 15, FontWeights.SemiBold, PageWidth - 2 * Margin);
            AddText(page, settings.Current.StoreAddress, Margin, 74, 9, FontWeights.Normal, PageWidth - 2 * Margin);
            string contact = string.Join(" · ", new[] { settings.Current.StorePhone, settings.Current.StoreGstin }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
            AddText(page, contact, Margin, 90, 9, FontWeights.Normal, PageWidth - 2 * Margin);
            if (report.Period is { } period)
                AddText(page, $"{period.From:dd-MM-yyyy} to {period.To:dd-MM-yyyy}", Margin, 106, 10, FontWeights.Normal, 300);
            double top = 130;
            double available = PageWidth - 2 * Margin;
            double columnWidth = available / Math.Max(1, report.Columns.Count);
            for (int column = 0; column < report.Columns.Count; column++)
                AddCell(page, report.Columns[column].Heading, Margin + column * columnWidth, top, columnWidth, true);
            int first = pageNumber * rowsPerPage;
            int count = Math.Min(rowsPerPage, renderedRows - first);
            for (int row = 0; row < count; row++)
                for (int column = 0; column < report.Columns.Count; column++)
                {
                    ReportColumn definition = report.Columns[column];
                    int sourceIndex = first + row;
                    bool isTotal = sourceIndex == report.Rows.Count;
                    object? source = isTotal
                        ? column == 0 ? "Total" : report.Totals.GetValueOrDefault(definition.Key)
                        : report.Rows[sourceIndex].Values.GetValueOrDefault(definition.Key);
                    string value = isTotal && column > 0 && !report.Totals.ContainsKey(definition.Key)
                        ? "" : Format(definition, source);
                    AddCell(page, value, Margin + column * columnWidth, top + 25 * (row + 1), columnWidth, isTotal);
                }
            AddText(page, $"Page {pageNumber + 1} of {pages}", Margin, PageHeight - 34, 9, FontWeights.Normal, 200);
            AddText(page, $"Printed {printedAt:dd-MM-yyyy HH:mm}", PageWidth - Margin - 220, PageHeight - 34, 9, FontWeights.Normal, 220, TextAlignment.Right);
            var content = new PageContent();
            ((IAddChild)content).AddChild(page);
            document.Pages.Add(content);
        }
        return document;
    }

    private static void WriteCell(IXLCell cell, ReportColumn definition, object? value)
    {
        if (value is null || value is DBNull) return;
        switch (definition.Kind)
        {
            case ReportValueKind.Money or ReportValueKind.Quantity or ReportValueKind.Percent:
                cell.Value = value switch { long x => Fx3.FromScaled(x), int x => Fx3.FromScaled(x), decimal x => x, _ => 0m };
                cell.Style.NumberFormat.Format = NumberFormat;
                break;
            case ReportValueKind.Count:
                cell.Value = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                break;
            case ReportValueKind.Date when DateOnly.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out DateOnly date):
                cell.Value = date.ToDateTime(TimeOnly.MinValue);
                cell.Style.NumberFormat.Format = "dd-mm-yyyy";
                break;
            default:
                cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                break;
        }
    }

    private static string Format(ReportColumn definition, object? value)
    {
        if (value is null || value is DBNull) return "";
        return definition.Kind switch
        {
            ReportValueKind.Money or ReportValueKind.Quantity or ReportValueKind.Percent =>
                (value switch { long x => Fx3.FromScaled(x), int x => Fx3.FromScaled(x), decimal x => x, _ => 0m }).ToString("N3", CultureInfo.GetCultureInfo("en-IN")),
            ReportValueKind.Date when DateOnly.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
    }

    private static void AddSection(List<ReportRow> rows, string section, IEnumerable<PnlLine> lines)
    {
        rows.Add(PnlRow(section, Money.Zero));
        rows.AddRange(lines.Select(line => PnlRow("  " + line.Label, line.Amount)));
    }
    private static ReportRow PnlRow(string label, Money amount) => new(new Dictionary<string, object?> { ["Label"] = label, ["Amount"] = amount.Scaled });
    private static string SafeSheetName(string title)
    {
        string safe = string.Concat(title.Where(static character => character is not ':' and not '\\' and not '/' and not '?' and not '*' and not '[' and not ']'));
        return safe.Length == 0 ? "Report" : safe[..Math.Min(31, safe.Length)];
    }
    private static void AddCell(FixedPage page, string text, double left, double top, double width, bool header)
    {
        var border = new Border { Width = width, Height = 25, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0.5), Background = header ? Brushes.Gainsboro : Brushes.White };
        border.Child = new TextBlock { Text = text, Margin = new Thickness(3), FontSize = 9, FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        FixedPage.SetLeft(border, left); FixedPage.SetTop(border, top); page.Children.Add(border);
    }
    private static void AddText(FixedPage page, string text, double left, double top, double size, FontWeight weight, double width, TextAlignment alignment = TextAlignment.Left)
    {
        var block = new TextBlock { Text = text, Width = width, FontSize = size, FontWeight = weight, TextAlignment = alignment, TextTrimming = TextTrimming.CharacterEllipsis };
        FixedPage.SetLeft(block, left); FixedPage.SetTop(block, top); page.Children.Add(block);
    }
}
