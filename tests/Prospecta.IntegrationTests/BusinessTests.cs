using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Prospecta.IntegrationTests.Support;

namespace Prospecta.IntegrationTests;

public class BusinessTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Admin, Guid W, Guid D, Guid C1, Guid C2)> SetupAsync(string tag)
    {
        var admin = await f.ClientAsync();
        var g = await admin.SeedGeo(" " + tag);
        return (admin, g.Wilaya, g.Daira, g.Commune1, g.Commune2);
    }

    [Fact]
    public async Task Geography_is_hierarchical_and_businesses_must_respect_it()
    {
        var (admin, w, d, c1, c2) = await SetupAsync("geo");
        var communes = await (await admin.GetAsync($"/api/v1/geo?level=Commune&parentId={d}")).OkJson();
        communes.GetArrayLength().Should().Be(2);

        // a commune cannot hang directly under a wilaya
        (await admin.PostAsJsonAsync("/api/v1/geo", new { level = "Commune", name = "Orpheline", parentId = w })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // duplicate name at the same level is refused (accent/case-insensitive)
        (await admin.PostAsJsonAsync("/api/v1/geo", new { level = "Commune", name = "ROUIBA geo", parentId = d })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // ancestors are derived from the commune
        var b = await admin.CreateBusiness(new { name = "Geo Auto", communeId = c1 });
        b.GetProperty("business").GetProperty("data").GetProperty("wilayaId").GetGuid().Should().Be(w);
        b.GetProperty("business").GetProperty("data").GetProperty("dairaId").GetGuid().Should().Be(d);

        // a daïra that does not match the commune is rejected
        var otherDaira = await admin.CreateGeo("Daira", "Autre daira geo", w);
        (await admin.PostAsJsonAsync("/api/v1/businesses", new { name = "Mismatch", communeId = c1, dairaId = otherDaira })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // coordinates outside Algeria are rejected
        (await admin.PostAsJsonAsync("/api/v1/businesses", new { name = "Hors zone", latitude = 48.8, longitude = 2.3 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Geographic_filters_return_only_matching_businesses()
    {
        var (admin, w, d, c1, c2) = await SetupAsync("flt");
        await admin.CreateBusiness(new { name = "Filtre Un", communeId = c1, phone = "0555 11 22 33" });
        await admin.CreateBusiness(new { name = "Filtre Deux", communeId = c2 });
        await admin.CreateBusiness(new { name = "Filtre Trois", communeId = c2, website = "https://exemple.dz" });

        async Task<string[]> Names(string query) =>
            (await (await admin.GetAsync("/api/v1/businesses?pageSize=100&" + query)).OkJson()).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).Where(n => n.StartsWith("Filtre")).OrderBy(n => n).ToArray();

        (await Names($"communeId={c1}")).Should().Equal("Filtre Un");
        (await Names($"communeId={c2}")).Should().Equal("Filtre Deux", "Filtre Trois");
        (await Names($"dairaId={d}")).Should().HaveCount(3);
        (await Names($"wilayaId={w}&hasPhone=true")).Should().Equal("Filtre Un");
        (await Names($"dairaId={d}&hasWebsite=false")).Should().Equal("Filtre Deux", "Filtre Un");
        (await Names($"dairaId={d}&search=trois")).Should().Equal("Filtre Trois");
        (await Names($"dairaId={d}&search=0555112233")).Should().Equal("Filtre Un");
    }

    [Fact]
    public async Task Census_processing_and_outcome_states_are_independent()
    {
        var (admin, _, _, c1, _) = await SetupAsync("st");
        var (_, salesId) = await f.CreateUserAsync("st-sales@test.local", "Salesperson");
        var b = await admin.CreateBusiness(new { name = "Etats Auto", communeId = c1 });
        var id = b.GetProperty("business").GetProperty("id").GetGuid();
        b.GetProperty("business").GetProperty("censusStatus").GetProperty("code").GetString().Should().Be("partial");
        b.GetProperty("business").GetProperty("processingStatus").GetProperty("code").GetString().Should().Be("unassigned");
        b.GetProperty("business").GetProperty("outcomeStatus").GetProperty("code").GetString().Should().Be("pending");

        // assigning moves ONLY the processing dimension
        (await admin.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = new[] { id }, userId = salesId })).StatusCode.Should().Be(HttpStatusCode.OK);
        var after = (await (await admin.GetAsync($"/api/v1/businesses/{id}")).OkJson());
        after.GetProperty("processingStatus").GetProperty("code").GetString().Should().Be("assigned");
        after.GetProperty("censusStatus").GetProperty("code").GetString().Should().Be("partial");
        after.GetProperty("outcomeStatus").GetProperty("code").GetString().Should().Be("pending");

        // outcome changes leave census and processing untouched
        var interested = await admin.StatusId("Outcome", "interested");
        await admin.PutAsJsonAsync($"/api/v1/businesses/{id}/status", new { kind = "Outcome", statusId = interested });
        // "visited" with a result still pending, and census still unverified
        var visited = await admin.StatusId("Processing", "visited");
        await admin.PutAsJsonAsync($"/api/v1/businesses/{id}/status", new { kind = "Processing", statusId = visited });
        var final = await (await admin.GetAsync($"/api/v1/businesses/{id}")).OkJson();
        final.GetProperty("censusStatus").GetProperty("code").GetString().Should().Be("partial");
        final.GetProperty("processingStatus").GetProperty("code").GetString().Should().Be("visited");
        final.GetProperty("outcomeStatus").GetProperty("code").GetString().Should().Be("interested");

        // a status of the wrong dimension is refused
        (await admin.PutAsJsonAsync($"/api/v1/businesses/{id}/status", new { kind = "Census", statusId = interested })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // history keeps every change
        var hist = await (await admin.GetAsync($"/api/v1/businesses/{id}/history")).OkJson();
        hist.GetProperty("items").EnumerateArray().Select(h => h.GetProperty("field").GetString()).Should().Contain(["Created", "Assignment", "OutcomeStatus", "ProcessingStatus"]);
    }

    [Fact]
    public async Task Users_only_see_and_edit_what_is_assigned_to_them_and_ids_cannot_be_guessed()
    {
        var (admin, _, _, c1, _) = await SetupAsync("idor");
        var (alice, aliceId) = await f.CreateUserAsync("alice@test.local", "Salesperson");
        var (bob, _) = await f.CreateUserAsync("bob@test.local", "Salesperson");
        var (mgr, _) = await f.CreateUserAsync("mgr-idor@test.local", "SalesManager");
        var mine = (await admin.CreateBusiness(new { name = "Scope Alice", communeId = c1 })).GetProperty("business").GetProperty("id").GetGuid();
        var other = (await admin.CreateBusiness(new { name = "Scope Libre", communeId = c1 })).GetProperty("business").GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = new[] { mine }, userId = aliceId });

        var aliceList = await (await alice.GetAsync("/api/v1/businesses?search=scope")).OkJson();
        aliceList.GetProperty("total").GetInt32().Should().Be(1);
        (await alice.GetAsync($"/api/v1/businesses/{mine}")).StatusCode.Should().Be(HttpStatusCode.OK);
        // other user's / unassigned record: indistinguishable from "does not exist"
        (await alice.GetAsync($"/api/v1/businesses/{other}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/v1/businesses/{mine}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/v1/businesses/{mine}/history")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.PutAsJsonAsync($"/api/v1/businesses/{mine}", new { name = "Piratage" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.PostAsync($"/api/v1/businesses/{mine}/verify", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await bob.GetAsync("/api/v1/businesses/export")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // salesperson cannot assign or delete
        (await alice.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = new[] { mine }, userId = aliceId })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await alice.DeleteAsync($"/api/v1/businesses/{mine}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // alice may complete her own record
        (await alice.PutAsJsonAsync($"/api/v1/businesses/{mine}", new { name = "Scope Alice", communeId = c1, address = "12 rue des Tests" })).StatusCode.Should().Be(HttpStatusCode.OK);
        // the manager sees the whole organization
        (await (await mgr.GetAsync("/api/v1/businesses?search=scope")).OkJson()).GetProperty("total").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Confirmed_data_is_protected_and_every_change_is_kept_in_history()
    {
        var (admin, _, _, c1, _) = await SetupAsync("prov");
        var (alice, aliceId) = await f.CreateUserAsync("prov-alice@test.local", "Salesperson");
        var id = (await admin.CreateBusiness(new { name = "Prov Auto", communeId = c1, phone = "0555 12 34 56", address = "1 rue A" })).GetProperty("business").GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = new[] { id }, userId = aliceId });
        (await admin.PostAsync($"/api/v1/businesses/{id}/verify", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var verified = await (await admin.GetAsync($"/api/v1/businesses/{id}")).OkJson();
        verified.GetProperty("censusStatus").GetProperty("code").GetString().Should().Be("verified");
        verified.GetProperty("origins").GetProperty("Phone").GetString().Should().Be("Confirmed");
        verified.GetProperty("lastVerifiedAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);

        // the salesperson cannot silently overwrite a confirmed value: it is reported as blocked
        var res = await (await alice.PutAsJsonAsync($"/api/v1/businesses/{id}", new { name = "Prov Auto", communeId = c1, phone = "0666 00 00 00", address = "2 rue B" })).OkJson();
        res.GetProperty("blockedFields").EnumerateArray().Select(x => x.GetString()).Should().Contain(s => s!.Contains("Phone"));
        res.GetProperty("business").GetProperty("data").GetProperty("phone").GetString().Should().Be("0555 12 34 56");

        // the verifier may change it; the old value stays in the history
        var ok = await (await admin.PutAsJsonAsync($"/api/v1/businesses/{id}", new { name = "Prov Auto", communeId = c1, phone = "0666 00 00 00", address = "1 rue A" })).OkJson();
        ok.GetProperty("business").GetProperty("data").GetProperty("phone").GetString().Should().Be("0666 00 00 00");
        var hist = (await (await admin.GetAsync($"/api/v1/businesses/{id}/history?pageSize=100")).OkJson()).GetProperty("items").EnumerateArray().ToList();
        hist.Should().Contain(h => h.GetProperty("field").GetString() == "Phone" && h.GetProperty("oldValue").GetString() == "0555 12 34 56" && h.GetProperty("newValue").GetString() == "0666 00 00 00");
    }

    [Fact]
    public async Task Invalid_input_is_rejected_with_clear_messages()
    {
        var (admin, _, _, _, _) = await SetupAsync("val");
        var r = await admin.PostAsJsonAsync("/api/v1/businesses", new { name = "", phone = "12345", website = "pas une url" });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await r.Json();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).Should().Contain(e => e!.Contains("obligatoire")).And.Contain(e => e!.Contains("téléphone"));
    }

    [Fact]
    public async Task Soft_deleted_businesses_disappear_but_stay_in_the_audit_trail()
    {
        var (admin, _, _, c1, _) = await SetupAsync("del");
        var id = (await admin.CreateBusiness(new { name = "Supprimable", communeId = c1 })).GetProperty("business").GetProperty("id").GetGuid();
        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/businesses/{id}") { Content = JsonContent.Create(new { reason = "test" }) };
        (await admin.SendAsync(del)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync($"/api/v1/businesses/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync("/api/v1/audit?action=business.delete")).OkJson().Result.GetProperty("items").EnumerateArray().Should().Contain(a => a.GetProperty("entityId").GetString() == id.ToString());
    }

    [Fact]
    public async Task Saved_filters_are_private_to_their_owner_and_sharing_is_restricted()
    {
        var (alice, _) = await f.CreateUserAsync("sf-alice@test.local", "Salesperson");
        var (bob, _) = await f.CreateUserAsync("sf-bob@test.local", "Salesperson");
        var (mgr, _) = await f.CreateUserAsync("sf-mgr@test.local", "SalesManager");
        var id = (await (await alice.PostAsJsonAsync("/api/v1/saved-filters", new { name = "Mes sans tel", filter = new { hasPhone = false } })).OkJson()).GetProperty("id").GetGuid();
        (await (await bob.GetAsync("/api/v1/saved-filters")).OkJson()).EnumerateArray().Should().NotContain(x => x.GetProperty("id").GetGuid() == id);
        (await bob.DeleteAsync($"/api/v1/saved-filters/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await alice.PostAsJsonAsync("/api/v1/saved-filters", new { name = "Partagée", filter = new { }, scope = "Shared" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var shared = (await (await mgr.PostAsJsonAsync("/api/v1/saved-filters", new { name = "Équipe", filter = new { hasWebsite = false }, scope = "Shared" })).OkJson()).GetProperty("id").GetGuid();
        (await (await bob.GetAsync("/api/v1/saved-filters")).OkJson()).EnumerateArray().Should().Contain(x => x.GetProperty("id").GetGuid() == shared);
        (await alice.DeleteAsync($"/api/v1/saved-filters/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Dashboard_counts_match_the_visible_scope()
    {
        var (admin, _, _, c1, _) = await SetupAsync("dash");
        var (alice, aliceId) = await f.CreateUserAsync("dash-alice@test.local", "Salesperson");
        var a = (await admin.CreateBusiness(new { name = "Dash A", communeId = c1 })).GetProperty("business").GetProperty("id").GetGuid();
        await admin.CreateBusiness(new { name = "Dash B", communeId = c1 });
        await admin.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = new[] { a }, userId = aliceId });
        var mine = await (await alice.GetAsync("/api/v1/dashboard")).OkJson();
        mine.GetProperty("total").GetInt32().Should().Be(1);
        mine.GetProperty("unassigned").GetInt32().Should().Be(0);
        var all = await (await admin.GetAsync("/api/v1/dashboard?search=dash")).OkJson();
        all.GetProperty("total").GetInt32().Should().Be(2);
        all.GetProperty("unassigned").GetInt32().Should().Be(1);
    }
}
