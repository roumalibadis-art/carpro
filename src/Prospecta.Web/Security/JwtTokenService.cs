using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Prospecta.Application.Identity;

namespace Prospecta.Web.Security;

public sealed class JwtOptions
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "prospecta";
    public string Audience { get; set; } = "prospecta-api";
    public int ExpiresMinutes { get; set; } = 60;
}

public sealed class JwtTokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options, TimeProvider clock)
{
    public (string Token, DateTime ExpiresAt) Create(ApplicationUser user, IEnumerable<string> roles)
    {
        var o = options.Value;
        var expires = clock.GetUtcNow().UtcDateTime.AddMinutes(o.ExpiresMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new("stamp", user.SecurityStamp ?? string.Empty),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Secret));
        var token = new JwtSecurityToken(o.Issuer, o.Audience, claims, expires: expires, signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
