// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Net;

namespace DamianH.Http.HybridCacheHandler;

[Collection(CultureSensitiveTestCollection.Name)]
public class AgeHeaderTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cached_response_emits_age_header()
    {
        var now = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "max-age=3600" },
                { "Date", now.ToString("R") }
            }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        fixture.SetUtcNow(now);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        fixture.AdvanceTime(TimeSpan.FromSeconds(3));
        var cachedResponse = await client.GetAsync("https://example.com/resource", _ct);

        var ageSeconds = ParseAgeSeconds(cachedResponse);
        ageSeconds.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Cached_response_updates_age_from_stored_age_value()
    {
        var now = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "max-age=3600" },
                { "Date", now.ToString("R") },
                { "Age", "30" }
            }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        fixture.SetUtcNow(now);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        fixture.AdvanceTime(TimeSpan.FromSeconds(3));
        var cachedResponse = await client.GetAsync("https://example.com/resource", _ct);

        var ageSeconds = ParseAgeSeconds(cachedResponse);
        ageSeconds.ShouldBeGreaterThanOrEqualTo(33);
    }

    [Fact]
    public async Task Cached_response_emits_age_header_using_ascii_digits()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;
        var testCulture = CultureInfo.GetCultureInfo("ar-SA");
        CultureInfo.CurrentCulture = testCulture;
        CultureInfo.CurrentUICulture = testCulture;

        try
        {
            var now = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
            var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("response"),
                Headers =
                {
                    { "Cache-Control", "max-age=3600" },
                    { "Date", now.ToString("R") }
                }
            });
            await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
            fixture.SetUtcNow(now);
            using var client = fixture.CreateClient();

            await client.GetAsync("https://example.com/resource", _ct);

            fixture.AdvanceTime(TimeSpan.FromSeconds(3));
            var cachedResponse = await client.GetAsync("https://example.com/resource", _ct);

            var ageValue = GetAgeHeaderValue(cachedResponse);
            ageValue.All(c => c is >= '0' and <= '9').ShouldBeTrue();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Fact]
    public async Task Cached_not_modified_response_updates_age_from_stored_age_value()
    {
        var now = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "max-age=3600" },
                { "Date", now.ToString("R") },
                { "ETag", "\"abcdef\"" },
                { "Age", "30" }
            }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        fixture.SetUtcNow(now);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(3));

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", "\"abcdef\"");
        var notModifiedResponse = await client.SendAsync(conditionalRequest, _ct);

        notModifiedResponse.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        ParseAgeSeconds(notModifiedResponse).ShouldBeGreaterThanOrEqualTo(33);
        mockHandler.RequestCount.ShouldBe(1);
    }

    private static string GetAgeHeaderValue(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Age", out var values).ShouldBeTrue();
        return values!.FirstOrDefault().ShouldNotBeNull();
    }

    private static int ParseAgeSeconds(HttpResponseMessage response)
    {
        int.TryParse(GetAgeHeaderValue(response), NumberStyles.None, CultureInfo.InvariantCulture, out var ageSeconds).ShouldBeTrue();
        return ageSeconds;
    }
}
