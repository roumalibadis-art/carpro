using FluentAssertions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Duplicates;
using Prospecta.Domain.Businesses;
using Prospecta.Domain.Common;

namespace Prospecta.UnitTests;

public class NormalizationTests
{
    [Theory]
    [InlineData("0555 12 34 56", "213555123456")]
    [InlineData("+213 555 12 34 56", "213555123456")]
    [InlineData("00213555123456", "213555123456")]
    [InlineData("555123456", "213555123456")]            // spreadsheet dropped the zero
    [InlineData("023 85 12 34", "21323851234")]
    [InlineData("0770-11-22-33", "213770112233")]
    public void Algerian_phones_are_normalized(string raw, string expected) => AlgerianPhone.Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData("12345")]
    [InlineData("0155 12 34 56")]
    [InlineData("+33 6 12 34 56 78")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    public void Invalid_phones_are_rejected_not_guessed(string? raw) => AlgerianPhone.Normalize(raw).Should().BeNull();

    [Fact]
    public void Phone_format_roundtrips() => AlgerianPhone.Format("213555123456").Should().Be("0555 12 34 56");

    [Theory]
    [InlineData("  Société  RÉGHAÏA Auto-Location ", "reghaia auto location")]
    [InlineData("SARL El Djazaïr", "el djazair")]
    [InlineData("SARL", "sarl")]
    public void Names_are_accent_case_and_legal_form_insensitive(string raw, string expected) => TextNormalizer.NormalizeName(raw).Should().Be(expected);

    [Theory]
    [InlineData("https://www.Example.dz/fr/", "example.dz")]
    [InlineData("example.dz", "example.dz")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("pas un site", null)]
    [InlineData("ftp://example.dz", null)]
    public void Website_host_only_accepts_http_urls(string raw, string? host) => TextNormalizer.WebsiteHost(raw).Should().Be(host);
}

public class DuplicateDetectorTests
{
    private static readonly DuplicateOptions Opt = new();
    private static DupProfile P(string name, string? phone = null, string? web = null, string addr = "", Guid? commune = null, double? lat = null, double? lon = null, params string[] ext) =>
        new(TextNormalizer.NormalizeName(name), AlgerianPhone.Normalize(phone), DuplicateDetector.WebsiteKey(web), TextNormalizer.Fold(addr), commune, lat, lon, ext.ToHashSet());

    [Fact]
    public void Same_provider_id_is_certain() =>
        DuplicateDetector.Score(P("A", ext: "gp:1"), P("B", ext: "gp:1"), Opt).Score.Should().Be(1.0);

    [Fact]
    public void Same_name_in_same_commune_is_suspect_but_below_strong()
    {
        var c = Guid.NewGuid();
        var s = DuplicateDetector.Score(P("Auto Location Alpha", commune: c), P("auto location ALPHA", commune: c), Opt);
        s.Score.Should().BeInRange(Opt.SuspectThreshold, Opt.StrongThreshold - 0.01);
    }

    [Fact]
    public void Same_name_alone_in_different_places_stays_borderline_and_different_names_do_not_match()
    {
        DuplicateDetector.Score(P("Pharmacie Centrale", commune: Guid.NewGuid()), P("Boulangerie Moderne", commune: Guid.NewGuid()), Opt).Score.Should().BeLessThan(Opt.SuspectThreshold);
        DuplicateDetector.Score(P("Auto Rouiba", commune: Guid.NewGuid()), P("Auto Reghaia", commune: Guid.NewGuid()), Opt).Score.Should().BeLessThan(Opt.SuspectThreshold);
    }

    [Fact]
    public void Phone_plus_close_coordinates_accumulate()
    {
        var a = P("X", "0555 00 11 22", lat: 36.7380, lon: 3.2800);
        var b = P("Y", "0555 00 11 22", lat: 36.73805, lon: 3.28005);
        var s = DuplicateDetector.Score(a, b, Opt);
        s.Reasons.Should().Contain(["Même téléphone", "Proximité géographique"]);
        s.Score.Should().BeGreaterThan(0.75);
    }

    [Fact]
    public void Two_pages_on_the_same_platform_are_not_the_same_website()
    {
        DuplicateDetector.WebsiteKey("https://www.facebook.com/agence-a").Should().NotBe(DuplicateDetector.WebsiteKey("https://facebook.com/agence-b"));
        DuplicateDetector.WebsiteKey("https://www.facebook.com/agence-a/").Should().Be(DuplicateDetector.WebsiteKey("facebook.com/agence-a"));
    }

