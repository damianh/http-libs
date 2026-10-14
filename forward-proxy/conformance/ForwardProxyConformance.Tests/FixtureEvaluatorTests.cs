// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.ForwardProxy.Conformance;

public class FixtureEvaluatorTests
{
    private const string Origin = "127.0.0.1:1234";

    private static string Echo(string forwarded) =>
        $"HTTP/1.1 200 OK\r\nServer: {EchoOrigin.Server}\r\nContent-Length: {forwarded.Length}\r\n\r\n{forwarded}";

    private static Fixture Fixture(string expect, params FixtureCheck[] checks) => new()
    {
        Id = "test",
        Reference = "RFC",
        Description = "test",
        Payload = ["GET http://{origin}/ HTTP/1.1\r\nHost: {origin}\r\n\r\n"],
        Expect = expect,
        Checks = checks,
    };

    private static FixtureResult Evaluate(Fixture fixture, string raw) =>
        FixtureEvaluator.Evaluate(fixture, raw, Origin, EchoOrigin.Server);

    [Fact]
    public void Forward_passes_when_checks_hold()
    {
        var result = Evaluate(
            Fixture(Expectations.Forward, new FixtureCheck { Uri = "/" }, new FixtureCheck { Header = "host", EqualTo = "{origin}" }),
            Echo($"GET / HTTP/1.1\r\nHost: {Origin}\r\n\r\n"));
        result.Reasons.ShouldBeEmpty();
        result.Outcome.ShouldBe(Outcomes.Pass);
    }

    [Fact]
    public void Forward_fails_on_malformed_request_line_and_field_mismatch()
    {
        var result = Evaluate(
            Fixture(Expectations.Forward, new FixtureCheck { Uri = "/" }, new FixtureCheck { Header = "x-hop", Absent = true }),
            Echo($"GET  HTTP/1.1\r\nHost: {Origin}\r\nX-Hop: 1\r\n\r\n"));
        result.Outcome.ShouldBe(Outcomes.Fail);
        result.Reasons.ShouldContain(r => r.StartsWith("origin received a malformed request line", StringComparison.Ordinal));
        result.Reasons.ShouldContain("uri \"HTTP/1.1\" != \"/\"");
        result.Reasons.ShouldContain("x-hop present: [\"1\"]");
    }

    [Fact]
    public void Forward_fails_on_proxy_error()
    {
        var result = Evaluate(Fixture(Expectations.Forward), "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n");
        result.Reasons.ShouldBe(["proxy responded [400]", "nothing was forwarded"]);
    }

    [Theory]
    [InlineData("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n", Outcomes.Pass)]
    [InlineData("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n", Outcomes.Fail)]
    [InlineData("", Outcomes.Fail)]
    public void Reject_requires_a_4xx_and_nothing_forwarded(string raw, string outcome) =>
        Evaluate(Fixture(Expectations.Reject), raw).Outcome.ShouldBe(outcome);

    [Fact]
    public void Reject_fails_when_the_origin_was_reached()
    {
        var result = Evaluate(Fixture(Expectations.Reject), Echo("GET / HTTP/1.1\r\nHost: h\r\n\r\n"));
        result.Reasons.ShouldBe(["1 request(s) reached the origin", "no error response (connection closed or timed out)"]);
    }

    [Fact]
    public void Reject_or_forward_accepts_a_clean_forward() =>
        Evaluate(Fixture(Expectations.RejectOrForward), Echo($"GET / HTTP/1.1\r\nHost: {Origin}\r\n\r\n")).Outcome.ShouldBe(Outcomes.Pass);

    [Fact]
    public void Responses_from_other_servers_are_proxy_responses()
    {
        var (observation, _) = FixtureEvaluator.Observe(
            "HTTP/1.1 200 OK\r\nServer: other\r\nContent-Length: 0\r\n\r\n", EchoOrigin.Server);
        observation.ProxyResponses.ShouldHaveSingleItem().Status.ShouldBe(200);
        observation.Forwarded.ShouldBeEmpty();
    }
}
