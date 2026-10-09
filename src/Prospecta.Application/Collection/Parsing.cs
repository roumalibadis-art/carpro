using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Prospecta.Application.Businesses;
using Prospecta.Domain.Common;

namespace Prospecta.Application.Collection;

/// <summary>A business discovered by a free connector, before any human review.</summary>
public sealed class Candidate
{
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? SourceUrl { get; set; }
    public string? Description { get; set; }
    public List<string> Notes { get; set; } = [];
}

/// <summary>Builds Overpass QL from validated parts only (no user text reaches the query unescaped).</summary>
public static partial class OverpassQuery
{
    [GeneratedRegex(@"^[a-z][a-z0-9_:]{0,40}=[A-Za-z0-9_:*\-]{1,60}$")]
    private static partial Regex TagFilter();

    public static bool IsValidFilter(string f) => TagFilter().IsMatch(f);

    public static IReadOnlyList<string> ParseFilters(string? osmFilter) =>
        (osmFilter ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsValidFilter).ToList();

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string KeywordRegex(IEnumerable<string> keywords) =>
        string.Join("|", keywords.Select(k => Regex.Escape(k.Trim()).Replace("\\ ", " ")).Where(k => k.Length > 0));

    /// <summary>Name spellings OSM may use for a place: as stored and without accents.</summary>
    public static string[] NameVariants(string name) => new[] { name.Trim(), TextNormalizer.Fold(name) is var f && f.Length > 0 ? string.Join(' ', f.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..])) : name.Trim() }.Distinct(StringComparer.Ordinal).ToArray();

    /// <param name="areas">(name, admin_level) pairs resolved from the administrative hierarchy; ignored when a bbox is given.</param>
    public static string Build(IReadOnlyList<(string Name, int Level)> areas, (double S, double W, double N, double E)? bbox, IReadOnlyList<string> filters, IReadOnlyList<string> keywords, int max, int timeoutSeconds = 25)
    {
        if (filters.Count == 0) throw new ArgumentException("Aucun filtre OpenStreetMap pour cette activité.");
        if (areas.Count == 0 && bbox is null) throw new ArgumentException("Zone manquante.");
        var kw = KeywordRegex(keywords.Take(5));
        var sb = new System.Text.StringBuilder();
        sb.Append($"[out:json][timeout:{Math.Clamp(timeoutSeconds, 5, 60)}];\n");
        string scope;
        if (bbox is { } b)
        {
            scope = string.Create(CultureInfo.InvariantCulture, $"({b.S:0.######},{b.W:0.######},{b.N:0.######},{b.E:0.######})");
        }
        else
        {
            for (var i = 0; i < Math.Min(areas.Count, 40); i++)
                sb.Append($"area[\"boundary\"=\"administrative\"][\"name\"~\"^({Esc(string.Join("|", NameVariants(areas[i].Name).Select(n => Regex.Escape(n).Replace("\\ ", " "))))})$\"][\"admin_level\"=\"{areas[i].Level}\"]->.a{i};\n");
            sb.Append('(').Append(string.Join("", Enumerable.Range(0, Math.Min(areas.Count, 40)).Select(i => $"area.a{i};"))).Append(")->.a;\n");
            scope = "(area.a)";
        }

        sb.Append("(\n");
        foreach (var f in filters.Take(10))
        {
            var (k, v) = (f[..f.IndexOf('=')], f[(f.IndexOf('=') + 1)..]);
            var cond = v == "*" ? $"[\"{k}\"]" : $"[\"{k}\"=\"{Esc(v)}\"]";
            sb.Append($"  nwr{cond}{(kw.Length > 0 ? $"[\"name\"~\"{Esc(kw)}\",i]" : "")}{scope};\n");
        }

        sb.Append($");\nout center tags {Math.Clamp(max, 1, 500)};");
        return sb.ToString();
    }
}

public static class OverpassParser
{
    public sealed record Parsed(IReadOnlyList<Candidate> Candidates, int SkippedWithoutName);

