using System.Net;
using System.Net.Http.Headers;

namespace DamianH.HttpHybridCacheHandler.Compatibility.Tests;

public sealed class HeaderCompatibilityTests
{
    [Fact]
    public void Header_reader_preserves_the_target_public_API_value_boundaries()
    {
        static HttpResponseMessage Create()
        {
            var response = CacheFixture.Response(policy: "PUBLIC, max-age=60, max-age=60");
            response.Headers.TryAddWithoutValidation("ETag", " \"v1\" ");
            response.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "a=1; Path=/", "b=2; Expires=Wed, 01 Jan 2031 00:00:00 GMT" });
            response.Headers.TryAddWithoutValidation("X-Opaque", new[] { "a, b", " c " });
            return response;
        }
        using var response = Create();
        using var reference = Create();
        var snapshot = HeaderCompatibility.Read(response.Headers).ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
#if STANDARD_ASSETS || NETFRAMEWORK
        var expected = reference.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("\"v1\"", Assert.Single(snapshot["ETag"]));
#else
        var expected = reference.Headers.NonValidated.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(" \"v1\" ", Assert.Single(snapshot["ETag"]));
#endif
        foreach (var pair in expected)
        {
            Assert.Equal(pair.Value, snapshot[pair.Key]);
        }
        Assert.Equal(new[] { "a=1; Path=/", "b=2; Expires=Wed, 01 Jan 2031 00:00:00 GMT" }, snapshot["Set-Cookie"]);
        Assert.Equal(new[] { "a, b", " c " }, snapshot["X-Opaque"]);
        var policy = HttpCacheHeaderParser.ParseCacheControl(snapshot["Cache-Control"]);
        Assert.True(policy.Public);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.MaxAge);
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("Wed, 01 Jan 2031 00:00:00 UTC")]
    [InlineData("2031:01-01T00:00:00Z")]
    public void Strict_date_parser_rejects_malformed_dates(string value)
    {
        Assert.Null(HttpCacheHeaderParser.ParseSingleHttpDate(new[] { value }));
    }

    [Fact]
    public void Valid_http_date_survives_header_enumeration()
    {
        using var response = CacheFixture.Response();
        response.Content.Headers.TryAddWithoutValidation("Expires", "Wed, 01 Jan 2031 00:00:00 GMT");
        var values = HeaderCompatibility.Read(response.Content.Headers).Single(h => h.Key == "Expires").Value;
        Assert.Equal(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero), HttpCacheHeaderParser.ParseSingleHttpDate(values));
    }

    [Fact]
    public void Malformed_UTC_date_normalization_is_an_explicit_target_difference()
    {
        using var response = CacheFixture.Response();
        response.Content.Headers.TryAddWithoutValidation("Expires", "Wed, 01 Jan 2031 00:00:00 UTC");
        var values = HeaderCompatibility.Read(response.Content.Headers).Single(h => h.Key == "Expires").Value;
#if STANDARD_ASSETS
        // The Standard assembly enumerates publicly even on this modern host.
        Assert.Equal(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero), HttpCacheHeaderParser.ParseSingleHttpDate(values));
#else
        // Framework leaves UTC unparsed; the modern raw snapshot also retains it verbatim.
        Assert.Null(HttpCacheHeaderParser.ParseSingleHttpDate(values));
#endif
    }

    [Fact]
    public async Task Noncacheable_pass_through_keeps_cookie_value_separation()
    {
        using var fixture = new CacheFixture(_ =>
        {
            var response = CacheFixture.Response(policy: "no-store");
            response.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "a=1; Path=/", "b=2; Path=/" });
            response.Headers.TryAddWithoutValidation("X-Opaque", new[] { "one,two", "three" });
            return response;
        });
        using var response = await fixture.Client.GetAsync("https://compatibility.example.test/pass", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "a=1; Path=/", "b=2; Path=/" }, response.Headers.GetValues("Set-Cookie"));
        Assert.Equal(new[] { "one,two", "three" }, response.Headers.GetValues("X-Opaque"));
        using var absent = await fixture.OnlyIfCached("https://compatibility.example.test/pass");
        Assert.Equal(HttpStatusCode.GatewayTimeout, absent.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cached_and_initial_uncacheable_snapshots_follow_the_selected_asset_contract(bool noStore)
    {
        HttpResponseMessage Create()
        {
            var response = CacheFixture.Response(policy: noStore ? "no-store" : "PUBLIC, max-age=3600");
            response.Headers.TryAddWithoutValidation("ETag", " \"v1\" ");
            response.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "a=1; Path=/", "b=2; Path=/" });
            response.Headers.TryAddWithoutValidation("X-Opaque", new[] { " one ", "two,three" });
            return response;
        }
        using var reference = Create();
#if STANDARD_ASSETS || NETFRAMEWORK
        var expected = reference.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray());
