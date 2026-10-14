// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;

namespace DamianH.Http.HybridCacheHandler;

public class FreshnessCalculationTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Response_fresh_until_Expires_date()
    {
        var expiresTime = DateTimeOffset.UtcNow.AddHours(1);
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
            {
                Headers = { Expires = expiresTime }
            }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance time to just before expiry
        fixture.AdvanceTime(TimeSpan.FromMinutes(59));

        // Second request - should be cached
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Expires_overridden_by_Cache_Control_max_age()
    {
        var expiresTime = DateTimeOffset.UtcNow.AddHours(2); // 2 hours
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
            {
                Headers = { Expires = expiresTime }
            },
            Headers = { { "Cache-Control", "max-age=3600" } } // 1 hour - should take precedence
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance time to 1.5 hours (past max-age, before Expires)
        fixture.AdvanceTime(TimeSpan.FromMinutes(90));

        // Second request - should fetch fresh (max-age expired, not Expires)
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Age_header_increases_response_age()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "max-age=3600" }, // 1 hour
                { "Age", "1800" } // Already 30 minutes old
            }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance time by 35 minutes (total age = 65 minutes > 60 minute max-age)
        fixture.AdvanceTime(TimeSpan.FromMinutes(35));

        // Second request - should fetch fresh
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Date_header_used_to_calculate_age()
    {
        // Capture a fixed time for the response Date header
        var fixedStartTime = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        var responseDate = fixedStartTime.AddMinutes(-30); // Response created 30 minutes ago

        var mockHandler = new MockHttpMessageHandler(() =>
        {
            return new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("response"),
                Headers =
                {
                    { "Cache-Control", "max-age=3600" }, // 1 hour
                    { "Date", responseDate.ToString("R") }
                }
            };
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        // Set the fake time provider to match our fixed time
        fixture.SetUtcNow(fixedStartTime);
        using var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance time by 35 minutes (total age = 65 minutes > 60 minute max-age)
        fixture.AdvanceTime(TimeSpan.FromMinutes(35));

        // Second request - should fetch fresh
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Current_age_correctly_reduces_freshness()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=3600" } } // 1 hour
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance time by 30 minutes - still fresh
        fixture.AdvanceTime(TimeSpan.FromMinutes(30));
        await client.GetAsync("https://example.com/resource", _ct);
        mockHandler.RequestCount.ShouldBe(1);

        // Advance another 35 minutes (total 65 minutes > 60 minute max-age)
        fixture.AdvanceTime(TimeSpan.FromMinutes(35));
        await client.GetAsync("https://example.com/resource", _ct);
        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Last_Modified_based_heuristic_10_percent_rule()
    {
        // Use a fixed time reference
        var fixedStartTime = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        // Resource last modified 10 days ago
        var lastModified = fixedStartTime.AddDays(-10);
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
            {
                Headers = { LastModified = lastModified }
            }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        fixture.SetUtcNow(fixedStartTime);
        using var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance time by 12 hours (< 10% of 10 days = 24 hours)
        fixture.AdvanceTime(TimeSpan.FromHours(12));

        // Second request - should be cached (heuristic freshness)
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Heuristic_only_when_no_explicit_freshness_info()
    {
        var lastModified = DateTimeOffset.UtcNow.AddDays(-10);
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
            {
                Headers = { LastModified = lastModified }
            },
            Headers = { { "Cache-Control", "max-age=600" } } // 10 minutes explicit
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance time by 12 minutes (past explicit max-age)
        fixture.AdvanceTime(TimeSpan.FromMinutes(12));

        // Second request - should fetch fresh (explicit max-age takes precedence)
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Configurable_heuristic_percentage()
    {
        // Use a fixed time reference
        var fixedStartTime = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        // Resource last modified 10 days ago
        var lastModified = fixedStartTime.AddDays(-10);
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
            {
                Headers = { LastModified = lastModified }
            }
        });

        // Configure with 20% heuristic instead of default 10%
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler, options =>
        {
            options.HeuristicFreshnessPercent = 0.2; // 20%
        });
        fixture.SetUtcNow(fixedStartTime);
        using var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance time by 30 hours (< 20% of 10 days = 48 hours)
        fixture.AdvanceTime(TimeSpan.FromHours(30));

        // Second request - should be cached (20% heuristic)
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Max_age_zero_takes_precedence_over_future_expires()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
            {
                Headers =
                {
                    Expires = DateTimeOffset.UtcNow.AddHours(1)
                }
            },
            Headers = { { "Cache-Control", "max-age=0" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Heuristic_minimum_freshness_applies_to_recent_last_modified()
    {
        var fixedStartTime = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
            {
                Headers = { LastModified = fixedStartTime.AddSeconds(-5) }
            },
            Headers = { Date = fixedStartTime }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        fixture.SetUtcNow(fixedStartTime);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(3));
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }
}
