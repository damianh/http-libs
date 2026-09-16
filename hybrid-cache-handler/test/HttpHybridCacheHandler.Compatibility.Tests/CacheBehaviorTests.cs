using System.Net;
using System.Net.Http.Headers;

namespace DamianH.HttpHybridCacheHandler.Compatibility.Tests;

public sealed class CacheBehaviorTests
{
    private const string Url = "https://compatibility.example.test/item";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fresh_hit_preserves_body_and_age_without_an_origin_call()
    {
        using var fixture = new CacheFixture(_ => CacheFixture.Response());
        using var miss = await fixture.Client.GetAsync(Url, Ct);
        Assert.Equal("MISS", Assert.Single(miss.Headers.GetValues("X-Cache-Diagnostic")));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        using var hit = await fixture.Client.GetAsync(Url, Ct);
        Assert.Equal("cached body", await hit.Content.ReadAsStringAsync());
        Assert.Equal(TimeSpan.FromSeconds(10), hit.Headers.Age);
        Assert.Equal("HIT-FRESH", Assert.Single(hit.Headers.GetValues("X-Cache-Diagnostic")));
        Assert.Equal(1, fixture.Origin.Calls);
    }

    [Theory]
    [InlineData("no-store, max-age=3600", CacheMode.Private, 2)]
    [InlineData("private, max-age=3600", CacheMode.Shared, 2)]
    [InlineData("private, max-age=3600", CacheMode.Private, 1)]
    [InlineData("public, max-age=3600", CacheMode.Shared, 1)]
    public async Task Privacy_and_no_store_are_preserved(string policy, CacheMode mode, int calls)
    {
        using var fixture = new CacheFixture(_ => CacheFixture.Response(policy: policy), o => o.Mode = mode);
        using var first = await fixture.Client.GetAsync(Url, Ct);
        using var second = await fixture.Client.GetAsync(Url, Ct);
        Assert.Equal("cached body", await second.Content.ReadAsStringAsync());
        Assert.Equal(calls, fixture.Origin.Calls);
    }

    [Fact]
    public async Task Request_no_store_bypasses_and_does_not_publish()
    {
        using var fixture = new CacheFixture(_ => CacheFixture.Response());
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        using var response = await fixture.Client.SendAsync(request, Ct);
        using var absent = await fixture.OnlyIfCached(Url);
        Assert.Equal(HttpStatusCode.GatewayTimeout, absent.StatusCode);
        Assert.Equal(1, fixture.Origin.Calls);
    }

