namespace Prospecta.Application.Reporting;

public enum SectionKind
{
    /// <summary>Collected, countable facts.</summary>
    Facts = 1,
    /// <summary>Generated reading of the facts or recommendations: always labelled as such.</summary>
    Analysis = 2,
}

public sealed class ReportBlock
{
    /// <summary>"p" paragraph, "kv" key/values, "table", "list", "note".</summary>
    public string Type { get; set; } = "p";
    public string? Text { get; set; }
    public string? Caption { get; set; }
    public List<string> Items { get; set; } = [];
    public List<KeyValuePair<string, string>> Pairs { get; set; } = [];
    public List<string> Headers { get; set; } = [];
    public List<List<string>> Rows { get; set; } = [];
}

public sealed class ReportSection
{
    public string Heading { get; set; } = string.Empty;
    public SectionKind Kind { get; set; } = SectionKind.Facts;
    public List<ReportBlock> Blocks { get; set; } = [];
}

/// <summary>Renderer-agnostic report: the same document feeds the HTML preview, the PDF and the Excel export.</summary>
public sealed class ReportDocument
{
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public List<KeyValuePair<string, string>> Meta { get; set; } = [];
    public List<ReportSection> Sections { get; set; } = [];
    public string? Footer { get; set; }
}

public static class Doc
{
    public const string NotProvided = "Non renseigné";

    public static string Or(string? v) => string.IsNullOrWhiteSpace(v) ? NotProvided : v!;

    public static ReportBlock P(string text) => new() { Type = "p", Text = text };
    public static ReportBlock Note(string text) => new() { Type = "note", Text = text };
    public static ReportBlock List(IEnumerable<string> items) => new() { Type = "list", Items = items.ToList() };
    public static ReportBlock Kv(params (string K, string V)[] pairs) => new() { Type = "kv", Pairs = pairs.Select(p => new KeyValuePair<string, string>(p.K, p.V)).ToList() };
    public static ReportBlock Table(string? caption, string[] headers, IEnumerable<IEnumerable<string>> rows) =>
        new() { Type = "table", Caption = caption, Headers = headers.ToList(), Rows = rows.Select(r => r.ToList()).ToList() };

    public static ReportSection Section(string heading, SectionKind kind, params ReportBlock[] blocks) => new() { Heading = heading, Kind = kind, Blocks = blocks.ToList() };
}
