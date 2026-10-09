using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Prospecta.Application.Collection;

namespace Prospecta.Infrastructure.Collection;

/// <summary>Free public Overpass instances (no key). Tries each configured mirror; rate limits and quotas are enforced upstream by <see cref="ConnectorQuota"/>.</summary>
public sealed class OverpassClient(IHttpClientFactory factory, IOptions<CollectionOptions> options, ILogger<OverpassClient> log) : IOverpassClient
{
    public async Task<string> QueryAsync(string query, CancellationToken ct)
    {
        var o = options.Value.Osm;
        var errors = new List<string>();
        foreach (var url in o.OverpassUrls)
        {
            try
            {
                var http = factory.CreateClient("overpass");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 5, 120)));
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = query }) };
                req.Headers.UserAgent.ParseAdd(o.UserAgent.Length > 200 ? o.UserAgent[..200] : o.UserAgent);
                req.Headers.Accept.ParseAdd("application/json");
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if ((int)res.StatusCode is 429 or 502 or 503 or 504) { errors.Add($"{new Uri(url).Host}: surchargé ({(int)res.StatusCode})"); continue; }
                if (!res.IsSuccessStatusCode) throw new ConnectorException($"OpenStreetMap a refusé la requête (HTTP {(int)res.StatusCode}). Vérifiez la zone et l'activité.", false);
                var buffer = new byte[10 * 1024 * 1024 + 1];
                await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
                var total = 0; int n;
                while (total < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(total), cts.Token)) > 0) total += n;
                if (total > 10 * 1024 * 1024) throw new ConnectorException("Réponse trop volumineuse : réduisez la zone ou le nombre de résultats.", false);
                var body = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
                if (!body.TrimStart().StartsWith('{')) { errors.Add($"{new Uri(url).Host}: réponse inattendue"); continue; }
                if (body.Contains("\"remark\"") && body.Contains("runtime error") && body.Contains("\"elements\": [") is false)
                    throw new ConnectorException("Le serveur OpenStreetMap a interrompu la requête (trop longue). Réduisez la zone ou le nombre de résultats.");
                return body;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { errors.Add($"{new Uri(url).Host}: délai dépassé"); }
            catch (HttpRequestException ex) { log.LogWarning(ex, "Overpass {Url} unreachable", url); errors.Add($"{new Uri(url).Host}: injoignable"); }
        }

        throw new ConnectorException("Le service OpenStreetMap est indisponible pour le moment (" + string.Join("; ", errors) + "). Réessayez plus tard ou utilisez le modèle Excel.");
    }
}
