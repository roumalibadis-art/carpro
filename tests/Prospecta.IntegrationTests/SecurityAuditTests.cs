using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Prospecta.IntegrationTests.Support;

namespace Prospecta.IntegrationTests;

public class LowRateLimitFactory : ApiFactory
{
    protected override Dictionary<string, string?> ExtraConfig => new() { ["RateLimit:LoginPerMinute"] = "3" };
}

/// <summary>Systematic authorization / injection / disclosure checks (PRD security audit).</summary>
public class SecurityAuditTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static string Fill(string route) => System.Text.RegularExpressions.Regex.Replace(route, @"\{(\w+)(:[^}]+)?\??\}", m => m.Groups[2].Value.Contains("guid") ? Guid.NewGuid().ToString() : "x");

    private List<(string Method, string Path)> ApiEndpoints() => f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
        .Where(e => e.RoutePattern.RawText!.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (m, "/" + Fill(e.RoutePattern.RawText!)))).ToList();

    [Fact]
    public async Task Every_api_endpoint_rejects_anonymous_callers_except_login()
    {
        var anon = f.CreateClient();
        var endpoints = ApiEndpoints();
        endpoints.Count.Should().BeGreaterThan(90, "the sweep must really cover the API surface");
        var failures = new List<string>();
        foreach (var (method, path) in endpoints)
        {
            if (method == "POST" && path == "/api/v1/auth/login") continue;
            var res = await anon.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = method is "GET" or "DELETE" ? null : JsonContent.Create(new { }) });
            if (res.StatusCode != HttpStatusCode.Unauthorized) failures.Add($"{method} {path} -> {(int)res.StatusCode}");
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public async Task A_salesperson_is_refused_on_every_management_endpoint()
    {
        var (sales, _) = await f.CreateUserAsync("sec-sales@test.local", "Salesperson");
        var denied = new[]
        {
            ("GET", "/api/v1/users"), ("POST", "/api/v1/users"), ("GET", "/api/v1/roles"), ("PUT", "/api/v1/roles/Admin/permissions"), ("GET", "/api/v1/audit"), ("POST", "/api/v1/geo"), ("POST", "/api/v1/categories"),
            ("POST", "/api/v1/statuses"), ("GET", "/api/v1/duplicates"), ("POST", "/api/v1/duplicates/rescan"), ("POST", "/api/v1/campaigns"), ("POST", "/api/v1/outings"), ("GET", "/api/v1/collection/connectors"),
            ("POST", "/api/v1/collection/search"), ("GET", "/api/v1/data/deleted"), ("DELETE", $"/api/v1/data/businesses/{Guid.NewGuid()}"), ("POST", "/api/v1/data/purge-older-than"),
            ("GET", "/api/v1/businesses/export"), ("GET", "/api/v1/imports"), ("POST", "/api/v1/businesses/assign"), ("GET", "/api/v1/reports?team=true"),
            ("POST", "/api/v1/reports/preview"), // Manager report type is checked inside; a bare body is refused as invalid, never executed
        };
        var failures = new List<string>();
        foreach (var (m, p) in denied)
        {
            var res = await sales.SendAsync(new HttpRequestMessage(new HttpMethod(m), p) { Content = m is "GET" or "DELETE" ? null : JsonContent.Create(new { }) });
            if (res.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.BadRequest) || (p != "/api/v1/reports/preview" && res.StatusCode != HttpStatusCode.Forbidden)) failures.Add($"{m} {p} -> {(int)res.StatusCode}");
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Forged_or_foreign_tokens_are_rejected()
    {
        var admin = await f.ClientAsync();
        var tokenText = admin.DefaultRequestHeaders.Authorization!.Parameter!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokenText);

        async Task<HttpStatusCode> With(string token) { var c = f.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return (await c.GetAsync("/api/v1/auth/me")).StatusCode; }
        (await With(tokenText)).Should().Be(HttpStatusCode.OK);
        (await With(tokenText[..^3] + "abc")).Should().Be(HttpStatusCode.Unauthorized); // signature altered
        var parts = tokenText.Split('.');
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        (await With($"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{parts[1]}.")).Should().Be(HttpStatusCode.Unauthorized); // alg=none
        var foreign = new JwtSecurityToken(jwt.Issuer, jwt.Audiences.First(), jwt.Claims, expires: DateTime.UtcNow.AddHours(1), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("another-secret-key-0123456789-abcdefghij")), SecurityAlgorithms.HmacSha256));
        (await With(new JwtSecurityTokenHandler().WriteToken(foreign))).Should().Be(HttpStatusCode.Unauthorized); // right claims, wrong key
        (await With("not.a.jwt")).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Responses_carry_hardening_headers_and_leak_nothing()
    {
        var anon = f.CreateClient();
        var login = await anon.GetAsync("/Account/Login");
        login.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        login.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        var csp = login.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("default-src 'self'").And.Contain("frame-ancestors 'none'").And.NotContain("unsafe-eval").And.NotContain("script-src 'self' 'unsafe-inline'");
        login.Headers.Contains("Server").Should().BeFalse();
        (await login.Content.ReadAsStringAsync()).Should().NotContain("<script>").And.NotMatchRegex(@"https?://[^""']*(unpkg|cdnjs|jsdelivr|googleapis|gstatic)");

        var admin = await f.ClientAsync();
        var bad = await admin.PostAsync("/api/v1/businesses", new StringContent("{ not json", Encoding.UTF8, "application/json"));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await bad.Content.ReadAsStringAsync();
        body.Should().NotContain(" at Prospecta").And.NotContain("System.").And.NotContain("Exception").And.Contain("\"success\":false");
        (await admin.GetAsync("/api/v1/businesses/not-a-guid")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var cookie = await f.CreateClient().PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>())); // no antiforgery token: refused, no session
        cookie.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        cookie.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Injection_attempts_are_inert_and_stored_markup_is_encoded()
    {
        var admin = await f.ClientAsync();
        var g = await admin.SeedGeo(" sec");
        await admin.CreateBusiness(new { name = "Sécurité Un", communeId = g.Commune1 });
        await admin.CreateBusiness(new { name = "<script>alert('xss')</script> Deux", communeId = g.Commune1, description = "<img src=x onerror=alert(1)>" });
        foreach (var attack in new[] { "' OR '1'='1", "'; DROP TABLE Businesses;--", "%", "_", "\\", "\" OR \"\"=\"", "{{7*7}}", "<script>", "ａｄｍｉｎ" })
        {
            var res = await admin.GetAsync($"/api/v1/businesses?search={Uri.EscapeDataString(attack)}&sortBy={Uri.EscapeDataString(attack)}");
            res.StatusCode.Should().Be(HttpStatusCode.OK, attack);
            var total = (await res.OkJson()).GetProperty("total").GetInt32();
            total.Should().BeLessThanOrEqualTo(attack == "<script>" ? 1 : 0, $"'{attack}' must not match everything");
        }

        (await (await admin.GetAsync("/api/v1/businesses?search=S%C3%A9curit%C3%A9")).OkJson()).GetProperty("total").GetInt32().Should().Be(1); // the table still exists

        // stored markup is HTML-encoded in every page that shows it
        var ui = f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await (await ui.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var cookieRes = await ui.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = ApiFactory.AdminEmail, ["Password"] = ApiFactory.AdminPassword, ["__RequestVerificationToken"] = token }));
        cookieRes.Headers.GetValues("Set-Cookie").Should().Contain(c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
        var id = (await (await admin.GetAsync("/api/v1/businesses?search=xss")).OkJson()).GetProperty("items")[0].GetProperty("id").GetGuid();
        foreach (var page in new[] { $"/Businesses?Filter.Search=xss", $"/Businesses/Details/{id}", $"/Businesses/Edit/{id}", "/Admin/Audit" })
        {
            var text = await (await ui.GetAsync(page)).Content.ReadAsStringAsync();
            text.Should().NotContain("<script>alert").And.NotContain("<img src=x", page);
        }

        var report = await (await admin.PostAsJsonAsync("/api/v1/reports", new { type = "MarketStudy", parameters = new { filter = new { search = "xss" } } })).OkJson();
        var viewHtml = await (await ui.GetAsync($"/Reports/View/{report.GetProperty("id").GetGuid()}")).Content.ReadAsStringAsync();
        viewHtml.Should().NotContain("<script>alert").And.Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task Over_posting_cannot_set_server_controlled_fields()
    {
        var admin = await f.ClientAsync();
        var res = await admin.PostAsJsonAsync("/api/v1/businesses", new { name = "Over Post", isDeleted = true, id = Guid.NewGuid(), censusStatusId = Guid.NewGuid(), createdByUserId = Guid.NewGuid(), isDemo = true, completenessPercent = 100, lastVerifiedAt = DateTime.UtcNow });
        var b = (await res.OkJson()).GetProperty("business");
        b.GetProperty("isDemo").GetBoolean().Should().BeFalse();
        b.GetProperty("lastVerifiedAt").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        b.GetProperty("completeness").GetInt32().Should().BeLessThan(100);
        (await admin.GetAsync($"/api/v1/businesses/{b.GetProperty("id").GetGuid()}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Purge_erases_a_deleted_business_and_everything_attached_and_leaves_a_trace()
    {
        var admin = await f.ClientAsync();
        var (mgr, mgrId) = await f.CreateUserAsync("purge-mgr@test.local", "SalesManager");
        var g = await admin.SeedGeo(" purge");
        var id = (await admin.CreateBusiness(new { name = "À effacer", communeId = g.Commune1, phone = "0555 77 66 55" })).GetProperty("business").GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync("/api/v1/businesses/assign", new { businessIds = new[] { id }, userId = mgrId });
        var v = (await (await mgr.PostAsJsonAsync("/api/v1/visits/plan", new { businessId = id, scheduledAt = DateTime.UtcNow.AddHours(2), action = "Visit" })).OkJson()).GetProperty("id").GetGuid();
        await mgr.PostAsJsonAsync($"/api/v1/visits/{v}/complete", new { contactMet = "Nom Personnel", nextFollowUpDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"), nextAction = "x" });

        (await admin.DeleteAsync($"/api/v1/data/businesses/{id}")).StatusCode.Should().Be(HttpStatusCode.Conflict); // not deleted yet: cannot be purged
        (await mgr.GetAsync("/api/v1/data/deleted")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/businesses/{id}") { Content = JsonContent.Create(new { reason = "demande d'effacement" }) });
        (await (await admin.GetAsync("/api/v1/data/deleted")).OkJson()).GetProperty("items").EnumerateArray().Should().Contain(d => d.GetProperty("id").GetGuid() == id && d.GetProperty("visits").GetInt32() == 1);
        (await admin.PostAsync("/api/v1/data/purge-older-than?days=5", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest); // retention floor
        (await admin.DeleteAsync($"/api/v1/data/businesses/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Prospecta.Infrastructure.Persistence.AppDbContext>();
        db.Businesses.IgnoreQueryFilters().Any(b => b.Id == id).Should().BeFalse();
        db.Visits.Any(x => x.BusinessId == id).Should().BeFalse();
        db.FollowUps.Any(x => x.BusinessId == id).Should().BeFalse();
        db.BusinessHistory.Any(x => x.BusinessId == id).Should().BeFalse();
        db.BusinessSources.Any(x => x.BusinessId == id).Should().BeFalse();
        db.BusinessAssignments.Any(x => x.BusinessId == id).Should().BeFalse();
        var trace = await (await admin.GetAsync("/api/v1/audit?action=data.purge")).OkJson();
        trace.GetProperty("items").EnumerateArray().Should().Contain(a => a.GetProperty("entityId").GetString() == id.ToString());
        trace.ToString().Should().NotContain("0555 77 66 55").And.NotContain("Nom Personnel");
    }
}

public class LoginRateLimitTests(LowRateLimitFactory f) : IClassFixture<LowRateLimitFactory>
{
    [Fact]
    public async Task Repeated_login_attempts_from_one_address_are_throttled()
    {
        var anon = f.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) codes.Add((await anon.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@test.local", password = "x" })).StatusCode);
        codes.Take(3).Should().OnlyContain(c => c == HttpStatusCode.Unauthorized);
        codes.Skip(3).Should().Contain(HttpStatusCode.TooManyRequests);
    }
}
