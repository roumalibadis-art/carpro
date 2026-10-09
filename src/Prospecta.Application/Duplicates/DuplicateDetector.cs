using Prospecta.Domain.Common;

namespace Prospecta.Application.Duplicates;

public sealed class DuplicateOptions
{
    /// <summary>Score from which a pair is proposed for human review. Merging is never automatic.</summary>
    public double SuspectThreshold { get; set; } = 0.55;
    public double StrongThreshold { get; set; } = 0.9;
    public double ProximityMeters { get; set; } = 75;
}

/// <summary>Comparable projection of a business or an import row.</summary>
public sealed record DupProfile(
    string NormalizedName,
    string? NormalizedPhone,
    string? WebsiteKey,
    string FoldedAddress,
    Guid? CommuneId,
    double? Latitude,
    double? Longitude,
    IReadOnlySet<string> ExternalIds);

public sealed record DupScore(double Score, IReadOnlyList<string> Reasons);

public static class DuplicateDetector
{
    private static readonly HashSet<string> AddressStopWords = new() { "rue", "de", "du", "la", "le", "des", "n", "no", "cite", "lot", "bt", "batiment", "algerie", "alger" };

    /// <summary>host + path of a website (so two Facebook pages on the same host are not "the same site").</summary>
    public static string? WebsiteKey(string? url)
    {
        var host = TextNormalizer.WebsiteHost(url);
        if (host is null)
        {
            return null;
        }

        var candidate = url!.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        var path = Uri.TryCreate(candidate, UriKind.Absolute, out var u) ? u.AbsolutePath.Trim('/').ToLowerInvariant() : string.Empty;
        return path.Length == 0 ? host : $"{host}/{path}";
    }

    /// <summary>Noisy-OR combination: independent weak signals add up, one certain signal gives 1.</summary>
    public static DupScore Score(DupProfile a, DupProfile b, DuplicateOptions options)
    {
        var reasons = new List<string>();
        var remaining = 1.0;

        void Add(double weight, string reason)
        {
            remaining *= 1 - weight;
            reasons.Add(reason);
        }

        if (a.ExternalIds.Overlaps(b.ExternalIds))
        {
            Add(1.0, "Même identifiant fournisseur");
        }

        if (a.NormalizedPhone is not null && a.NormalizedPhone == b.NormalizedPhone)
        {
            Add(0.65, "Même téléphone");
        }

        if (a.WebsiteKey is not null && a.WebsiteKey == b.WebsiteKey)
        {
            Add(0.6, "Même site web / page");
        }

        var nameSim = a.NormalizedName.Length == 0 || b.NormalizedName.Length == 0 ? 0 : NameSimilarity(a.NormalizedName, b.NormalizedName);
        if (nameSim >= 0.95)
        {
            Add(0.55, "Nom identique ou quasi identique");
        }
        else if (nameSim >= 0.88)
        {
            Add(0.35, "Nom très proche");
        }

        if (nameSim >= 0.88 && a.CommuneId is not null && a.CommuneId == b.CommuneId)
        {
            Add(0.1, "Même commune");
        }

        if (AddressSimilarity(a.FoldedAddress, b.FoldedAddress) >= 0.7)
        {
            Add(0.35, "Adresse similaire");
        }

        if (a.Latitude is not null && a.Longitude is not null && b.Latitude is not null && b.Longitude is not null &&
            DistanceMeters(a.Latitude.Value, a.Longitude.Value, b.Latitude.Value, b.Longitude.Value) <= options.ProximityMeters)
        {
            Add(0.35, "Proximité géographique");
        }

        return new DupScore(Math.Round(1 - remaining, 3), reasons);
    }

    public static double NameSimilarity(string a, string b)
    {
        if (a == b)
        {
            return 1;
        }

        var jw = JaroWinkler(a, b);
        // Same words in another order ("auto location rouiba" / "rouiba auto location").
        var ta = a.Split(' ').OrderBy(x => x, StringComparer.Ordinal);
        var tb = b.Split(' ').OrderBy(x => x, StringComparer.Ordinal);
        var sortedJw = JaroWinkler(string.Join(' ', ta), string.Join(' ', tb));
        return Math.Max(jw, sortedJw);
    }

    public static double AddressSimilarity(string a, string b)
    {
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !AddressStopWords.Contains(t)).ToHashSet();
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !AddressStopWords.Contains(t)).ToHashSet();
        if (ta.Count < 2 || tb.Count < 2)
        {
            return 0; // too little information to claim similarity
        }

        return ta.Intersect(tb).Count() / (double)ta.Union(tb).Count();
    }

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    public static double JaroWinkler(string s1, string s2)
    {
        if (s1 == s2) return 1;
        if (s1.Length == 0 || s2.Length == 0) return 0;
        var range = Math.Max(Math.Max(s1.Length, s2.Length) / 2 - 1, 0);
        var m1 = new bool[s1.Length];
        var m2 = new bool[s2.Length];
        var matches = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            var lo = Math.Max(0, i - range);
            var hi = Math.Min(s2.Length - 1, i + range);
            for (var j = lo; j <= hi; j++)
            {
                if (m2[j] || s1[i] != s2[j]) continue;
                m1[i] = m2[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0;
        var t = 0;
        var k = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            if (!m1[i]) continue;
            while (!m2[k]) k++;
            if (s1[i] != s2[k]) t++;
            k++;
        }

        var jaro = (matches / (double)s1.Length + matches / (double)s2.Length + (matches - t / 2.0) / matches) / 3;
        var prefix = 0;
        for (var i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)) && s1[i] == s2[i]; i++) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }
}
