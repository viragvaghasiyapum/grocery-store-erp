using System.Text;

namespace GroceryErp.Localization;

public static class PseudoLocalizer
{
    public static string Transform(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder(value.Length * 2).Append("[!! ");
        bool placeholder = false;
        foreach (char character in value)
        {
            if (character == '{') placeholder = true;
            result.Append(placeholder ? character : Accentuate(character));
            if (character == '}') placeholder = false;
        }
        return result.Append('~', Math.Max(2, value.Length * 2 / 5)).Append(" !!]").ToString();
    }

    private static char Accentuate(char value) => value switch
    {
        'a' => 'à',
        'e' => 'é',
        'i' => 'î',
        'o' => 'ô',
        'u' => 'ü',
        'A' => 'Å',
        'E' => 'É',
        'I' => 'Î',
        'O' => 'Ô',
        'U' => 'Ü',
        'S' => 'Š',
        's' => 'š',
        'C' => 'Ç',
        'c' => 'ç',
        _ => value,
    };
}
