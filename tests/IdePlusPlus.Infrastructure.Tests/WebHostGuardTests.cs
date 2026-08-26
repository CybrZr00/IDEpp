using System.Net;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Infrastructure.Tests;

/// <summary>
/// This is the actual safety boundary fetch_url relies on, so every range it claims to block is
/// checked directly against IsBlocked rather than through a real DNS lookup or HTTP call - a wrong
/// bit here is a silent SSRF hole, not just a failed test.
/// </summary>
public class WebHostGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")] // cloud metadata endpoint
    [InlineData("0.0.0.0")]
    public void IsBlocked_ForPrivateOrLocalIPv4_IsTrue(string address)
    {
        WebHostGuard.IsBlocked(IPAddress.Parse(address)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")] // just below 172.16.0.0/12
    [InlineData("172.32.0.0")]     // just above 172.16.0.0/12
    [InlineData("192.167.255.255")]
    [InlineData("192.169.0.0")]
    public void IsBlocked_ForPublicIPv4_IsFalse(string address)
    {
        WebHostGuard.IsBlocked(IPAddress.Parse(address)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("::1")]                // loopback
    [InlineData("fe80::1")]            // link-local
    [InlineData("fc00::1")]            // unique-local
    [InlineData("fd12:3456:789a::1")]  // unique-local
    public void IsBlocked_ForPrivateOrLocalIPv6_IsTrue(string address)
    {
        WebHostGuard.IsBlocked(IPAddress.Parse(address)).ShouldBeTrue();
    }

    [Fact]
    public void IsBlocked_ForPublicIPv6_IsFalse()
    {
        WebHostGuard.IsBlocked(IPAddress.Parse("2001:4860:4860::8888")).ShouldBeFalse(); // Google public DNS
    }

    /// <summary>The classic bypass this guards against: a private IPv4 address wearing an IPv6
    /// ::ffff:a.b.c.d wrapper. Blocked only if it's unwrapped and re-checked as IPv4 first.</summary>
    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void IsBlocked_ForIPv4MappedPrivateAddress_IsTrue(string address)
    {
        WebHostGuard.IsBlocked(IPAddress.Parse(address)).ShouldBeTrue();
    }

    [Fact]
    public void IsBlocked_ForIPv4MappedPublicAddress_IsFalse()
    {
        WebHostGuard.IsBlocked(IPAddress.Parse("::ffff:8.8.8.8")).ShouldBeFalse();
    }
}
