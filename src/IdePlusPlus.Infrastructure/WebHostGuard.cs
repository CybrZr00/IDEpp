using System.Net;
using System.Net.Sockets;

namespace IdePlusPlus.Infrastructure;

/// <summary>
/// Decides whether a resolved address is safe for the IDE's web-fetch tool to connect to.
///
/// fetch_url takes a URL from the model, which took it from the user or from whatever the model
/// itself has been reading - so "resolves to the machine's own network" has to be impossible, not
/// merely something the confirmation dialog warns about. There is no legitimate "fetch my own
/// machine" use case for a tool meant to read public webpages: the IDE already has read_file/
/// list_files for local access, and this check is unconditional, not something a confirmation can
/// override.
///
/// Applied to the resolved IP, never the hostname - a hostname check alone is what DNS rebinding
/// defeats (a name that resolves to a public address the first time and a private one the next).
/// WebContentFetcher pins the connection to the exact address checked here, so there is no second
/// resolution for an attacker to race.
/// </summary>
public static class WebHostGuard
{
    private static readonly IPAddress CloudMetadataAddress = IPAddress.Parse("169.254.169.254");

    public static bool IsBlocked(IPAddress address)
    {
        // ::ffff:10.0.0.1 and friends are IPv4-private addresses wearing an IPv6 costume - unwrapped
        // first so the IPv4 range checks below actually see them.
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(candidate)
            || candidate.IsIPv6LinkLocal
            || candidate.IsIPv6SiteLocal
            || candidate.IsIPv6UniqueLocal
            || candidate.Equals(CloudMetadataAddress))
        {
            return true;
        }

        if (candidate.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = candidate.GetAddressBytes();
        return b[0] switch
        {
            0 => true,                              // 0.0.0.0/8 - "this network"
            10 => true,                              // 10.0.0.0/8
            127 => true,                              // 127.0.0.0/8 - loopback (belt and braces beyond IsLoopback)
            169 when b[1] == 254 => true,             // 169.254.0.0/16 - link-local, including the cloud metadata address
            172 when b[1] is >= 16 and <= 31 => true, // 172.16.0.0/12
            192 when b[1] == 168 => true,             // 192.168.0.0/16
            _ => false,
        };
    }
}
