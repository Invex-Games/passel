using System.Net;
using System.Net.Sockets;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Decides which addresses the preview fetcher may connect to. The fetcher asks for every address a host
///     resolves to, on the URL the user sent and on every redirect, and refuses the whole fetch when any one is
///     not allowed. The integration tests substitute a policy that lets the fetcher reach a page on the loopback.
/// </summary>
internal interface ILinkPreviewAddressPolicy
{
    bool Allows(IPAddress address);
}

/// <summary>
///     Allows public unicast addresses only: nothing on the loopback, in the private or carrier-grade NAT ranges,
///     on the link (where the cloud metadata services live), in the reserved, documentation or multicast ranges,
///     in IPv4 or IPv6. An IPv6 address that stands for an IPv4 one (mapped, or NAT64) is judged by that IPv4
///     address, since a connection to it ends up there; the deprecated IPv4-compatible and 6to4 forms are refused
///     outright.
/// </summary>
internal sealed class LinkPreviewAddressPolicy : ILinkPreviewAddressPolicy
{
    private static readonly IPNetwork[] BlockedV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"), // "this" network
        IPNetwork.Parse("10.0.0.0/8"), // private
        IPNetwork.Parse("100.64.0.0/10"), // carrier-grade NAT (some clouds serve metadata here)
        IPNetwork.Parse("127.0.0.0/8"), // loopback
        IPNetwork.Parse("169.254.0.0/16"), // link-local, including 169.254.169.254
        IPNetwork.Parse("172.16.0.0/12"), // private
        IPNetwork.Parse("192.0.0.0/24"), // IETF protocol assignments
        IPNetwork.Parse("192.0.2.0/24"), // documentation
        IPNetwork.Parse("192.88.99.0/24"), // former 6to4 relay anycast
        IPNetwork.Parse("192.168.0.0/16"), // private
        IPNetwork.Parse("198.18.0.0/15"), // benchmarking
        IPNetwork.Parse("198.51.100.0/24"), // documentation
        IPNetwork.Parse("203.0.113.0/24"), // documentation
        IPNetwork.Parse("224.0.0.0/4"), // multicast
        IPNetwork.Parse("240.0.0.0/4"), // reserved, and the broadcast address
    ];

    private static readonly IPNetwork[] BlockedV6 =
    [
        IPNetwork.Parse("::/96"), // unspecified, loopback and the deprecated IPv4-compatible addresses
        IPNetwork.Parse("::ffff:0:0:0/96"), // deprecated IPv4-translated addresses
        IPNetwork.Parse("64:ff9b:1::/48"), // local-use NAT64
        IPNetwork.Parse("100::/64"), // discard-only
        IPNetwork.Parse("2001:2::/48"), // benchmarking
        IPNetwork.Parse("2001:db8::/32"), // documentation
        IPNetwork.Parse("2002::/16"), // deprecated 6to4
        IPNetwork.Parse("fc00::/7"), // unique local
        IPNetwork.Parse("fe80::/10"), // link-local
        IPNetwork.Parse("fec0::/10"), // deprecated site-local
        IPNetwork.Parse("ff00::/8"), // multicast
    ];

    /// <summary>
    ///     The well-known NAT64 prefix: the last four bytes are the IPv4 address a translator forwards to.
    /// </summary>
    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");

    public bool Allows(IPAddress address) =>
        IsPublic(address);

    internal static bool IsPublic(IPAddress address)
    {
        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                return !BlockedV4.Any(network => network.Contains(address));

            case AddressFamily.InterNetworkV6:
                if (address.IsIPv4MappedToIPv6)
                    return IsPublic(address.MapToIPv4());

                if (Nat64.Contains(address))
                    return IsPublic(new IPAddress(address.GetAddressBytes()[12..]));

                return !BlockedV6.Any(network => network.Contains(address));

            default:
                return false;
        }
    }
}