#else
        var expected = reference.Headers.NonValidated.ToDictionary(h => h.Key, h => h.Value.ToArray());
#endif
        using var fixture = new CacheFixture(_ => Create());
        for (var i = 0; i < 2; i++)
        {
            // Use cold keys for no-store: a cached null entry bypasses the snapshotting factory.
            var url = "https://compatibility.example.test/snapshot" + (noStore ? "-" + i : "");
            using var response = await fixture.Client.GetAsync(url, TestContext.Current.CancellationToken);
            var actual = PublicSnapshot(response.Headers);
            foreach (var header in expected)
            {
                Assert.Equal(header.Value, actual[header.Key]);
            }
            Assert.Equal("cached body", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(noStore ? 2 : 1, fixture.Origin.Calls);
    }

    [Theory]
    [InlineData("max-age=3600", "no-store", CacheMode.Private, 2)]
    [InlineData("max-age=3600, no-store", "no-store", CacheMode.Private, 2)]
    [InlineData("max-age=3600", "private", CacheMode.Shared, 2)]
    [InlineData("max-age=3600, private", "private", CacheMode.Shared, 2)]
    [InlineData("max-age=3600, public", "public", CacheMode.Shared, 1)]
    public async Task Separate_and_duplicate_Cache_Control_fields_preserve_storage_protections(
        string firstField, string secondField, CacheMode mode, int calls)
    {
        using var fixture = new CacheFixture(_ =>
        {
            var response = CacheFixture.Response(policy: firstField);
            response.Headers.TryAddWithoutValidation("Cache-Control", secondField);
            return response;
        }, options => options.Mode = mode);
        using var first = await fixture.Client.GetAsync("https://compatibility.example.test/fields", TestContext.Current.CancellationToken);
        using var second = await fixture.Client.GetAsync("https://compatibility.example.test/fields", TestContext.Current.CancellationToken);
        Assert.Equal("cached body", await second.Content.ReadAsStringAsync());
        Assert.Equal(calls, fixture.Origin.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_qualified_no_cache_directives_strip_every_named_field(bool separateFields)
    {
        using var fixture = new CacheFixture(_ =>
        {
            var response = CacheFixture.Response(policy: separateFields
                ? "max-age=3600, no-cache=\"Set-Cookie\""
                : "max-age=3600, no-cache=\"Set-Cookie\", no-cache=\"X-Trace-Id\"");
            if (separateFields) response.Headers.TryAddWithoutValidation("Cache-Control", "no-cache=\"X-Trace-Id\"");
            response.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "a=1", "b=2" });
            response.Headers.TryAddWithoutValidation("X-Trace-Id", "trace");
            return response;
        });
        using var first = await fixture.Client.GetAsync("https://compatibility.example.test/qualified", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "a=1", "b=2" }, first.Headers.GetValues("Set-Cookie"));
        Assert.True(first.Headers.Contains("X-Trace-Id"));
        using var second = await fixture.Client.GetAsync("https://compatibility.example.test/qualified", TestContext.Current.CancellationToken);
        Assert.False(second.Headers.Contains("Set-Cookie"));
        Assert.False(second.Headers.Contains("X-Trace-Id"));
        Assert.Equal("cached body", await second.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Origin.Calls);
    }

    [Fact]
    public async Task Revalidated_304_replays_target_specific_validator_snapshot()
    {
        var calls = 0;
        using var fixture = new CacheFixture(_ =>
        {
            if (++calls == 1)
            {
                var initial = CacheFixture.Response(policy: "max-age=1");
                initial.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return initial;
            }
            var updated = new HttpResponseMessage(HttpStatusCode.NotModified);
            updated.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
            updated.Headers.TryAddWithoutValidation("ETag", " W/\"v2\" ");
            return updated;
        });
        using var first = await fixture.Client.GetAsync("https://compatibility.example.test/validator", TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        using var updatedResponse = await fixture.Client.GetAsync("https://compatibility.example.test/validator", TestContext.Current.CancellationToken);
        using var cached = await fixture.Client.GetAsync("https://compatibility.example.test/validator", TestContext.Current.CancellationToken);
        foreach (var response in new[] { updatedResponse, cached })
        {
            var snapshot = PublicSnapshot(response.Headers);
#if STANDARD_ASSETS || NETFRAMEWORK
            Assert.Equal("W/\"v2\"", Assert.Single(snapshot["ETag"]));
#else
            Assert.Equal(" W/\"v2\" ", Assert.Single(snapshot["ETag"]));
#endif
            Assert.Equal("cached body", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, fixture.Origin.Calls);
    }

    private static Dictionary<string, string[]> PublicSnapshot(HttpHeaders headers)
    {
#if NETFRAMEWORK
        return headers.ToDictionary(h => h.Key, h => h.Value.ToArray());
#else
        return headers.NonValidated.ToDictionary(h => h.Key, h => h.Value.ToArray());
#endif
    }
}