    public static Parsed Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<Candidate>(); var skipped = 0;
        if (!doc.RootElement.TryGetProperty("elements", out var els)) return new Parsed(list, 0);
        foreach (var e in els.EnumerateArray())
        {
            if (!e.TryGetProperty("tags", out var tags)) { skipped++; continue; }
            string? T(string k) => tags.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
            var name = T("name") ?? T("name:fr") ?? T("brand") ?? T("operator");
            if (name is null) { skipped++; continue; }
            var type = e.GetProperty("type").GetString()!; var id = e.GetProperty("id").GetInt64();
            double? lat = null, lon = null;
            if (e.TryGetProperty("lat", out var la) && e.TryGetProperty("lon", out var lo)) { lat = la.GetDouble(); lon = lo.GetDouble(); }
            else if (e.TryGetProperty("center", out var c)) { lat = c.GetProperty("lat").GetDouble(); lon = c.GetProperty("lon").GetDouble(); }
            var cand = new Candidate { ExternalId = $"osm:{type}/{id}", Name = name.Length > 200 ? name[..200] : name, SourceUrl = $"https://www.openstreetmap.org/{type}/{id}", City = T("addr:city") };
            if (lat is { } la2 && lon is { } lo2 && BusinessRules.CoordinatesPlausible(la2, lo2)) { cand.Latitude = la2; cand.Longitude = lo2; }
            var rawPhone = T("phone") ?? T("contact:phone") ?? T("contact:mobile") ?? T("mobile");
            if (rawPhone is not null)
            {
                var first = rawPhone.Split(new[] { ';', ',', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => AlgerianPhone.Normalize(p)).FirstOrDefault(p => p is not null);
                if (first is not null) cand.Phone = AlgerianPhone.Format(first); else cand.Notes.Add($"Téléphone « {rawPhone} » ignoré : format algérien non reconnu.");
            }

            var site = T("website") ?? T("contact:website") ?? T("url");
            if (site is not null) { if (TextNormalizer.WebsiteHost(site) is not null) cand.Website = site.Contains("://") ? site : "https://" + site; else cand.Notes.Add("Site web ignoré : URL invalide."); }
            var composed = string.Join(" ", new[] { T("addr:housenumber"), T("addr:street"), T("addr:postcode"), T("addr:city") }.Where(x => x is not null));
            cand.Address = T("addr:full") ?? (composed.Length > 0 ? composed : null);
            cand.Description = T("description");
            list.Add(cand);
        }

        return new Parsed(list, skipped);
    }
}

public static partial class MapsUrlParser
{
    public sealed record Result(double? Latitude, double? Longitude, string? Name, string? Error);

    [GeneratedRegex(@"@(-?\d{1,3}\.\d+),(-?\d{1,3}\.\d+)")] private static partial Regex At();
    [GeneratedRegex(@"!3d(-?\d{1,3}\.\d+)!4d(-?\d{1,3}\.\d+)")] private static partial Regex Data();
    [GeneratedRegex(@"[?&](?:q|ll|query|center)=(-?\d{1,3}\.\d+)(?:,|%2C)(-?\d{1,3}\.\d+)")] private static partial Regex Query();
    [GeneratedRegex(@"#map=\d+/(-?\d{1,3}\.\d+)/(-?\d{1,3}\.\d+)")] private static partial Regex OsmHash();
    [GeneratedRegex(@"[?&]mlat=(-?\d{1,3}\.\d+)&mlon=(-?\d{1,3}\.\d+)")] private static partial Regex OsmMark();
    [GeneratedRegex(@"/maps/place/([^/@?]+)")] private static partial Regex Place();

