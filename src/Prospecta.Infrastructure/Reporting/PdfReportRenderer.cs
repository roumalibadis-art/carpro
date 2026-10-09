using System.Reflection;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Reporting;

namespace Prospecta.Infrastructure.Reporting;

/// <summary>Resolves the bundled Liberation Sans faces (no system font dependency, accents render everywhere).</summary>
internal sealed class EmbeddedFontResolver : IFontResolver
{
    private const string Family = "Liberation Sans";

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(Family + (bold ? "-Bold" : "") + (italic ? "-Italic" : ""));

    public byte[]? GetFont(string faceName)
    {
        var style = faceName.Replace(Family, "").TrimStart('-');
        var file = style switch { "Bold" => "LiberationSans-Bold.ttf", "Italic" => "LiberationSans-Italic.ttf", "Bold-Italic" => "LiberationSans-BoldItalic.ttf", _ => "LiberationSans-Regular.ttf" };
        var asm = typeof(EmbeddedFontResolver).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(file, StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    internal static string FamilyName => Family;
}

/// <summary>Free PDF generation (PDFsharp/MigraDoc, MIT): cover page, sections, tables, page numbers.</summary>
public sealed class PdfReportRenderer : IReportPdfRenderer
{
    private static readonly object Lock = new();
    private static bool _fontsReady;

    private static void EnsureFonts()
    {
        if (_fontsReady) return;
        lock (Lock)
        {
            if (_fontsReady) return;
            GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
            _fontsReady = true;
        }
    }

    public byte[] Render(ReportDocument d)
    {
        EnsureFonts();
        var doc = new Document { Info = { Title = d.Title, Author = "Prospecta" } };
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.FamilyName; normal.Font.Size = 10;
        normal.ParagraphFormat.SpaceAfter = 4;

        var sec = doc.AddSection();
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.LeftMargin = sec.PageSetup.RightMargin = Unit.FromCentimeter(2);
        sec.PageSetup.TopMargin = Unit.FromCentimeter(2); sec.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        var footer = sec.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 8; footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddText(Clean(d.Title.Length > 70 ? d.Title[..70] + "…" : d.Title) + " — page "); footer.AddPageField(); footer.AddText(" / "); footer.AddNumPagesField();

        // Cover
        var cover = sec.AddParagraph(); cover.Format.SpaceBefore = Unit.FromCentimeter(5);
        var t = cover.AddFormattedText(Clean(d.Title), TextFormat.Bold); t.Size = 24; t.Color = Colors.DarkBlue;
        if (d.Subtitle is not null) { var sp = sec.AddParagraph(Clean(d.Subtitle)); sp.Format.Font.Size = 13; sp.Format.SpaceAfter = Unit.FromCentimeter(1); }
        var meta = sec.AddTable(); meta.Borders.Visible = false; meta.AddColumn(Unit.FromCentimeter(5.5)); meta.AddColumn(Unit.FromCentimeter(11.5));
        foreach (var m in d.Meta)
        {
            var row = meta.AddRow(); row.Cells[0].AddParagraph(Clean(m.Key)).Format.Font.Bold = true; row.Cells[1].AddParagraph(Clean(m.Value));
            row.Cells[0].Format.Font.Color = Colors.DarkGray;
        }

        if (d.Footer is not null) { var f = sec.AddParagraph(Clean(d.Footer)); f.Format.Font.Italic = true; f.Format.Font.Size = 8; f.Format.SpaceBefore = Unit.FromCentimeter(1); }
        sec.AddPageBreak();

        foreach (var s in d.Sections)
        {
            var h = sec.AddParagraph(); h.Format.SpaceBefore = 12; h.Format.KeepWithNext = true;
            var ht = h.AddFormattedText(Clean(s.Heading), TextFormat.Bold); ht.Size = 13; ht.Color = Colors.DarkBlue;
            if (s.Kind == SectionKind.Analysis)
            {
                var tag = sec.AddParagraph("ANALYSE ET RECOMMANDATIONS GÉNÉRÉES AUTOMATIQUEMENT — ce ne sont pas des faits collectés."); tag.Format.Font.Size = 7.5; tag.Format.Font.Color = Colors.DarkOrange; tag.Format.KeepWithNext = true;
            }

            foreach (var b in s.Blocks) Block(sec, b);
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static void Block(Section sec, ReportBlock b)
    {
        switch (b.Type)
        {
            case "p": sec.AddParagraph(Clean(b.Text)); break;
            case "note": { var p = sec.AddParagraph(Clean(b.Text)); p.Format.Font.Italic = true; p.Format.Font.Size = 8.5; p.Format.Font.Color = Colors.DarkGray; break; }
            case "list": foreach (var i in b.Items) { var p = sec.AddParagraph("• " + Clean(i)); p.Format.LeftIndent = Unit.FromCentimeter(0.5); p.Format.FirstLineIndent = Unit.FromCentimeter(-0.4); } break;
            case "kv":
                {
                    var t = sec.AddTable(); t.Borders.Bottom.Width = 0.25; t.Borders.Bottom.Color = Colors.LightGray;
                    t.AddColumn(Unit.FromCentimeter(6)); t.AddColumn(Unit.FromCentimeter(11));
                    foreach (var kv in b.Pairs) { var r = t.AddRow(); r.Cells[0].AddParagraph(Clean(kv.Key)).Format.Font.Color = Colors.DarkGray; r.Cells[1].AddParagraph(Clean(kv.Value)); }
                    sec.AddParagraph().Format.SpaceAfter = 2;
                    break;
                }
            case "table":
                {
                    if (b.Caption is not null) { var c = sec.AddParagraph(Clean(b.Caption)); c.Format.Font.Bold = true; c.Format.Font.Size = 9; c.Format.KeepWithNext = true; c.Format.SpaceBefore = 4; }
                    if (b.Headers.Count == 0) break;
                    var t = sec.AddTable(); t.Borders.Width = 0.25; t.Borders.Color = Colors.LightGray; t.Format.Font.Size = b.Headers.Count > 6 ? 6.5 : b.Headers.Count > 4 ? 7.5 : 8.5;
                    t.Rows.LeftIndent = 0;
                    var width = 17.0 / b.Headers.Count;
                    // Wide "definition" style columns get more room than numeric ones.
                    var weights = b.Headers.Select((h, i) => b.Rows.Count == 0 ? 1.0 : Math.Clamp(Math.Max(b.Headers[i].Length * 0.8, b.Rows.Take(30).Average(r => i < r.Count ? r[i].Length : 0)), 7, 40)).ToList();
                    var sum = weights.Sum();
                    for (var i = 0; i < b.Headers.Count; i++) t.AddColumn(Unit.FromCentimeter(17.0 * weights[i] / sum));
                    var hr = t.AddRow(); hr.HeadingFormat = true; hr.Shading.Color = Colors.DarkBlue; hr.Format.Font.Color = Colors.White; hr.Format.Font.Bold = true;
                    for (var i = 0; i < b.Headers.Count; i++) hr.Cells[i].AddParagraph(Clean(b.Headers[i]));
                    foreach (var rw in b.Rows)
                    {
                        var r = t.AddRow(); r.Format.SpaceAfter = 0;
                        for (var i = 0; i < b.Headers.Count; i++) r.Cells[i].AddParagraph(Clean(i < rw.Count ? rw[i] : ""));
                    }

                    _ = width;
                    sec.AddParagraph().Format.SpaceAfter = 2;
                    break;
                }
        }
    }

    /// <summary>Strips control characters and breaks very long unbroken tokens so layout cannot overflow.</summary>
    private static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        var run = 0;
        foreach (var ch in s)
        {
            if (char.IsControl(ch) && ch is not '\n') continue;
            run = char.IsWhiteSpace(ch) ? 0 : run + 1;
            sb.Append(ch);
            if (run >= 40) { sb.Append('​'); run = 0; }
        }

        return sb.ToString();
    }
}
