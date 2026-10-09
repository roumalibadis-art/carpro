using Microsoft.AspNetCore.Identity;

namespace Prospecta.Application.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    /// <summary>Team lead: a sales manager sees the reports of the users who report to them.</summary>
    public Guid? ManagerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
