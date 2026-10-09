using System.Globalization;
using Prospecta.Domain.Businesses;
using Prospecta.Domain.Common;

namespace Prospecta.Application.Businesses;

/// <summary>Pure, unit-tested rules: completeness, confidence, geo-bounds.</summary>
public static class BusinessRules
{
    // Rough bounding box of Algeria — used only to reject obvious typos (swapped / mistyped coordinates).
    public const double MinLat = 18.0, MaxLat = 38.0, MinLon = -9.0, MaxLon = 12.5;

    public static bool CoordinatesPlausible(double lat, double lon) =>
        lat is >= MinLat and <= MaxLat && lon is >= MinLon and <= MaxLon;

    /// <summary>Share of the 7 key attributes that are known (category, commune, address, phone, website, coordinates, description).</summary>
    public static int Completeness(Business b)
    {
        var known = 0;
        if (b.CategoryId is not null) known++;
        if (b.CommuneId is not null) known++;
        if (!string.IsNullOrWhiteSpace(b.Address)) known++;
        if (!string.IsNullOrWhiteSpace(b.NormalizedPhone)) known++;
        if (!string.IsNullOrWhiteSpace(b.Website)) known++;
        if (b.Latitude is not null && b.Longitude is not null) known++;
        if (!string.IsNullOrWhiteSpace(b.Description)) known++;
        return (int)Math.Round(known * 100.0 / 7);
    }

    /// <summary>High: verified by a person and well filled. Medium: corroborated or well filled. Low: otherwise.</summary>
    public static ConfidenceLevel Confidence(Business b, int sourceCount, int confirmedFieldCount)
    {
        if (b.LastVerifiedAt is not null && b.CompletenessPercent >= 70)
        {
            return ConfidenceLevel.High;
        }

        if (sourceCount >= 2 || confirmedFieldCount > 0 || b.CompletenessPercent >= 70)
        {
            return ConfidenceLevel.Medium;
        }

        return ConfidenceLevel.Low;
    }

    /// <summary>Recomputes every derived column (search keys, completeness, confidence). Call before each save.</summary>
    public static void Finalize(Business b)
    {
        b.NormalizedName = TextNormalizer.NormalizeName(b.Name);
        b.NormalizedPhone = AlgerianPhone.Normalize(b.Phone);
        b.WebsiteHost = TextNormalizer.WebsiteHost(b.Website);
        b.CompletenessPercent = Completeness(b);
        b.Confidence = Confidence(b, b.Sources.Count, b.Provenances.Count(p => p.Origin == FieldOrigin.Confirmed));
    }

    public static string FormatDouble(double? v) => v?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;

    public static bool TryParseDouble(string? s, out double value) =>
        double.TryParse(s?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
