using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Prospecta.Domain.Common;

/// <summary>Pure normalization helpers used for matching, search and de-duplication.</summary>
public static partial class TextNormalizer
{
    private static readonly HashSet<string> LegalForms = new(StringComparer.Ordinal)
    {
        "sarl", "eurl", "spa", "snc", "scs", "sas", "ets", "etablissement", "etablissements", "ste", "societe", "sté",
    };

    /// <summary>Lower-case, accent-free, punctuation-free, single-spaced, without legal-form noise.</summary>
    public static string NormalizeName(string? value)
    {
        var folded = Fold(value);
        if (folded.Length == 0)
        {
            return string.Empty;
        }

        var tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !LegalForms.Contains(t))
            .ToArray();
        // If everything was noise (e.g. "SARL"), keep the folded text rather than an empty key.
        return tokens.Length == 0 ? folded : string.Join(' ', tokens);
    }

    /// <summary>Same folding as <see cref="NormalizeName"/> but keeps every token (addresses, free text).</summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>Accent-safe slug-like code (letters/digits only) for stable identifiers.</summary>
    public static string ToCode(string? value) => Fold(value).Replace(' ', '-');

    /// <summary>Host part of a URL without "www." (null when the value is not a usable http(s) URL).</summary>
    public static string? WebsiteHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var candidate = url.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.Host.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
