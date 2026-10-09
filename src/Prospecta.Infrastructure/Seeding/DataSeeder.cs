using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Prospecta.Application.Identity;
using Prospecta.Application.Security;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;
using Prospecta.Domain.Prospecting;
using Prospecta.Infrastructure.Persistence;

namespace Prospecta.Infrastructure.Seeding;

/// <summary>
/// Idempotent start-up seeding: schema, roles/permissions, status vocabularies, the 58 wilayas, a starter activity list.
/// Never overwrites what an administrator has edited. Demo content only when Seed:DemoData=true.
/// </summary>
public static class DataSeeder
{
    private static readonly (string Code, string Name)[] Wilayas =
    [
        ("01", "Adrar"), ("02", "Chlef"), ("03", "Laghouat"), ("04", "Oum El Bouaghi"), ("05", "Batna"), ("06", "Béjaïa"), ("07", "Biskra"),
        ("08", "Béchar"), ("09", "Blida"), ("10", "Bouira"), ("11", "Tamanrasset"), ("12", "Tébessa"), ("13", "Tlemcen"), ("14", "Tiaret"),
        ("15", "Tizi Ouzou"), ("16", "Alger"), ("17", "Djelfa"), ("18", "Jijel"), ("19", "Sétif"), ("20", "Saïda"), ("21", "Skikda"),
        ("22", "Sidi Bel Abbès"), ("23", "Annaba"), ("24", "Guelma"), ("25", "Constantine"), ("26", "Médéa"), ("27", "Mostaganem"),
        ("28", "M'Sila"), ("29", "Mascara"), ("30", "Ouargla"), ("31", "Oran"), ("32", "El Bayadh"), ("33", "Illizi"),
        ("34", "Bordj Bou Arréridj"), ("35", "Boumerdès"), ("36", "El Tarf"), ("37", "Tindouf"), ("38", "Tissemsilt"), ("39", "El Oued"),
        ("40", "Khenchela"), ("41", "Souk Ahras"), ("42", "Tipaza"), ("43", "Mila"), ("44", "Aïn Defla"), ("45", "Naâma"),
        ("46", "Aïn Témouchent"), ("47", "Ghardaïa"), ("48", "Relizane"), ("49", "Timimoun"), ("50", "Bordj Badji Mokhtar"),
        ("51", "Ouled Djellal"), ("52", "Béni Abbès"), ("53", "In Salah"), ("54", "In Guezzam"), ("55", "Touggourt"), ("56", "Djanet"),
        ("57", "El M'Ghair"), ("58", "El Menia"),
    ];

    private static readonly (StatusKind Kind, string Code, string Label, bool System)[] Statuses =
    [
        (StatusKind.Census, StatusCodes.ToVerify, "À vérifier", true), (StatusKind.Census, StatusCodes.Verified, "Vérifié", true),
        (StatusKind.Census, StatusCodes.Partial, "Informations partielles", true), (StatusKind.Census, StatusCodes.PotentialDuplicate, "Doublon potentiel", true),
        (StatusKind.Census, StatusCodes.Closed, "Fermé ou inactif", true),
        (StatusKind.Processing, StatusCodes.Unassigned, "Non affecté", true), (StatusKind.Processing, StatusCodes.Assigned, "Affecté", true),
        (StatusKind.Processing, "to_contact", "À contacter", false), (StatusKind.Processing, "contacted", "Contacté", false),
        (StatusKind.Processing, "visit_planned", "Visite planifiée", false), (StatusKind.Processing, "visited", "Visité", false),
        (StatusKind.Processing, "to_follow_up", "À relancer", false), (StatusKind.Processing, "done", "Traitement terminé", false),
        (StatusKind.Outcome, StatusCodes.Pending, "En attente", true), (StatusKind.Outcome, "interested", "Intéressé", true),
        (StatusKind.Outcome, "not_interested", "Non intéressé", false), (StatusKind.Outcome, "to_recontact", "À recontacter", false),
        (StatusKind.Outcome, "appointment_requested", "Rendez-vous demandé", true), (StatusKind.Outcome, "demo_requested", "Démonstration demandée", true),
        (StatusKind.Outcome, "proposal_sent", "Proposition envoyée", true), (StatusKind.Outcome, "negotiation", "Négociation", false),
        (StatusKind.Outcome, "won", "Client acquis", true), (StatusKind.Outcome, "dropped", "Sans suite", false),
    ];

