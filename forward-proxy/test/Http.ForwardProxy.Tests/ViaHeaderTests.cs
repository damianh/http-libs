// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using DamianH.Http.ForwardProxy.Internal.Forwarding;
using Microsoft.Extensions.Primitives;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class ViaHeaderTests
{
    [Theory]
    [InlineData("1.1 forward-proxy")]
    [InlineData("1.1 Forward-Proxy")]
    [InlineData("HTTP/1.1 forward-proxy")]
    [InlineData("1.0 gateway, 1.1 forward-proxy")]
    [InlineData("1.1 forward-proxy (comment), 1.1 gateway")]
    [InlineData("1.1 gateway (a, b), \t1.1\tforward-proxy\t")]
    [InlineData("1.1 gateway (nested (x, y) \\) ,), 1.1 forward-proxy")]
    public void FindsTheProxyName(string via) =>
        ViaHeader.ContainsReceivedBy(new StringValues(via), "forward-proxy").ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("forward-proxy")]
    [InlineData("1.1 forward-proxy-2")]
    [InlineData("1.1 gateway (via forward-proxy)")]
    [InlineData("1.1 gateway (a, 1.1 forward-proxy)")]
    [InlineData("1.1 forward-proxy.example.com:8080")]
    public void IgnoresOtherEntries(string via) =>
        ViaHeader.ContainsReceivedBy(new StringValues(via), "forward-proxy").ShouldBeFalse();

    [Fact]
    public void ChecksEveryFieldLine() =>
        ViaHeader.ContainsReceivedBy(new StringValues(["1.1 gateway", "1.1 forward-proxy"]), "forward-proxy").ShouldBeTrue();
}
