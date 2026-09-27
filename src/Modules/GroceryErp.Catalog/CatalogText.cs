using System.Text;

namespace GroceryErp.Catalog;

internal static class CatalogText
{
    public static string Required(string? value, int maximum, string field)
    {
        string normalized = Optional(value, maximum) ?? string.Empty;
        if (normalized.Length == 0) throw new ArgumentException($"{field} is required.", field);
        return normalized;
    }

    public static string? Optional(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = string.Join(' ', value.Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > maximum) throw new ArgumentException($"Value cannot exceed {maximum} characters.");
        return normalized;
    }
}
