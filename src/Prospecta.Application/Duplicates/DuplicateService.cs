using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Security;
using Prospecta.Domain.Businesses;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;
using Microsoft.Extensions.Options;

namespace Prospecta.Application.Duplicates;

public sealed record DuplicateSide(Guid Id, string Name, string? Address, string? Phone, string? Website, string? Commune, string? Category,
    string CensusStatus, DateTime CollectedAt, int SourceCount, int Assignments, Dictionary<string, string?> Fields, Dictionary<string, FieldOrigin> Origins);

public sealed record DuplicateDto(Guid Id, double Score, IReadOnlyList<string> Reasons, DuplicateStatus Status, DuplicateSide A, DuplicateSide B, bool Strong);

public sealed record DuplicateMatch(Guid BusinessId, string Name, double Score, IReadOnlyList<string> Reasons);

public sealed class DuplicateService(
    IAppDbContext db, ICurrentUser user, IAuditService audit, TimeProvider clock,
    IOptions<DuplicateOptions> options, IEnumerable<IBusinessMergeParticipant> participants)
{
    private DuplicateOptions Opt => options.Value;

    private void RequireManage()
    {
        if (!user.HasPermission(Permissions.DuplicateManage) || !user.HasPermission(Permissions.BusinessViewAll))
        {
            throw new ForbiddenException();
        }
    }

    public static DupProfile ProfileOf(Business b, IEnumerable<string> externalIds) => new(
        b.NormalizedName, b.NormalizedPhone, DuplicateDetector.WebsiteKey(b.Website), TextNormalizer.Fold(b.Address),
        b.CommuneId, b.Latitude, b.Longitude, externalIds.ToHashSet());

    private sealed record Row(Guid Id, string Name, string NormalizedName, string? NormalizedPhone, string? Website, string? Address,
        Guid? CommuneId, double? Latitude, double? Longitude, List<string> Ext);

    /// <summary>Blocking step: only businesses sharing an identifier, a commune, the exact name or the same spot are scored.</summary>
    private async Task<List<Row>> CandidatesAsync(DupProfile p, Guid? wilayaId, Guid? excludeId, string? host, CancellationToken ct)
    {
        var ids = p.ExternalIds.ToList();
        var np = p.NormalizedPhone;
        var name = p.NormalizedName;
        var commune = p.CommuneId;
        var hasGeo = p.Latitude is not null && p.Longitude is not null;
        var latMin = (p.Latitude ?? 0) - 0.002; var latMax = (p.Latitude ?? 0) + 0.002;
        var lonMin = (p.Longitude ?? 0) - 0.002; var lonMax = (p.Longitude ?? 0) + 0.002;

        return await db.Businesses.AsNoTracking()
            .Where(x => x.Id != excludeId && !x.IsDeleted && (
                (np != null && x.NormalizedPhone == np) ||
                (host != null && x.WebsiteHost == host) ||
                (commune != null && x.CommuneId == commune) ||
                (commune == null && wilayaId != null && x.WilayaId == wilayaId && x.NormalizedName == name) ||
                x.NormalizedName == name ||
                (hasGeo && x.Latitude >= latMin && x.Latitude <= latMax && x.Longitude >= lonMin && x.Longitude <= lonMax) ||
                (ids.Count > 0 && x.Sources.Any(s => s.ExternalId != null && ids.Contains(s.Provider + ":" + s.ExternalId)))))
            .OrderBy(x => x.Id).Take(500)
            .Select(x => new Row(x.Id, x.Name, x.NormalizedName, x.NormalizedPhone, x.Website, x.Address, x.CommuneId, x.Latitude, x.Longitude,
                x.Sources.Where(s => s.ExternalId != null).Select(s => s.Provider + ":" + s.ExternalId).ToList()))
            .ToListAsync(ct);
    }

    private static DupProfile ToProfile(Row r) => new(r.NormalizedName, r.NormalizedPhone, DuplicateDetector.WebsiteKey(r.Website),
        TextNormalizer.Fold(r.Address), r.CommuneId, r.Latitude, r.Longitude, r.Ext.ToHashSet());

    /// <summary>Non-persisting lookup used by the import preview and by manual creation warnings.</summary>
    public async Task<IReadOnlyList<DuplicateMatch>> FindMatchesAsync(DupProfile profile, Guid? wilayaId, Guid? excludeId = null, CancellationToken ct = default)
    {
        var host = profile.WebsiteKey?.Split('/')[0];
        var rows = await CandidatesAsync(profile, wilayaId, excludeId, host, ct);
        return rows.Select(r => (r, s: DuplicateDetector.Score(profile, ToProfile(r), Opt)))
            .Where(x => x.s.Score >= Opt.SuspectThreshold)
            .OrderByDescending(x => x.s.Score).Take(5)
            .Select(x => new DuplicateMatch(x.r.Id, x.r.Name, x.s.Score, x.s.Reasons)).ToList();
    }

    /// <summary>Scores a stored business against the rest and upserts pending candidates (no automatic merge, ever).</summary>
    public async Task<IReadOnlyList<DuplicateMatch>> DetectAsync(Business b, CancellationToken ct = default)
    {
        var ext = await db.BusinessSources.Where(s => s.BusinessId == b.Id && s.ExternalId != null).Select(s => s.Provider + ":" + s.ExternalId).ToListAsync(ct);
        var profile = ProfileOf(b, ext);
        var matches = await FindMatchesFullAsync(profile, b, ct);
        var found = new List<DuplicateMatch>();
        foreach (var (row, score) in matches)
        {
            var (aId, bId) = b.Id.CompareTo(row.Id) < 0 ? (b.Id, row.Id) : (row.Id, b.Id);
            var existing = await db.DuplicateCandidates.FirstOrDefaultAsync(c => c.BusinessAId == aId && c.BusinessBId == bId, ct);
            if (existing is null)
            {
                db.DuplicateCandidates.Add(new DuplicateCandidate
                {
                    BusinessAId = aId, BusinessBId = bId, Score = score.Score, Reasons = string.Join(" · ", score.Reasons),
                    CreatedAt = clock.GetUtcNow().UtcDateTime,
                });
            }
            else if (existing.Status == DuplicateStatus.Pending)
            {
                existing.Score = score.Score;
                existing.Reasons = string.Join(" · ", score.Reasons);
            }
            else
            {
                continue; // a human already decided about this pair
            }

            found.Add(new DuplicateMatch(row.Id, row.Name, score.Score, score.Reasons));
            await FlagCensusAsync(b, ct);
            await FlagCensusAsync(row.Id, ct);
        }

        if (found.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return found;
    }

    private async Task<List<(Row Row, DupScore Score)>> FindMatchesFullAsync(DupProfile profile, Business b, CancellationToken ct)
    {
        var rows = await CandidatesAsync(profile, b.WilayaId, b.Id, b.WebsiteHost, ct);
        return rows.Select(r => (r, DuplicateDetector.Score(profile, ToProfile(r), Opt))).Where(x => x.Item2.Score >= Opt.SuspectThreshold).ToList();
    }

    private async Task FlagCensusAsync(Guid id, CancellationToken ct)
    {
        var other = await db.Businesses.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (other is not null) await FlagCensusAsync(other, ct);
    }

    /// <summary>Only unverified records flip to "potential duplicate"; a verified/closed state is never overwritten.</summary>
    private async Task FlagCensusAsync(Business b, CancellationToken ct)
    {
        var codes = await db.StatusValues.Where(s => s.Kind == StatusKind.Census).ToDictionaryAsync(s => s.Id, s => s.Code, ct);
        var dup = await db.StatusValues.FirstAsync(s => s.Kind == StatusKind.Census && s.Code == StatusCodes.PotentialDuplicate, ct);
        if (codes.TryGetValue(b.CensusStatusId, out var code) && code is StatusCodes.ToVerify or StatusCodes.Partial)
        {
            b.CensusStatusId = dup.Id;
            b.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        }
    }

    // ---------- Review UI ----------
    public async Task<PagedResult<DuplicateDto>> ListAsync(DuplicateStatus status, int page, int pageSize, CancellationToken ct = default)
    {
        RequireManage();
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = db.DuplicateCandidates.AsNoTracking().Where(c => c.Status == status && !c.BusinessA!.IsDeleted && !c.BusinessB!.IsDeleted);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(c => c.Score).ThenBy(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = new List<DuplicateDto>();
        foreach (var c in rows) items.Add(await ToDtoAsync(c, ct));
        return new PagedResult<DuplicateDto>(items, total, page, pageSize);
    }

    public async Task<DuplicateDto> CompareAsync(Guid candidateId, CancellationToken ct = default)
    {
        RequireManage();
        var c = await db.DuplicateCandidates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == candidateId, ct) ?? throw new NotFoundException();
        return await ToDtoAsync(c, ct);
    }

    private async Task<DuplicateDto> ToDtoAsync(DuplicateCandidate c, CancellationToken ct)
    {
        var a = await SideAsync(c.BusinessAId, ct);
        var b = await SideAsync(c.BusinessBId, ct);
        return new DuplicateDto(c.Id, c.Score, c.Reasons.Split(" · ", StringSplitOptions.RemoveEmptyEntries), c.Status, a, b, c.Score >= Opt.StrongThreshold);
    }

    private async Task<DuplicateSide> SideAsync(Guid id, CancellationToken ct)
    {
        var b = await db.Businesses.IgnoreQueryFilters().AsNoTracking().Include(x => x.Provenances).Include(x => x.Commune).Include(x => x.Category)
            .Include(x => x.CensusStatus).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        var fields = FieldUpdater.TrackedFields.ToDictionary(f => f, f => FieldUpdater.GetValue(b, f));
        var origins = b.Provenances.ToDictionary(p => p.Field, p => p.Origin);
        var sources = await db.BusinessSources.CountAsync(s => s.BusinessId == id, ct);
        var assigns = await db.BusinessAssignments.CountAsync(s => s.BusinessId == id && s.IsActive, ct);
        return new DuplicateSide(b.Id, b.Name, b.Address, b.Phone, b.Website, b.Commune?.Name, b.Category?.Name,
            b.CensusStatus?.Label ?? "", b.CollectedAt, sources, assigns, fields, origins);
    }

    public async Task DismissAsync(Guid candidateId, CancellationToken ct = default)
    {
        RequireManage();
        var c = await db.DuplicateCandidates.FirstOrDefaultAsync(x => x.Id == candidateId, ct) ?? throw new NotFoundException();
        if (c.Status != DuplicateStatus.Pending) throw new ConflictException("Ce couple a déjà été traité.");
        c.Status = DuplicateStatus.NotDuplicate;
        c.ResolvedAt = clock.GetUtcNow().UtcDateTime;
        c.ResolvedByUserId = user.Id;
        await db.SaveChangesAsync(ct);
        foreach (var id in new[] { c.BusinessAId, c.BusinessBId }) await RestoreCensusAsync(id, ct);
        audit.Record("duplicate.dismiss", "DuplicateCandidate", c.Id);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>When no pending candidate remains, a business flagged only by the detector goes back to "to verify".</summary>
    private async Task RestoreCensusAsync(Guid businessId, CancellationToken ct)
    {
        var b = await db.Businesses.Include(x => x.CensusStatus).FirstOrDefaultAsync(x => x.Id == businessId, ct);
        if (b?.CensusStatus?.Code != StatusCodes.PotentialDuplicate) return;
        var stillPending = await db.DuplicateCandidates.AnyAsync(c => c.Status == DuplicateStatus.Pending && (c.BusinessAId == businessId || c.BusinessBId == businessId), ct);
        if (stillPending) return;
        var backCode = b.CompletenessPercent < 50 ? StatusCodes.Partial : StatusCodes.ToVerify; // same rule as at creation
        var back = await db.StatusValues.FirstAsync(s => s.Kind == StatusKind.Census && s.Code == backCode, ct);
        b.CensusStatusId = back.Id;
    }

    /// <param name="useOther">Fields for which the value of the other record wins over the survivor's.</param>
    public async Task<Guid> MergeAsync(Guid candidateId, Guid survivorId, IReadOnlySet<string>? useOther, CancellationToken ct = default)
    {
        RequireManage();
        var c = await db.DuplicateCandidates.FirstOrDefaultAsync(x => x.Id == candidateId, ct) ?? throw new NotFoundException();
        if (c.Status != DuplicateStatus.Pending) throw new ConflictException("Ce couple a déjà été traité.");
        if (survivorId != c.BusinessAId && survivorId != c.BusinessBId) throw new ValidationException("La fiche conservée doit appartenir au couple.");
        var otherId = survivorId == c.BusinessAId ? c.BusinessBId : c.BusinessAId;

        await using var tx = await UnitOfWork.BeginAsync(db.Database, ct);
        var survivor = await db.Businesses.Include(x => x.Provenances).Include(x => x.Sources).Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == survivorId, ct) ?? throw new NotFoundException();
        var other = await db.Businesses.Include(x => x.Provenances).Include(x => x.Sources).Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == otherId, ct) ?? throw new NotFoundException();
        var now = clock.GetUtcNow().UtcDateTime;

        var updater = new FieldUpdater(survivor, clock, user.Id, canOverrideConfirmed: true);
        foreach (var field in FieldUpdater.TrackedFields)
        {
            var theirs = FieldUpdater.GetValue(other, field);
            if (theirs is null) continue;
            var mine = FieldUpdater.GetValue(survivor, field);
            var wantsTheirs = useOther?.Contains(field) == true;
            if (mine is not null && !wantsTheirs) continue;
            var theirOrigin = other.Provenances.FirstOrDefault(p => p.Field == field)?.Origin ?? FieldOrigin.Manual;
            var mineConfirmed = survivor.Provenances.Any(p => p.Field == field && p.Origin == FieldOrigin.Confirmed);
            var origin = mineConfirmed && theirOrigin != FieldOrigin.Confirmed ? FieldOrigin.Manual : theirOrigin;
            updater.Apply(field, theirs, origin, $"Fusion avec {other.Id}");
        }

        if (!string.IsNullOrWhiteSpace(other.InternalNotes) && other.InternalNotes != survivor.InternalNotes)
        {
            survivor.InternalNotes = string.IsNullOrWhiteSpace(survivor.InternalNotes)
                ? other.InternalNotes
                : survivor.InternalNotes + "\n--- notes de la fiche fusionnée ---\n" + other.InternalNotes;
        }

        foreach (var s in other.Sources.ToList()) s.BusinessId = survivor.Id;
        survivor.Sources.AddRange(other.Sources);
        foreach (var asg in other.Assignments.Where(a => a.IsActive).ToList())
        {
            if (survivor.Assignments.Any(a => a.IsActive && a.UserId == asg.UserId))
            {
                asg.IsActive = false; asg.EndedAt = now;
            }
            else
            {
                asg.BusinessId = survivor.Id;
            }
        }

        updater.History.Add(new BusinessDataHistory
        {
            BusinessId = survivor.Id, Field = "Merge", OldValue = other.Id.ToString(), NewValue = survivor.Id.ToString(),
            Origin = FieldOrigin.Manual, ChangedByUserId = user.Id, CreatedAt = now,
            Reason = $"Fiche « {other.Name} » fusionnée (score {c.Score:0.00})",
        });
        db.BusinessHistory.AddRange(updater.History);

        other.IsDeleted = true; other.DeletedAt = now; other.MergedIntoId = survivor.Id; other.UpdatedAt = now;
        c.Status = DuplicateStatus.Merged; c.ResolvedAt = now; c.ResolvedByUserId = user.Id;

        var stale = await db.DuplicateCandidates.Where(x => x.Id != c.Id && x.Status == DuplicateStatus.Pending && (x.BusinessAId == otherId || x.BusinessBId == otherId)).ToListAsync(ct);
        db.DuplicateCandidates.RemoveRange(stale); // re-detected against the survivor below if still relevant

        survivor.UpdatedAt = now; survivor.UpdatedByUserId = user.Id;
        BusinessRules.Finalize(survivor);
        await db.SaveChangesAsync(ct);
        foreach (var p in participants) await p.MergeAsync(survivor.Id, other.Id, ct);
        audit.Record("duplicate.merge", "Business", survivor.Id, $"merged={other.Id}; candidate={c.Id}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await RestoreCensusAsync(survivor.Id, ct);
        await db.SaveChangesAsync(ct);
        await DetectAsync(survivor, ct);
        return survivor.Id;
    }

    /// <summary>Re-scans up to <paramref name="max"/> businesses (oldest scan first is not tracked: simple bounded sweep).</summary>
    public async Task<int> RescanAsync(int max, CancellationToken ct = default)
    {
        RequireManage();
        var found = 0;
        var batch = await db.Businesses.OrderBy(b => b.Id).Take(Math.Clamp(max, 1, 2000)).ToListAsync(ct);
        foreach (var b in batch) found += (await DetectAsync(b, ct)).Count;
        audit.Record("duplicate.rescan", "Business", null, $"scanned={batch.Count}; pairs={found}");
        await db.SaveChangesAsync(ct);
        return found;
    }
}
