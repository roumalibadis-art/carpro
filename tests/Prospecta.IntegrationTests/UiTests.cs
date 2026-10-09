using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Prospecta.IntegrationTests.Support;

namespace Prospecta.IntegrationTests;

/// <summary>Exercises the server-rendered Razor UI with a real cookie session (login, antiforgery, permissions).</summary>
public class UiTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private HttpClient NewClient() => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> TokenAsync(HttpClient c, string url)
    {
        var html = await (await c.GetAsync(url)).Content.ReadAsStringAsync();
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        m.Success.Should().BeTrue($"{url} should contain an antiforgery token");
        return m.Groups[1].Value;
    }

    private async Task<HttpClient> LoginAsync(string email, string password)
    {
        var c = NewClient();
        var token = await TokenAsync(c, "/Account/Login");
        var res = await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = email, ["Password"] = password, ["__RequestVerificationToken"] = token }));
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return c;
    }

    [Fact]
    public async Task Anonymous_visitors_are_sent_to_the_login_page_and_bad_credentials_show_a_clear_message()
    {
        var anon = NewClient();
        var r = await anon.GetAsync("/Businesses");
        r.StatusCode.Should().Be(HttpStatusCode.Redirect);
        r.Headers.Location!.ToString().Should().Contain("/Account/Login");
        (await anon.GetAsync("/Admin/Users")).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var token = await TokenAsync(anon, "/Account/Login");
        var bad = await anon.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = ApiFactory.AdminEmail, ["Password"] = "nope", ["__RequestVerificationToken"] = token }));
        bad.StatusCode.Should().Be(HttpStatusCode.OK);
        (await bad.Content.ReadAsStringAsync()).Should().Contain("Identifiants incorrects");
        // POST without antiforgery token is rejected
        (await anon.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "a", ["Password"] = "b" }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Every_admin_page_renders_and_the_business_form_creates_a_record()
    {
        var admin = await f.ClientAsync();
        var geo = await admin.SeedGeo(" ui");
        var existing = (await admin.CreateBusiness(new { name = "Fiche UI", communeId = geo.Commune1 })).GetProperty("business").GetProperty("id").GetGuid();

        var ui = await LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        foreach (var url in new[] { "/", "/Businesses", $"/Businesses?Filter.WilayaId={geo.Wilaya}&Filter.DairaId={geo.Daira}&Filter.CommuneId={geo.Commune1}&Filter.HasPhone=false",
                     "/Businesses/Edit", $"/Businesses/Edit/{existing}", $"/Businesses/Details/{existing}", "/Imports", "/Duplicates", "/Admin/Users", "/Admin/Roles",
                     "/Admin/Geography", $"/Admin/Geography?WilayaId={geo.Wilaya}&DairaId={geo.Daira}", "/Admin/Categories", "/Admin/Statuses", "/Admin/Audit" })
        {
            var res = await ui.GetAsync(url);
            res.StatusCode.Should().Be(HttpStatusCode.OK, url);
            (await res.Content.ReadAsStringAsync()).Should().Contain("Prospecta", url);
        }

        var list = await (await ui.GetAsync($"/Businesses?Filter.CommuneId={geo.Commune1}&Filter.DairaId={geo.Daira}&Filter.WilayaId={geo.Wilaya}")).Content.ReadAsStringAsync();
        list.Should().Contain("Fiche UI").And.Contain("Informations partielles");

        // the dependent dropdown helper only serves the requested level
        var communes = await (await ui.GetAsync($"/ui/geo?level=Commune&parentId={geo.Daira}")).Content.ReadAsStringAsync();
        communes.Should().Contain("Rouïba ui").And.Contain("Réghaïa ui");

        var token = await TokenAsync(ui, "/Businesses/Edit");
        var post = await ui.PostAsync("/Businesses/Edit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Name"] = "Créée via le formulaire", ["Input.CommuneId"] = geo.Commune2.ToString(), ["Input.Phone"] = "0555 77 88 99", ["Input.Priority"] = "High", ["__RequestVerificationToken"] = token,
        }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.ToString().Should().Contain("/Businesses/Details/");
        var detail = await (await ui.GetAsync(post.Headers.Location)).Content.ReadAsStringAsync();
        detail.Should().Contain("Créée via le formulaire").And.Contain("0555 77 88 99").And.Contain("Historique des modifications");

        // validation errors come back on the form, not as an error page
        var token2 = await TokenAsync(ui, "/Businesses/Edit");
        var invalid = await ui.PostAsync("/Businesses/Edit", new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Name"] = "", ["Input.Phone"] = "123", ["__RequestVerificationToken"] = token2 }));
        invalid.StatusCode.Should().Be(HttpStatusCode.OK);
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("obligatoire");
    }

    [Fact]
    public async Task Ui_enforces_the_same_permissions_as_the_api()
    {
        await f.CreateUserAsync("ui-sales@test.local", "Salesperson");
        var sales = await LoginAsync("ui-sales@test.local", "User#Test2026x");
        (await sales.GetAsync("/Businesses")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var url in new[] { "/Admin/Users", "/Admin/Roles", "/Admin/Audit", "/Duplicates", "/Imports" })
        {
            var res = await sales.GetAsync(url);
            // a fallback-authenticated user without the permission must never see the page
            res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
        }

        var home = await (await sales.GetAsync("/")).Content.ReadAsStringAsync();
        home.Should().NotContain("/Admin/Users").And.NotContain("/Duplicates");
        (await sales.GetAsync("/Businesses?handler=Export&format=csv")).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
    }
}
