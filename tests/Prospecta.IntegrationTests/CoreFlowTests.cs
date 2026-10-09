using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Prospecta.IntegrationTests.Support;

namespace Prospecta.IntegrationTests;

public class CoreFlowTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_is_open_but_api_requires_authentication()
    {
        var anon = f.CreateClient();
        (await anon.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anon.GetAsync("/api/v1/businesses")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_failures_are_generic_and_the_account_locks_after_repeated_attempts()
    {
        await f.CreateUserAsync("lock@test.local", "Salesperson");
        var anon = f.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var bad = await anon.PostAsJsonAsync("/api/v1/auth/login", new { email = "lock@test.local", password = "wrong" });
            bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var locked = await anon.PostAsJsonAsync("/api/v1/auth/login", new { email = "lock@test.local", password = "User#Test2026x" });
        locked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await locked.Json()).GetProperty("message").GetString().Should().Contain("verrouillé");
        var unknown = await anon.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@test.local", password = "x" });
        (await unknown.Json()).GetProperty("message").GetString().Should().Be("Identifiants incorrects.");
    }

    [Fact]
    public async Task Salesperson_cannot_reach_admin_endpoints_and_deactivated_users_lose_access_immediately()
    {
        var (sales, id) = await f.CreateUserAsync("s1@test.local", "Salesperson");
        (await sales.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.GetAsync("/api/v1/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.GetAsync("/api/v1/roles")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.PostAsJsonAsync("/api/v1/geo", new { level = "Wilaya", name = "X" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = await f.ClientAsync();
        (await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { fullName = "S1", isActive = false, role = "Salesperson" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sales.GetAsync("/api/v1/businesses")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Last_active_admin_cannot_be_removed_and_role_permission_changes_apply_immediately()
    {
        var admin = await f.ClientAsync();
        var users = await (await admin.GetAsync("/api/v1/users")).OkJson();
        var adminId = users.GetProperty("items").EnumerateArray().First(u => u.GetProperty("email").GetString() == ApiFactory.AdminEmail).GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/api/v1/users/{adminId}", new { fullName = "A", isActive = true, role = "Salesperson" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var (mgr, _) = await f.CreateUserAsync("m1@test.local", "SalesManager");
        (await mgr.GetAsync("/api/v1/businesses/export?format=csv")).StatusCode.Should().Be(HttpStatusCode.OK);
        var perms = new[] { "Business.View", "Business.ViewAll", "Business.Create", "Business.Edit", "Business.Verify", "Business.Assign", "Business.Import", "Duplicate.Manage" };
        (await admin.PutAsJsonAsync("/api/v1/roles/SalesManager/permissions", perms)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await mgr.GetAsync("/api/v1/businesses/export?format=csv")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PutAsJsonAsync("/api/v1/roles/Admin/permissions", new[] { "Business.View" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await admin.PutAsJsonAsync("/api/v1/roles/SalesManager/permissions", perms.Append("Business.Export"));
    }

    [Fact]
    public async Task Restarting_on_an_existing_database_is_safe_and_changes_nothing()
    {
        var admin = await f.ClientAsync(); // forces the first start-up seeding
        async Task<(int Statuses, int Wilayas, int Roles, int Users, int Categories)> Counts()
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Prospecta.Infrastructure.Persistence.AppDbContext>();
            return (db.StatusValues.Count(), db.GeographicAreas.Count(a => a.Level == Prospecta.Domain.Common.GeoLevel.Wilaya), db.Roles.Count(), db.Users.Count(), db.BusinessCategories.Count());
        }

        var before = await Counts();
        await Prospecta.Infrastructure.Seeding.DataSeeder.RunAsync(f.Services);
        await Prospecta.Infrastructure.Seeding.DataSeeder.RunAsync(f.Services);
        (await Counts()).Should().Be(before);
        before.Wilayas.Should().Be(58);
        (await admin.GetAsync("/api/v1/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Important_actions_are_audited()
    {
        var admin = await f.ClientAsync();
        var geo = await admin.SeedGeo(" audit");
        var created = await admin.CreateBusiness(new { name = "Audit Auto", communeId = geo.Commune1 });
        var id = created.GetProperty("business").GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/v1/businesses/{id}/verify", null);
        var log = await (await admin.GetAsync("/api/v1/audit?pageSize=100")).OkJson();
        var actions = log.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        actions.Should().Contain(["login.success", "geo.create", "business.create", "business.verify"]);
    }
}
