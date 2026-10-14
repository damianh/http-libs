// Copyright Damian Hickey

using DamianH.Http.HybridCacheHandler;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);

// Add HybridCache
builder.Services.AddHybridCache();

// Configure HTTP client for YARP with caching handler
builder.Services.AddHttpClient("YarpCachingClient")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler())
    .AddHttpMessageHandler(sp => new HttpHybridCacheHandler(
        sp.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(),
        TimeProvider.System,
        Options.Create(new HttpHybridCacheHandlerOptions
        {
            Mode = CacheMode.Shared, // Shared cache for proxy scenario
            FallbackCacheDuration = TimeSpan.FromMinutes(10),
            MaxCacheableContentSize = 50 * 1024 * 1024 // 50MB
        }),
        sp.GetRequiredService<ILogger<HttpHybridCacheHandler>>()
    ));

// Add YARP with direct forwarding
builder.Services.AddHttpForwarder();

var app = builder.Build();

// Get the forwarder and HTTP client factory
var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();
var httpClient = httpClientFactory.CreateClient("YarpCachingClient");

// Map a route that forwards to GitHub API
app.Map("/api/{**catch-all}", async httpContext =>
{
    var destinationPrefix = "https://api.github.com/";
    await forwarder.SendAsync(httpContext, destinationPrefix, httpClient);
});

Console.WriteLine("YARP Caching Proxy running on http://localhost:5000");
Console.WriteLine("Try: http://localhost:5000/api/repos/dotnet/runtime");
Console.WriteLine("\nProxy will cache responses from GitHub API (Shared Cache Mode)");
Console.WriteLine("  - Does NOT cache 'Cache-Control: private' responses");
Console.WriteLine("  - Prefers 's-maxage' over 'max-age'");
Console.WriteLine("  - RFC 9111 compliant shared cache behavior");

app.Run();
