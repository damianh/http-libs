// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers;
using System.Text;
using DamianH.Http.ForwardProxy.Internal;
using Shouldly;
using static DamianH.Http.ForwardProxy.Internal.ConnectRequestParser;

namespace DamianH.Http.ForwardProxy;

public class ConnectRequestParserTests
{
    [Theory]
    [InlineData("", nameof(Detection.NeedMoreData))]
    [InlineData("CONN", nameof(Detection.NeedMoreData))]
    [InlineData("CONNECT", nameof(Detection.NeedMoreData))]
    [InlineData("CONNECT ", nameof(Detection.Connect))]
    [InlineData("CONNECT example.com:443", nameof(Detection.Connect))]
    [InlineData("GET / HTTP/1.1", nameof(Detection.NotConnect))]
    [InlineData("connect ", nameof(Detection.NotConnect))]
    [InlineData("CONNEC/", nameof(Detection.NotConnect))]
    [InlineData("PRI * HTTP/2.0", nameof(Detection.NotConnect))]
    public void DetectsConnect(string input, string expected) =>
        Detect(Sequence(input)).ShouldBe(Enum.Parse<Detection>(expected));

    [Fact]
    public void ParsesRequestAndLeavesTrailingBytes()
    {
        var buffer = Sequence("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\nProxy-Authorization:  Basic abc \r\nX-Empty:\r\n\r\n\x16\x03\x01");

        TryParse(buffer, 1024, out var request, out var consumed, out _).ShouldBe(ParseStatus.Complete);

        request!.Target.ShouldBe("example.com:443");
        request.Protocol.ShouldBe("HTTP/1.1");
        request.Headers.ShouldBe([new("Host", "example.com:443"), new("Proxy-Authorization", "Basic abc"), new("X-Empty", "")]);
        buffer.Slice(consumed).ToArray().ShouldBe(new byte[] { 0x16, 0x03, 0x01 });
    }

    [Fact]
    public void ParsesRequestWithoutHeadersAndHttp10()
    {
        TryParse(Sequence("CONNECT [::1]:443 HTTP/1.0\r\n\r\n"), 1024, out var request, out _, out _).ShouldBe(ParseStatus.Complete);
        request!.Target.ShouldBe("[::1]:443");
        request.Protocol.ShouldBe("HTTP/1.0");
        request.Headers.ShouldBeEmpty();
    }

    [Fact]
    public void ParsesSegmentedInput()
    {
        var first = new Segment(Encoding.ASCII.GetBytes("CONNECT example.com:443 HTTP/1.1\r"));
        var last = first.Append(Encoding.ASCII.GetBytes("\nHost: x\r\n\r"))
            .Append(Encoding.ASCII.GetBytes("\nrest"));
        var buffer = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);

        TryParse(buffer, 1024, out var request, out var consumed, out _).ShouldBe(ParseStatus.Complete);
        request!.Headers.Count.ShouldBe(1);
        Encoding.ASCII.GetString(buffer.Slice(consumed).ToArray()).ShouldBe("rest");
    }

    [Fact]
    public void IncompleteUntilTerminator() =>
        TryParse(Sequence("CONNECT example.com:443 HTTP/1.1\r\nHost: x\r\n"), 1024, out _, out _, out _).ShouldBe(ParseStatus.Incomplete);

    [Fact]
    public void RejectsOversizedHeads()
    {
        TryParse(Sequence("CONNECT example.com:443 HTTP/1.1\r\nX: " + new string('a', 100)), 64, out _, out _, out var status).ShouldBe(ParseStatus.Invalid);
        status.ShouldBe(431);

        TryParse(Sequence("CONNECT example.com:443 HTTP/1.1\r\nX: " + new string('a', 100) + "\r\n\r\n"), 64, out _, out _, out status).ShouldBe(ParseStatus.Invalid);
        status.ShouldBe(431);
    }

    [Fact]
    public void RejectsTooManyHeaders()
    {
        var head = "CONNECT example.com:443 HTTP/1.1\r\n" + string.Concat(Enumerable.Range(0, MaxHeaderCount + 1).Select(i => $"X-{i}: v\r\n")) + "\r\n";
        TryParse(Sequence(head), 64 * 1024, out _, out _, out var status).ShouldBe(ParseStatus.Invalid);
        status.ShouldBe(431);
    }

    [Theory]
    [InlineData("CONNECT example.com:443 HTTP/2.0\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1 \r\n\r\n")]
    [InlineData("CONNECT  example.com:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com:443\r\n\r\n")]
    [InlineData("CONNECT exa\x01mple.com:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nHost : x\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nHost: x\r\n folded\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\n\tHost: x\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nNoColon\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\n: empty-name\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nX: a\rb\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nX: a\nY: b\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1\nHost: x\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nContent-Length: 5\r\n\r\n")]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")]
    public void RejectsMalformedRequests(string head)
    {
        TryParse(Sequence(head), 1024, out _, out _, out var status).ShouldBe(ParseStatus.Invalid);
        status.ShouldBe(400);
    }

    [Fact]
    public void AllowsZeroContentLength() =>
        TryParse(Sequence("CONNECT example.com:443 HTTP/1.1\r\nContent-Length: 0\r\n\r\n"), 1024, out _, out _, out _).ShouldBe(ParseStatus.Complete);

    private static ReadOnlySequence<byte> Sequence(string value) => new(Encoding.Latin1.GetBytes(value));

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
