// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.ForwardProxy.Conformance;

public class Http1Tests
{
    [Fact]
    public void Parses_pipelined_requests_with_content_length_and_chunked_bodies()
    {
        var (requests, leftover) = Http1.ParseRequests(
            "POST /a HTTP/1.1\r\nHost: h\r\nContent-Length: 5\r\n\r\nhello" +
            "POST /b HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n2;ext=1\r\nhe\r\n3\r\nllo\r\n0\r\nX-Trailer: t\r\n\r\n");

        leftover.ShouldBeEmpty();
        requests.Select(r => (r.Method, r.Target, r.Version, r.Body)).ShouldBe([("POST", "/a", "1.1", "hello"), ("POST", "/b", "1.1", "hello")]);
    }

    [Fact]
    public void Empty_request_target_still_parses_so_checks_can_report_it()
    {
        Http1.TryParseRequest("GET  HTTP/1.1\r\nHost: h\r\n\r\n", out var request, out _).ShouldBeTrue();
        request.Target.ShouldBe("HTTP/1.1");
        request.Version.ShouldBe("");
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: h\r\n")]
    [InlineData("GET / HTTP/1.1\r\nno colon\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nab\r\n")]
    [InlineData("GET / HTTP/1.1\r\nContent-Length: +5\r\n\r\nhello")]
    [InlineData("POST / HTTP/1.1\r\nContent-Length: 5\r\n\r\nhi")]
    [InlineData("POST / HTTP/1.1\r\nContent-Length: 9223372036854775807\r\n\r\nhi")]
    [InlineData("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n5junk\r\nhello\r\n0\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n5;ext=\"unterminated\r\nhello\r\n0\r\n\r\n")]
    public void Incomplete_or_unframeable_requests_are_left_unparsed(string raw)
    {
        var (requests, leftover) = Http1.ParseRequests(raw);
        requests.ShouldBeEmpty();
        leftover.ShouldBe(raw);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nX-Ok: a b\r\n\r\n", true)]
    [InlineData("GET / HTTP/1.1\r\nX(Bad): 1\r\n\r\n", false)]
    [InlineData("GET / HTTP/1.1\r\n X-Leading: 1\r\n\r\n", false)]
    [InlineData("GET / HTTP/1.1\r\nX-Cr: a\rb\r\n\r\n", false)]
    [InlineData("GET / HTTP/1.1\r\nX-Nul: a\0b\r\n\r\n", false)]
    [InlineData("G(T / HTTP/1.1\r\n\r\n", false)]
    public void IsWellFormed_checks_method_and_field_syntax(string raw, bool expected)
    {
        Http1.TryParseRequest(raw, out var request, out _).ShouldBeTrue();
        request.IsWellFormed.ShouldBe(expected);
    }

    [Fact]
    public void Response_without_framing_is_read_to_end_of_stream()
    {
        Http1.TryParseResponse("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\noops", out var response, out var rest).ShouldBeTrue();
        response.Status.ShouldBe(400);
        response.Body.ShouldBe("oops");
        rest.ShouldBeEmpty();
    }

    [Fact]
    public void Truncated_response_is_not_parsed() =>
        Http1.TryParseResponse("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhi", out _, out _).ShouldBeFalse();

    [Theory]
    [InlineData("5;ext=token")]
    [InlineData("5 ; ext = \"a\\\"b\";flag")]
    public void Parses_legal_chunk_extensions(string sizeLine)
    {
        Http1.TryParseRequest($"POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n{sizeLine}\r\nhello\r\n0\r\n\r\n",
            out var request, out var rest).ShouldBeTrue();
        request.Body.ShouldBe("hello");
        rest.ShouldBeEmpty();
    }
}
