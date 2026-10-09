using System.Security.Claims;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Security;

namespace Prospecta.Web.Security;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? Id => Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? UserName => Principal?.FindFirstValue(ClaimTypes.Name);
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true && Id is not null;
    public bool HasPermission(string permission) => IsAuthenticated && Principal!.HasClaim(Permissions.ClaimType, permission);
}