    /// <summary>Reads coordinates (and the place name) from a pasted Google Maps / OpenStreetMap link. Purely local: no request is made.</summary>
    public static Result Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return new(null, null, null, "Collez l'URL complète du lieu (https://…).");
        var host = u.Host.ToLowerInvariant();
        if (host is "goo.gl" or "maps.app.goo.gl" || host.EndsWith(".goo.gl")) return new(null, null, null, "Lien raccourci : ouvrez-le dans votre navigateur puis collez l'adresse complète affichée dans la barre d'adresse.");
        var ok = host.Contains("google.") || host.EndsWith("openstreetmap.org");
        if (!ok) return new(null, null, null, "Seuls les liens Google Maps et OpenStreetMap sont reconnus.");
        var s = u.ToString();
        foreach (var rx in new[] { Data(), At(), Query(), OsmMark(), OsmHash() })
        {
            var m = rx.Match(s);
            if (!m.Success) continue;
            var lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); var lon = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (!BusinessRules.CoordinatesPlausible(lat, lon)) return new(null, null, null, "Coordonnées hors d'Algérie.");
            var pm = Place().Match(s);
            return new(lat, lon, pm.Success ? WebUtility.UrlDecode(pm.Groups[1].Value.Replace('+', ' ')) : null, null);
        }

        return new(null, null, null, "Aucune coordonnée trouvée dans ce lien (ouvrez la fiche du lieu, puis copiez l'adresse complète).");
    }
}

public static class RobotsTxt
{
    /// <summary>Minimal robots.txt evaluation (longest matching rule wins; Allow beats Disallow on ties).</summary>
    public static bool IsAllowed(string? robots, string userAgentToken, string path)
    {
        if (string.IsNullOrWhiteSpace(robots)) return true;
        var groups = new List<(List<string> Agents, List<(bool Allow, string Pattern)> Rules)>();
        List<string>? agents = null; var lastWasAgent = false;
        foreach (var raw in robots.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            var key = line[..i].Trim().ToLowerInvariant(); var val = line[(i + 1)..].Trim();
            if (key == "user-agent")
            {
                if (!lastWasAgent || agents is null) { agents = []; groups.Add((agents, [])); }
                agents.Add(val.ToLowerInvariant()); lastWasAgent = true;
            }
            else if (key is "allow" or "disallow" && groups.Count > 0)
            {
                groups[^1].Rules.Add((key == "allow", val)); lastWasAgent = false;
            }
            else lastWasAgent = false;
        }

        var token = userAgentToken.ToLowerInvariant();
        var group = groups.FirstOrDefault(g => g.Agents.Any(a => a != "*" && token.Contains(a)));
        if (group.Agents is null) group = groups.FirstOrDefault(g => g.Agents.Contains("*"));
        if (group.Rules is null) return true;
        var best = (Len: -1, Allow: true);
        foreach (var (allow, pattern) in group.Rules)
        {
            if (pattern.Length == 0) continue; // empty Disallow = allow all
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\$", "$");
            if (!Regex.IsMatch(path, rx)) continue;
            if (pattern.Length > best.Len || (pattern.Length == best.Len && allow)) best = (pattern.Length, allow);
        }

        return best.Len < 0 || best.Allow;
    }
}

