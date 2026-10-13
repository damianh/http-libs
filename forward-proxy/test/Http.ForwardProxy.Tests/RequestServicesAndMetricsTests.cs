// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class RequestServicesAndMetricsTests
{
    [Fact]
    public async Task RecordsHttpForwardingFailuresAsErrors()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync();
        var meterFactory = proxy.Services.GetRequiredService<IMeterFactory>();
        var outcomes = new ConcurrentQueue<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                // The meter comes from this host's IMeterFactory, so measurements from other tests are excluded.
                if (instrument.Meter.Scope == meterFactory && instrument.Name == "forward_proxy.requests")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "forward_proxy.outcome")
                {
                    outcomes.Enqueue((string)tag.Value!);
                }
            }
        });
        meterListener.Start();
        using var client = ConnectTunnelTests.CreateClient(proxy);

        using var failed = await client.GetAsync($"http://127.0.0.1:{closedPort}/", TestContext.Current.CancellationToken);
        using var succeeded = await client.GetAsync(origin.Uri, TestContext.Current.CancellationToken);

        ((int)failed.StatusCode).ShouldBe(502);
        succeeded.IsSuccessStatusCode.ShouldBeTrue();
        outcomes.ShouldBe(["error", "allowed"]);
    }

    [Fact]
    public async Task ResolvesScopedFiltersPerRequest()
    {
        await using var origin = await OriginHost.StartAsync();
        using var echo = new EchoServer();
        var tracker = new FilterTracker();
        await using var proxy = await ProxyHost.StartAsync(configureServices: services => services
            .AddSingleton(tracker)
            .AddScoped<IForwardProxyDestinationFilter, ScopedFilter>());
        using var client = ConnectTunnelTests.CreateClient(proxy);

        using (var response = await client.GetAsync(origin.Uri, TestContext.Current.CancellationToken))
        {
            response.IsSuccessStatusCode.ShouldBeTrue();
        }

        using (var socket = await RawProxy.ConnectAsync(proxy.Port))
        {
            await RawProxy.SendAsync(socket, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\n\r\n");
            (await RawProxy.ReadResponseAsync(socket)).StatusCode.ShouldBe(200);
        }

        tracker.Instances.Count.ShouldBe(2);
        tracker.Instances.Distinct().Count().ShouldBe(2);
    }

    private sealed class FilterTracker
    {
        public ConcurrentQueue<ScopedFilter> Instances { get; } = new();
    }

    private sealed class ScopedFilter(FilterTracker tracker) : IForwardProxyDestinationFilter
    {
        public ValueTask<bool> IsAllowedAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken)
        {
            tracker.Instances.Enqueue(this);
            return ValueTask.FromResult(true);
        }
    }
}
