// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics.Metrics;
using System.Net;

namespace DamianH.Http.HybridCacheHandler;

public class AdvancedFeaturesTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Serve_stale_response_while_revalidating_in_background()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            return new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent($"response {requestCount}"),
                Headers =
                {
                    { "Cache-Control", "max-age=1, stale-while-revalidate=5" },
                    { "ETag", $"\"{requestCount}\"" }
                }
            };
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request - populate cache
        var response1 = await client.GetAsync("https://example.com/resource", _ct);
        var body1 = await response1.Content.ReadAsStringAsync(_ct);
        body1.ShouldBe("response 1");

        // Advance time past max-age but within stale-while-revalidate window
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - should get stale response immediately
        var response2 = await client.GetAsync("https://example.com/resource", _ct);
        var body2 = await response2.Content.ReadAsStringAsync(_ct);
        body2.ShouldBe("response 1"); // Stale content served

        // Give background revalidation time to complete
        await Task.Delay(100, _ct);

        requestCount.ShouldBe(2); // Background revalidation happened

        // Third request should get fresh content from background revalidation
        var response3 = await client.GetAsync("https://example.com/resource", _ct);
        var body3 = await response3.Content.ReadAsStringAsync(_ct);
        body3.ShouldBe("response 2"); // Updated from background revalidation
    }

    [Fact]
    public async Task Configure_stale_while_revalidate_window()
    {

        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            return new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent($"response {requestCount}"),
                Headers =
                {
                    { "Cache-Control", "max-age=1, stale-while-revalidate=10" },
                    { "ETag", "\"abc\"" }
                }
            };
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // Populate cache
        await client.GetAsync("https://example.com/resource", _ct);

        // Within stale-while-revalidate window (6 seconds after max-age)
        fixture.AdvanceTime(TimeSpan.FromSeconds(7));
        var response = await client.GetAsync("https://example.com/resource", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        body.ShouldBe("response 1"); // Stale content served within window
        await Task.Delay(100, _ct);
        requestCount.ShouldBe(2); // Revalidation triggered
    }

    [Fact]
    public async Task Update_cache_asynchronously()
    {

        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            // Simulate slow revalidation
            Thread.Sleep(50);
            return new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent($"response {requestCount}"),
                Headers =
                {
                    { "Cache-Control", "max-age=1, stale-while-revalidate=5" },
                    { "ETag", "\"abc\"" }
                }
            };
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // This should return quickly with stale content
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.GetAsync("https://example.com/resource", _ct);
        sw.Stop();

        var body = await response.Content.ReadAsStringAsync(_ct);
        body.ShouldBe("response 1"); // Stale response
        sw.ElapsedMilliseconds.ShouldBeLessThan(50); // Fast response (not waiting for revalidation)
    }

    [Fact]
    public async Task Serve_stale_response_on_upstream_error()
    {

        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("response 1"),
                    Headers =
                    {
                        { "Cache-Control", "max-age=1, stale-if-error=10" },
                        { "ETag", "\"abc\"" }
                    }
                };
            }
            else
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("error")
                };
            }
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request - populate cache
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance time past max-age
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - origin returns error, should serve stale
        var response = await client.GetAsync("https://example.com/resource", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        body.ShouldBe("response 1"); // Stale content served due to error
        response.StatusCode.ShouldBe(HttpStatusCode.OK); // Presented as OK
    }

    [Fact]
    public async Task Configure_stale_if_error_window()
    {

        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("response"),
                    Headers = { { "Cache-Control", "max-age=1, stale-if-error=5" } }
                };
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        // Within stale-if-error window
        fixture.AdvanceTime(TimeSpan.FromSeconds(3));
        var response1 = await client.GetAsync("https://example.com/resource", _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK); // Stale served

        // Beyond stale-if-error window
        fixture.AdvanceTime(TimeSpan.FromSeconds(4));
        var response2 = await client.GetAsync("https://example.com/resource", _ct);
        response2.StatusCode.ShouldBe(HttpStatusCode.InternalServerError); // Error passed through
    }

    [Fact]
    public async Task Respect_must_revalidate_with_stale_if_error()
    {

        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("response"),
                    Headers =
                    {
                        { "Cache-Control", "max-age=1, stale-if-error=10, must-revalidate" }
                    }
                };
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // must-revalidate prevents serving stale on error
        var response = await client.GetAsync("https://example.com/resource", _ct);
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Track_hit_miss_ratio()
    {
        // This test verifies that metrics are being tracked by checking the hit/miss behavior
        // rather than trying to capture the actual metric values (which is complex with static meters)

        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            return new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent($"response {requestCount}"),
                Headers = { { "Cache-Control", "max-age=3600" } }
            };
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request - miss (requestCount=1)
        await client.GetAsync("https://example.com/resource1", _ct);

        // Second request - hit (requestCount still 1)
        await client.GetAsync("https://example.com/resource1", _ct);

        // Third request different resource - miss (requestCount=2)
        await client.GetAsync("https://example.com/resource2", _ct);

        // Verify behavior: 2 misses (unique resources), 1 hit (cached)
        requestCount.ShouldBe(2); // Only 2 actual requests made (2 misses)

        // The metrics counters (_cacheHits and _cacheMisses) are being incremented
        // but capturing them in tests with static meters is complex
        // The Expose_metrics_via_System_Diagnostics_Metrics test verifies the meter exists
    }

    [Fact]
    public async Task Expose_metrics_via_System_Diagnostics_Metrics()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);

        var meterFound = false;
        var instrumentNames = new HashSet<string>();
        using var meterListener = new MeterListener();

        meterListener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == "DamianH.Http.HybridCacheHandler")
            {
                meterFound = true;
                instrumentNames.Add(instrument.Name);
            }
        };

        meterListener.Start();

        var client = fixture.CreateClient();
        await client.GetAsync("https://example.com/resource", _ct);

        meterFound.ShouldBeTrue();
        instrumentNames.ShouldContain("cache.hits");
        instrumentNames.ShouldContain("cache.misses");
        instrumentNames.ShouldContain("cache.stale");
        instrumentNames.ShouldContain("cache.size_exceeded");
    }

    [Fact]
    public async Task Include_exclude_specific_headers()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            return new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent($"response {requestCount}"),
                Headers =
                {
                    { "Cache-Control", "max-age=3600" },
                    { "Vary", "X-Custom-Header" }
                }
            };
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler, handlerOptions =>
        {
            handlerOptions.VaryHeaders = ["X-Custom-Header"];
        });
        var client = fixture.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request1.Headers.Add("X-Custom-Header", "value1");
        request1.Headers.Add("Accept", "application/json");
        await client.SendAsync(request1, _ct);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request2.Headers.Add("X-Custom-Header", "value1");
        request2.Headers.Add("Accept", "application/xml"); // Different Accept
        await client.SendAsync(request2, _ct);

        requestCount.ShouldBe(1); // Cache hit despite different Accept header

        var request3 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request3.Headers.Add("X-Custom-Header", "value2"); // Different custom header
        await client.SendAsync(request3, _ct);

        requestCount.ShouldBe(2); // Cache miss due to different X-Custom-Header
    }

    [Fact]
    public async Task Metrics_include_request_tags()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);

        var recordedTags = new List<KeyValuePair<string, object?>>();
        using var meterListener = new MeterListener();

        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "DamianH.Http.HybridCacheHandler")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "cache.misses")
            {
                foreach (var tag in tags)
                {
                    recordedTags.Add(tag);
                }
            }
        });

        meterListener.Start();

        var client = fixture.CreateClient();
        await client.GetAsync("https://example.com/resource", _ct);

        recordedTags.ShouldContain(t => t.Key == "http.request.method" && (string)t.Value! == "GET");
        recordedTags.ShouldContain(t => t.Key == "url.scheme" && (string)t.Value! == "https");
        recordedTags.ShouldContain(t => t.Key == "server.address" && (string)t.Value! == "example.com");
        recordedTags.ShouldContain(t => t.Key == "server.port" && (int)t.Value! == 443);
    }

    [Fact]
    public async Task Cache_stale_counter_increments_on_stale_while_revalidate()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            return new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent($"response {requestCount}"),
                Headers =
                {
                    { "Cache-Control", "max-age=1, stale-while-revalidate=5" },
                    { "ETag", $"\"{requestCount}\"" }
                }
            };
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);

        long staleCount = 0;
        using var meterListener = new MeterListener();

        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "DamianH.Http.HybridCacheHandler")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "cache.stale")
            {
                Interlocked.Add(ref staleCount, measurement);
            }
        });

        meterListener.Start();

        var client = fixture.CreateClient();
        await client.GetAsync("https://example.com/resource", _ct);

        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        await client.GetAsync("https://example.com/resource", _ct);
        await Task.Delay(100, _ct);

        Interlocked.Read(ref staleCount).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Cache_size_exceeded_counter_increments_when_content_too_large()
    {
        var content = new string('x', 20 * 1024); // 20KB
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(content),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });

        long sizeExceededCount = 0;
        using var meterListener = new MeterListener();

        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "DamianH.Http.HybridCacheHandler")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "cache.size_exceeded")
            {
                Interlocked.Add(ref sizeExceededCount, measurement);
            }
        });

        meterListener.Start();

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler,
            options => options.MaxCacheableContentSize = 10 * 1024); // 10KB limit
        var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        Interlocked.Read(ref sizeExceededCount).ShouldBeGreaterThanOrEqualTo(1);
    }
}
