using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Prospecta.Application.Collection;
using Prospecta.Infrastructure.Collection;

namespace Prospecta.UnitTests;

public class OverpassTests
{
    [Fact]
    public void Query_is_built_from_validated_parts_only()
    {
        var q = OverpassQuery.Build([("Rouïba", 8)], null, ["amenity=car_rental", "shop=car_rental"], ["auto", "loc\"ation"], 50);
        q.Should().Contain("[out:json]").And.Contain("[\"admin_level\"=\"8\"]").And.Contain("nwr[\"amenity\"=\"car_rental\"]").And.Contain("nwr[\"shop\"=\"car_rental\"]").And.Contain("out center tags 50;");
        q.Should().Contain("Rouïba").And.Contain("Rouiba"); // accent variants
        q.Should().Contain("loc\\\"ation").And.NotContain("loc\"ation"); // quotes escaped: no query injection
        OverpassQuery.Build([], (36.7, 3.2, 36.8, 3.3), ["amenity=cafe"], [], 10).Should().Contain("(36.7,3.2,36.8,3.3)");
        OverpassQuery.Build([("Alger", 4)], null, ["shop=*"], [], 9999).Should().Contain("out center tags 500;").And.Contain("nwr[\"shop\"]");
    }

    [Theory]
    [InlineData("amenity=car_rental", true)]
    [InlineData("shop=*", true)]
    [InlineData("amenity=car_rental;out:json", false)]
    [InlineData("amenity=\"x\"", false)]
    [InlineData("=x", false)]
    [InlineData("a b=c", false)]
    public void Tag_filters_are_strictly_validated(string f, bool ok) => OverpassQuery.IsValidFilter(f).Should().Be(ok);

