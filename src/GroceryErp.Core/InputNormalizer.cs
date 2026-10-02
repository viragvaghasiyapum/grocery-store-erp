using System.Text;

namespace GroceryErp.Core;

/// <summary>Normalizes Unicode text and Gujarati input digits without changing scanner separators.</summary>
public static class InputNormalizer
{
    /// <summary>Returns NFC text with Gujarati digits converted to ASCII digits.</summary>
    public static string Normalize(string? value) => string.Concat((value ?? string.Empty)
        .Normalize(NormalizationForm.FormC)
        .Select(static character => character is >= '\u0ae6' and <= '\u0aef'
            ? (char)('0' + character - '\u0ae6') : character));
}
