using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DamianH.HttpHybridCacheHandler.Compatibility.Tests;

internal sealed class CacheFixture : IDisposable
{
    private readonly ServiceProvider _services;
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public OriginHandler Origin { get; }
    public HttpClient Client { get; }
    public HybridCache Cache => _services.GetRequiredKeyedService<HybridCache>(ServiceCollectionExtensions.HybridCacheKey);

    public CacheFixture(Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<HttpHybridCacheHandlerOptions>? configure = null,
        ILargeHttpCacheContentStore? store = null,
        Action<IServiceCollection>? configureServices = null)
    {
        Origin = new OriginHandler(respond);
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(Clock)
            .AddLogging()
            .AddHttpHybridCacheHandler(options =>
            {
                options.IncludeDiagnosticHeaders = true;
                configure?.Invoke(options);
            });
        if (store != null)
        {
            services.AddSingleton(store);
        }
        configureServices?.Invoke(services);
        services.AddHttpClient("Compatibility")
            .ConfigurePrimaryHttpMessageHandler(() => Origin)
            .AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>());
        _services = services.BuildServiceProvider();
        Client = _services.GetRequiredService<IHttpClientFactory>().CreateClient("Compatibility");
    }

    public static HttpResponseMessage Response(string body = "cached body", string policy = "max-age=3600")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.TryAddWithoutValidation("Cache-Control", policy);
        return response;
    }

    public async Task<HttpResponseMessage> OnlyIfCached(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue { OnlyIfCached = true };
        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        Client.Dispose();
        _services.Dispose();
        Origin.Dispose();
    }
}

internal sealed class OriginHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncResponse { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (AsyncResponse != null)
        {
            return AsyncResponse(request, cancellationToken);
        }
        var response = respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

}

internal static class StorageDirectory
{
    public static string New()
    {
        // MTP changes the working directory to bin; keep Framework filesystem paths below MAX_PATH.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, ".git")) && !File.Exists(Path.Combine(root.FullName, ".git")))
        {
            root = root.Parent ?? throw new InvalidOperationException("Cannot find the repository storage root.");
        }
        return Path.Combine(root.FullName, ".compat-storage", Guid.NewGuid().ToString("N"));
    }
}