    [Theory]
    [InlineData("max-age=1")]
    [InlineData("no-cache, max-age=3600")]
    public async Task Conditional_304_merges_metadata_and_keeps_body(string policy)
    {
        var calls = 0;
        using var fixture = new CacheFixture(request =>
        {
            if (++calls == 1)
            {
                var response = CacheFixture.Response(policy: policy);
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                response.Headers.Add("X-Metadata", "before");
                return response;
            }
            Assert.Equal("\"v1\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            var updated = new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new ByteArrayContent([]) };
            updated.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
            updated.Headers.Add("X-Metadata", "after");
            updated.Content.Headers.TryAddWithoutValidation("Content-Language", "fr");
            return updated;
        });
        using var first = await fixture.Client.GetAsync(Url, Ct);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        using var revalidated = await fixture.Client.GetAsync(Url, Ct);
        using var cached = await fixture.Client.GetAsync(Url, Ct);
        foreach (var response in new[] { revalidated, cached })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("cached body", await response.Content.ReadAsStringAsync());
            Assert.Equal("after", Assert.Single(response.Headers.GetValues("X-Metadata")));
            Assert.Equal("fr", Assert.Single(response.Content.Headers.ContentLanguage));
        }
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Fresh_client_validator_returns_bodyless_304()
    {
        using var fixture = new CacheFixture(_ =>
        {
            var response = CacheFixture.Response();
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        });
        using var first = await fixture.Client.GetAsync(Url, Ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"v1\""));
        using var response = await fixture.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, fixture.Origin.Calls);
    }

    [Theory]
    [InlineData("GET", HttpStatusCode.NoContent)]
    [InlineData("GET", HttpStatusCode.OK)]
    [InlineData("HEAD", HttpStatusCode.OK)]
    public async Task Origin_without_assigned_content_is_empty_and_passes_through(string method, HttpStatusCode status)
    {
        using var fixture = new CacheFixture(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            response.Headers.Add("X-Bodyless", "origin");
            return response;
        });
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), Url);
            using var response = await fixture.Client.SendAsync(request, Ct);
            Assert.Equal(status, response.StatusCode);
            Assert.NotNull(response.Content);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("origin", Assert.Single(response.Headers.GetValues("X-Bodyless")));
        }
        // Preserve the existing policy: responses without Content-Type are not stored.
        Assert.Equal(2, fixture.Origin.Calls);
    }

    [Fact]
    public async Task Origin_304_without_assigned_content_updates_metadata_and_retains_cached_body()
    {
        var calls = 0;
        using var fixture = new CacheFixture(request =>
        {
            if (++calls == 1)
            {
                var initial = CacheFixture.Response(policy: "max-age=1");
                initial.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return initial;
            }
            Assert.Equal("\"v1\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            var updated = new HttpResponseMessage(HttpStatusCode.NotModified);
            updated.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            updated.Headers.Add("X-Bodyless", "revalidated");
            return updated;
        });
        using var first = await fixture.Client.GetAsync(Url, Ct);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        using var updatedResponse = await fixture.Client.GetAsync(Url, Ct);
        using var cached = await fixture.Client.GetAsync(Url, Ct);
        foreach (var response in new[] { updatedResponse, cached })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("cached body", await response.Content.ReadAsStringAsync());
            Assert.Equal("revalidated", Assert.Single(response.Headers.GetValues("X-Bodyless")));
        }
        Assert.Equal(2, fixture.Origin.Calls);
    }

    [Fact]
    public async Task Head_updates_get_metadata_without_replacing_get_body()
    {
        using var fixture = new CacheFixture(request =>
        {
            var head = request.Method == HttpMethod.Head;
            var response = CacheFixture.Response(head ? "" : "cached body", head ? "max-age=3600" : "max-age=1");
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            response.Content.Headers.ContentLength = 11;
            response.Headers.Add("X-Metadata", head ? "after-head" : "before");
            return response;
        });
        using var initial = await fixture.Client.GetAsync(Url, Ct);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, Url);
        using var head = await fixture.Client.SendAsync(headRequest, Ct);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal(11, head.Content.Headers.ContentLength);
        using var get = await fixture.Client.GetAsync(Url, Ct);
        Assert.Equal("cached body", await get.Content.ReadAsStringAsync());
        Assert.Equal("after-head", Assert.Single(get.Headers.GetValues("X-Metadata")));
        Assert.Equal(2, fixture.Origin.Calls);
    }

    [Fact]
    public async Task Vary_partitions_and_unsafe_methods_invalidate_all_variants()
    {
        using var fixture = new CacheFixture(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            var value = Assert.Single(request.Headers.GetValues("Accept-Language"));
            var response = CacheFixture.Response(value);
            response.Headers.Vary.Add("Accept-Language");
            return response;
        });
        async Task Get(string language)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.Add("Accept-Language", language);
            using var response = await fixture.Client.SendAsync(request, Ct);
            Assert.Equal(language, await response.Content.ReadAsStringAsync());
        }
        await Get("en");
        await Get("fr");
        await Get("en");
        await Get("fr");
        Assert.Equal(2, fixture.Origin.Calls);
        using var posted = await fixture.Client.PostAsync(Url, new StringContent("change"), Ct);
        await Get("en");
        await Get("fr");
        Assert.Equal(5, fixture.Origin.Calls);
    }

    [Theory]
    [InlineData(CacheMode.Private, 2)]
    [InlineData(CacheMode.Shared, 1)]
    public async Task Targeted_cache_control_only_affects_shared_caches(CacheMode mode, int calls)
    {
        using var fixture = new CacheFixture(_ =>
        {
            var response = CacheFixture.Response(policy: "max-age=0");
            response.Headers.Add("CDN-Cache-Control", "max-age=3600");
            return response;
        }, options => options.Mode = mode);
        using var first = await fixture.Client.GetAsync(Url, Ct);
        using var second = await fixture.Client.GetAsync(Url, Ct);
        Assert.Equal(calls, fixture.Origin.Calls);
    }

    [Fact]
    public async Task Compression_roundtrip_preserves_original_bytes()
    {
        var body = new string('a', 32 * 1024) + "héllo \u2603";
        using var fixture = new CacheFixture(_ => CacheFixture.Response(body), o => o.CompressionThreshold = 16);
        using var first = await fixture.Client.GetAsync(Url, Ct);
        using var second = await fixture.Client.GetAsync(Url, Ct);
        Assert.Equal(body, await first.Content.ReadAsStringAsync());
        Assert.Equal(body, await second.Content.ReadAsStringAsync());
        Assert.Equal("true", Assert.Single(second.Headers.GetValues("X-Cache-Compressed")));
        Assert.Equal(1, fixture.Origin.Calls);
    }

    [Fact]
    public void Request_key_format_does_not_change_between_library_targets()
    {
        var generator = new CacheKeyGenerator(new HttpHybridCacheHandlerOptions { VaryHeaders = new[] { "Accept-Language" } });
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("Accept-Language", "en");
        request.Headers.Range = new RangeHeaderValue(0, 9);
        Assert.Equal("GET:" + Url + "::Accept-Language:en", generator.GenerateVaryAwareCacheKey(request));
        Assert.Equal("HEAD:" + Url + "::Accept-Language:en|Range:bytes=0-9",
            generator.GenerateVaryAwareCacheKey(request, HttpMethod.Head, includeRange: true));
    }

    [Fact]
    public async Task Background_revalidation_owns_a_request_snapshot_after_caller_disposal()
    {
        using var fixture = new CacheFixture(_ =>
        {
            var response = CacheFixture.Response("before", "max-age=1, stale-while-revalidate=60");
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        });
        using var initial = await fixture.Client.GetAsync(Url, Ct);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var started = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Origin.AsyncResponse = async (snapshot, _) =>
        {
            started.TrySetResult(snapshot);
            await release.Task;
            return CacheFixture.Response("after");
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, Url) { Version = new Version(1, 1) };
        request.Headers.Add("X-Snapshot", "original");
        try
        {
            using var stale = await fixture.Client.SendAsync(request, Ct);
            Assert.Equal("before", await stale.Content.ReadAsStringAsync());
            Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
            var snapshot = await started.Task;
            request.Headers.Remove("X-Snapshot");
            request.Dispose();
            Assert.NotSame(request, snapshot);
            Assert.Equal("original", Assert.Single(snapshot.Headers.GetValues("X-Snapshot")));
            Assert.Equal("\"v1\"", Assert.Single(snapshot.Headers.IfNoneMatch).Tag);
            Assert.Equal(new Version(1, 1), snapshot.Version);
        }
        finally
        {
            release.TrySetResult(true);
        }
        var refreshed = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var cached = await fixture.OnlyIfCached(Url);
            if (await cached.Content.ReadAsStringAsync() == "after")
            {
                refreshed = true;
                break;
            }
            await Task.Delay(20, Ct);
        }
        Assert.True(refreshed, "Background revalidation did not publish the updated response.");
    }
}
