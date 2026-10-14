// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Sockets;
using DamianH.Http.ForwardProxy.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class ProxyDialerTests
{
    [Fact]
    public async Task ReportsSlowNameResolutionAsDnsTimeout()
    {
        var dialer = Dialer(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return [];
            },
            TimeSpan.FromMilliseconds(100));

        var ex = await Should.ThrowAsync<ProxyErrorException>(() => dialer.ConnectAsync("slow.example", 443, TestContext.Current.CancellationToken));

        ex.Error.StatusCode.ShouldBe(504);
        ex.Error.ErrorType.ShouldBe(ProxyErrorTypes.DnsTimeout);
    }

    [Fact]
    public async Task ReportsNamesWithoutAddressesAsDnsError()
    {
        var dialer = Dialer((_, _) => Task.FromResult<IPAddress[]>([]));

        var ex = await Should.ThrowAsync<ProxyErrorException>(() => dialer.ConnectAsync("empty.example", 443, TestContext.Current.CancellationToken));

        ex.Error.StatusCode.ShouldBe(502);
        ex.Error.ErrorType.ShouldBe(ProxyErrorTypes.DnsError);
    }

    [Fact]
    public async Task RequestCancellationDuringResolutionIsNotATimeout()
    {
        using var cts = new CancellationTokenSource();
        var dialer = Dialer(async (_, cancellationToken) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        });

        await Should.ThrowAsync<OperationCanceledException>(() => dialer.ConnectAsync("slow.example", 443, cts.Token));
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, 502, ProxyErrorTypes.ConnectionRefused)]
    [InlineData(SocketError.HostUnreachable, 502, ProxyErrorTypes.DestinationIpUnroutable)]
    [InlineData(SocketError.NetworkUnreachable, 502, ProxyErrorTypes.DestinationIpUnroutable)]
    [InlineData(SocketError.TimedOut, 504, ProxyErrorTypes.ConnectionTimeout)]
    [InlineData(SocketError.ConnectionReset, 502, ProxyErrorTypes.DestinationUnavailable)]
    [InlineData(SocketError.AddressNotAvailable, 502, ProxyErrorTypes.DestinationUnavailable)]
    public void MapsConnectFailuresToProxyStatusErrors(SocketError socketError, int statusCode, string errorType)
    {
        var error = ProxyDialer.ConnectError(socketError);

        error.StatusCode.ShouldBe(statusCode);
        error.ErrorType.ShouldBe(errorType);
    }

    private static ProxyDialer Dialer(Func<string, CancellationToken, Task<IPAddress[]>> resolveHostAsync, TimeSpan? connectTimeout = null)
    {
        var options = Options.Create(new ForwardProxyOptions { ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10) });
        return new ProxyDialer(new DestinationPolicy(options), options, NullLogger<ProxyDialer>.Instance)
        {
            ResolveHostAsync = resolveHostAsync,
        };
    }
}
