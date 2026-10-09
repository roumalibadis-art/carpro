using Prospecta.Domain.Common;

namespace Prospecta.Domain.Auditing;

public class AuditLog : Entity
{
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}

public class SavedFilter : Entity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public SavedFilterScope Scope { get; set; } = SavedFilterScope.Personal;
    public string FilterJson { get; set; } = "{}";
}
