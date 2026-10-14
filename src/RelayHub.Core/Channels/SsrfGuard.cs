using System.Net;
using System.Net.Sockets;

namespace RelayHub.Core.Channels;

/// <summary>Result of an SSRF check: the public addresses a host resolved to, or why it was refused. <see cref="Transient"/> = DNS hiccup, worth a retry.</summary>
public sealed record SsrfVerdict(IPAddress[] Addresses, string? Error, bool Transient = false)
{
    public bool Allowed => Error is null;

    public static SsrfVerdict Refuse(string error, bool transient = false) => new([], error, transient);
}

/// <summary>
/// Keeps owner-configured HTTP destinations from reaching anything that is not a public https server: the cloud instance
/// metadata endpoint (169.254.169.254), loopback, private and link-local ranges, multicast, unspecified addresses and
/// <c>localhost</c>. Applied when the destination is saved and again right before every send (the DNS answer can change
/// in between, and the connector pins the TCP connection to the vetted address so a late rebinding cannot slip through).
/// </summary>
public static class SsrfGuard
{
    /// <summary>Shape rules that need no network: https only, a real host name, no credentials, no local names, no private IP literal.</summary>
    public static string? CheckUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return "url must be an absolute https URL";
        if (parsed.Scheme != Uri.UriSchemeHttps) return "url must use https";
        if (parsed.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic || string.IsNullOrEmpty(parsed.Host)) return "url has no valid host";
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return "url must not contain credentials";

        var host = parsed.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host, out var literal))
        {
            if (!IsPublic(literal)) return $"url must not point to a private or local address ({literal})";
        }
        else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                 || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
                 || !host.Contains('.'))
        {
            return "url must point to a public host name, not a local or internal one";
        }

        uri = parsed;
        return null;
    }

    /// <summary>Full check: shape rules plus DNS resolution of the host.</summary>
    public static async Task<SsrfVerdict> ResolveAsync(string? url, CancellationToken ct)
    {
        var error = CheckUrl(url, out var uri);
        if (error is not null || uri is null) return SsrfVerdict.Refuse(error ?? "invalid url");
        return await ResolveHostAsync(uri.IdnHost, ct);
    }

    /// <summary>Resolves <paramref name="host"/> and refuses it when any answer is a non-public address.</summary>
    public static async Task<SsrfVerdict> ResolveHostAsync(string host, CancellationToken ct)
    {
        host = host.TrimEnd('.');
        if (IPAddress.TryParse(host, out var literal))
            return IsPublic(literal) ? new SsrfVerdict([literal], null) : SsrfVerdict.Refuse($"{host} is not a public address");

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException ex)
        {
            return SsrfVerdict.Refuse($"{host} could not be resolved ({ex.SocketErrorCode})", transient: true);
        }
        if (addresses.Length == 0) return SsrfVerdict.Refuse($"{host} could not be resolved", transient: true);

        var blocked = addresses.FirstOrDefault(a => !IsPublic(a));
        return blocked is null
            ? new SsrfVerdict(addresses, null)
            : SsrfVerdict.Refuse($"{host} resolves to a private or local address ({blocked}); refusing to send");
    }

    /// <summary>
    /// False for loopback, private (RFC 1918 / fc00::/7), link-local (169.254/16 incl. the cloud metadata endpoint, fe80::/10),
    /// multicast, unspecified, IPv6 site-local, carrier-grade NAT (100.64/10), "this network" (0/8) and IPv4-mapped forms of any of those.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal || address.IsIPv6Teredo);

        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] switch
        {
            0 or 10 or 127 => false,                        // this network, private, loopback
            100 when b[1] is >= 64 and <= 127 => false,     // carrier-grade NAT
            169 when b[1] == 254 => false,                  // link-local incl. 169.254.169.254
            172 when b[1] is >= 16 and <= 31 => false,      // private
            192 when b[1] == 168 => false,                  // private
            192 when b[1] == 0 && b[2] == 0 => false,       // IETF protocol assignments
            198 when b[1] is 18 or 19 => false,             // benchmarking
            >= 224 => false,                                // multicast, reserved, broadcast
            _ => true,
        };
    }
}
