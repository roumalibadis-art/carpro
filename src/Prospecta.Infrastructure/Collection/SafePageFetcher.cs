using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Prospecta.Application.Collection;

namespace Prospecta.Infrastructure.Collection;

/// <summary>
/// Polite fetch of one public page: only public addresses (checked at connection time, so DNS tricks cannot reach internal hosts),
/// robots.txt honoured, bounded size/time, a few redirects re-validated at every hop. Blocked or protected pages are reported, never bypassed.
/// </summary>
public sealed class SafePageFetcher(IOptions<CollectionOptions> options, IMemoryCache cache) : IPublicPageFetcher
{
    private static readonly string[] CaptchaMarkers = ["g-recaptcha", "h-captcha", "cf-challenge", "cf-chl", "captcha-delivery", "Attention Required! | Cloudflare"];

    private HttpClient? _client;

    private HttpClient Client => _client ??= new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var allowed = options.Value.Website.AllowPrivateNetworks ? addrs : addrs.Where(NetworkGuard.IsPublic).ToArray();
            if (allowed.Length == 0) throw new ConnectorException("Adresse non autorisée : seules les pages publiques d'internet sont lues.", false);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(allowed[0], ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<PageFetch> FetchAsync(string url, CancellationToken ct)
    {
        var o = options.Value.Website;
        if (!Uri.TryCreate(url.Contains("://") ? url : "https://" + url, UriKind.Absolute, out var uri) || !NetworkGuard.IsAllowedUrl(uri, o.AllowPrivateNetworks))
            throw new ConnectorException("URL non autorisée (http/https public uniquement, sans identifiants).", false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 3, 60)));
        try
        {
            for (var hop = 0; hop < 4; hop++)
            {
                if (!NetworkGuard.IsAllowedUrl(uri, o.AllowPrivateNetworks)) throw new ConnectorException("Redirection vers une adresse non autorisée.", false);
                if (!await RobotsAllowAsync(uri, o, cts.Token)) throw new ConnectorException("Le site interdit l'accès automatisé (robots.txt) : saisissez les informations manuellement.", false);
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.UserAgent.ParseAdd(o.UserAgent);
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
                req.Headers.AcceptLanguage.ParseAdd("fr,ar;q=0.7,en;q=0.5");
                using var res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var code = (int)res.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308 && res.Headers.Location is { } loc) { uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc); continue; }
                if (code is 401 or 403 or 429) throw new ConnectorException($"Le site restreint l'accès automatisé (HTTP {code}) : nous ne contournons ni connexion ni protection. Saisissez les informations manuellement.", false);
                if (code == 404 || code == 410) throw new ConnectorException("Page introuvable (HTTP " + code + ") : le site a peut-être changé ou fermé.", false);
                if (!res.IsSuccessStatusCode) throw new ConnectorException($"Le site a répondu HTTP {code}.");
                var type = res.Content.Headers.ContentType?.MediaType ?? "";
                if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) throw new ConnectorException("La page n'est pas du HTML.", false);
                var buf = new byte[o.MaxBytes + 1]; var total = 0; int n;
                await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
                while (total < buf.Length && (n = await stream.ReadAsync(buf.AsMemory(total), cts.Token)) > 0) total += n;
                var html = System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(total, o.MaxBytes));
                if (total < 20_000 && CaptchaMarkers.Any(m => html.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    throw new ConnectorException("Le site demande une vérification anti-robot : nous ne la contournons pas. Saisissez les informations manuellement.", false);
                return new PageFetch(html, uri.ToString());
            }

            throw new ConnectorException("Trop de redirections.", false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ConnectorException("Le site n'a pas répondu à temps."); }
        catch (HttpRequestException ex) { throw ex.GetBaseException() as ConnectorException ?? new ConnectorException("Site injoignable (" + ex.GetBaseException().Message + ")."); }
    }

    private async Task<bool> RobotsAllowAsync(Uri uri, WebsiteOptions o, CancellationToken ct)
    {
        if (!o.RespectRobots) return true;
        var key = "robots:" + uri.Scheme + "://" + uri.Authority;
        if (!cache.TryGetValue(key, out string? robots))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/robots.txt"));
                req.Headers.UserAgent.ParseAdd(o.UserAgent);
                using var res = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
                robots = res.IsSuccessStatusCode ? (await res.Content.ReadAsStringAsync(ct)) : null;
                if (robots is { Length: > 200_000 }) robots = robots[..200_000];
                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false; // restricted: treat as "do not crawl"
            }
            catch (HttpRequestException ex) when (ex.GetBaseException() is ConnectorException) { throw ex.GetBaseException(); }
            catch (HttpRequestException) { robots = null; }
            cache.Set(key, robots, TimeSpan.FromHours(1));
        }

        return RobotsTxt.IsAllowed(robots, o.UserAgent.ToLowerInvariant(), uri.PathAndQuery);
    }
}
