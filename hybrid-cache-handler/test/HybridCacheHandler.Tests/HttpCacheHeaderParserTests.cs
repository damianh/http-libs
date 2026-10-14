// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

public class HttpCacheHeaderParserTests
{
    [Fact]
    public void Parse_cache_control_supports_lenient_max_age_formats()
    {
        var parsed = HttpCacheHeaderParser.ParseCacheControl(
        [
            "max-age=\"3600\", max-age=1800",
            "max-age=3600.5",
            "max-age=a3600",
            "max-age=3600a"
        ]);

        parsed.MaxAge.ShouldNotBeNull();
        parsed.MaxAge.Value.ShouldBe(TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public void Parse_cache_control_rejects_invalid_max_age_spacing_and_single_quotes()
    {
        var parsed = HttpCacheHeaderParser.ParseCacheControl(
        [
            "max-age =3600",
            "max-age= 3600",
            "max-age='3600'"
        ]);

        parsed.MaxAge.ShouldBeNull();
    }

    [Fact]
    public void Parse_cache_control_parses_large_s_maxage()
    {
        var parsed = HttpCacheHeaderParser.ParseCacheControl(["s-maxage=2147483648"]);

        parsed.SharedMaxAge.ShouldNotBeNull();
        parsed.SharedMaxAge.Value.ShouldBe(TimeSpan.FromSeconds(2147483648));
    }

    [Fact]
    public void Parse_cache_control_applies_parameterized_no_cache_and_private()
    {
        var parsed = HttpCacheHeaderParser.ParseCacheControl(["no-cache=\"Set-Cookie\", private=\"Authorization\""]);

        parsed.NoCache.ShouldBeTrue();
        parsed.Private.ShouldBeTrue();
    }

    [Fact]
    public void Parse_age_uses_first_line_first_member_and_allows_parameters()
    {
        HttpCacheHeaderParser.ParseAge(["7200;foo=bar"]).ShouldBe(TimeSpan.FromSeconds(7200));
        HttpCacheHeaderParser.ParseAge(["7200, 0"]).ShouldBe(TimeSpan.FromSeconds(7200));
        HttpCacheHeaderParser.ParseAge(["0, 7200"]).ShouldBe(TimeSpan.Zero);
        HttpCacheHeaderParser.ParseAge(["abc"]).ShouldBeNull();
    }

    [Fact]
    public void Parse_age_saturates_large_values()
    {
        var age = HttpCacheHeaderParser.ParseAge(["2147483649"]);

        age.ShouldNotBeNull();
        age.Value.ShouldBe(TimeSpan.FromSeconds(2147483649));
    }

    [Fact]
    public void Parse_age_rejects_non_ascii_digits()
    {
        HttpCacheHeaderParser.ParseAge(["١٢٣"]).ShouldBeNull();
        HttpCacheHeaderParser.ParseAge(["１２３"]).ShouldBeNull();
    }

    [Fact]
    public void Parse_cache_control_rejects_non_ascii_digits_in_delta_seconds()
    {
        var parsed = HttpCacheHeaderParser.ParseCacheControl(["max-age=١٢٣, s-maxage=１２３"]);

        parsed.MaxAge.ShouldBeNull();
        parsed.SharedMaxAge.ShouldBeNull();
    }

    [Fact]
    public void Parse_http_date_accepts_supported_formats()
    {
        HttpCacheHeaderParser.ParseSingleHttpDate(["Thursday, 18-Aug-50 02:01:18 GMT"]).ShouldNotBeNull();
        HttpCacheHeaderParser.ParseSingleHttpDate(["Thu Aug  8 02:01:18 2050"]).ShouldNotBeNull();
        HttpCacheHeaderParser.ParseSingleHttpDate(["Thu, 18 Aug 2050 02:01:18 gMT"]).ShouldNotBeNull();
    }

    [Fact]
    public void Parse_http_date_maps_rfc850_two_digit_years_per_http_spec()
    {
        HttpCacheHeaderParser.ParseSingleHttpDate(["Thursday, 18-Aug-69 02:01:18 GMT"])
            .ShouldBe(new DateTimeOffset(2069, 8, 18, 2, 1, 18, TimeSpan.Zero));
        HttpCacheHeaderParser.ParseSingleHttpDate(["Thursday, 18-Aug-70 02:01:18 GMT"])
            .ShouldBe(new DateTimeOffset(1970, 8, 18, 2, 1, 18, TimeSpan.Zero));
        HttpCacheHeaderParser.ParseSingleHttpDate(["Thursday, 18-Aug-80 02:01:18 GMT"])
            .ShouldBe(new DateTimeOffset(1980, 8, 18, 2, 1, 18, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_http_date_rejects_unsupported_formats()
    {
        HttpCacheHeaderParser.ParseSingleHttpDate(["Thu, 18 Aug 2050 02:01:18 UTC"]).ShouldBeNull();
        HttpCacheHeaderParser.ParseSingleHttpDate(["Thu, 18  Aug  2050 02:01:18 GMT"]).ShouldBeNull();
        HttpCacheHeaderParser.ParseSingleHttpDate(["Thu, 18 Aug ٢٠٥٠ 02:01:18 GMT"]).ShouldBeNull();
        HttpCacheHeaderParser.ParseSingleHttpDate(
            [
                "Thu, 18 Aug 2050 02:01:18 GMT",
                "Thu, 18 Aug 2050 02:01:19 GMT"
            ]).ShouldBeNull();
    }
}
