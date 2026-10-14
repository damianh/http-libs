// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

namespace DamianH.Http.ForwardProxy.Conformance;

internal static class Outcomes
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Error = "error";
}

internal sealed record FixtureResult(
    string Outcome,
    string Expect,
    string Reference,
    IReadOnlyList<string> Reasons,
    Observation? Observed = null);

internal sealed record ProxyResponse(int Status, string? ProxyStatus);

internal sealed record ForwardedRequest(
    string Method,
    string Uri,
    string Version,
    IReadOnlyList<string[]> Headers,
    string Body,
    bool WellFormed);

/// <summary>What the client received: proxy responses, and the origin's echoes of the bytes it was sent.</summary>
internal sealed record Observation(
    IReadOnlyList<ProxyResponse> ProxyResponses,
    IReadOnlyList<ForwardedRequest> Forwarded,
    IReadOnlyList<string> OriginReceived,
    string UnparsedForwarded,
    string UnparsedResponse,
    bool NoResponse);

internal static partial class FixtureEvaluator
{
    [GeneratedRegex(@"\A[!#$%&'*+.^_`|~0-9A-Za-z-]+ [^\x00-\x20\x7f]+ HTTP/1\.[01]\r\n")]
    private static partial Regex StrictRequestLine();

    public static string Substitute(string text, string origin) => text.Replace("{origin}", origin, StringComparison.Ordinal);

    /// <param name="raw">Everything the client read from the proxy, as Latin-1.</param>
    /// <param name="origin">The echo origin's host:port.</param>
    /// <param name="echoServer">The <c>Server</c> value that identifies an echo origin response.</param>
    public static FixtureResult Evaluate(Fixture fixture, string raw, string origin, string echoServer)
    {
        var (observation, requests) = Observe(raw, echoServer);
        var reasons = fixture.Expect switch
        {
            Expectations.Forward => CheckForward(fixture, observation, requests, origin),
            Expectations.Reject => CheckReject(fixture, observation),
            Expectations.NotForwarded => CheckNotForwarded(fixture, observation),
            Expectations.RejectOrForward => CheckRejectOrForward(fixture, observation, requests, origin),
            _ => throw new InvalidDataException($"Unknown expectation {ConformanceJson.Quote(fixture.Expect)} in {fixture.Id}."),
        };
        return new FixtureResult(reasons.Count == 0 ? Outcomes.Pass : Outcomes.Fail, fixture.Expect, fixture.Reference, reasons, observation);
    }

    internal static (Observation Observation, IReadOnlyList<ParsedRequest> Requests) Observe(string raw, string echoServer)
    {
        var proxyResponses = new List<ProxyResponse>();
        var requests = new List<ParsedRequest>();
        var originReceived = new List<string>();
        var unparsedForwarded = "";
        var remaining = raw;
        while (remaining.Length > 0 && Http1.TryParseResponse(remaining, out var response, out var rest))
        {
            remaining = rest;
            if (response.Status == 200 && response.Field("Server") == echoServer)
            {
                originReceived.Add(response.Body);
                var (parsed, leftover) = Http1.ParseRequests(response.Body);
                requests.AddRange(parsed);
                unparsedForwarded += leftover;
            }
            else
            {
                proxyResponses.Add(new ProxyResponse(response.Status, response.Field("Proxy-Status")));
            }
        }

        var forwarded = requests
            .Select(r => new ForwardedRequest(r.Method, r.Target, r.Version, [.. r.Fields.Select(f => new[] { f.Key, f.Value })], r.Body, r.IsWellFormed))
            .ToList();
        return (new Observation(proxyResponses, forwarded, originReceived, unparsedForwarded, remaining, raw.Length == 0), requests);
    }

