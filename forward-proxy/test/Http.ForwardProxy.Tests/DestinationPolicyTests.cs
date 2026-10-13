// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using DamianH.Http.ForwardProxy.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class DestinationPolicyTests
{
    [Theory]
    [InlineData("*", "anything.example", true)]
    [InlineData("example.com", "example.com", true)]
    [InlineData("EXAMPLE.com.", "example.com", true)]
    [InlineData("example.com", "www.example.com", false)]
    [InlineData("*.example.com", "www.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("*.example.com", "badexample.com", false)]
    [InlineData("192.0.2.1", "192.0.2.1", true)]
    [InlineData("[2001:db8::1]", "2001:db8::1", true)]
    [InlineData("2001:db8::1", "2001:db8:0::1", true)]
    [InlineData("192.0.2.1", "::ffff:192.0.2.1", true)]
    public void MatchesHostPatterns(string pattern, string host, bool expected) =>
        DestinationPolicy.Matches(pattern, host).ShouldBe(expected);

    [Fact]
    public void DefaultPortsAllow443ForConnectAnd80ForHttp()
    {
        var policy = Policy();
        policy.CheckDestination(new("example.com", 443, ForwardProxyRequestKind.Connect)).ShouldBeNull();
        policy.CheckDestination(new("example.com", 80, ForwardProxyRequestKind.Http)).ShouldBeNull();
        policy.CheckDestination(new("example.com", 22, ForwardProxyRequestKind.Connect))!.Value.ErrorType.ShouldBe(ProxyErrorTypes.HttpRequestDenied);
        policy.CheckDestination(new("example.com", 443, ForwardProxyRequestKind.Http))!.Value.StatusCode.ShouldBe(403);
    }

    [Fact]
    public void NullPortSetsAllowAnyPort()
    {
        var policy = Policy(o => o.AllowedConnectPorts = null);
        policy.CheckDestination(new("example.com", 22, ForwardProxyRequestKind.Connect)).ShouldBeNull();
    }

    [Fact]
    public void DeniedHostsWinOverAllowedHosts()
    {
        var policy = Policy(o =>
        {
            o.AllowedHosts.Add("*.example.com");
            o.DeniedHosts.Add("secret.example.com");
        });
        policy.CheckDestination(new("www.example.com", 443, ForwardProxyRequestKind.Connect)).ShouldBeNull();
        policy.CheckDestination(new("secret.example.com", 443, ForwardProxyRequestKind.Connect)).ShouldNotBeNull();
        policy.CheckDestination(new("example.org", 443, ForwardProxyRequestKind.Connect)).ShouldNotBeNull();
    }

    [Fact]
    public void NonPublicIpLiteralsFailFast()
    {
        var result = Policy().CheckDestination(new("169.254.169.254", 443, ForwardProxyRequestKind.Connect));
        result!.Value.ErrorType.ShouldBe(ProxyErrorTypes.DestinationIpProhibited);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%2")]
    [InlineData("ff02::1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2001:db8::1")]
    public void BlocksNonPublicAddresses(string address) =>
        Policy().IsAddressAllowed(IPAddress.Parse(address)).ShouldBeFalse();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::")]
    public void AllowsPublicAddresses(string address) =>
        Policy().IsAddressAllowed(IPAddress.Parse(address)).ShouldBeTrue();

    [Fact]
    public void AllowedNetworksOverrideBuiltInBlocksAndDeniedNetworksWin()
    {
        var policy = Policy(o =>
        {
            o.AllowedNetworks.Add(IPNetwork.Parse("10.0.0.0/8"));
            o.DeniedNetworks.Add(IPNetwork.Parse("10.9.0.0/16"));
            o.DeniedNetworks.Add(IPNetwork.Parse("8.8.8.0/24"));
        });
        policy.IsAddressAllowed(IPAddress.Parse("10.1.2.3")).ShouldBeTrue();
        policy.IsAddressAllowed(IPAddress.Parse("::ffff:10.1.2.3")).ShouldBeTrue();
        policy.IsAddressAllowed(IPAddress.Parse("10.9.1.1")).ShouldBeFalse();
        policy.IsAddressAllowed(IPAddress.Parse("8.8.8.8")).ShouldBeFalse();
        policy.IsAddressAllowed(IPAddress.Parse("127.0.0.1")).ShouldBeFalse();
    }

    [Fact]
    public void BlockingCanBeDisabled() =>
        Policy(o => o.BlockNonPublicAddresses = false).IsAddressAllowed(IPAddress.Loopback).ShouldBeTrue();

    [Fact]
    public async Task DialerBlocksNamesResolvingToNonPublicAddresses()
    {
        var options = Options.Create(new ForwardProxyOptions());
        var dialer = new ProxyDialer(new DestinationPolicy(options), options, NullLogger<ProxyDialer>.Instance);

        var ex = await Should.ThrowAsync<ProxyErrorException>(() => dialer.ConnectAsync("localhost", 443, TestContext.Current.CancellationToken));

        ex.Error.StatusCode.ShouldBe(403);
        ex.Error.ErrorType.ShouldBe(ProxyErrorTypes.DestinationIpProhibited);
    }

    private static DestinationPolicy Policy(Action<ForwardProxyOptions>? configure = null)
    {
        var options = new ForwardProxyOptions();
        configure?.Invoke(options);
        return new DestinationPolicy(Options.Create(options));
    }
}
