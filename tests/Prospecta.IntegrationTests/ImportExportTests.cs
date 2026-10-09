using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using Prospecta.IntegrationTests.Support;

namespace Prospecta.IntegrationTests;

public class ImportExportTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static string Csv(params string[] lines) => string.Join("\n", lines);

    private async Task<(HttpClient Admin, Guid Commune)> SetupAsync(string commune)
    {
        var admin = await f.ClientAsync();
        var w = await admin.WilayaId();
        var d = await admin.CreateGeo("Daira", "Daïra " + commune, w);
        var c = await admin.CreateGeo("Commune", commune, d);
        return (admin, c);
    }

    [Fact]
    public async Task Csv_import_previews_flags_invalid_and_duplicate_rows_and_only_imports_valid_ones_after_confirmation()
    {
        var (admin, commune) = await SetupAsync("ImpCommuneA");
        await admin.CreateBusiness(new { name = "Existante Auto", communeId = commune, phone = "0555 20 20 99" });

        var csv = Csv(
            "Nom;Téléphone;Commune;Wilaya;Activité;Site web",
            "Import Valide Un;0555 20 20 01;ImpCommuneA;Alger;Location de véhicules;https://un.example.dz",
            ";0555 20 20 02;ImpCommuneA;Alger;;",
            "Import Tel Faux;12345;ImpCommuneA;Alger;;",
            "Import Commune Inconnue;0555 20 20 04;Nullepart;Alger;;",
            "Import Valide Cinq;0555 20 20 05;ImpCommuneA;Alger;Santé;",
            "Import Valide Un;0555 20 20 01;ImpCommuneA;Alger;;",
            "Existante Auto SARL;0555 20 20 99;ImpCommuneA;Alger;;",
            "=HYPERLINK(\"http://evil\");0555 20 20 08;ImpCommuneA;Alger;;");

        var batch = await (await admin.PostAsync("/api/v1/imports", Api.File("clients.csv", csv))).OkJson();
        var id = batch.GetProperty("id").GetGuid();
        batch.GetProperty("totalRows").GetInt32().Should().Be(8);
        batch.GetProperty("mapping").GetProperty("name").GetString().Should().Be("Nom"); // columns auto-suggested
        (await admin.PostAsync($"/api/v1/imports/{id}/commit", JsonContent.Create(new { }))).StatusCode.Should().Be(HttpStatusCode.Conflict); // not mapped yet

        var mapped = await (await admin.PutAsJsonAsync($"/api/v1/imports/{id}/mapping", batch.GetProperty("mapping"))).OkJson();
        mapped.GetProperty("validRows").GetInt32().Should().Be(3);        // 2 good rows + the formula-looking name (a valid name, neutralised on export)
        mapped.GetProperty("invalidRows").GetInt32().Should().Be(3);      // no name, bad phone, unknown commune
        mapped.GetProperty("duplicateRows").GetInt32().Should().Be(2);    // in-file duplicate + look-alike of an existing record

        var invalid = await (await admin.GetAsync($"/api/v1/imports/{id}/rows?status=Invalid")).OkJson();
        invalid.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("errors").GetString()).Should()
            .Contain(e => e!.Contains("Nom commercial manquant")).And.Contain(e => e!.Contains("Téléphone invalide")).And.Contain(e => e!.Contains("Commune inconnue"));
        invalid.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("rowNumber").GetInt32()).Should().Contain([3, 4, 5]);

        // nothing exists before confirmation
        (await admin.GetAsync("/api/v1/businesses?search=import valide")).OkJson().Result.GetProperty("total").GetInt32().Should().Be(0);

        var done = await (await admin.PostAsJsonAsync($"/api/v1/imports/{id}/commit", new { includePotentialDuplicates = false })).OkJson();
        done.GetProperty("importedRows").GetInt32().Should().Be(3);
        done.GetProperty("status").GetString().Should().Be("Committed");
        (await admin.PostAsJsonAsync($"/api/v1/imports/{id}/commit", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict); // no double import

        var found = await (await admin.GetAsync("/api/v1/businesses?search=import valide&pageSize=50")).OkJson();
        found.GetProperty("total").GetInt32().Should().Be(2);
        var detail = await (await admin.GetAsync($"/api/v1/businesses/{found.GetProperty("items")[0].GetProperty("id").GetGuid()}")).OkJson();
        detail.GetProperty("sources")[0].GetProperty("sourceType").GetString().Should().Be("FileImport");
        detail.GetProperty("origins").GetProperty("Name").GetString().Should().Be("External");

        // the exported file neutralises spreadsheet formulas
        var export = await (await admin.GetAsync("/api/v1/businesses/export?format=csv&search=hyperlink")).Content.ReadAsStringAsync();
        export.Should().Contain("'=HYPERLINK").And.NotContain(";=HYPERLINK");
    }

    [Fact]
    public async Task Xlsx_import_works_and_numeric_phone_cells_keep_working()
    {
        var (admin, _) = await SetupAsync("ImpCommuneX");
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Feuil1");
            ws.Cell(1, 1).Value = "Nom commercial"; ws.Cell(1, 2).Value = "Tel"; ws.Cell(1, 3).Value = "Commune";
            ws.Cell(2, 1).Value = "Xlsx Entreprise"; ws.Cell(2, 2).Value = 555123456; ws.Cell(2, 3).Value = "ImpCommuneX"; // Excel drops the leading zero
            wb.SaveAs(ms);
        }

        var batch = await (await admin.PostAsync("/api/v1/imports", Api.File("liste.xlsx", ms.ToArray()))).OkJson();
        var mapped = await (await admin.PutAsJsonAsync($"/api/v1/imports/{batch.GetProperty("id").GetGuid()}/mapping", batch.GetProperty("mapping"))).OkJson();
        mapped.GetProperty("validRows").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Malformed_or_hostile_files_are_rejected_cleanly()
    {
        var admin = await f.ClientAsync();
        (await admin.PostAsync("/api/v1/imports", Api.File("virus.exe", "MZ"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsync("/api/v1/imports", Api.File("vide.csv", ""))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsync("/api/v1/imports", Api.File("entetes.csv", "Nom;Nom\nA;B"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsync("/api/v1/imports", Api.File("faux.xlsx", Encoding.UTF8.GetBytes("ceci n'est pas un classeur")))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsync("/api/v1/imports", Api.File("gros.csv", new string('a', 6 * 1024 * 1024)))).StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge);
        var ok = await (await admin.PostAsync("/api/v1/imports", Api.File("x.csv", "Colonne;Autre\n1;2"))).OkJson();
        (await admin.PutAsJsonAsync($"/api/v1/imports/{ok.GetProperty("id").GetGuid()}/mapping", new Dictionary<string, string> { ["phone"] = "Colonne" })).StatusCode.Should().Be(HttpStatusCode.BadRequest); // name is required
    }

    [Fact]
    public async Task Import_batches_belong_to_their_uploader()
    {
        var (mgr1, _) = await f.CreateUserAsync("imp1@test.local", "SalesManager");
        var (mgr2, _) = await f.CreateUserAsync("imp2@test.local", "SalesManager");
        var (sales, _) = await f.CreateUserAsync("imp3@test.local", "Salesperson");
        var batch = await (await mgr1.PostAsync("/api/v1/imports", Api.File("p.csv", "Nom\nPrivé SARL"))).OkJson();
        var id = batch.GetProperty("id").GetGuid();
        (await mgr2.GetAsync($"/api/v1/imports/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await mgr2.GetAsync($"/api/v1/imports/{id}/rows")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await mgr2.PostAsJsonAsync($"/api/v1/imports/{id}/commit", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.PostAsync("/api/v1/imports", Api.File("p.csv", "Nom\nX"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await (await mgr1.GetAsync("/api/v1/imports")).OkJson()).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Exports_respect_the_selected_filters_and_the_callers_scope()
    {
        var (admin, c) = await SetupAsync("ExpCommune");
        var (mgr, _) = await f.CreateUserAsync("exp-mgr@test.local", "SalesManager");
        await admin.CreateBusiness(new { name = "Export Avec Tel", communeId = c, phone = "0555 40 40 01" });
        await admin.CreateBusiness(new { name = "Export Sans Tel", communeId = c });

        var csv = await (await mgr.GetAsync($"/api/v1/businesses/export?format=csv&communeId={c}&hasPhone=true")).Content.ReadAsStringAsync();
        csv.Should().Contain("Export Avec Tel").And.NotContain("Export Sans Tel");
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2); // header + 1 row

        var xlsx = await (await mgr.GetAsync($"/api/v1/businesses/export?format=xlsx&communeId={c}")).Content.ReadAsByteArrayAsync();
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        wb.Worksheet(1).LastRowUsed()!.RowNumber().Should().Be(3);
        (await mgr.GetAsync("/api/v1/businesses/export?format=pdf")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var audit = await (await admin.GetAsync("/api/v1/audit?action=business.export")).OkJson();
        audit.GetProperty("total").GetInt32().Should().BeGreaterThan(0);
    }
}
