using Prospecta.Application.Abstractions;
using Prospecta.Domain.Auditing;

namespace Prospecta.Application.Common;

public sealed class AuditService(IAppDbContext db, ICurrentUser user, TimeProvider clock) : IAuditService
{
    public void Record(string action, string entityType, object? entityId, string? details = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            UserId = user.Id,
            UserName = user.UserName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            Details = details is { Length: > 2000 } ? details[..2000] : details,
            IpAddress = user.IpAddress,
        });
    }
}
