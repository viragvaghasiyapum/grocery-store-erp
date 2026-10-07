using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using ClosedXML.Excel;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;
using GroceryErp.Localization;

namespace GroceryErp.Reports;

public sealed class ReportOutputService(ISettings settings, TranslationSource? translations = null)
{
    private const string NumberFormat = "#,##0.000";
    private const double PageWidth = 793.7;
    private const double PageHeight = 1122.5;
    private const double Margin = 45.35;
    private static readonly int[] FirstColumn = [0];

    public void ExportExcel(TabularReport report, string path)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var workbook = new XLWorkbook();
        workbook.Properties.Author = settings.Current.StoreName;
        IXLWorksheet sheet = workbook.Worksheets.Add(SafeSheetName(report.Title));
        sheet.Cell(1, 1).Value = report.Title;
        sheet.Range(1, 1, 1, Math.Max(1, report.Columns.Count)).Merge().Style.Font.SetBold().Font.SetFontSize(16);
        int headerRow = (report.Period is null ? 3 : 4) + (report.IsTruncated ? 1 : 0);
        if (report.Period is { } period)
            sheet.Cell(2, 1).Value = $"{period.From:dd-MM-yyyy} to {period.To:dd-MM-yyyy}";
        if (report.IsTruncated)
        {
            sheet.Cell(headerRow - 1, 1).Value = Text("Reports_PartialExport", "Partial report: totals include displayed rows only.");
            sheet.Cell(headerRow - 1, 1).Style.Font.SetBold();
        }
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
            sheet.Cell(totalsRow, 1).Value = Text("Reports_Column_Total", "Total");
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
        => ExportExcel(BuildPnlReport(statement), path);

    public TabularReport BuildPnlReport(PnlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var rows = new List<ReportRow>();
        AddSection(rows, "Reports_Section_Income", "Income", statement.Income);
        rows.Add(PnlRow(Text("Reports_Section_NetSales", "Net sales"), statement.NetSales));
        AddSection(rows, "Reports_Section_Cogs", "Cost of goods sold", statement.CostOfGoodsSold);
        rows.Add(PnlRow(Text("Reports_Section_GrossProfit", "Gross profit"), statement.GrossProfit, statement.GrossMargin));
        AddSection(rows, "Reports_Section_Expenses", "Operating expenses", statement.Expenses);
        rows.Add(PnlRow(Text("Reports_Section_TotalExpenses", "Total expenses"), statement.TotalExpenses));
        AddSection(rows, "Reports_Section_OtherIncome", "Other income", statement.OtherIncome);
        rows.Add(PnlRow(Text("Reports_Section_NetProfit", "Net profit"), statement.NetProfit, statement.NetMargin));
        return new TabularReport(Text("Reports_Kind_ProfitAndLoss", "Profit and loss"), statement.Period,
            [new("Label", Text("Reports_Particulars", "Particulars"), ReportValueKind.Text),
                new("Amount", Text("Reports_Amount", "Amount"), ReportValueKind.Money),
                new("Margin", Text("Reports_Column_MarginPercent", "Margin %"), ReportValueKind.Percent)],
            rows, new Dictionary<string, decimal> { ["Amount"] = statement.NetProfit.Value });
    }

