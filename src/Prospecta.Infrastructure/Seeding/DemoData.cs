using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prospecta.Application.Identity;
using Prospecta.Domain.Businesses;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;
using Prospecta.Infrastructure.Persistence;

namespace Prospecta.Infrastructure.Seeding;

/// <summary>
/// EXPLICIT DEMONSTRATION DATA (Seed:DemoData=true, development only). Every record is flagged IsDemo, uses the source type
/// "Demo", carries "[DÉMO]" in its name and fictitious 0555 00 00 xx numbers. It is never real market data.
/// </summary>
public static class DemoData
{
    public static async Task SeedAsync(IServiceProvider s, CancellationToken ct)
    {
        var db = s.GetRequiredService<AppDbContext>();
        if (await db.Businesses.IgnoreQueryFilters().AnyAsync(b => b.IsDemo, ct)) return;

        var alger = await db.GeographicAreas.FirstAsync(a => a.Level == GeoLevel.Wilaya && a.Code == "16", ct);
        GeographicArea Mk(GeoLevel l, string name, GeographicArea parent) => new()
        {
            Level = l, Name = name, NormalizedName = TextNormalizer.NormalizeName(name), Parent = parent, CreatedAt = DateTime.UtcNow,
        };
        var daira = Mk(GeoLevel.Daira, "Zone démo Est", alger);
        var c1 = Mk(GeoLevel.Commune, "Rouïba", daira);
        var c2 = Mk(GeoLevel.Commune, "Réghaïa", daira);
        if (!await db.GeographicAreas.AnyAsync(a => a.Level == GeoLevel.Commune && a.NormalizedName == c1.NormalizedName, ct))
        {
            db.GeographicAreas.AddRange(daira, c1, c2);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            return; // the administrator already loaded real geography: do not mix demo rows into it
        }

        var cat = await db.BusinessCategories.FirstAsync(c => c.Name == "Location de véhicules", ct);
        var sub = await db.BusinessCategories.FirstAsync(c => c.Name == "Location de voitures", ct);
        var census = await db.StatusValues.Where(x => x.Kind == StatusKind.Census).ToDictionaryAsync(x => x.Code, ct);
        var proc = await db.StatusValues.FirstAsync(x => x.Kind == StatusKind.Processing && x.Code == StatusCodes.Unassigned, ct);
        var outc = await db.StatusValues.FirstAsync(x => x.Kind == StatusKind.Outcome && x.Code == StatusCodes.Pending, ct);

        var now = DateTime.UtcNow;
        for (var i = 1; i <= 12; i++)
        {
            var commune = i % 2 == 0 ? c1 : c2;
            var phone = $"0555 00 00 {i:00}";
            var b = new Business
            {
                Name = $"[DÉMO] Agence de location {i}", CategoryId = cat.Id, SubCategoryId = sub.Id,
                WilayaId = alger.Id, DairaId = daira.Id, CommuneId = commune.Id, Address = $"Rue démo n°{i}, {commune.Name}",
                Phone = phone, NormalizedPhone = AlgerianPhone.Normalize(phone), CollectedAt = now, CreatedAt = now, UpdatedAt = now,
                CensusStatusId = census[i % 3 == 0 ? StatusCodes.Partial : StatusCodes.ToVerify].Id, ProcessingStatusId = proc.Id, OutcomeStatusId = outc.Id,
                IsDemo = true, Latitude = 36.738 + i * 0.001, Longitude = 3.28 + i * 0.001,
            };
            b.NormalizedName = TextNormalizer.NormalizeName(b.Name);
            b.Sources.Add(new BusinessSource { BusinessId = b.Id, SourceType = SourceType.Demo, Provider = "demo", CollectedAt = now, CreatedAt = now });
            b.Completeness();
            db.Businesses.Add(b);
        }

        await db.SaveChangesAsync(ct);
    }

    private static void Completeness(this Business b)
    {
        var known = 0;
        if (b.CategoryId is not null) known++;
        if (b.CommuneId is not null) known++;
        if (b.Address is not null) known++;
        if (b.NormalizedPhone is not null) known++;
        if (b.Latitude is not null) known++;
        b.CompletenessPercent = (int)Math.Round(known * 100.0 / 7);
    }
}