/// <summary>Extracts what a public page publishes about itself: schema.org JSON-LD first, then meta tags and tel: links.</summary>
public static partial class HtmlExtractor
{
    [GeneratedRegex(@"<script[^>]+type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex LdJson();
    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex Title();
    [GeneratedRegex(@"<meta[^>]+(?:property|name)\s*=\s*[""'](?<k>og:title|og:site_name|description|og:description)[""'][^>]*content\s*=\s*[""'](?<v>[^""']*)[""']", RegexOptions.IgnoreCase)] private static partial Regex Meta();
    [GeneratedRegex(@"href\s*=\s*[""']tel:([^""']+)[""']", RegexOptions.IgnoreCase)] private static partial Regex Tel();
    [GeneratedRegex(@"<address[^>]*>(.*?)</address>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex AddressTag();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();

    private static readonly string[] BusinessTypes = ["LocalBusiness", "Organization", "Store", "Restaurant", "AutoRental", "AutomotiveBusiness", "ProfessionalService", "Corporation", "Hotel", "MedicalBusiness", "FoodEstablishment", "HomeAndConstructionBusiness"];

    private static string Text(string html) => WebUtility.HtmlDecode(Tags().Replace(html, " ")).Replace(' ', ' ').Trim() is var t && t.Length > 0 ? Regex.Replace(t, @"\s+", " ") : string.Empty;

    public static Candidate Extract(string html, string pageUrl)
    {
        var c = new Candidate { SourceUrl = pageUrl, Website = pageUrl };
        foreach (Match m in LdJson().Matches(html).Take(10))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value.Trim());
                foreach (var node in Flatten(doc.RootElement))
                {
                    if (!IsBusiness(node)) continue;
                    c.Name = Str(node, "name") ?? c.Name;
                    c.Description ??= Str(node, "description");
                    if (Str(node, "telephone") is { } tel && AlgerianPhone.Normalize(tel.Split(',', ';', '/')[0]) is { } np) c.Phone ??= AlgerianPhone.Format(np);
                    if (node.TryGetProperty("address", out var a))
                    {
                        if (a.ValueKind == JsonValueKind.Object) c.Address ??= string.Join(", ", new[] { Str(a, "streetAddress"), Str(a, "postalCode"), Str(a, "addressLocality") }.Where(x => x is not null));
                        else if (a.ValueKind == JsonValueKind.String) c.Address ??= a.GetString();
                        if (a.ValueKind == JsonValueKind.Object) c.City ??= Str(a, "addressLocality");
                    }

                    if (node.TryGetProperty("geo", out var g) && g.ValueKind == JsonValueKind.Object && Num(g, "latitude") is { } la && Num(g, "longitude") is { } lo && BusinessRules.CoordinatesPlausible(la, lo)) { c.Latitude ??= la; c.Longitude ??= lo; }
                }
            }
            catch (JsonException) { c.Notes.Add("Un bloc JSON-LD illisible a été ignoré."); }
        }

        if (string.IsNullOrWhiteSpace(c.Name))
        {
            var metas = Meta().Matches(html).ToDictionary(x => x.Groups["k"].Value.ToLowerInvariant(), x => WebUtility.HtmlDecode(x.Groups["v"].Value), StringComparer.Ordinal);
            var raw = metas.GetValueOrDefault("og:site_name") ?? metas.GetValueOrDefault("og:title") ?? (Title().Match(html) is { Success: true } t ? Text(t.Groups[1].Value) : null);
            if (!string.IsNullOrWhiteSpace(raw)) { c.Name = raw!.Split(new[] { '|', '–', '—', '-' })[0].Trim(); c.Notes.Add("Nom déduit du titre de la page : à vérifier."); }
            c.Description ??= metas.GetValueOrDefault("description") ?? metas.GetValueOrDefault("og:description");
        }

        if (c.Phone is null)
        {
            var phones = Tel().Matches(html).Select(m => AlgerianPhone.Normalize(WebUtility.UrlDecode(m.Groups[1].Value))).Where(p => p is not null).Distinct().ToList();
            if (phones.Count > 0) { c.Phone = AlgerianPhone.Format(phones[0]!); if (phones.Count > 1) c.Notes.Add($"{phones.Count} numéros trouvés sur la page : le premier est proposé."); }
        }

        if (c.Address is null && AddressTag().Match(html) is { Success: true } am && Text(am.Groups[1].Value) is { Length: > 5 and < 300 } at) { c.Address = at; c.Notes.Add("Adresse lue dans la balise <address> : à vérifier."); }
        c.Name = c.Name?.Length > 200 ? c.Name[..200] : c.Name ?? string.Empty;
        c.Description = c.Description?.Length > 1000 ? c.Description[..1000] : c.Description;
        return c;
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) { foreach (var i in e.EnumerateArray()) foreach (var x in Flatten(i)) yield return x; }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            yield return e;
            if (e.TryGetProperty("@graph", out var g)) foreach (var x in Flatten(g)) yield return x;
        }
    }

    private static bool IsBusiness(JsonElement n)
    {
        if (!n.TryGetProperty("@type", out var t)) return false;
        var types = t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString()) : [t.GetString()];
        return types.Any(x => x is not null && BusinessTypes.Contains(x));
    }

    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? WebUtility.HtmlDecode(v.GetString()!.Trim()) : null;

    private static double? Num(JsonElement e, string k) => !e.TryGetProperty(k, out var v) ? null
        : v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
