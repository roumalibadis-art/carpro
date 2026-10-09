using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prospecta.Application.Security;

namespace Prospecta.IntegrationTests.Support;

/// <summary>Boots the real app over an in-memory SQLite database (one factory per test class = isolated data).</summary>
/// <summary>Stands in for the public Overpass servers: no test ever calls the real internet.</summary>
public sealed class FakeOverpassHandler : HttpMessageHandler
{
    public static Func<HttpRequestMessage, string, HttpResponseMessage> Responder { get; set; } = (_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
    public static List<string> Queries { get; } = [];
    public static int Calls;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Interlocked.Increment(ref Calls);
        lock (Queries) Queries.Add(Uri.UnescapeDataString(body.Replace("+", " ")));
        return Responder(request, body);
    }

    public static HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

/// <summary>Replaces the real page fetcher so URL inspection can be tested without internet.</summary>
public sealed class FakePageFetcher : Prospecta.Application.Collection.IPublicPageFetcher
{
    public static Func<string, Prospecta.Application.Collection.PageFetch> Responder { get; set; } = u => throw new Prospecta.Application.Collection.ConnectorException("Site injoignable (test).");

    public Task<Prospecta.Application.Collection.PageFetch> FetchAsync(string url, CancellationToken ct) => Task.FromResult(Responder(url));
}

public class ApiFactory : WebApplicationFactory<Program>
{
    protected virtual Dictionary<string, string?> ExtraConfig => [];
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin#Test2026";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    /// <summary>Set PROSPECTA_TEST_MYSQL='Server=...;User=...;Password=...;' to run the whole suite on real MySQL (one throw-away database per class).</summary>
    private static readonly string? MySqlBase = Environment.GetEnvironmentVariable("PROSPECTA_TEST_MYSQL");
    private readonly string _mysqlDb = "prospecta_test_" + Guid.NewGuid().ToString("N")[..12];

    public ApiFactory()
    {
        _connection.Open();
        if (MySqlBase is not null)
        {
            // Minimal hosting reads these before the factory's configuration callbacks run, so they go through the environment.
            Environment.SetEnvironmentVariable("Database__Provider", "MySql");
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"{MySqlBase}Database={_mysqlDb};");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = MySqlBase is null ? "Sqlite" : "MySql",
            ["ConnectionStrings:Default"] = MySqlBase is null ? "DataSource=:memory:" : $"{MySqlBase}Database={_mysqlDb};",
            ["Seed:DemoData"] = "false",
            ["Bootstrap:AdminEmail"] = AdminEmail,
            ["Bootstrap:AdminPassword"] = AdminPassword,
            ["Jwt:Secret"] = "integration-tests-secret-key-0123456789-abcdef",
            ["RateLimit:LoginPerMinute"] = "1000",
            ["Notifications:Enabled"] = "false",
            ["Collection:Osm:MinSecondsBetweenCalls"] = "0",
        }).AddInMemoryCollection(ExtraConfig));
        builder.ConfigureServices(s =>
        {
            s.AddHttpClient("overpass").ConfigurePrimaryHttpMessageHandler(() => new FakeOverpassHandler());
            s.AddSingleton<Prospecta.Application.Collection.IPublicPageFetcher, FakePageFetcher>();
        });
        if (MySqlBase is not null) return;
        builder.ConfigureServices(s =>
        {
            // Reuse the single open connection so the in-memory database survives across scopes.
            var desc = s.Where(d => d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<Prospecta.Infrastructure.Persistence.AppDbContext>)).ToList();
            foreach (var d in desc) s.Remove(d);
            s.AddDbContext<Prospecta.Infrastructure.Persistence.AppDbContext>(o => o.UseSqliteConnection(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _connection.Dispose();
        if (MySqlBase is not null)
        {
            using var c = new MySqlConnector.MySqlConnection(MySqlBase);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{_mysqlDb}`";
            cmd.ExecuteNonQuery();
        }
    }

    public async Task<HttpClient> ClientAsync(string email = AdminEmail, string password = AdminPassword)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("token").GetString());
        return client;
    }

    /// <summary>Creates a user through the admin API and returns an authenticated client for it.</summary>
    public async Task<(HttpClient Client, Guid Id)> CreateUserAsync(string email, string role, Guid? managerId = null, string password = "User#Test2026x")
    {
        var admin = await ClientAsync();
        var res = await admin.PostAsJsonAsync("/api/v1/users", new { email, fullName = "User " + email, password, role, managerId });
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (await ClientAsync(email, password), id);
    }
}

internal static class SqliteExt
{
    public static Microsoft.EntityFrameworkCore.DbContextOptionsBuilder UseSqliteConnection(this Microsoft.EntityFrameworkCore.DbContextOptionsBuilder o, SqliteConnection c) =>
        Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(o, c);
}
