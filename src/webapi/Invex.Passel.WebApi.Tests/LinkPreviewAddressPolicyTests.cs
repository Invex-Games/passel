using System.Net;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     The addresses the preview fetcher may and may not connect to. Every address a linked host resolves to goes
///     through this, so the table is the whole of the server-side request forgery guard at the address level.
/// </summary>
public sealed class LinkPreviewAddressPolicyTests
{
    private static readonly LinkPreviewAddressPolicy Policy = new();

    [Theory]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("127.255.255.255", "loopback, last address")]
    [InlineData("10.0.0.1", "private 10/8")]
    [InlineData("10.255.255.255", "private 10/8, last address")]
    [InlineData("172.16.0.1", "private 172.16/12, first network")]
    [InlineData("172.31.255.255", "private 172.16/12, last address")]
    [InlineData("192.168.1.1", "private 192.168/16")]
    [InlineData("169.254.169.254", "link-local: the cloud metadata service")]
    [InlineData("169.254.0.1", "link-local")]
    [InlineData("100.64.0.1", "carrier-grade NAT")]
    [InlineData("100.100.100.200", "carrier-grade NAT: a cloud metadata service")]
    [InlineData("100.127.255.255", "carrier-grade NAT, last address")]
    [InlineData("0.0.0.0", "unspecified")]
    [InlineData("0.1.2.3", "this network")]
    [InlineData("192.0.0.1", "IETF protocol assignments")]
    [InlineData("192.0.2.1", "documentation")]
    [InlineData("192.88.99.1", "former 6to4 relay")]
    [InlineData("198.18.0.1", "benchmarking")]
    [InlineData("198.19.255.255", "benchmarking, last address")]
    [InlineData("198.51.100.1", "documentation")]
    [InlineData("203.0.113.1", "documentation")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("239.255.255.255", "multicast, last address")]
    [InlineData("240.0.0.1", "reserved")]
    [InlineData("255.255.255.255", "broadcast")]
    [InlineData("::1", "IPv6 loopback")]
    [InlineData("::", "IPv6 unspecified")]
    [InlineData("::127.0.0.1", "IPv4-compatible (deprecated)")]
    [InlineData("::ffff:127.0.0.1", "IPv4-mapped loopback")]
    [InlineData("::ffff:10.0.0.1", "IPv4-mapped private")]
    [InlineData("::ffff:169.254.169.254", "IPv4-mapped metadata")]
    [InlineData("::ffff:0:127.0.0.1", "IPv4-translated (deprecated)")]
    [InlineData("64:ff9b::7f00:1", "NAT64 of 127.0.0.1")]
    [InlineData("64:ff9b::a00:1", "NAT64 of 10.0.0.1")]
    [InlineData("64:ff9b::a9fe:a9fe", "NAT64 of 169.254.169.254")]
    [InlineData("64:ff9b:1::808:808", "local-use NAT64, even of a public address")]
    [InlineData("2002:7f00:1::", "6to4 (deprecated)")]
    [InlineData("2002:808:808::", "6to4 (deprecated), even of a public address")]
    [InlineData("fe80::1", "IPv6 link-local")]
    [InlineData("febf::1", "IPv6 link-local, last network")]
    [InlineData("fc00::1", "unique local")]
    [InlineData("fd12:3456:789a::1", "unique local")]
    [InlineData("fec0::1", "site-local (deprecated)")]
    [InlineData("ff02::1", "IPv6 multicast")]
    [InlineData("100::1", "discard-only")]
    [InlineData("2001:db8::1", "IPv6 documentation")]
    [InlineData("2001:2::1", "IPv6 benchmarking")]
    public void Refuses_addresses_the_fetcher_must_never_reach(string address, string because)
    {
        Assert.False(Policy.Allows(IPAddress.Parse(address)), because);
    }

    [Theory]
    [InlineData("93.184.216.34", "a public address")]
    [InlineData("8.8.8.8", "a public address")]
    [InlineData("1.1.1.1", "a public address")]
    [InlineData("9.255.255.255", "just below 10/8")]
    [InlineData("11.0.0.0", "just above 10/8")]
    [InlineData("172.15.255.255", "just below 172.16/12")]
    [InlineData("172.32.0.0", "just above 172.16/12")]
    [InlineData("192.167.255.255", "just below 192.168/16")]
    [InlineData("192.169.0.0", "just above 192.168/16")]
    [InlineData("100.63.255.255", "just below carrier-grade NAT")]
    [InlineData("100.128.0.0", "just above carrier-grade NAT")]
    [InlineData("169.253.255.255", "just below link-local")]
    [InlineData("169.255.0.0", "just above link-local")]
    [InlineData("223.255.255.255", "just below multicast")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", "a public IPv6 address")]
    [InlineData("2001:4860:4860::8888", "a public IPv6 address")]
    [InlineData("2001:db7:ffff:ffff:ffff:ffff:ffff:ffff", "just below the documentation range")]
    [InlineData("::ffff:8.8.8.8", "IPv4-mapped public address")]
    [InlineData("64:ff9b::808:808", "NAT64 of a public address")]
    [InlineData("fbff::1", "just below unique local")]
    [InlineData("fe00::1", "just above unique local, below link-local")]
    public void Allows_public_addresses(string address, string because)
    {
        Assert.True(Policy.Allows(IPAddress.Parse(address)), because);
    }
}
