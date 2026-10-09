using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Prospecta.Application.Prospecting;
using Prospecta.IntegrationTests.Support;

namespace Prospecta.IntegrationTests;

public class ProspectingTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(offset)).ToString("yyyy-MM-dd");

    private sealed record Ctx(HttpClient Admin, HttpClient Mgr, Guid MgrId, HttpClient Sales, Guid SalesId, HttpClient Other, Guid OtherId, (Guid Wilaya, Guid Daira, Guid Commune1, Guid Commune2) Geo);

    private async Task<Ctx> SetupAsync(string tag)
    {
        var admin = await f.ClientAsync();
        var (mgr, mgrId) = await f.CreateUserAsync($"p2-mgr-{tag}@test.local", "SalesManager");
        var (sales, salesId) = await f.CreateUserAsync($"p2-sales-{tag}@test.local", "Salesperson", mgrId);
        var (other, otherId) = await f.CreateUserAsync($"p2-other-{tag}@test.local", "Salesperson", mgrId);
        return new Ctx(admin, mgr, mgrId, sales, salesId, other, otherId, await admin.SeedGeo(" p2" + tag));
    }

    private static async Task<Guid> NewBusiness(HttpClient c, string name, Guid commune) =>
        (await c.CreateBusiness(new { name, communeId = commune })).GetProperty("business").GetProperty("id").GetGuid();

    private static async Task<Guid> NewCampaign(HttpClient mgr, string name, object? extra = null)
    {
        var r = await mgr.PostAsJsonAsync("/api/v1/campaigns", new { name, objective = "Tester", startDate = Day(0), endDate = Day(30), visitTarget = 10, budget = 50000 });
        return (await r.OkJson()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Campaign_rules_visibility_and_target_assignment()
    {
        var x = await SetupAsync("camp");
        // validation
        (await x.Mgr.PostAsJsonAsync("/api/v1/campaigns", new { name = "Mauvaise", startDate = Day(10), endDate = Day(1) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Mgr.PostAsJsonAsync("/api/v1/campaigns", new { name = "Budget", startDate = Day(0), endDate = Day(1), budget = -5 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Sales.PostAsJsonAsync("/api/v1/campaigns", new { name = "Interdit", startDate = Day(0), endDate = Day(1) })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var camp = await NewCampaign(x.Mgr, "Location Rouïba");
        var b1 = await NewBusiness(x.Mgr, "Agence Alpha Camp", x.Geo.Commune1);
        var b2 = await NewBusiness(x.Mgr, "Garage Zeta Camp", x.Geo.Commune1);
        var b3 = await NewBusiness(x.Mgr, "Boulangerie Omega Camp", x.Geo.Commune2);

        (await x.Mgr.PostAsJsonAsync($"/api/v1/campaigns/{camp}/targets", new { businessIds = new[] { b1, b2 }, assigneeId = x.SalesId })).StatusCode.Should().Be(HttpStatusCode.OK);
        await x.Mgr.PostAsJsonAsync($"/api/v1/campaigns/{camp}/targets", new { businessIds = new[] { b3 } });
        // adding the same business again creates nothing new
        (await (await x.Mgr.PostAsJsonAsync($"/api/v1/campaigns/{camp}/targets", new { businessIds = new[] { b1 }, assigneeId = x.SalesId })).OkJson()).GetProperty("changed").GetInt32().Should().Be(0);

        var detail = await (await x.Mgr.GetAsync($"/api/v1/campaigns/{camp}")).OkJson();
        detail.GetProperty("targets").GetInt32().Should().Be(3);
        detail.GetProperty("assignedTargets").GetInt32().Should().Be(2);

        // the assigned salesperson sees the campaign and only their own targets; the others see nothing
        (await x.Sales.GetAsync($"/api/v1/campaigns/{camp}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await x.Sales.GetAsync($"/api/v1/campaigns/{camp}/targets")).OkJson()).GetProperty("total").GetInt32().Should().Be(2);
        (await x.Other.GetAsync($"/api/v1/campaigns/{camp}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await x.Other.GetAsync($"/api/v1/campaigns/{camp}/targets")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await x.Other.GetAsync("/api/v1/campaigns")).OkJson()).GetProperty("items").EnumerateArray().Should().NotContain(c => c.GetProperty("id").GetGuid() == camp);
        (await x.Sales.PostAsJsonAsync($"/api/v1/campaigns/{camp}/targets", new { businessIds = new[] { b3 } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // assignment gave the salesperson access to the business and moved only the processing state
        var asBiz = await (await x.Sales.GetAsync($"/api/v1/businesses/{b1}")).OkJson();
        asBiz.GetProperty("processingStatus").GetProperty("code").GetString().Should().Be("assigned");
        asBiz.GetProperty("censusStatus").GetProperty("code").GetString().Should().Be("partial");
        (await x.Sales.GetAsync($"/api/v1/businesses/{b3}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // lifecycle
        (await x.Mgr.PutAsJsonAsync($"/api/v1/campaigns/{camp}/status", new { status = "Completed" })).StatusCode.Should().Be(HttpStatusCode.Conflict); // Draft → Completed not allowed
        (await x.Mgr.PutAsJsonAsync($"/api/v1/campaigns/{camp}/status", new { status = "Active" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await x.Mgr.PutAsJsonAsync($"/api/v1/campaigns/{camp}/status", new { status = "Completed" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await x.Mgr.PostAsJsonAsync($"/api/v1/campaigns/{camp}/targets", new { businessIds = new[] { b3 } })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Visits_keep_a_full_history_and_drive_processing_and_outcome_but_never_census()
    {
        var x = await SetupAsync("visit");
        var camp = await NewCampaign(x.Mgr, "Visites");
        var biz = await NewBusiness(x.Mgr, "Visite Cible", x.Geo.Commune1);
        await x.Mgr.PostAsJsonAsync($"/api/v1/businesses/assign", new { businessIds = new[] { biz }, userId = x.SalesId });
        var interested = await x.Mgr.StatusId("Outcome", "interested");

        // a salesperson cannot plan on a business they do not see, nor for someone else
        var other = await NewBusiness(x.Mgr, "Visite Hors portée", x.Geo.Commune1);
        (await x.Sales.PostAsJsonAsync("/api/v1/visits/plan", new { businessId = other, scheduledAt = DateTime.UtcNow.AddDays(1), action = "Visit" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await x.Sales.PostAsJsonAsync("/api/v1/visits/plan", new { businessId = biz, userId = x.OtherId, scheduledAt = DateTime.UtcNow.AddDays(1), action = "Visit" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var planned = await (await x.Sales.PostAsJsonAsync("/api/v1/visits/plan", new { businessId = biz, scheduledAt = DateTime.UtcNow.AddDays(1), action = "Visit", campaignId = camp })).OkJson();
        planned.GetProperty("status").GetString().Should().Be("Planned");
        planned.GetProperty("wasPlanned").GetBoolean().Should().BeTrue();
        (await (await x.Sales.GetAsync($"/api/v1/businesses/{biz}")).OkJson()).GetProperty("processingStatus").GetProperty("code").GetString().Should().Be("visit_planned");
        var v1 = planned.GetProperty("id").GetGuid();

        // another salesperson cannot read, complete or cancel it
        (await x.Other.GetAsync($"/api/v1/visits/{v1}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await x.Other.PostAsJsonAsync($"/api/v1/visits/{v1}/cancel", new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var done = await (await x.Sales.PostAsJsonAsync($"/api/v1/visits/{v1}/complete", new
        {
            contactMet = "M. Benali", interest = "High", outcomeStatusId = interested, objections = "Prix", needIdentified = "Flotte de 5 véhicules",
            requestedInfo = "Devis", nextAction = "Envoyer le devis", nextFollowUpDate = Day(3), comment = "Bon accueil",
        })).OkJson();
        done.GetProperty("status").GetString().Should().Be("Done");
        done.GetProperty("outcomeLabel").GetString().Should().Be("Intéressé");

        var after = await (await x.Sales.GetAsync($"/api/v1/businesses/{biz}")).OkJson();
        after.GetProperty("processingStatus").GetProperty("code").GetString().Should().Be("visited");
        after.GetProperty("outcomeStatus").GetProperty("code").GetString().Should().Be("interested");
        after.GetProperty("censusStatus").GetProperty("code").GetString().Should().Be("partial"); // untouched

        // the recorded result is immutable; a second call on the same business adds a second record
        (await x.Sales.PostAsJsonAsync($"/api/v1/visits/{v1}/complete", new { comment = "écrase" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var call = await (await x.Sales.PostAsJsonAsync("/api/v1/visits/log", new { visit = new { businessId = biz, scheduledAt = DateTime.UtcNow, action = "Call" }, result = new { contactMet = "Standard", comment = "Rappel demandé" } })).OkJson();
        call.GetProperty("wasPlanned").GetBoolean().Should().BeFalse();
        var history = await (await x.Sales.GetAsync($"/api/v1/visits/business/{biz}")).OkJson();
        history.GetArrayLength().Should().Be(2);
        history.EnumerateArray().Select(h => h.GetProperty("action").GetString()).Should().BeEquivalentTo("Visit", "Call");
        (await x.Sales.GetAsync($"/api/v1/businesses/{biz}")).OkJson().Result.GetProperty("processingStatus").GetProperty("code").GetString().Should().Be("visited"); // never moved backwards by a call

        // the next follow-up was created automatically
        var fu = await (await x.Sales.GetAsync($"/api/v1/followups?businessId={biz}")).OkJson();
        fu.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("reason").GetString() == "Envoyer le devis" && i.GetProperty("dueDate").GetString() == Day(3));

        // cancel and postpone are distinct states
        var p2 = (await (await x.Sales.PostAsJsonAsync("/api/v1/visits/plan", new { businessId = biz, scheduledAt = DateTime.UtcNow.AddDays(5), action = "Demo" })).OkJson()).GetProperty("id").GetGuid();
        var moved = await (await x.Sales.PostAsJsonAsync($"/api/v1/visits/{p2}/postpone", new { newScheduledAt = DateTime.UtcNow.AddDays(9) })).OkJson();
        moved.GetProperty("status").GetString().Should().Be("Planned");
        moved.GetProperty("postponedFromId").GetGuid().Should().Be(p2);
        (await (await x.Sales.GetAsync($"/api/v1/visits/{p2}")).OkJson()).GetProperty("status").GetString().Should().Be("Postponed");
        (await x.Sales.PostAsJsonAsync($"/api/v1/visits/{p2}/postpone", new { newScheduledAt = DateTime.UtcNow.AddDays(12) })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var p3 = moved.GetProperty("id").GetGuid();
        (await x.Sales.PostAsJsonAsync($"/api/v1/visits/{p3}/cancel", new { reason = "Fermé ce jour" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var statuses = (await (await x.Sales.GetAsync($"/api/v1/visits?businessId={biz}")).OkJson()).GetProperty("items").EnumerateArray().Select(v => v.GetProperty("status").GetString()).ToList();
        statuses.Should().BeEquivalentTo("Done", "Done", "Postponed", "Cancelled");

        // the manager sees team activity; the other salesperson sees none of it
        (await (await x.Mgr.GetAsync($"/api/v1/visits?userId={x.SalesId}")).OkJson()).GetProperty("total").GetInt32().Should().Be(4);
        (await (await x.Other.GetAsync("/api/v1/visits")).OkJson()).GetProperty("total").GetInt32().Should().Be(0);
        (await x.Sales.PostAsJsonAsync($"/api/v1/visits/{Guid.NewGuid()}/complete", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Followups_overdue_chain_ownership_and_reminders()
    {
        var x = await SetupAsync("fu");
        var biz = await NewBusiness(x.Mgr, "Relance Cible", x.Geo.Commune1);
        await x.Mgr.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = new[] { biz }, userId = x.SalesId });

        (await x.Sales.PostAsJsonAsync("/api/v1/followups", new { businessId = biz, dueDate = Day(1), reason = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Sales.PostAsJsonAsync("/api/v1/followups", new { businessId = biz, assignedUserId = x.OtherId, dueDate = Day(1), reason = "Pour un autre" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var late = (await (await x.Sales.PostAsJsonAsync("/api/v1/followups", new { businessId = biz, dueDate = Day(-3), reason = "Rappeler le gérant", priority = "High" })).OkJson());
        late.GetProperty("overdue").GetBoolean().Should().BeTrue();
        var soon = (await (await x.Sales.PostAsJsonAsync("/api/v1/followups", new { businessId = biz, dueDate = Day(4), reason = "Envoyer la brochure" })).OkJson());
        soon.GetProperty("overdue").GetBoolean().Should().BeFalse();
        await x.Sales.PostAsJsonAsync("/api/v1/followups", new { businessId = biz, dueDate = Day(40), reason = "Loin" });

        (await (await x.Sales.GetAsync("/api/v1/followups?when=overdue")).OkJson()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reason").GetString()).Should().Equal("Rappeler le gérant");
        (await (await x.Sales.GetAsync("/api/v1/followups?when=upcoming")).OkJson()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reason").GetString()).Should().Equal("Envoyer la brochure");
        (await (await x.Other.GetAsync("/api/v1/followups")).OkJson()).GetProperty("total").GetInt32().Should().Be(0);
        var lateId = late.GetProperty("id").GetGuid();
        (await x.Other.PostAsJsonAsync($"/api/v1/followups/{lateId}/complete", new { result = "vol" })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // reminders: created once per day, only for the owner, logged
        using (var scope = f.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<FollowUpReminderService>();
            (await svc.RunAsync()).Should().BeGreaterThanOrEqualTo(1);
            (await svc.RunAsync()).Should().Be(0);
        }

        var mine = await (await x.Sales.GetAsync("/api/v1/notifications")).OkJson();
        mine.GetProperty("unread").GetInt32().Should().Be(1);
        var note = mine.GetProperty("page").GetProperty("items")[0];
        note.GetProperty("kind").GetString().Should().Be("followup.overdue");
        (await (await x.Other.GetAsync("/api/v1/notifications")).OkJson()).GetProperty("unread").GetInt32().Should().Be(0);
        var nid = note.GetProperty("id").GetGuid();
        (await x.Other.PostAsync($"/api/v1/notifications/{nid}/read", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await x.Sales.PostAsync($"/api/v1/notifications/{nid}/read", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await x.Sales.GetAsync("/api/v1/notifications")).OkJson()).GetProperty("unread").GetInt32().Should().Be(0);
        (await (await x.Admin.GetAsync("/api/v1/audit?action=notifications.followups")).OkJson()).GetProperty("total").GetInt32().Should().BeGreaterThan(0);

        // completing with a next due date chains a new follow-up and keeps the old one as history
        (await x.Sales.PostAsJsonAsync($"/api/v1/followups/{lateId}/complete", new { result = "Gérant joint, intéressé", nextDueDate = Day(7) })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await x.Sales.PostAsJsonAsync($"/api/v1/followups/{lateId}/complete", new { result = "bis" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var all = (await (await x.Sales.GetAsync("/api/v1/followups?pageSize=50")).OkJson()).GetProperty("items").EnumerateArray().ToList();
        all.Should().Contain(i => i.GetProperty("status").GetString() == "Done" && i.GetProperty("result").GetString() == "Gérant joint, intéressé");
        all.Should().Contain(i => i.GetProperty("reason").GetString()!.StartsWith("Relance suivante") && i.GetProperty("dueDate").GetString() == Day(7));
        (await (await x.Sales.GetAsync("/api/v1/followups?when=overdue")).OkJson()).GetProperty("total").GetInt32().Should().Be(0);

        // postpone must move forward
        var soonId = soon.GetProperty("id").GetGuid();
        (await x.Sales.PostAsJsonAsync($"/api/v1/followups/{soonId}/postpone", new { newDueDate = Day(2) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Sales.PostAsJsonAsync($"/api/v1/followups/{soonId}/postpone", new { newDueDate = Day(8) })).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Outings_track_planned_and_done_visits_and_expenses_with_clear_permissions()
    {
        var x = await SetupAsync("out");
        var camp = await NewCampaign(x.Mgr, "Sortie camp");
        var biz = await NewBusiness(x.Mgr, "Sortie Cible", x.Geo.Commune1);
        await x.Mgr.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = new[] { biz }, userId = x.SalesId });

        (await x.Sales.PostAsJsonAsync("/api/v1/outings", new { date = Day(1) })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await x.Mgr.PostAsJsonAsync("/api/v1/outings", new { date = Day(1), durationMinutes = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var created = await (await x.Mgr.PostAsJsonAsync("/api/v1/outings", new
        {
            date = Day(1), departureTime = "08:30:00", durationMinutes = 240, startPoint = "Siège", zone = "Rouïba centre", campaignId = camp, participantIds = new[] { x.SalesId },
        })).OkJson();
        var outing = created.GetProperty("id").GetGuid();

        (await x.Other.GetAsync($"/api/v1/outings/{outing}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await x.Sales.GetAsync($"/api/v1/outings/{outing}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var v = (await (await x.Sales.PostAsJsonAsync("/api/v1/visits/plan", new { businessId = biz, scheduledAt = DateTime.UtcNow.AddDays(1), action = "Visit", outingId = outing })).OkJson()).GetProperty("id").GetGuid();
        await x.Sales.PostAsJsonAsync("/api/v1/visits/plan", new { businessId = biz, scheduledAt = DateTime.UtcNow.AddDays(1), action = "Appointment", outingId = outing });
        await x.Sales.PostAsJsonAsync($"/api/v1/visits/{v}/complete", new { comment = "ok" });

        // expenses: planned = managers only, actual = participants; amounts validated
        (await x.Sales.PostAsJsonAsync("/api/v1/outings/expenses", new { outingId = outing, kind = "Planned", category = "Transport", amount = 3000, date = Day(1) })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await x.Mgr.PostAsJsonAsync("/api/v1/outings/expenses", new { outingId = outing, kind = "Planned", category = "Transport", amount = 3000, date = Day(1) })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await x.Sales.PostAsJsonAsync("/api/v1/outings/expenses", new { outingId = outing, kind = "Actual", category = "Transport", amount = 2500.50, date = Day(1), description = "Carburant" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var brochure = (await (await x.Sales.PostAsJsonAsync("/api/v1/outings/expenses", new { outingId = outing, kind = "Actual", category = "Marketing", amount = 1200, date = Day(1), receiptReference = "REC-1" })).OkJson()).GetProperty("id").GetGuid();
        (await x.Sales.PostAsJsonAsync("/api/v1/outings/expenses", new { outingId = outing, kind = "Actual", category = "Other", amount = -1, date = Day(1) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Sales.PostAsJsonAsync("/api/v1/outings/expenses", new { outingId = outing, kind = "Actual", category = "Other", amount = 10.123, date = Day(1) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await x.Other.PostAsJsonAsync("/api/v1/outings/expenses", new { outingId = outing, kind = "Actual", category = "Other", amount = 10, date = Day(1) })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await x.Other.DeleteAsync($"/api/v1/outings/expenses/{brochure}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var detail = await (await x.Mgr.GetAsync($"/api/v1/outings/{outing}")).OkJson();
        detail.GetProperty("visitsPlanned").GetInt32().Should().Be(2);
        detail.GetProperty("visitsDone").GetInt32().Should().Be(1);
        detail.GetProperty("plannedTotal").GetDecimal().Should().Be(3000m);
        detail.GetProperty("actualTotal").GetDecimal().Should().Be(3700.50m);
        detail.GetProperty("actualByCategory").GetProperty("Marketing").GetDecimal().Should().Be(1200m);

        // the campaign aggregates the outing's costs
        var camp2 = await (await x.Mgr.GetAsync($"/api/v1/campaigns/{camp}")).OkJson();
        camp2.GetProperty("expensesActual").GetDecimal().Should().Be(3700.50m);
        camp2.GetProperty("visitsPlanned").GetInt32().Should().Be(2);

        (await x.Mgr.PostAsJsonAsync($"/api/v1/outings/{outing}/close", new { status = "Done", observations = "Bonne journée" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await x.Mgr.PostAsJsonAsync($"/api/v1/outings/{outing}/close", new { status = "Done" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await x.Mgr.PutAsJsonAsync($"/api/v1/outings/{outing}", new { date = Day(2) })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Phase2_ui_pages_are_reachable_with_the_right_permissions()
    {
        var x = await SetupAsync("ui");
        var ui = f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await (await ui.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        (await ui.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "p2-mgr-ui@test.local", ["Password"] = "User#Test2026x", ["__RequestVerificationToken"] = token }))).StatusCode.Should().Be(HttpStatusCode.Redirect);
        var camp = await NewCampaign(x.Mgr, "UI camp");
        var biz = await NewBusiness(x.Mgr, "Cible UI", x.Geo.Commune1);
        await x.Mgr.PostAsJsonAsync($"/api/v1/campaigns/{camp}/targets", new { businessIds = new[] { biz }, assigneeId = x.SalesId });
        var outing = (await (await x.Mgr.PostAsJsonAsync("/api/v1/outings", new { date = Day(1), zone = "Zone UI", campaignId = camp })).OkJson()).GetProperty("id").GetGuid();
        var visit = (await (await x.Mgr.PostAsJsonAsync("/api/v1/visits/plan", new { businessId = biz, scheduledAt = DateTime.UtcNow.AddDays(1), action = "Visit", outingId = outing })).OkJson()).GetProperty("id").GetGuid();
        await x.Mgr.PostAsJsonAsync("/api/v1/followups", new { businessId = biz, dueDate = Day(-1), reason = "Relance UI" });
        foreach (var url in new[] { "/Campaigns", "/Campaigns/Edit", $"/Campaigns/Edit/{camp}", $"/Campaigns/Details/{camp}", "/Outings", "/Outings/Edit", $"/Outings/Details/{outing}", "/Visits",
                     $"/Visits/New?businessId={biz}", $"/Visits/Complete/{visit}", "/FollowUps", "/FollowUps?When=overdue", "/Notifications", $"/Businesses/Details/{biz}", "/Businesses" })
        {
            var res = await ui.GetAsync(url);
            res.StatusCode.Should().Be(HttpStatusCode.OK, url);
        }

        (await (await ui.GetAsync("/FollowUps?When=overdue")).Content.ReadAsStringAsync()).Should().Contain("Relance UI").And.Contain("en retard");
        (await (await ui.GetAsync($"/Businesses/Details/{biz}")).Content.ReadAsStringAsync()).Should().Contain("Visites, appels et relances").And.Contain("Planifier / enregistrer");
    }
}
