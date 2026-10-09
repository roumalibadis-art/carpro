namespace Prospecta.Domain.Common;

/// <summary>
/// Normalizes Algerian phone numbers to a canonical international form (digits only, "213…").
/// Mobiles: 05/06/07 + 8 digits. Landlines: 02x/03x/04x + 7 digits. Anything else is invalid, never guessed.
/// </summary>
public static class AlgerianPhone
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        string national;
        if (digits.StartsWith("00213", StringComparison.Ordinal))
        {
            national = digits[5..];
        }
        else if (digits.StartsWith("213", StringComparison.Ordinal) && (digits.Length == 11 || digits.Length == 12))
        {
            national = digits[3..];
        }
        else if (digits.StartsWith('0'))
        {
            national = digits[1..];
        }
        else if ((digits.Length == 9 && digits[0] is '5' or '6' or '7') || (digits.Length == 8 && digits[0] is '2' or '3' or '4'))
        {
            national = digits; // spreadsheets drop the leading zero of numeric cells
        }
        else
        {
            return null;
        }

        var valid = national.Length switch
        {
            9 => national[0] is '5' or '6' or '7',
            8 => national[0] is '2' or '3' or '4',
            _ => false,
        };
        return valid ? "213" + national : null;
    }

    public static bool IsValid(string? raw) => Normalize(raw) is not null;

    /// <summary>Display form: 0555 12 34 56 / 023 85 12 34.</summary>
    public static string Format(string normalized)
    {
        var n = normalized.StartsWith("213", StringComparison.Ordinal) ? normalized[3..] : normalized;
        return n.Length == 9
            ? $"0{n[..3]} {n.Substring(3, 2)} {n.Substring(5, 2)} {n.Substring(7, 2)}"
            : $"0{n[..2]} {n.Substring(2, 2)} {n.Substring(4, 2)} {n.Substring(6, 2)}";
    }
}
