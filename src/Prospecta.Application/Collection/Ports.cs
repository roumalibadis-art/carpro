namespace Prospecta.Application.Collection;

public sealed class CollectionOptions
{
    public OsmOptions Osm { get; set; } = new();
    public WebsiteOptions Website { get; set; } = new();
}

public sealed class OsmOptions
{
    public bool Enabled { get; set; } = true;
    public string[] OverpassUrls { get; set; } = ["https://overpass-api.de/api/interpreter", "https://overpass.kumi.systems/api/interpreter"];
    public int DailyCalls { get; set; } = 40;
    public int MinSecondsBetweenCalls { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 40;
    public int MaxResults { get; set; } = 200;
    public string UserAgent { get; set; } = "Prospecta/1.0 (business census tool; contact: set Collection:Osm:UserAgent)";
}

public sealed class WebsiteOptions
{
    public bool Enabled { get; set; } = true;
    public int DailyCalls { get; set; } = 200;
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxBytes { get; set; } = 1_000_000;
    public string UserAgent { get; set; } = "ProspectaBot/1.0";
    public bool RespectRobots { get; set; } = true;
    /// <summary>TESTS ONLY. Disables the public-address guard; never enable in production.</summary>
    public bool AllowPrivateNetworks { get; set; }
}

/// <summary>Raised by connectors when a source cannot be used right now (network, quota, access restriction). The message is shown to the user.</summary>
public sealed class ConnectorException(string message, bool transient = true) : Exception(message)
{
    public bool Transient { get; } = transient;
}

public interface IOverpassClient
{
    /// <summary>Sends an Overpass QL query to the free public instances and returns the raw JSON.</summary>
    Task<string> QueryAsync(string query, CancellationToken ct);
}

public sealed record PageFetch(string Html, string FinalUrl);

public interface IPublicPageFetcher
{
    /// <summary>Fetches one public HTML page politely: robots.txt respected, no private addresses, size/time limits, no CAPTCHA or login bypass.</summary>
    Task<PageFetch> FetchAsync(string url, CancellationToken ct);
}
