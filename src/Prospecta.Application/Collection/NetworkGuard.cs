using System.Net;
using System.Net.Sockets;

namespace Prospecta.Application.Collection;

/// <summary>SSRF protection: a public-page fetch may only reach public internet addresses.</summary>
public static class NetworkGuard
{
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC) return false; // fc00::/7 unique local
            return true;
        }

        var o = ip.GetAddressBytes();
        return !(o[0] == 10 || o[0] == 127 || o[0] == 0 || o[0] >= 224
                 || (o[0] == 172 && o[1] is >= 16 and <= 31) || (o[0] == 192 && o[1] == 168) || (o[0] == 169 && o[1] == 254)
                 || (o[0] == 100 && o[1] is >= 64 and <= 127) || (o[0] == 192 && o[1] == 0 && o[2] == 0) || (o[0] == 198 && o[1] is 18 or 19));
    }

    /// <summary>Scheme/credentials/port screening; addresses themselves are checked at connection time (see the fetcher).</summary>
    public static bool IsAllowedUrl(Uri u, bool anyPort = false) => u.Scheme is "http" or "https" && string.IsNullOrEmpty(u.UserInfo) && (anyPort || u.IsDefaultPort || u.Port is 80 or 443 or 8080 or 8443);
}