    [Fact]
    public void Distance_is_in_meters() =>
        DuplicateDetector.DistanceMeters(36.0, 3.0, 36.001, 3.0).Should().BeApproximately(111, 2);

    [Fact]
    public void Missing_data_is_never_a_match() =>
        DuplicateDetector.Score(P("", null), P("", null), Opt).Score.Should().Be(0);
}

public class BusinessRulesTests
{
    [Fact]
    public void Completeness_counts_only_known_attributes()
    {
        var b = new Business { CategoryId = Guid.NewGuid(), CommuneId = Guid.NewGuid(), Address = "x", NormalizedPhone = "213555123456" };
        BusinessRules.Completeness(b).Should().Be(57);
        BusinessRules.Completeness(new Business()).Should().Be(0);
    }

    [Theory]
    [InlineData(36.7, 3.0, true)]
    [InlineData(48.8, 2.3, false)]   // Paris
    [InlineData(3.0, 36.7, false)]   // swapped
    public void Coordinates_must_be_in_algeria(double lat, double lon, bool ok) => BusinessRules.CoordinatesPlausible(lat, lon).Should().Be(ok);

    [Fact]
    public void Confidence_needs_human_verification_for_high()
    {
        var b = new Business { CompletenessPercent = 90 };
        BusinessRules.Confidence(b, 1, 0).Should().Be(ConfidenceLevel.Medium);
        b.LastVerifiedAt = DateTime.UtcNow;
        BusinessRules.Confidence(b, 1, 0).Should().Be(ConfidenceLevel.High);
        BusinessRules.Confidence(new Business { CompletenessPercent = 10 }, 1, 0).Should().Be(ConfidenceLevel.Low);
    }
}

public class FieldUpdaterTests
{
    private static FieldUpdater Updater(Business b, bool canOverride = false) => new(b, TimeProvider.System, Guid.NewGuid(), canOverride);

    [Fact]
    public void External_data_never_overwrites_a_confirmed_value()
    {
        var b = new Business { Name = "X", Phone = "0555 00 00 01" };
        var u = Updater(b);
        u.Apply("Phone", "0555 00 00 01", FieldOrigin.Confirmed);
        u.Apply("Phone", "0666 11 11 11", FieldOrigin.External).Should().Be(FieldChange.Blocked);
        b.Phone.Should().Be("0555 00 00 01");
        u.BlockedFields.Should().Contain("Phone");
    }

    [Fact]
    public void A_manual_edit_overrides_confirmed_data_only_with_the_right()
    {
        var b = new Business { Name = "X", Address = "a" };
        Updater(b).Apply("Address", "a", FieldOrigin.Confirmed);
        Updater(b).Apply("Address", "b", FieldOrigin.Manual).Should().Be(FieldChange.Blocked);
        var u = Updater(b, canOverride: true);
        u.Apply("Address", "b", FieldOrigin.Manual).Should().Be(FieldChange.Changed);
        u.History.Single().Should().Match<BusinessDataHistory>(h => h.OldValue == "a" && h.NewValue == "b");
    }

    [Fact]
    public void An_external_source_reporting_nothing_does_not_blank_existing_data()
    {
        var b = new Business { Name = "X", Website = "https://x.dz" };
        Updater(b).Apply("Website", null, FieldOrigin.External).Should().Be(FieldChange.Unchanged);
        b.Website.Should().Be("https://x.dz");
        Updater(b).Apply("Website", null, FieldOrigin.Manual).Should().Be(FieldChange.Changed); // a person may clear it
        b.Website.Should().BeNull();
    }

    [Fact]
    public void Unchanged_values_leave_no_history_and_provenance_tracks_origin()
    {
        var b = new Business { Name = "X" };
        var u = Updater(b);
        u.Apply("Name", "X", FieldOrigin.Manual).Should().Be(FieldChange.Unchanged);
        u.History.Should().BeEmpty();
        u.Apply("Address", "rue 1", FieldOrigin.Estimated);
        b.Provenances.Single(p => p.Field == "Address").Origin.Should().Be(FieldOrigin.Estimated);
    }
}