    private static readonly (string Name, string[] Subs)[] Categories =
    [
        ("Location de véhicules", ["Location de voitures", "Location avec chauffeur", "Location utilitaires"]),
        ("Commerce de détail", ["Alimentation", "Habillement", "Électronique"]),
        ("Restauration et hôtellerie", ["Restaurants", "Cafés", "Hôtels"]),
        ("Services aux entreprises", ["Comptabilité et conseil", "Informatique", "Transport et logistique"]),
        ("Santé", ["Cliniques", "Pharmacies", "Laboratoires"]),
        ("Éducation et formation", ["Écoles privées", "Centres de formation"]),
        ("Bâtiment et immobilier", ["Promotion immobilière", "Agences immobilières", "Matériaux"]),
    ];

    public static async Task RunAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        var s = scope.ServiceProvider;
        var db = s.GetRequiredService<AppDbContext>();
        var config = s.GetRequiredService<IConfiguration>();
        var log = s.GetRequiredService<ILoggerFactory>().CreateLogger("Seeder");

        if (db.Database.IsSqlite()) await db.Database.EnsureCreatedAsync(ct); else await db.Database.MigrateAsync(ct);

        await SeedRolesAsync(s, ct);
        await GrantPhase2PermissionsAsync(s, db, ct);
        await SeedStatusesAsync(db, ct);
        await SeedWilayasAsync(db, ct);
        await SeedCategoriesAsync(db, ct);
        await SeedUsersAsync(s, config, log, ct);
        if (config.GetValue<bool>("Seed:DemoData")) await DemoData.SeedAsync(s, ct);
    }

    private static async Task SeedRolesAsync(IServiceProvider s, CancellationToken ct)
    {
        var roles = s.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var (name, perms) in Roles.DefaultPermissions)
        {
            if (await roles.RoleExistsAsync(name)) continue; // never reset permissions an admin has customised
            var role = new IdentityRole<Guid>(name);
            await roles.CreateAsync(role);
            foreach (var p in perms) await roles.AddClaimAsync(role, new Claim(Permissions.ClaimType, p));
        }
    }

    /// <summary>
    /// Roles created before phase 2 lack the new permissions. They are granted once to the default roles (Admin: all; manager and salesperson: their defaults);
    /// afterwards an administrator's edits are never overwritten (guarded by a flag).
    /// </summary>
    private static async Task GrantPhase2PermissionsAsync(IServiceProvider s, AppDbContext db, CancellationToken ct)
    {
        await GrantOnceAsync(s, db, PermissionIntroductions.Phase2Flag, PermissionIntroductions.Phase2, ct);
        await GrantOnceAsync(s, db, PermissionIntroductions.Phase3Flag, PermissionIntroductions.Phase3, ct);
    }

    private static async Task GrantOnceAsync(IServiceProvider s, AppDbContext db, string flag, string[] introduced, CancellationToken ct)
    {
        if (await db.SystemFlags.AnyAsync(f => f.Key == flag, ct)) return;
        var roles = s.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var (name, defaults) in Roles.DefaultPermissions)
        {
            var role = await roles.FindByNameAsync(name);
            if (role is null) continue;
            var have = (await roles.GetClaimsAsync(role)).Where(c => c.Type == Permissions.ClaimType).Select(c => c.Value).ToHashSet();
            foreach (var p in introduced.Where(p => defaults.Contains(p) && !have.Contains(p)))
                await roles.AddClaimAsync(role, new Claim(Permissions.ClaimType, p));
        }

        db.SystemFlags.Add(new SystemFlag { Key = flag, Value = "done" });
        await db.SaveChangesAsync(ct);
        s.GetRequiredService<Prospecta.Application.Abstractions.IPermissionStore>().Invalidate();
    }

    private static async Task SeedStatusesAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.StatusValues.Select(x => x.Kind + ":" + x.Code).ToListAsync(ct);
        var order = new Dictionary<StatusKind, int>();
        foreach (var (kind, code, label, system) in Statuses)
        {
            order[kind] = order.GetValueOrDefault(kind) + 1;
            if (existing.Contains(kind + ":" + code)) continue;
            db.StatusValues.Add(new StatusValue { Kind = kind, Code = code, Label = label, SortOrder = order[kind] * 10, IsSystem = system, CreatedAt = DateTime.UtcNow });
        }

        // Outcomes feeding the indicators cannot be disabled (also applied to databases seeded before they were flagged).
        var indicatorCodes = new[] { "interested", "appointment_requested", "demo_requested", "proposal_sent", "won" };
        foreach (var st in await db.StatusValues.Where(x => x.Kind == StatusKind.Outcome && indicatorCodes.Contains(x.Code) && !x.IsSystem).ToListAsync(ct)) { st.IsSystem = true; st.IsActive = true; }
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedWilayasAsync(AppDbContext db, CancellationToken ct)
    {
        var codes = await db.GeographicAreas.Where(a => a.Level == GeoLevel.Wilaya).Select(a => a.Code).ToListAsync(ct);
        foreach (var (code, name) in Wilayas.Where(w => !codes.Contains(w.Code)))
            db.GeographicAreas.Add(new GeographicArea { Level = GeoLevel.Wilaya, Code = code, Name = name, NormalizedName = TextNormalizer.NormalizeName(name), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedCategoriesAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.BusinessCategories.AnyAsync(ct)) return;
        foreach (var (name, subs) in Categories)
        {
            var parent = new BusinessCategory { Name = name, NormalizedName = TextNormalizer.NormalizeName(name), CreatedAt = DateTime.UtcNow };
            db.BusinessCategories.Add(parent);
            foreach (var sub in subs)
                db.BusinessCategories.Add(new BusinessCategory { Name = sub, NormalizedName = TextNormalizer.NormalizeName(sub), Parent = parent, CreatedAt = DateTime.UtcNow });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedUsersAsync(IServiceProvider s, IConfiguration config, ILogger log, CancellationToken ct)
    {
        var users = s.GetRequiredService<UserManager<ApplicationUser>>();
        if (users.Users.Any()) return;

        async Task Create(string email, string name, string password, string role, Guid? manager = null)
        {
            var u = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = name, ManagerId = manager, CreatedAt = DateTime.UtcNow };
            var r = await users.CreateAsync(u, password);
            if (!r.Succeeded) throw new InvalidOperationException("Seed user failed: " + string.Join("; ", r.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(u, role);
        }

        var adminEmail = config["Bootstrap:AdminEmail"];
        var adminPassword = config["Bootstrap:AdminPassword"];
        if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword))
        {
            await Create(adminEmail, "Administrateur", adminPassword, Roles.Admin);
            log.LogInformation("Compte administrateur initial créé : {Email}", adminEmail);
        }
        else if (config.GetValue<bool>("Seed:DemoData"))
        {
            // Development-only fake accounts (never reused in production).
            await Create("admin@example.local", "Admin Démo", "Admin#2026!x", Roles.Admin);
            await Create("manager@example.local", "Responsable Démo", "Manager#2026!x", Roles.SalesManager);
            var manager = await users.FindByEmailAsync("manager@example.local");
            await Create("sales@example.local", "Commercial Démo", "Sales#2026!xx", Roles.Salesperson, manager!.Id);
            log.LogWarning("Comptes de démonstration créés (admin@example.local, manager@example.local, sales@example.local).");
        }
        else
        {
            log.LogWarning("Aucun utilisateur : définissez Bootstrap:AdminEmail et Bootstrap:AdminPassword pour créer le premier administrateur.");
        }
    }
}
