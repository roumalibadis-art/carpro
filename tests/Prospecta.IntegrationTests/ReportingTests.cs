using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using FluentAssertions;
using Prospecta.IntegrationTests.Support;

namespace Prospecta.IntegrationTests;

public class ReportingTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(offset)).ToString("yyyy-MM-dd");

    private sealed record Ctx(HttpClient Admin, HttpClient Mgr, Guid MgrId, HttpClient Sales, Guid SalesId, HttpClient Other, Guid OtherId, HttpClient Outsider, Guid[] Biz, (Guid Wilaya, Guid Daira, Guid Commune1, Guid Commune2) Geo);

    private async Task<Ctx> SetupAsync(string tag)
    {
        var admin = await f.ClientAsync();
        var (mgr, mgrId) = await f.CreateUserAsync($"rp-mgr-{tag}@test.local", "SalesManager");
        var (sales, salesId) = await f.CreateUserAsync($"rp-sales-{tag}@test.local", "Salesperson", mgrId);
        var (other, otherId) = await f.CreateUserAsync($"rp-other-{tag}@test.local", "Salesperson", mgrId);
        var (_, mgr2Id) = await f.CreateUserAsync($"rp-mgr2-{tag}@test.local", "SalesManager");
        var (outsider, _) = await f.CreateUserAsync($"rp-out-{tag}@test.local", "Salesperson", mgr2Id);
        var geo = await admin.SeedGeo(" rp" + tag);
        var names = new[] { "Agence Alpha", "Garage Bravo", "Boulangerie Charlie", "Pharmacie Delta" };
        var biz = new List<Guid>();
        foreach (var n in names) biz.Add((await mgr.CreateBusiness(new { name = $"{n} {tag}", communeId = geo.Commune1 })).GetProperty("business").GetProperty("id").GetGuid());
        await mgr.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = biz, userId = salesId });
        return new Ctx(admin, mgr, mgrId, sales, salesId, other, otherId, outsider, biz.ToArray(), geo);
    }

    private static async Task<Guid> Plan(HttpClient c, Guid biz, int hours = 1, string action = "Visit", Guid? outing = null) =>
        (await (await c.PostAsJsonAsync("/api/v1/visits/plan", new { businessId = biz, scheduledAt = DateTime.UtcNow.AddHours(hours), action, outingId = outing })).OkJson()).GetProperty("id").GetGuid();

    private static decimal? Val(System.Text.Json.JsonElement set, string key)
    {
        var i = set.GetProperty("indicators").EnumerateArray().First(x => x.GetProperty("key").GetString() == key).GetProperty("value");
        return i.ValueKind == System.Text.Json.JsonValueKind.Null ? null : i.GetDecimal();
    }

    [Fact]
    public async Task Indicators_follow_the_documented_definitions_and_never_treat_missing_data_as_zero()
    {
        var x = await SetupAsync("ind");
        var interested = await x.Mgr.StatusId("Outcome", "interested");
        var notInterested = await x.Mgr.StatusId("Outcome", "not_interested");

        var camp = (await (await x.Mgr.PostAsJsonAsync("/api/v1/campaigns", new { name = "Ind camp", startDate = Day(-5), endDate = Day(20) })).OkJson()).GetProperty("id").GetGuid();
        var outing = (await (await x.Mgr.PostAsJsonAsync("/api/v1/outings", new { date = Day(0), zone = "Z", campaignId = camp, participantIds = new[] { x.SalesId } })).OkJson()).GetProperty("id").GetGuid();
        var v1 = await Plan(x.Sales, x.Biz[0], 1, "Visit", outing);
        var v2 = await Plan(x.Sales, x.Biz[1], 1, "Appointment", outing);
        var v3 = await Plan(x.Sales, x.Biz[2], 1, "Demo", outing);
        await x.Sales.PostAsJsonAsync($"/api/v1/visits/{v1}/complete", new { interest = "High", outcomeStatusId = interested });
        await x.Sales.PostAsJsonAsync($"/api/v1/visits/{v2}/complete", new { interest = "Low", outcomeStatusId = notInterested });
        await x.Sales.PostAsJsonAsync($"/api/v1/visits/{v3}/cancel", new { reason = "fermé" });
        await x.Sales.PostAsJsonAsync("/api/v1/outings/expenses", new { outingId = outing, kind = "Actual", category = "Transport", amount = 3000, date = Day(0) });
        var late = (await (await x.Sales.PostAsJsonAsync("/api/v1/followups", new { businessId = x.Biz[0], dueDate = Day(-1), reason = "a" })).OkJson()).GetProperty("id").GetGuid();
        await x.Sales.PostAsJsonAsync("/api/v1/followups", new { businessId = x.Biz[1], dueDate = Day(-1), reason = "b" });
        await x.Sales.PostAsJsonAsync($"/api/v1/followups/{late}/complete", new { result = "ok" });

        var set = await (await x.Mgr.GetAsync($"/api/v1/indicators?from={Day(-3)}&to={Day(1)}&userId={x.SalesId}")).OkJson();
        Val(set, "realisation").Should().Be(66.7m);   // 2 done / 3 planned (the cancelled one still counts as planned)
        Val(set, "coverage").Should().Be(50m);        // 2 distinct treated / 4 assigned
        Val(set, "interest").Should().Be(50m);        // 1 interested / 2 contacted
        Val(set, "conversion").Should().Be(0m);       // computable: 0 won / 2 contacted
        Val(set, "followups").Should().Be(50m);       // 1 done / 2 due
        Val(set, "costpervisit").Should().Be(1500m);  // 3000 DA / 2 visits done
        set.GetProperty("counts").GetProperty("treated").GetInt32().Should().Be(2);
        set.GetProperty("counts").GetProperty("calls").GetInt32().Should().Be(0);
        set.GetProperty("filters").EnumerateArray().Select(e => e.GetString()).Should().Contain(s => s!.StartsWith("Période"));

        // a user with no activity: every indicator is "Non calculable", never an error and never 0 %
        var empty = await (await x.Mgr.GetAsync($"/api/v1/indicators?from={Day(-3)}&to={Day(1)}&userId={x.OtherId}")).OkJson();
        foreach (var i in empty.GetProperty("indicators").EnumerateArray()) i.GetProperty("value").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null, i.GetProperty("key").GetString());
        empty.GetProperty("indicators")[0].GetProperty("display").GetString().Should().Be("Non calculable");

        // comparable previous period has no data yet: no evolution is invented
        set.GetProperty("indicators")[0].GetProperty("delta").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);

        // campaign filter restricts the population; inverted periods are refused
        (await x.Mgr.GetAsync($"/api/v1/indicators?from={Day(-3)}&to={Day(1)}&campaignId={camp}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await x.Mgr.GetAsync($"/api/v1/indicators?from={Day(5)}&to={Day(1)}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // scope: a salesperson only sees themselves; nobody reads someone outside their reporting line
        (await x.Sales.GetAsync($"/api/v1/indicators?from={Day(-3)}&to={Day(1)}&userId={x.OtherId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await x.Mgr.GetAsync($"/api/v1/indicators?from={Day(-3)}&to={Day(1)}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var outsiderId = (await (await x.Admin.GetAsync("/api/v1/users?search=rp-out-ind")).OkJson()).GetProperty("items")[0].GetProperty("id").GetGuid();
        (await x.Mgr.GetAsync($"/api/v1/indicators?from={Day(-3)}&to={Day(1)}&userId={outsiderId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Private_reports_stay_private_sharing_opens_them_to_the_hierarchy_and_snapshots_never_change()
    {
        var x = await SetupAsync("acc");
        var v1 = await Plan(x.Sales, x.Biz[0]);
        await x.Sales.PostAsJsonAsync($"/api/v1/visits/{v1}/complete", new { interest = "High", comment = "Très bon accueil" });
        var p = new { type = "Individual", parameters = new { from = Day(-3), to = Day(1), title = "Mon bilan" }, share = false };

        var saved = await (await x.Sales.PostAsJsonAsync("/api/v1/reports", p)).OkJson();
        var id = saved.GetProperty("id").GetGuid();
        saved.GetProperty("shared").GetBoolean().Should().BeFalse();

        // private: invisible to everyone else, including the manager and the administrator
        foreach (var other in new[] { x.Mgr, x.Admin, x.Other, x.Outsider }) (await other.GetAsync($"/api/v1/reports/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await x.Mgr.GetAsync("/api/v1/reports?team=true")).OkJson()).GetProperty("items").EnumerateArray().Should().NotContain(r => r.GetProperty("id").GetGuid() == id);
        (await x.Sales.GetAsync("/api/v1/reports?team=true")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await x.Mgr.PostAsJsonAsync($"/api/v1/reports/{id}/validate", new { note = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await x.Other.DeleteAsync($"/api/v1/reports/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // shared: the owner's manager and the administrator can read it; colleagues and other lines still cannot
        (await x.Sales.PostAsJsonAsync($"/api/v1/reports/{id}/share", new { shared = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await x.Mgr.GetAsync($"/api/v1/reports/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await x.Admin.GetAsync($"/api/v1/reports/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await x.Other.GetAsync($"/api/v1/reports/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await x.Outsider.GetAsync($"/api/v1/reports/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await x.Mgr.GetAsync("/api/v1/reports?team=true")).OkJson()).GetProperty("items").EnumerateArray().Should().Contain(r => r.GetProperty("id").GetGuid() == id);

        // validation stamps the report without touching its figures; the author cannot validate themselves
        (await x.Sales.PostAsJsonAsync($"/api/v1/reports/{id}/validate", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await x.Mgr.PostAsJsonAsync($"/api/v1/reports/{id}/validate", new { note = "Vérifié" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await x.Mgr.PostAsJsonAsync($"/api/v1/reports/{id}/validate", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var before = await (await x.Sales.GetAsync($"/api/v1/reports/{id}")).OkJson();
        before.GetProperty("summary").GetProperty("validatedBy").GetString().Should().NotBeNull();
        (await x.Sales.PostAsJsonAsync($"/api/v1/reports/{id}/share", new { shared = false })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // snapshot: the saved report does not move when the data does
        string Fingerprint(System.Text.Json.JsonElement view) => string.Join("|", view.GetProperty("document").GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("blocks").EnumerateArray())
            .Where(b => b.GetProperty("type").GetString() == "table").SelectMany(b => b.GetProperty("rows").EnumerateArray().Select(r => string.Join(",", r.EnumerateArray().Select(c => c.GetString())))));
        var fpBefore = Fingerprint(before);
        var v2 = await Plan(x.Sales, x.Biz[1]);
        await x.Sales.PostAsJsonAsync($"/api/v1/visits/{v2}/complete", new { interest = "High" });
        var after = await (await x.Sales.GetAsync($"/api/v1/reports/{id}")).OkJson();
        Fingerprint(after).Should().Be(fpBefore);
        after.GetProperty("document").GetProperty("title").GetString().Should().Be("Mon bilan");

        // duplicating produces a NEW report with fresh figures and a link to the source
        var copy = await (await x.Sales.PostAsJsonAsync($"/api/v1/reports/{id}/duplicate", new { })).OkJson();
        var copyView = await (await x.Sales.GetAsync($"/api/v1/reports/{copy.GetProperty("id").GetGuid()}")).OkJson();
        copyView.GetProperty("sourceReportId").GetGuid().Should().Be(id);
        Fingerprint(copyView).Should().NotBe(fpBefore);
        (await (await x.Sales.GetAsync("/api/v1/reports")).OkJson().ContinueWith(t => t.Result.GetProperty("total").GetInt32())).Should().Be(2);

        // exports: real PDF and Excel files
        var pdf = await (await x.Sales.GetAsync($"/api/v1/reports/{id}/export?format=pdf")).Content.ReadAsByteArrayAsync();
        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        pdf.Length.Should().BeGreaterThan(3000);
        var xlsx = await (await x.Sales.GetAsync($"/api/v1/reports/{id}/export?format=xlsx")).Content.ReadAsByteArrayAsync();
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        wb.Worksheets.Count.Should().BeGreaterThan(1);
        (await x.Other.GetAsync($"/api/v1/reports/{id}/export?format=pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await x.Sales.GetAsync($"/api/v1/reports/{id}/export?format=doc")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Market_study_separates_facts_from_generated_analysis_and_never_invents_market_figures()
    {
        var x = await SetupAsync("mkt");
        var doc = await (await x.Mgr.PostAsJsonAsync("/api/v1/reports/preview", new { type = "MarketStudy", parameters = new { title = "Étude Rouïba", objective = "Cartographier l'offre", filter = new { communeId = x.Geo.Commune1 } } })).OkJson();
        var sections = doc.GetProperty("sections").EnumerateArray().ToList();
        var headings = sections.Select(s => s.GetProperty("heading").GetString()).ToList();
        headings.Should().Contain(["Objectif et périmètre", "Méthodologie et sources", "Tableau récapitulatif des entreprises recensées", "Fiches détaillées", "Répartition par commune et activité",
            "Coordonnées disponibles", "Informations manquantes", "Doublons potentiels", "État de vérification des fiches", "Limites de l'étude"]);
        headings.Should().Contain(h => h!.StartsWith("Analyse descriptive")).And.Contain(h => h!.StartsWith("Conclusions et recommandations"));
        sections.Where(s => s.GetProperty("heading").GetString()!.Contains("(générée")).Should().OnlyContain(s => s.GetProperty("kind").GetString() == "Analysis");
        sections.First(s => s.GetProperty("heading").GetString() == "Objectif et périmètre").GetProperty("kind").GetString().Should().Be("Facts");
        var text = doc.ToString();
        text.Should().Contain("Cartographier l'offre").And.Contain("ne permettent pas de les calculer");
        text.ToLowerInvariant().Should().NotContain("chiffre d'affaires de").And.NotContain("taille du marché est");

        // a salesperson sees only what is assigned to them: the same study returns their subset
        var mine = await (await x.Other.PostAsJsonAsync("/api/v1/reports/preview", new { type = "MarketStudy", parameters = new { filter = new { communeId = x.Geo.Commune1 } } })).OkJson();
        mine.ToString().Should().Contain("Aucune entreprise dans le périmètre");
    }

    [Fact]
    public async Task Outing_balance_marks_missing_information_and_manager_report_compares_comparable_populations()
    {
        var x = await SetupAsync("bal");
        var outing = (await (await x.Mgr.PostAsJsonAsync("/api/v1/outings", new { date = Day(0), zone = "Centre", participantIds = new[] { x.SalesId } })).OkJson()).GetProperty("id").GetGuid();
        var v = await Plan(x.Sales, x.Biz[0], 1, "Visit", outing);
        await x.Sales.PostAsJsonAsync($"/api/v1/visits/{v}/complete", new { contactMet = "M. Karim", interest = "Medium", objections = "Prix trop élevé", requestedInfo = "Brochure", nextAction = "Envoyer brochure", nextFollowUpDate = Day(3) });
        await x.Mgr.PostAsJsonAsync("/api/v1/outings/expenses", new { outingId = outing, kind = "Planned", category = "Transport", amount = 2000, date = Day(0) });

        var saved = await (await x.Mgr.PostAsJsonAsync("/api/v1/reports", new { type = "OutingBalance", parameters = new { outingId = outing, positiveFeedback = "Bon contact" }, share = true })).OkJson();
        var view = await (await x.Mgr.GetAsync($"/api/v1/reports/{saved.GetProperty("id").GetGuid()}")).OkJson();
        var text = view.ToString();
        text.Should().Contain("M. Karim").And.Contain("Prix trop élevé").And.Contain("Envoyer brochure").And.Contain("Bon contact");
        text.Should().Contain("Non renseigné");   // heure de départ, difficultés, améliorations… were not provided: nothing invented
        view.GetProperty("document").GetProperty("meta").EnumerateArray().Should().Contain(m => m.GetProperty("key").GetString() == "Vérificateur" && m.GetProperty("value").GetString() == "Non renseigné");
        (await x.Other.PostAsJsonAsync("/api/v1/reports/preview", new { type = "OutingBalance", parameters = new { outingId = outing } })).StatusCode.Should().Be(HttpStatusCode.NotFound); // not a participant

        var d = await (await x.Mgr.PostAsJsonAsync("/api/v1/reports/preview", new { type = "Manager", parameters = new { from = Day(-3), to = Day(1) } })).OkJson();
        var dt = d.ToString();
        dt.Should().Contain("Comparaison des commerciaux").And.Contain("Mêmes définitions").And.Contain("User rp-sales-bal@test.local");
        d.GetProperty("sections").EnumerateArray().Any(s => s.GetProperty("kind").GetString() == "Analysis").Should().BeTrue();
        (await x.Sales.PostAsJsonAsync("/api/v1/reports/preview", new { type = "Manager", parameters = new { from = Day(-3), to = Day(1) } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await x.Sales.PostAsJsonAsync("/api/v1/reports/preview", new { type = "Individual", parameters = new { to = Day(1) } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Sales.PostAsJsonAsync("/api/v1/reports/preview", new { type = "Individual", parameters = new { from = Day(-3), to = Day(1), userId = x.OtherId } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Report_pages_render_and_download()
    {
        var x = await SetupAsync("ui");
        var saved = await (await x.Mgr.PostAsJsonAsync("/api/v1/reports", new { type = "Manager", parameters = new { from = Day(-3), to = Day(1) }, share = true })).OkJson();
        var id = saved.GetProperty("id").GetGuid();
        var ui = f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await (await ui.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        await ui.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "rp-mgr-ui@test.local", ["Password"] = "User#Test2026x", ["__RequestVerificationToken"] = token }));
        foreach (var url in new[] { "/Evaluation", $"/Evaluation?From={Day(-3)}&To={Day(1)}", "/Reports", "/Reports?Team=true", "/Reports/New", "/Reports/New?Type=Manager", "/Reports/New?Type=OutingBalance", "/Reports/New?Type=Individual", $"/Reports/View/{id}" })
            (await ui.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK, url);
        var pdf = await ui.GetAsync($"/Reports/View/{id}?handler=Export&format=pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
    }
}
