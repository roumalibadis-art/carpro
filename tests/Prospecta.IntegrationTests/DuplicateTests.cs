using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Prospecta.IntegrationTests.Support;

namespace Prospecta.IntegrationTests;

public class DuplicateTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Similar_businesses_are_flagged_never_merged_automatically_and_distinct_ones_are_left_alone()
    {
        var admin = await f.ClientAsync();
        var g = await admin.SeedGeo(" dup1");
        var first = (await admin.CreateBusiness(new { name = "Auto Location Alpha", communeId = g.Commune1, phone = "0555 30 30 01" })).GetProperty("business").GetProperty("id").GetGuid();
        var second = await admin.CreateBusiness(new { name = "AUTO LOCATION  alpha", communeId = g.Commune1, phone = "0555 30 30 02" });
        second.GetProperty("duplicateWarnings").GetArrayLength().Should().BeGreaterThan(0);
        var secondId = second.GetProperty("business").GetProperty("id").GetGuid();
        var distinct = await admin.CreateBusiness(new { name = "Boulangerie Zeta", communeId = g.Commune1, phone = "0555 30 30 03" });
        distinct.GetProperty("duplicateWarnings").GetArrayLength().Should().Be(0);

        // both records still exist (no automatic merge) and are marked as potential duplicates
        (await admin.GetAsync($"/api/v1/businesses/{first}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await (await admin.GetAsync($"/api/v1/businesses/{secondId}")).OkJson();
        detail.GetProperty("censusStatus").GetProperty("code").GetString().Should().Be("potential_duplicate");

        var list = await (await admin.GetAsync("/api/v1/duplicates")).OkJson();
        var pair = list.GetProperty("items").EnumerateArray().Single(p => new[] { p.GetProperty("a").GetProperty("id").GetGuid(), p.GetProperty("b").GetProperty("id").GetGuid() }.Contains(first));
        pair.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).Should().Contain("Nom identique ou quasi identique");
        (await admin.GetAsync("/api/v1/businesses?pendingDuplicate=true&search=alpha")).OkJson().Result.GetProperty("total").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Same_phone_or_same_provider_id_is_a_strong_signal_and_dismissal_is_remembered()
    {
        var admin = await f.ClientAsync();
        var g = await admin.SeedGeo(" dup2");
        var a = (await admin.CreateBusiness(new { name = "Entreprise Un", communeId = g.Commune1, phone = "0770 11 22 33" })).GetProperty("business").GetProperty("id").GetGuid();
        var b = await admin.CreateBusiness(new { name = "Complètement Autre Nom", communeId = g.Commune2, phone = "+213 770 11 22 33" });
        b.GetProperty("duplicateWarnings").GetArrayLength().Should().Be(1);
        var bId = b.GetProperty("business").GetProperty("id").GetGuid();

        var candidate = (await (await admin.GetAsync("/api/v1/duplicates")).OkJson()).GetProperty("items").EnumerateArray()
            .Single(p => p.GetProperty("a").GetProperty("id").GetGuid() == bId || p.GetProperty("b").GetProperty("id").GetGuid() == bId).GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/duplicates/{candidate}/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync($"/api/v1/businesses/{bId}")).OkJson().Result.GetProperty("censusStatus").GetProperty("code").GetString().Should().Be("partial");

        // editing the record again must not resurrect a pair a human already rejected
        var edit = await (await admin.PutAsJsonAsync($"/api/v1/businesses/{bId}", new { name = "Complètement Autre Nom", communeId = g.Commune2, phone = "0770 11 22 33", address = "Rue du Test 5" })).OkJson();
        edit.GetProperty("duplicateWarnings").GetArrayLength().Should().Be(0);
        (await admin.PostAsync($"/api/v1/duplicates/{candidate}/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        a.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Merge_keeps_sources_history_and_assignments_and_soft_deletes_the_other_record()
    {
        var admin = await f.ClientAsync();
        var (sales, salesId) = await f.CreateUserAsync("dup-sales@test.local", "Salesperson");
        var g = await admin.SeedGeo(" dup3");
        var keep = (await admin.CreateBusiness(new { name = "Garage Merge", communeId = g.Commune1 })).GetProperty("business").GetProperty("id").GetGuid();
        var other = (await admin.CreateBusiness(new { name = "Garage Merge", communeId = g.Commune1, phone = "0661 00 11 22", website = "https://garage-merge.dz", address = "10 avenue Test" })).GetProperty("business").GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = new[] { other }, userId = salesId });

        var cand = (await (await admin.GetAsync("/api/v1/duplicates")).OkJson()).GetProperty("items").EnumerateArray()
            .Single(p => p.GetProperty("a").GetProperty("id").GetGuid() == keep || p.GetProperty("b").GetProperty("id").GetGuid() == keep).GetProperty("id").GetGuid();

        (await sales.GetAsync("/api/v1/duplicates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.PostAsJsonAsync($"/api/v1/duplicates/{cand}/merge", new { survivorId = keep })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync($"/api/v1/duplicates/{cand}/merge", new { survivorId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await admin.PostAsJsonAsync($"/api/v1/duplicates/{cand}/merge", new { survivorId = keep })).StatusCode.Should().Be(HttpStatusCode.OK);

        var merged = await (await admin.GetAsync($"/api/v1/businesses/{keep}")).OkJson();
        merged.GetProperty("data").GetProperty("phone").GetString().Should().Be("0661 00 11 22");      // gaps filled from the other record
        merged.GetProperty("data").GetProperty("website").GetString().Should().Be("https://garage-merge.dz");
        merged.GetProperty("sources").GetArrayLength().Should().Be(2);                                    // provenance kept
        merged.GetProperty("assignments").EnumerateArray().Should().Contain(a => a.GetProperty("userId").GetGuid() == salesId); // work follows the merge
        (await admin.GetAsync($"/api/v1/businesses/{other}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var hist = (await (await admin.GetAsync($"/api/v1/businesses/{keep}/history?pageSize=100")).OkJson()).GetProperty("items").EnumerateArray().Select(h => h.GetProperty("field").GetString()).ToList();
        hist.Should().Contain("Merge").And.Contain("Phone");
        (await admin.PostAsJsonAsync($"/api/v1/duplicates/{cand}/merge", new { survivorId = keep })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