    private static List<string> CheckForward(Fixture fixture, Observation observed, IReadOnlyList<ParsedRequest> requests, string origin)
    {
        var reasons = new List<string>();
        if (observed.ProxyResponses.Count > 0)
        {
            reasons.Add($"proxy responded {ConformanceJson.Quote(observed.ProxyResponses.Select(r => r.Status))}");
        }
        if (observed.UnparsedResponse.Length > 0)
        {
            reasons.Add($"client received unparseable response bytes {ConformanceJson.Quote(observed.UnparsedResponse)}");
        }
        if (requests.Count == 0)
        {
            reasons.Add("nothing was forwarded");
            return reasons;
        }
        if (observed.UnparsedForwarded.Length > 0)
        {
            reasons.Add($"origin received unparseable bytes {ConformanceJson.Quote(observed.UnparsedForwarded)}");
        }
        // The request parser is permissive (e.g. "GET  HTTP/1.1" parses), so check raw request lines too.
        foreach (var request in requests.Where(request => !StrictRequestLine().IsMatch(request.RawStartLine)))
        {
            reasons.Add($"origin received a malformed request line {ConformanceJson.Quote(request.RawStartLine.Split('\n', 2)[0])}");
        }
        for (var index = 0; index < requests.Count; index++)
        {
            if (!requests[index].IsWellFormed)
            {
                reasons.Add($"forwarded request {index} has an invalid method, field name or field value");
            }
        }

        var expectedCount = fixture.Checks.FirstOrDefault(c => c.Count is not null && c.Header is null)?.Count ?? 1;
        if (requests.Count != expectedCount)
        {
            reasons.Add($"forwarded {requests.Count} requests, expected {expectedCount}");
        }

        foreach (var check in fixture.Checks)
        {
            if (check.Count is not null && check.Header is null)
            {
                continue;
            }
            if (check.Request >= requests.Count)
            {
                reasons.Add($"request {check.Request} was not forwarded");
                continue;
            }
            var request = requests[check.Request];
            Compare("method", request.Method, check.Method);
            Compare("uri", request.Target, check.Uri);
            Compare("version", request.Version, check.Version);
            Compare("body", request.Body, check.Body);
            if (check.Header is { } name)
            {
                var values = request.Values(name);
                var quoted = ConformanceJson.Quote(values);
                if (check.Absent && values.Count > 0)
                {
                    reasons.Add($"{name} present: {quoted}");
                }
                if (check.Count is { } count && values.Count != count)
                {
                    reasons.Add($"{name} appears {values.Count} times, expected {count}");
                }
                if (check.EqualTo is { } equalTo && !values.SequenceEqual([Substitute(equalTo, origin)]))
                {
                    reasons.Add($"{name} {quoted} != {ConformanceJson.Quote(new[] { Substitute(equalTo, origin) })}");
                }
                if (check.NotEqualTo is { } notEqualTo && values.Contains(Substitute(notEqualTo, origin)))
                {
                    reasons.Add($"{name} has forbidden value {ConformanceJson.Quote(notEqualTo)}");
                }
                if (check.Matches is { } matches && !values.Any(v => Regex.IsMatch(v, matches)))
                {
                    reasons.Add($"{name} {quoted} does not match {ConformanceJson.Quote(matches)}");
                }
                if (check.NotMatches is { } notMatches && values.Any(v => Regex.IsMatch(v, notMatches)))
                {
                    reasons.Add($"{name} {quoted} matches forbidden {ConformanceJson.Quote(notMatches)}");
                }
            }
        }
        return reasons;

        void Compare(string field, string actual, string? expected)
        {
            if (expected is not null && actual != Substitute(expected, origin))
            {
                reasons.Add($"{field} {ConformanceJson.Quote(actual)} != {ConformanceJson.Quote(Substitute(expected, origin))}");
            }
        }
    }

    private static List<string> CheckNotForwarded(Observation observed) =>
        observed.Forwarded.Count > 0 || observed.UnparsedForwarded.Length > 0
            ? [$"{observed.Forwarded.Count} request(s) reached the origin"]
            : [];

    private static List<string> CheckNotForwarded(Fixture fixture, Observation observed)
    {
        var reasons = CheckNotForwarded(observed);
        if (fixture.Status is { } allowed && !observed.ProxyResponses.Any(r => allowed.Contains(r.Status)))
        {
            reasons.Add($"no proxy response with status in {ConformanceJson.Quote(allowed)}");
        }
        return reasons;
    }

    private static List<string> CheckReject(Fixture fixture, Observation observed)
    {
        var reasons = CheckNotForwarded(observed);
        var statuses = observed.ProxyResponses.Select(r => r.Status).ToList();
        if (statuses.Count == 0)
        {
            reasons.Add("no error response (connection closed or timed out)");
        }
        else if (!statuses.Any(s => fixture.Status is { } allowed ? allowed.Contains(s) : s is >= 400 and < 500))
        {
            var allowed = fixture.Status is { } listed ? ConformanceJson.Quote(listed) : "4xx";
            reasons.Add($"status {ConformanceJson.Quote(statuses)} not in {allowed}");
        }
        return reasons;
    }

    private static List<string> CheckRejectOrForward(Fixture fixture, Observation observed, IReadOnlyList<ParsedRequest> requests, string origin)
    {
        var rejected = CheckReject(fixture, observed);
        if (rejected.Count == 0)
        {
            return [];
        }
        var forwarded = CheckForward(fixture, observed, requests, origin);
        return forwarded.Count == 0 ? [] : [$"not rejected ({string.Join("; ", rejected)}) and forward checks failed", .. forwarded];
    }
}
