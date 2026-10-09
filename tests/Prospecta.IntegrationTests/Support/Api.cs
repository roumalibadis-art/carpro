using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Prospecta.IntegrationTests.Support;

public static class Api
{
    public static async Task<JsonElement> Json(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<JsonElement> OkJson(this HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"HTTP {(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<Guid> WilayaId(this HttpClient c, string code = "16") =>
        (await (await c.GetAsync("/api/v1/geo?level=Wilaya")).OkJson()).EnumerateArray().First(x => x.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

    public static async Task<Guid> CreateGeo(this HttpClient c, string level, string name, Guid? parent)
    {
        var r = await c.PostAsJsonAsync("/api/v1/geo", new { level, name, code = "", parentId = parent, isActive = true });
        return (await r.OkJson()).GetProperty("id").GetGuid();
    }

    /// <summary>Alger → daïra → two communes. Returns the ids.</summary>
    public static async Task<(Guid Wilaya, Guid Daira, Guid Commune1, Guid Commune2)> SeedGeo(this HttpClient admin, string suffix = "")
    {
        var w = await admin.WilayaId();
        var d = await admin.CreateGeo("Daira", "Daïra Test" + suffix, w);
        var c1 = await admin.CreateGeo("Commune", "Rouïba" + suffix, d);
        var c2 = await admin.CreateGeo("Commune", "Réghaïa" + suffix, d);
        return (w, d, c1, c2);
    }

    public static async Task<Guid> CategoryId(this HttpClient c, string name, bool root = true)
    {
        var all = await (await c.GetAsync("/api/v1/categories")).OkJson();
        return all.EnumerateArray().First(x => x.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> StatusId(this HttpClient c, string kind, string code)
    {
        var all = await (await c.GetAsync($"/api/v1/statuses?kind={kind}")).OkJson();
        return all.EnumerateArray().First(x => x.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> CreateBusiness(this HttpClient c, object input)
    {
        var r = await c.PostAsJsonAsync("/api/v1/businesses", input);
        return await r.OkJson();
    }

    public static MultipartFormDataContent File(string name, string content) => File(name, Encoding.UTF8.GetBytes(content));

    public static MultipartFormDataContent File(string name, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        form.Add(part, "file", name);
        return form;
    }

    public static async Task<HttpStatusCode> Status(this Task<HttpResponseMessage> t) => (await t).StatusCode;
}