    [Fact]
    public void Queries_require_a_scope_and_a_filter()
    {
        FluentActions.Invoking(() => OverpassQuery.Build([("A", 8)], null, [], [], 10)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => OverpassQuery.Build([], null, ["a=b"], [], 10)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Response_is_normalised_without_inventing_data()
    {
        const string json = """
        {"elements":[
          {"type":"node","id":1,"lat":36.7391,"lon":3.2812,"tags":{"name":"Auto Soleil","phone":"+213 555 12 34 56; 023851234","website":"www.soleil.dz","addr:street":"Rue A","addr:city":"Rouïba","amenity":"car_rental"}},
          {"type":"way","id":2,"center":{"lat":36.74,"lon":3.29},"tags":{"name":"Garage B","phone":"123","website":"pas un site"}},
          {"type":"node","id":3,"lat":48.8,"lon":2.3,"tags":{"name":"Hors Algérie"}},
          {"type":"node","id":4,"lat":36.7,"lon":3.2,"tags":{"amenity":"car_rental"}}
        ]}
        """;
        var p = OverpassParser.Parse(json);
        p.Candidates.Should().HaveCount(3); p.SkippedWithoutName.Should().Be(1);
        var a = p.Candidates[0];
        a.ExternalId.Should().Be("osm:node/1"); a.Phone.Should().Be("0555 12 34 56"); a.Website.Should().Be("https://www.soleil.dz"); a.Address.Should().Be("Rue A Rouïba"); a.City.Should().Be("Rouïba");
        a.SourceUrl.Should().Be("https://www.openstreetmap.org/node/1");
        var b = p.Candidates[1];
        b.Latitude.Should().Be(36.74); b.Phone.Should().BeNull(); b.Website.Should().BeNull(); b.Notes.Should().HaveCount(2);
        p.Candidates[2].Latitude.Should().BeNull(); // implausible coordinates are dropped, the record is kept for review
        OverpassParser.Parse("""{"elements":[]}""").Candidates.Should().BeEmpty();
        OverpassParser.Parse("""{"version":0.6}""").Candidates.Should().BeEmpty();
    }
}

public class MapsUrlTests
{
    [Theory]
    [InlineData("https://www.google.com/maps/place/Agence+Soleil/@36.7391,3.2812,17z/data=!3m1", 36.7391, 3.2812, "Agence Soleil")]
    [InlineData("https://www.google.com/maps/place/X/data=!4m2!3d36.75!4d3.05", 36.75, 3.05, "X")]
    [InlineData("https://maps.google.com/?q=36.7,3.2", 36.7, 3.2, null)]
    [InlineData("https://www.openstreetmap.org/#map=17/36.7391/3.2812", 36.7391, 3.2812, null)]
    [InlineData("https://www.openstreetmap.org/?mlat=36.7&mlon=3.2", 36.7, 3.2, null)]
    public void Coordinates_are_read_locally(string url, double lat, double lon, string? name)
    {
        var r = MapsUrlParser.Parse(url);
        r.Error.Should().BeNull(); r.Latitude.Should().Be(lat); r.Longitude.Should().Be(lon); r.Name.Should().Be(name);
    }

    [Theory]
    [InlineData("https://maps.app.goo.gl/abc123")]
    [InlineData("https://www.google.com/maps/@48.85,2.35,12z")]
    [InlineData("https://example.com/@36.7,3.2")]
    [InlineData("pas une url")]
    [InlineData("")]
    [InlineData("https://www.google.com/maps")]
    public void Unusable_links_give_a_clear_error_instead_of_guessing(string url) => MapsUrlParser.Parse(url).Error.Should().NotBeNullOrEmpty();
}

public class RobotsTests
{
    private const string Robots = "User-agent: *\nDisallow: /private\nAllow: /private/open\nDisallow: /*.pdf$\n\nUser-agent: prospectabot\nDisallow: /nobots\n";

    [Theory]
    [InlineData("/", true)] [InlineData("/contact", true)] [InlineData("/private/x", true)] [InlineData("/nobots/a", false)]
    public void Specific_agent_group_wins(string path, bool ok) => RobotsTxt.IsAllowed(Robots, "prospectabot/1.0", path).Should().Be(ok);

    [Theory]
    [InlineData("/private", false)] [InlineData("/private/closed", false)] [InlineData("/private/open", true)] [InlineData("/doc.pdf", false)] [InlineData("/doc.pdf?x=1", true)] [InlineData("/ok", true)]
    public void Longest_rule_wins_for_other_agents(string path, bool ok) => RobotsTxt.IsAllowed(Robots, "otherbot", path).Should().Be(ok);

    [Fact]
    public void Missing_or_empty_robots_allows() { RobotsTxt.IsAllowed(null, "x", "/a").Should().BeTrue(); RobotsTxt.IsAllowed("User-agent: *\nDisallow:", "x", "/a").Should().BeTrue(); RobotsTxt.IsAllowed("User-agent: *\nDisallow: /", "x", "/a").Should().BeFalse(); }
}

public class HtmlExtractionTests
{
    [Fact]
    public void Json_ld_is_the_preferred_source()
    {
        const string html = """
        <html><head><title>Accueil | Soleil</title>
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"AutoRental","name":"Agence Soleil Location","telephone":"+213 555 12 34 56",
          "address":{"@type":"PostalAddress","streetAddress":"12 rue A","addressLocality":"Rouïba","postalCode":"16012"},"geo":{"@type":"GeoCoordinates","latitude":"36.7391","longitude":3.2812}}</script>
        </head><body><a href="tel:023851234">Appeler</a></body></html>
        """;
        var c = HtmlExtractor.Extract(html, "https://soleil.dz/");
        c.Name.Should().Be("Agence Soleil Location"); c.Phone.Should().Be("0555 12 34 56"); c.Address.Should().Be("12 rue A, 16012, Rouïba"); c.Latitude.Should().Be(36.7391); c.Longitude.Should().Be(3.2812); c.City.Should().Be("Rouïba");
    }

    [Fact]
    public void Fallbacks_are_flagged_for_review_and_bad_markup_is_survived()
    {
        var c = HtmlExtractor.Extract("""<html><head><title>Garage Bravo | Alger</title><meta name="description" content="Réparation"><script type="application/ld+json">{ not json</script></head><body><a href="tel:+213 770 11 22 33">x</a><a href="tel:12345">y</a><address>5 rue B, Alger</address></body></html>""", "https://g.dz");
        c.Name.Should().Be("Garage Bravo"); c.Phone.Should().Be("0770 11 22 33"); c.Address.Should().Be("5 rue B, Alger"); c.Description.Should().Be("Réparation");
        c.Notes.Should().Contain(n => n.Contains("à vérifier")).And.Contain(n => n.Contains("illisible"));
        var empty = HtmlExtractor.Extract("<html></html>", "https://x.dz");
        empty.Name.Should().BeEmpty(); empty.Phone.Should().BeNull();
    }
}

public class NetworkGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", false)] [InlineData("10.1.2.3", false)] [InlineData("172.16.0.1", false)] [InlineData("172.32.0.1", true)] [InlineData("192.168.1.1", false)] [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)] [InlineData("0.0.0.0", false)] [InlineData("::1", false)] [InlineData("fc00::1", false)] [InlineData("fe80::1", false)] [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)] [InlineData("93.184.216.34", true)] [InlineData("2606:4700:4700::1111", true)]
    public void Only_public_addresses_pass(string ip, bool ok) => NetworkGuard.IsPublic(IPAddress.Parse(ip)).Should().Be(ok);

    [Theory]
    [InlineData("https://example.dz/a", true)] [InlineData("ftp://example.dz", false)] [InlineData("http://user:pw@example.dz", false)] [InlineData("https://example.dz:22/", false)]
    public void Urls_are_restricted(string url, bool ok) => NetworkGuard.IsAllowedUrl(new Uri(url)).Should().Be(ok);
}

public class SafePageFetcherTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _base;