    public FixedDocument CreateA4(TabularReport report, DateTime printedAt)
    {
        ArgumentNullException.ThrowIfNull(report);
        bool landscape = report.Columns.Count > 7;
        double pageWidth = landscape ? PageHeight : PageWidth;
        double pageHeight = landscape ? PageWidth : PageHeight;
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);
        int rowsPerPage = landscape ? (report.IsTruncated ? 21 : 22) : (report.IsTruncated ? 26 : 27);
        int renderedRows = report.Rows.Count + (report.Totals.Count > 0 ? 1 : 0);
        int rowPages = Math.Max(1, (renderedRows + rowsPerPage - 1) / rowsPerPage);
        int bands = report.Columns.Count <= 8 ? 1 : (report.Columns.Count - 2) / 7 + 1;
        int pages = rowPages * bands;
        for (int pageNumber = 0; pageNumber < pages; pageNumber++)
        {
            int rowPage = pageNumber / bands;
            int band = pageNumber % bands;
            List<int> columnIndexes = report.Columns.Count <= 8
                ? Enumerable.Range(0, report.Columns.Count).ToList()
                : FirstColumn.Concat(Enumerable.Range(1 + band * 7,
                    Math.Min(7, report.Columns.Count - 1 - band * 7))).ToList();
            var page = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
            NumberSubstitution.SetCultureSource(page, NumberCultureSource.Override);
            NumberSubstitution.SetCultureOverride(page, CultureInfo.GetCultureInfo("en-IN"));
            NumberSubstitution.SetSubstitution(page, NumberSubstitutionMethod.European);
            AddText(page, settings.Current.StoreName, Margin, 24, 18, FontWeights.Bold, pageWidth - 2 * Margin);
            AddText(page, report.Title, Margin, 52, 15, FontWeights.SemiBold, pageWidth - 2 * Margin);
            AddText(page, settings.Current.StoreAddress, Margin, 74, 9, FontWeights.Normal, pageWidth - 2 * Margin);
            string contact = string.Join(" · ", new[] { settings.Current.StorePhone, settings.Current.StoreGstin }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
            AddText(page, contact, Margin, 90, 9, FontWeights.Normal, pageWidth - 2 * Margin);
            if (report.Period is { } period)
                AddText(page, $"{period.From:dd-MM-yyyy} to {period.To:dd-MM-yyyy}", Margin, 106, 10, FontWeights.Normal, 300);
            if (bands > 1)
                AddText(page, Text("Reports_Print_Band", "Columns") + $" {band + 1}/{bands}",
                    pageWidth - Margin - 180, 106, 10, FontWeights.Normal, 180, TextAlignment.Right);
            if (report.IsTruncated)
                AddText(page, Text("Reports_PartialExport", "Partial report: totals include displayed rows only."),
                    Margin, 124, 9, FontWeights.SemiBold, pageWidth - 2 * Margin);
            double top = report.IsTruncated ? 150 : 130;
            double available = pageWidth - 2 * Margin;
            double columnWidth = available / Math.Max(1, columnIndexes.Count);
            for (int column = 0; column < columnIndexes.Count; column++)
                AddCell(page, report.Columns[columnIndexes[column]].Heading, Margin + column * columnWidth, top, columnWidth, true);
            int first = rowPage * rowsPerPage;
            int count = Math.Min(rowsPerPage, renderedRows - first);
            for (int row = 0; row < count; row++)
                for (int column = 0; column < columnIndexes.Count; column++)
                {
                    int sourceColumn = columnIndexes[column];
                    ReportColumn definition = report.Columns[sourceColumn];
                    int sourceIndex = first + row;
                    bool isTotal = sourceIndex == report.Rows.Count;
                    object? source = isTotal
                        ? sourceColumn == 0 ? Text("Reports_Column_Total", "Total") : report.Totals.GetValueOrDefault(definition.Key)
                        : report.Rows[sourceIndex].Values.GetValueOrDefault(definition.Key);
                    string value = isTotal && sourceColumn > 0 && !report.Totals.ContainsKey(definition.Key)
                        ? "" : Format(definition, source);
                    AddCell(page, value, Margin + column * columnWidth, top + 25 * (row + 1), columnWidth, isTotal,
                        definition.Kind is ReportValueKind.Money or ReportValueKind.Quantity or ReportValueKind.Percent or ReportValueKind.Count);
                }
            AddText(page, Text("Reports_Print_Page", "Page") + $" {pageNumber + 1} / {pages}", Margin, pageHeight - 34, 9, FontWeights.Normal, 200);
            AddText(page, Text("Reports_Print_Printed", "Printed") + $" {printedAt:dd-MM-yyyy HH:mm}", pageWidth - Margin - 220, pageHeight - 34, 9, FontWeights.Normal, 220, TextAlignment.Right);
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

    private void AddSection(List<ReportRow> rows, string key, string fallback, IEnumerable<PnlLine> lines)
    {
        rows.Add(new ReportRow(new Dictionary<string, object?> { ["Label"] = Text(key, fallback) }));
        rows.AddRange(lines.Select(line => PnlRow("  " + line.Label, line.Amount)));
    }
    private static ReportRow PnlRow(string label, Money amount, Percent? margin = null) =>
        new(new Dictionary<string, object?>
        {
            ["Label"] = label,
            ["Amount"] = amount.Scaled,
            ["Margin"] = margin?.Scaled
        });
    private static string SafeSheetName(string title)
    {
        string safe = string.Concat(title.Where(static character => character is not ':' and not '\\' and not '/' and not '?' and not '*' and not '[' and not ']'));
        return safe.Length == 0 ? "Report" : safe[..Math.Min(31, safe.Length)];
    }
    private static void AddCell(FixedPage page, string text, double left, double top, double width, bool header, bool numeric = false)
    {
        var border = new Border { Width = width, Height = 25, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0.5), Background = header ? Brushes.Gainsboro : Brushes.White };
        border.Child = new TextBlock { Text = text, Margin = new Thickness(3), FontSize = 9, FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left };
        FixedPage.SetLeft(border, left); FixedPage.SetTop(border, top); page.Children.Add(border);
    }
    private static void AddText(FixedPage page, string text, double left, double top, double size, FontWeight weight, double width, TextAlignment alignment = TextAlignment.Left)
    {
        var block = new TextBlock { Text = text, Width = width, FontSize = size, FontWeight = weight, TextAlignment = alignment, TextTrimming = TextTrimming.CharacterEllipsis };
        FixedPage.SetLeft(block, left); FixedPage.SetTop(block, top); page.Children.Add(block);
    }

    private string Text(string key, string fallback) => translations is null ? fallback : translations[key];
}
