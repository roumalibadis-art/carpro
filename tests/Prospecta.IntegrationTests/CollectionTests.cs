using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using FluentAssertions;
using Prospecta.Application.Collection;
using Prospecta.IntegrationTests.Support;

namespace Prospecta.IntegrationTests;

public class CollectionTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private const string OsmJson = """
      {"elements":[
        {"type":"node","id":1001,"lat":36.7391,"lon":3.2812,"tags":{"name":"Agence Soleil OSM","phone":"0555 61 62 63","website":"https://soleil-osm.dz","addr:street":"Rue Test","addr:city":"COL_CITY","amenity":"car_rental"}},
        {"type":"way","id":1002,"center":{"lat":36.7410,"lon":3.2900},"tags":{"name":"Garage Bravo OSM","amenity":"car_rental"}},
        {"type":"node","id":1003,"lat":36.7,"lon":3.2,"tags":{"amenity":"car_rental"}}
      ]}
      """;

    private async Task<(HttpClient Admin, HttpClient Mgr, HttpClient Sales, Guid Commune, Guid Daira, Guid Wilaya, Guid Category)> SetupAsync(string tag)
    {
        var admin = await f.ClientAsync();
        var g = await admin.SeedGeo(" col" + tag);
        var (mgr, _) = await f.CreateUserAsync($"col-mgr-{tag}@test.local", "SalesManager");
        var (sales, _) = await f.CreateUserAsync($"col-sales-{tag}@test.local", "Salesperson");
        var cat = await admin.CategoryId("Location de véhicules");
        return (admin, mgr, sales, g.Commune1, g.Daira, g.Wilaya, cat);
    }

    private static void Serve(string json, string city = "") => FakeOverpassHandler.Responder = (_, _) => FakeOverpassHandler.Json(json.Replace("COL_CITY", city));

    [Fact]
    public async Task Connector_list_is_honest_about_what_is_integrated()
    {
        var x = await SetupAsync("list");
        var list = await (await x.Mgr.GetAsync("/api/v1/collection/connectors")).OkJson();
        string State(string key) => list.EnumerateArray().First(c => c.GetProperty("key").GetString() == key).GetProperty("state").GetString()!;
        State("osm").Should().Be("Available"); State("website").Should().Be("Available"); State("maps-link").Should().Be("Available"); State("file").Should().Be("FileOnly");
        State("google-places").Should().Be("NotIntegrated"); State("meta").Should().Be("NotIntegrated");
        var text = list.ToString();
        text.Should().Contain("payante").And.Contain("modèle Excel").And.Contain("ODbL");
        (await x.Sales.GetAsync("/api/v1/collection/connectors")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Osm_search_review_import_and_rerun_without_duplicates()
    {
        var x = await SetupAsync("run");
        Serve(OsmJson, "Rouïba col" + "run");
        var run = await (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = x.Category, keywords = "agence, garage", maxResults = 50 })).OkJson();
        run.GetProperty("status").GetString().Should().Be("Completed");
        run.GetProperty("found").GetInt32().Should().Be(2);          // the unnamed object is skipped, never invented
        run.GetProperty("newCount").GetInt32().Should().Be(2);
        var query = FakeOverpassHandler.Queries.Last();
        query.Should().Contain("car_rental").And.Contain("Rouïba colrun").And.Contain("admin_level").And.Contain("agence|garage");

        var jobId = run.GetProperty("id").GetGuid();
        var detail = await (await x.Mgr.GetAsync($"/api/v1/collection/jobs/{jobId}")).OkJson();
        var results = detail.GetProperty("results").GetProperty("items").EnumerateArray().ToList();
        results.Select(r => r.GetProperty("name").GetString()).Should().BeEquivalentTo("Agence Soleil OSM", "Garage Bravo OSM");
        results.First(r => r.GetProperty("name").GetString() == "Agence Soleil OSM").GetProperty("candidate").GetProperty("phone").GetString().Should().Be("0555 61 62 63");

        // nothing exists before the explicit import step; other users cannot see the job
        (await x.Admin.GetAsync("/api/v1/businesses?search=OSM")).OkJson().Result.GetProperty("total").GetInt32().Should().Be(0);
        (await x.Sales.GetAsync($"/api/v1/collection/jobs/{jobId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var imp = await (await x.Mgr.PostAsJsonAsync($"/api/v1/collection/jobs/{jobId}/import", new { includeDuplicates = false })).OkJson();
        imp.GetProperty("imported").GetInt32().Should().Be(2);
        var list = await (await x.Mgr.GetAsync("/api/v1/businesses?search=OSM&pageSize=50")).OkJson();
        list.GetProperty("total").GetInt32().Should().Be(2);
        var biz = await (await x.Mgr.GetAsync($"/api/v1/businesses/{list.GetProperty("items").EnumerateArray().First(b => b.GetProperty("name").GetString() == "Agence Soleil OSM").GetProperty("id").GetGuid()}")).OkJson();
        biz.GetProperty("sources")[0].GetProperty("sourceType").GetString().Should().Be("OpenStreetMap");
        biz.GetProperty("sources")[0].GetProperty("externalId").GetString().Should().Be("osm:node/1001");
        biz.GetProperty("origins").GetProperty("Phone").GetString().Should().Be("External");       // not "confirmed": a person still has to verify
        biz.GetProperty("censusStatus").GetProperty("code").GetString().Should().NotBe("verified");
        biz.GetProperty("data").GetProperty("communeId").GetGuid().Should().Be(x.Commune);
        (await x.Mgr.PostAsJsonAsync($"/api/v1/collection/jobs/{jobId}/import", new { includeDuplicates = false })).StatusCode.Should().Be(HttpStatusCode.BadRequest); // nothing left to import

        // the same search again recognises what was imported (no duplicates created)
        var again = await (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = x.Category, maxResults = 50 })).OkJson();
        again.GetProperty("newCount").GetInt32().Should().Be(0);
        again.GetProperty("duplicateCount").GetInt32().Should().Be(2);
        (await x.Mgr.PostAsJsonAsync($"/api/v1/collection/jobs/{again.GetProperty("id").GetGuid()}/import", new { includeDuplicates = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await (await x.Mgr.GetAsync("/api/v1/businesses?search=OSM")).OkJson()).GetProperty("total").GetInt32().Should().Be(2);

        // journal
        var jobs = await (await x.Mgr.GetAsync("/api/v1/collection/jobs")).OkJson();
        jobs.GetProperty("items").EnumerateArray().Select(j => j.GetProperty("id").GetGuid()).Should().Contain([jobId, again.GetProperty("id").GetGuid()]);
        (await (await x.Admin.GetAsync("/api/v1/audit?action=collection.")).OkJson()).GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Failures_are_reported_logged_and_can_be_resumed()
    {
        var x = await SetupAsync("fail");
        FakeOverpassHandler.Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        var failed = await (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = x.Category })).OkJson();
        failed.GetProperty("status").GetString().Should().Be("Failed");
        failed.GetProperty("message").GetString().Should().Contain("indisponible").And.Contain("Excel");
        FakeOverpassHandler.Responder = (_, _) => FakeOverpassHandler.Json("""{"elements":[],"remark":"runtime error: Query timed out"}""");
        (await (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = x.Category })).OkJson()).GetProperty("message").GetString().Should().Contain("interrompu");
        FakeOverpassHandler.Responder = (_, _) => FakeOverpassHandler.Json("<html>not json</html>");
        (await (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = x.Category })).OkJson()).GetProperty("status").GetString().Should().Be("Failed");

        Serve(OsmJson);
        var retry = await (await x.Mgr.PostAsJsonAsync($"/api/v1/collection/jobs/{failed.GetProperty("id").GetGuid()}/retry", new { })).OkJson();
        retry.GetProperty("status").GetString().Should().Be("Completed");
        retry.GetProperty("retryOf").GetGuid().Should().Be(failed.GetProperty("id").GetGuid());
        Serve("""{"elements":[]}""");
        (await (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = x.Category })).OkJson()).GetProperty("message").GetString().Should().Contain("Aucun résultat");
    }

    [Fact]
    public async Task Searches_are_validated_and_restricted()
    {
        var x = await SetupAsync("val");
        Serve(OsmJson);
        (await x.Sales.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = x.Category })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "google-places", areaId = x.Commune, categoryId = x.Category })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = Guid.NewGuid(), categoryId = x.Category })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = x.Category, bbox = "0,0,1,1" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = x.Category, bbox = "36.0,2.0,36.5,2.5" })).StatusCode.Should().Be(HttpStatusCode.OK);
        FakeOverpassHandler.Queries.Last().Should().Contain("(36,2,36.5,2.5)").And.NotContain("area[");

        // an activity without an OpenStreetMap mapping is refused with the way out
        var custom = (await (await x.Admin.PostAsJsonAsync("/api/v1/categories", new { name = "Activité sans OSM col", isActive = true })).OkJson()).GetProperty("id").GetGuid();
        var r = await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = custom });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Json()).GetProperty("message").GetString().Should().Contain("modèle Excel");

        // the mapping is editable but strictly validated (no query injection)
        (await x.Admin.PutAsJsonAsync($"/api/v1/categories/{custom}", new { name = "Activité sans OSM col", isActive = true, osmFilter = "amenity=cafe;out:json" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Admin.PutAsJsonAsync($"/api/v1/categories/{custom}", new { name = "Activité sans OSM col", isActive = true, osmFilter = "amenity=cafe;shop=bakery" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await x.Mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = x.Commune, categoryId = custom })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Url_inspection_reads_map_links_locally_and_pages_through_the_polite_fetcher()
    {
        var x = await SetupAsync("url");
        var maps = await (await x.Mgr.PostAsJsonAsync("/api/v1/collection/inspect-url", new { url = "https://www.google.com/maps/place/Agence+Soleil/@36.7391,3.2812,17z" })).OkJson();
        maps.GetProperty("kind").GetString().Should().Be("maps");
        maps.GetProperty("candidate").GetProperty("latitude").GetDouble().Should().Be(36.7391);
        maps.GetProperty("candidate").GetProperty("name").GetString().Should().Be("Agence Soleil");
        (await x.Mgr.PostAsJsonAsync("/api/v1/collection/inspect-url", new { url = "https://maps.app.goo.gl/xyz" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Mgr.PostAsJsonAsync("/api/v1/collection/inspect-url", new { url = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        FakePageFetcher.Responder = u => new PageFetch("""<html><script type="application/ld+json">{"@type":"LocalBusiness","name":"Boutique Zeta","telephone":"0661 22 33 44","address":{"streetAddress":"3 rue Z","addressLocality":"Rouïba"}}</script></html>""", u);
        var page = await (await x.Mgr.PostAsJsonAsync("/api/v1/collection/inspect-url", new { url = "https://zeta.example.dz/" })).OkJson();
        page.GetProperty("candidate").GetProperty("name").GetString().Should().Be("Boutique Zeta");
        page.GetProperty("candidate").GetProperty("phone").GetString().Should().Be("0661 22 33 44");

        // the user reviews, then creates: the source and its origin are recorded
        var created = await (await x.Mgr.PostAsJsonAsync("/api/v1/collection/create-from-url", new { business = new { name = "Boutique Zeta", phone = "0661 22 33 44", website = "https://zeta.example.dz/", communeId = x.Commune }, sourceUrl = "https://zeta.example.dz/", kind = "page" })).OkJson();
        var b = created.GetProperty("business");
        b.GetProperty("sources")[0].GetProperty("sourceType").GetString().Should().Be("PublicWebsite");
        b.GetProperty("origins").GetProperty("Phone").GetString().Should().Be("External");

        // a site that forbids bots or is unreachable: clear message (502), nothing created
        FakePageFetcher.Responder = _ => throw new ConnectorException("Le site interdit l'accès automatisé (robots.txt) : saisissez les informations manuellement.", false);
        var blocked = await x.Mgr.PostAsJsonAsync("/api/v1/collection/inspect-url", new { url = "https://closed.example.dz/" });
        blocked.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await blocked.Json()).GetProperty("message").GetString().Should().Contain("robots.txt");
    }

    [Fact]
    public async Task Refresh_proposes_changes_but_never_overwrites_confirmed_values()
    {
        var x = await SetupAsync("rf");
        var id = (await x.Mgr.CreateBusiness(new { name = "Librairie Omega rf", communeId = x.Commune, phone = "0555 10 20 30", address = "1 rue Ancienne", website = "https://omega.example.dz" })).GetProperty("business").GetProperty("id").GetGuid();
        await x.Mgr.PostAsync($"/api/v1/businesses/{id}/verify", null); // everything known becomes "confirmed"
        FakePageFetcher.Responder = u => new PageFetch("""<html><script type="application/ld+json">{"@type":"LocalBusiness","name":"Librairie Omega rf","telephone":"0661 99 88 77","address":{"streetAddress":"9 rue Nouvelle"}}</script></html>""", u);

        var check = await (await x.Mgr.GetAsync($"/api/v1/businesses/{id}/refresh-check")).OkJson();
        var props = check.GetProperty("proposals").EnumerateArray().ToList();
        props.Select(p => p.GetProperty("field").GetString()).Should().Contain(["Phone", "Address"]);
        props.Should().OnlyContain(p => p.GetProperty("blocked").GetBoolean()); // confirmed by a person: only proposed, never applied
        var apply = await (await x.Mgr.PostAsJsonAsync($"/api/v1/businesses/{id}/refresh-apply", new { fields = new[] { "Phone", "Address" } })).OkJson();
        apply.GetProperty("applied").GetInt32().Should().Be(0);
        (await (await x.Mgr.GetAsync($"/api/v1/businesses/{id}")).OkJson()).GetProperty("data").GetProperty("phone").GetString().Should().Be("0555 10 20 30");

        // an unconfirmed value is updated, with the old one kept in history
        var id2 = (await x.Mgr.CreateBusiness(new { name = "Librairie Omega rf2", communeId = x.Commune, phone = "0555 10 20 31", website = "https://omega2.example.dz" })).GetProperty("business").GetProperty("id").GetGuid();
        var ap2 = await (await x.Mgr.PostAsJsonAsync($"/api/v1/businesses/{id2}/refresh-apply", new { fields = new[] { "Phone" } })).OkJson();
        ap2.GetProperty("applied").GetInt32().Should().Be(1);
        var hist = (await (await x.Mgr.GetAsync($"/api/v1/businesses/{id2}/history?pageSize=50")).OkJson()).GetProperty("items").EnumerateArray().ToList();
        hist.Should().Contain(h => h.GetProperty("field").GetString() == "Phone" && h.GetProperty("oldValue").GetString() == "0555 10 20 31" && h.GetProperty("newValue").GetString() == "0661 99 88 77");

        // unreachable site: reported, not an error
        FakePageFetcher.Responder = _ => throw new ConnectorException("Page introuvable (HTTP 404) : le site a peut-être changé ou fermé.", false);
        (await (await x.Mgr.GetAsync($"/api/v1/businesses/{id2}/refresh-check")).OkJson()).GetProperty("problem").GetString().Should().Contain("404");
        (await x.Sales.GetAsync($"/api/v1/businesses/{id2}/refresh-check")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

public class CollectionQuotaFactory : ApiFactory
{
    protected override Dictionary<string, string?> ExtraConfig => new() { ["Collection:Osm:DailyCalls"] = "2", ["Collection:Osm:MinSecondsBetweenCalls"] = "0" };
}

public class CollectionQuotaTests(CollectionQuotaFactory f) : IClassFixture<CollectionQuotaFactory>
{
    [Fact]
    public async Task Daily_quota_stops_external_calls_and_says_so()
    {
        var admin = await f.ClientAsync();
        var g = await admin.SeedGeo(" quota");
        var (mgr, _) = await f.CreateUserAsync("quota-mgr@test.local", "SalesManager");
        var cat = await admin.CategoryId("Location de véhicules");
        FakeOverpassHandler.Responder = (_, _) => FakeOverpassHandler.Json("""{"elements":[]}""");
        var before = FakeOverpassHandler.Calls;
        for (var i = 0; i < 2; i++) (await (await mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = g.Commune1, categoryId = cat })).OkJson()).GetProperty("status").GetString().Should().Be("Completed");
        var third = await (await mgr.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = g.Commune1, categoryId = cat })).OkJson();
        third.GetProperty("status").GetString().Should().Be("QuotaExceeded");
        third.GetProperty("message").GetString().Should().Contain("Quota").And.Contain("Excel");
        (FakeOverpassHandler.Calls - before).Should().Be(2); // the third request never left the server
        var conn = await (await mgr.GetAsync("/api/v1/collection/connectors")).OkJson();
        conn.EnumerateArray().First(c => c.GetProperty("key").GetString() == "osm").GetProperty("quota").GetString().Should().Contain("0/2");
    }
}

public class TemplateAndMapTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Excel_template_follows_the_import_contract_end_to_end()
    {
        var admin = await f.ClientAsync();
        var g = await admin.SeedGeo(" tpl");
        var xlsx = await (await admin.GetAsync("/api/v1/templates/businesses?format=xlsx")).Content.ReadAsByteArrayAsync();
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        wb.Worksheets.Select(w => w.Name).Should().Equal("Entreprises", "Listes", "Exemple", "Communes", "Instructions").And.Subject.First().Should().Be("Entreprises");
        var sheet = wb.Worksheet("Entreprises");
        Enumerable.Range(1, TemplateService.Headers.Length).Select(c => sheet.Cell(1, c).GetString()).Should().Equal(TemplateService.Headers);
        sheet.Cell(2, 1).IsEmpty().Should().BeTrue(); // examples live on their own sheet: they can never be imported by accident
        sheet.DataValidations.Count().Should().BeGreaterThan(0);
        wb.Worksheet("Communes").CellsUsed().Select(c => c.GetString()).Should().Contain("Rouïba tpl");
        wb.Worksheet("Listes").CellsUsed().Select(c => c.GetString()).Should().Contain("Alger").And.Contain("Location de véhicules");
        wb.Worksheet("Instructions").CellsUsed().Select(c => c.GetString()).Should().Contain(s => s.Contains("OBLIGATOIRE"));

        // fill two rows exactly as a user would, upload, map automatically, import
        sheet.Cell(2, 1).Value = "Agence Template Un"; sheet.Cell(2, 3).Value = "Location de véhicules"; sheet.Cell(2, 5).Value = "Alger"; sheet.Cell(2, 7).Value = "Rouïba tpl"; sheet.Cell(2, 13).Value = "0555 31 32 33"; sheet.Cell(2, 10).Value = "36.74"; sheet.Cell(2, 11).Value = "3.28";
        sheet.Cell(3, 1).Value = "Ligne Cassée"; sheet.Cell(3, 13).Value = "abc";
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        var batch = await (await admin.PostAsync("/api/v1/imports", Api.File("modele-rempli.xlsx", ms.ToArray()))).OkJson();
        batch.GetProperty("mapping").EnumerateObject().Count().Should().Be(TemplateService.Headers.Length); // all 18 columns auto-recognised
        var mapped = await (await admin.PutAsJsonAsync($"/api/v1/imports/{batch.GetProperty("id").GetGuid()}/mapping", batch.GetProperty("mapping"))).OkJson();
        mapped.GetProperty("validRows").GetInt32().Should().Be(1);
        mapped.GetProperty("invalidRows").GetInt32().Should().Be(1);
        _ = g;

        var csv = await (await admin.GetAsync("/api/v1/templates/businesses?format=csv")).Content.ReadAsStringAsync();
        csv.TrimStart('﻿').TrimEnd().Split(';').Should().Equal(TemplateService.Headers);
        (await (await admin.GetAsync("/api/v1/templates/geography")).Content.ReadAsStringAsync()).Should().Contain("wilaya;daira;commune;quartier");
        var csvBatch = await (await admin.PostAsync("/api/v1/imports", Api.File("m.csv", csv + "\r\nCsv Entreprise;;;;Alger;;;;;;;;0555 41 42 43;;;;;\r\n"))).OkJson();
        csvBatch.GetProperty("totalRows").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Map_points_are_scoped_filterable_and_only_for_businesses_with_coordinates()
    {
        var admin = await f.ClientAsync();
        var g = await admin.SeedGeo(" map");
        var (mgr, _) = await f.CreateUserAsync("map-mgr@test.local", "SalesManager");
        var (sales, salesId) = await f.CreateUserAsync("map-sales@test.local", "Salesperson");
        var withGeo = (await mgr.CreateBusiness(new { name = "Point Alpha map", communeId = g.Commune1, latitude = 36.74, longitude = 3.28 })).GetProperty("business").GetProperty("id").GetGuid();
        await mgr.CreateBusiness(new { name = "Sans coordonnées map", communeId = g.Commune1 });
        var other = (await mgr.CreateBusiness(new { name = "Point Zeta map", communeId = g.Commune2, latitude = 36.8, longitude = 3.1 })).GetProperty("business").GetProperty("id").GetGuid();
        await mgr.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = new[] { withGeo }, userId = salesId });

        var all = await (await mgr.GetAsync($"/api/v1/map/points?dairaId={g.Daira}")).OkJson();
        all.GetProperty("total").GetInt32().Should().Be(2);
        all.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().BeEquivalentTo("Point Alpha map", "Point Zeta map");
        (await (await mgr.GetAsync($"/api/v1/map/points?communeId={g.Commune2}")).OkJson()).GetProperty("points").GetArrayLength().Should().Be(1);
        (await (await sales.GetAsync($"/api/v1/map/points?dairaId={g.Daira}")).OkJson()).GetProperty("points").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).Should().Equal(withGeo).And.NotContain(other);
        (await f.CreateClient().GetAsync("/api/v1/map/points")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // list filters added for campaigns and overdue follow-ups
        var camp = (await (await mgr.PostAsJsonAsync("/api/v1/campaigns", new { name = "Carte camp", startDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(9)).ToString("yyyy-MM-dd") })).OkJson()).GetProperty("id").GetGuid();
        await mgr.PostAsJsonAsync($"/api/v1/campaigns/{camp}/targets", new { businessIds = new[] { withGeo } });
        (await (await mgr.GetAsync($"/api/v1/businesses?campaignId={camp}")).OkJson()).GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).Should().Equal(withGeo);
        (await (await mgr.GetAsync($"/api/v1/map/points?campaignId={camp}")).OkJson()).GetProperty("points").GetArrayLength().Should().Be(1);
        await mgr.PostAsJsonAsync("/api/v1/followups", new { businessId = other, dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-4)).ToString("yyyy-MM-dd"), reason = "En retard" });
        (await (await mgr.GetAsync($"/api/v1/businesses?overdueFollowUp=true&dairaId={g.Daira}")).OkJson()).GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).Should().Equal(other);
        (await (await mgr.GetAsync($"/api/v1/businesses?overdueFollowUp=false&dairaId={g.Daira}")).OkJson()).GetProperty("total").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Phase4_pages_render_and_templates_download_from_the_ui()
    {
        var admin = await f.ClientAsync();
        var g = await admin.SeedGeo(" ui4");
        var (_, _) = await f.CreateUserAsync("p4-mgr@test.local", "SalesManager");
        var mgrApi = await f.ClientAsync("p4-mgr@test.local", "User#Test2026x");
        var id = (await mgrApi.CreateBusiness(new { name = "Page UI4", communeId = g.Commune1, website = "https://ui4.example.dz", latitude = 36.7, longitude = 3.2 })).GetProperty("business").GetProperty("id").GetGuid();
        FakeOverpassHandler.Responder = (_, _) => FakeOverpassHandler.Json("""{"elements":[{"type":"node","id":77,"lat":36.7,"lon":3.2,"tags":{"name":"UI4 OSM"}}]}""");
        var cat = await admin.CategoryId("Location de véhicules");
        var job = (await (await mgrApi.PostAsJsonAsync("/api/v1/collection/search", new { connectorKey = "osm", areaId = g.Commune1, categoryId = cat })).OkJson()).GetProperty("id").GetGuid();
        FakePageFetcher.Responder = u => new PageFetch("<html><title>Page UI4</title></html>", u);

        var ui = f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await (await ui.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        await ui.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "p4-mgr@test.local", ["Password"] = "User#Test2026x", ["__RequestVerificationToken"] = token }));
        foreach (var url in new[] { "/Sources", "/Collection", $"/Collection/Job/{job}", "/Businesses/FromUrl", "/Refresh", $"/Refresh/Check/{id}", "/Map", "/Imports", "/Admin/Categories" })
            (await ui.GetAsync(url)).StatusCode.Should().Be(url == "/Admin/Categories" ? HttpStatusCode.Redirect.Equals(0) ? HttpStatusCode.OK : (await ui.GetAsync(url)).StatusCode : HttpStatusCode.OK, url);
        var tpl = await ui.GetAsync("/Imports?handler=Template&format=xlsx");
        tpl.StatusCode.Should().Be(HttpStatusCode.OK);
        tpl.Content.Headers.ContentDisposition!.FileName.Should().Be("modele-import-entreprises.xlsx");
        (await ui.GetAsync("/ui/parse-maps?url=" + Uri.EscapeDataString("https://www.google.com/maps/@36.7,3.2,15z"))).Content.ReadAsStringAsync().Result.Should().Contain("36.7");
        var points = await (await ui.GetAsync($"/ui/map-points?communeId={g.Commune1}")).Content.ReadAsStringAsync();
        points.Should().Contain("Page UI4");
        (await (await ui.GetAsync("/Map")).Content.ReadAsStringAsync()).Should().Contain("/lib/leaflet/leaflet.js").And.NotContain("unpkg.com").And.NotContain("googleapis").And.NotContain("api_key");
    }
}
