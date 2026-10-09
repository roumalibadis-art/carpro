using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Common;
using Prospecta.Domain.Auditing;
using Prospecta.Domain.Common;
using Prospecta.Domain.Prospecting;

namespace Prospecta.Application.Prospecting;

public sealed record NotificationDto(Guid Id, string Kind, string Title, string? Body, string? Link, DateTime CreatedAt, bool Read);

/// <summary>In-app notifications: always scoped to the caller (no id lets you read someone else's).</summary>
public sealed class NotificationService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    private Guid Me => user.Id ?? throw new ForbiddenException();

    public Task<int> UnreadCountAsync(CancellationToken ct = default) => db.Notifications.CountAsync(n => n.UserId == Me && n.ReadAt == null, ct);

    public async Task<PagedResult<NotificationDto>> ListAsync(int page, int pageSize, CancellationToken ct = default)
    {
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = db.Notifications.AsNoTracking().Where(n => n.UserId == Me);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<NotificationDto>(rows.Select(n => new NotificationDto(n.Id, n.Kind, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt is not null)).ToList(), total, page, pageSize);
    }

    public async Task MarkReadAsync(Guid? id, CancellationToken ct = default)
    {
        var q = db.Notifications.Where(n => n.UserId == Me && n.ReadAt == null);
        if (id is not null) q = q.Where(n => n.Id == id);
        var list = await q.ToListAsync(ct);
        if (id is not null && list.Count == 0 && !await db.Notifications.AnyAsync(n => n.Id == id && n.UserId == Me, ct)) throw new NotFoundException();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var n in list) n.ReadAt = now;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Creates reminders for follow-ups that are overdue, due today or due tomorrow. Idempotent per follow-up and day (unique DedupKey);
/// every run is logged in the audit trail. Runs from a hosted service and can be invoked directly.
/// </summary>
public sealed class FollowUpReminderService(IAppDbContext db, TimeProvider clock)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var today = Dates.Today(clock);
        var horizon = today.AddDays(1);
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.FollowUps.AsNoTracking().Where(f => f.Status == FollowUpStatus.ToDo && f.DueDate <= horizon)
            .Select(f => new { f.Id, f.AssignedUserId, f.DueDate, f.Reason, Business = f.Business!.Name }).Take(5000).ToListAsync(ct);
        var keys = due.Select(f => $"fu:{f.Id}:{today:yyyyMMdd}").ToList();
        var existing = (await db.Notifications.Where(n => keys.Contains(n.DedupKey)).Select(n => n.DedupKey).ToListAsync(ct)).ToHashSet();

        var created = 0;
        foreach (var f in due)
        {
            var key = $"fu:{f.Id}:{today:yyyyMMdd}";
            if (existing.Contains(key)) continue;
            var overdue = f.DueDate < today;
            db.Notifications.Add(new Notification
            {
                UserId = f.AssignedUserId, Kind = overdue ? "followup.overdue" : "followup.due", DedupKey = key, CreatedAt = now,
                Title = overdue ? $"Relance en retard : {f.Business}" : f.DueDate == today ? $"Relance à faire aujourd'hui : {f.Business}" : $"Relance demain : {f.Business}",
                Body = $"{f.Reason} (échéance {f.DueDate:dd/MM/yyyy})", Link = "/FollowUps",
            });
            created++;
        }

        db.AuditLogs.Add(new AuditLog { CreatedAt = now, Action = "notifications.followups", EntityType = "Notification", Details = $"checked={due.Count}; created={created}" });
        await db.SaveChangesAsync(ct);
        return created;
    }
}
