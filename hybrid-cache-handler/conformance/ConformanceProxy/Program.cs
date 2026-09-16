// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

// Reverse proxy wrapping HttpHybridCacheHandler for the http-tests/cache-tests
// RFC 9111 conformance suite (https://github.com/http-tests/cache-tests).
// The suite's client sends requests through this proxy to the suite's origin server.

using System.Diagnostics;
using System.Net;
using Conformance;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);

var port = int.TryParse(builder.Configuration["port"], out var p) ? p : 8081;
var origin = builder.Configuration["origin"] ?? "http://127.0.0.1:8000";

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddHttpForwarder();
var framework = builder.Configuration["framework"] ?? "net10.0";
#if CONFORMANCE_STANDARD
if (framework != "netstandard2.0") throw new ArgumentException("This host only tests netstandard2.0 assets.");
#else
if (framework is not ("net10.0" or "net472")) throw new ArgumentException("Use the Standard proxy project for netstandard2.0.");
#endif
var useFileSystem = bool.TryParse(builder.Configuration["file-system"], out var enabled) && enabled;
var root = builder.Configuration["content-root"];
if (framework != "net472") CacheRuntime.AddServices(builder.Services, useFileSystem, root);

await using var app = builder.Build();
FrameworkBridge? bridge = null;
HttpMessageHandler handler;
string provenance;
if (framework == "net472")
{
    bridge = new FrameworkBridge(builder.Configuration["worker"]
        ?? throw new ArgumentException("--worker is required for net472."), useFileSystem, root ?? "");
    handler = bridge;
    try
    {
        await bridge.StartAsync();
    }
    catch
    {
        bridge.Dispose();
        throw;
    }
    provenance = bridge.Provenance;
}
else
{
    provenance = CacheRuntime.Verify(framework);
    handler = CacheRuntime.CreateHandler(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current)
    }, app.Services, useFileSystem);
}
Console.WriteLine(provenance);
using var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
var requestConfig = new ForwarderRequestConfig
{
    Version = HttpVersion.Version11,
    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
};

app.Map("/proxy-health", () => bridge is null || bridge.Healthy
    ? Results.Json(new { framework, provenance, ready = true, processId = Environment.ProcessId, workerProcessId = bridge?.WorkerProcessId })
    : Results.Problem("The .NET Framework worker failed.", statusCode: 503));

app.Map("/{**catch-all}", async httpContext =>
{
    var error = await forwarder.SendAsync(httpContext, origin, invoker, requestConfig);
    if (error != ForwarderError.None)
    {
        var errorFeature = httpContext.GetForwarderErrorFeature();
        app.Logger.LogWarning(errorFeature?.Exception, "Forwarding error: {Error} for {Method} {Path}", error,
            httpContext.Request.Method, httpContext.Request.Path);
    }
});

app.Logger.LogWarning("ConformanceProxy listening on http://127.0.0.1:{Port}, forwarding to {Origin}", port, origin);

await app.RunAsync();
