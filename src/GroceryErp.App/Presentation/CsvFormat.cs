namespace GroceryErp.App.Presentation;

/// <summary>Writes user text into CSV as text: quoted, and never read as a spreadsheet formula.</summary>
internal static class CsvFormat
{
    public static string Text(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