    public SafePageFetcherTests()
    {
        var port = new Random().Next(20000, 40000);
        _base = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(_base + "/"); _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); } catch { return; }
                var path = c.Request.Url!.AbsolutePath; string body = "<html><title>Boutique Zeta</title></html>"; var code = 200; var type = "text/html";
                if (path == "/robots.txt") body = "User-agent: *\nDisallow: /private";
                else if (path == "/private/page") body = "<html>secret</html>";
                else if (path == "/forbidden") (code, body) = (403, "no");
                else if (path == "/captcha") body = "<html><div class=\"g-recaptcha\"></div></html>";
                else if (path == "/json") (type, body) = ("application/json", "{}");
                else if (path == "/redirect") { c.Response.StatusCode = 302; c.Response.RedirectLocation = "http://169.254.169.254/latest"; c.Response.Close(); continue; }
                c.Response.StatusCode = code; c.Response.ContentType = type;
                var bytes = System.Text.Encoding.UTF8.GetBytes(body); await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
            }
        });
    }

    private static SafePageFetcher Fetcher(bool allowPrivate) => new(Options.Create(new CollectionOptions { Website = { AllowPrivateNetworks = allowPrivate, TimeoutSeconds = 5 } }), new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task Internal_addresses_are_refused_by_default()
    {
        var ex = await FluentActions.Awaiting(() => Fetcher(false).FetchAsync(_base + "/", CancellationToken.None)).Should().ThrowAsync<ConnectorException>();
        ex.Which.Message.Should().NotBeNullOrEmpty();
        await FluentActions.Awaiting(() => Fetcher(false).FetchAsync("http://169.254.169.254/latest/meta-data", CancellationToken.None)).Should().ThrowAsync<ConnectorException>();
        await FluentActions.Awaiting(() => Fetcher(false).FetchAsync("file:///etc/passwd", CancellationToken.None)).Should().ThrowAsync<ConnectorException>();
    }

    [Fact]
    public async Task Polite_fetch_honours_robots_and_never_bypasses_protections()
    {
        var f = Fetcher(true);
        (await f.FetchAsync(_base + "/", CancellationToken.None)).Html.Should().Contain("Boutique Zeta");
        (await FluentActions.Awaiting(() => f.FetchAsync(_base + "/private/page", CancellationToken.None)).Should().ThrowAsync<ConnectorException>()).Which.Message.Should().Contain("robots.txt");
        (await FluentActions.Awaiting(() => f.FetchAsync(_base + "/forbidden", CancellationToken.None)).Should().ThrowAsync<ConnectorException>()).Which.Message.Should().Contain("ne contournons");
        (await FluentActions.Awaiting(() => f.FetchAsync(_base + "/captcha", CancellationToken.None)).Should().ThrowAsync<ConnectorException>()).Which.Message.Should().Contain("anti-robot");
        (await FluentActions.Awaiting(() => f.FetchAsync(_base + "/json", CancellationToken.None)).Should().ThrowAsync<ConnectorException>()).Which.Message.Should().Contain("HTML");
        // a redirect to an internal address is re-validated at the next hop
        await FluentActions.Awaiting(() => Fetcher(false).FetchAsync(_base + "/redirect", CancellationToken.None)).Should().ThrowAsync<ConnectorException>();
    }

    public void Dispose() => _listener.Close();
}

public class TemplateTests
{
    [Fact]
    public void Every_template_header_is_recognised_by_the_import_wizard() => TemplateService.AllHeadersAutoMap().Should().BeTrue();
}
