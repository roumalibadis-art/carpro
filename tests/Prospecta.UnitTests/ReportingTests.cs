using ClosedXML.Excel;
using FluentAssertions;
using Prospecta.Application.Prospecting;
using Prospecta.Application.Reporting;
using Prospecta.Infrastructure.Reporting;
using static Prospecta.Application.Reporting.Doc;

namespace Prospecta.UnitTests;

public class ReportingRenderingTests
{
    private static ReportDocument Sample(int rows = 3)
    {
        var d = new ReportDocument { Title = "Étude de marché — Rouïba & Réghaïa", Subtitle = "Données de démonstration", Footer = "Pied de page" };
        d.Meta.Add(new("Auteur", "Amine Bélaïd"));
        d.Sections.Add(Section("Faits", SectionKind.Facts, P("Texte avec accents éàùç et un mot très long " + new string('x', 200)), List(["un", "deux"]), Kv(("Clé", "Valeur")),
            Table("Tableau", ["Entreprise", "Téléphone", "Statut"], Enumerable.Range(0, rows).Select(i => new[] { $"Entreprise {i} =HYPERLINK(\"x\")", "0555 12 34 56", "Vérifié" }))));
        d.Sections.Add(Section("Analyse", SectionKind.Analysis, Note("Générée")));
        return d;
    }

    [Fact]
    public void Pdf_is_generated_with_accents_long_tokens_and_many_pages()
    {
        var bytes = new PdfReportRenderer().Render(Sample(400));
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        bytes.Length.Should().BeGreaterThan(10_000);
        new PdfReportRenderer().Render(new ReportDocument { Title = "Vide" }).Length.Should().BeGreaterThan(500); // empty documents still render
    }

    [Fact]
    public void Excel_export_has_one_sheet_per_table_and_neutralises_formulas()
    {
        using var wb = new XLWorkbook(new MemoryStream(ReportService.ToXlsx(Sample())));
        wb.Worksheets.Select(w => w.Name).Should().Contain("Résumé").And.Contain("Tableau");
        var cell = wb.Worksheet("Tableau").Cell(2, 1);
        cell.DataType.Should().Be(XLDataType.Text);
        cell.GetString().Should().Be("Entreprise 0 =HYPERLINK(\"x\")"); // text, not a formula
        ReportService.ToXlsx(new ReportDocument { Title = "=1+1" }).Should().NotBeEmpty();
    }
}

public class DatesTests
{
    [Fact]
    public void Algerian_calendar_day_is_utc_plus_one()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 3, 1, 23, 30, 0, TimeSpan.Zero)); // 00:30 on 2 March in Algiers
        Dates.Today(clock).Should().Be(new DateOnly(2026, 3, 2));
        Dates.LocalToUtc(new DateTime(2026, 3, 2, 0, 0, 0)).Should().Be(new DateTime(2026, 3, 1, 23, 0, 0));
        Dates.UtcToLocal(new DateTime(2026, 3, 1, 23, 0, 0)).Should().Be(new DateTime(2026, 3, 2, 0, 0, 0));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
