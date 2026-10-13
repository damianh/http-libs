// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.IO.Pipelines;
using System.Text;
using DamianH.Http.ForwardProxy.Internal;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class Http1RequestHeadObserverTests
{
    private const string Pipelined =
        "GET http://a.example/one HTTP/1.1\r\nHost: a.example\r\nConnection: close, X-A\r\n\r\n"
        + "POST http://a.example/two HTTP/1.1\r\nHost: a.example\r\nContent-Length: 24\r\n\r\nConnection: X-Fake\r\n\r\n\r\n"
        + "POST http://a.example/three HTTP/1.1\r\nTransfer-Encoding: gzip, chunked\r\nX-Long-Header-Name-That-Is-Skipped: v\r\n\r\n"
        + "5;ext=1\r\nGET /\r\n0\r\nX-Trailer: GET / HTTP/1.1\r\n\r\n"
        + "GET http://a.example/four HTTP/1.1\r\nconnection: keep-alive, X-B\r\nConnection:X-C , upgrade\r\nUpgrade: foo\r\nContent-Length: 0\r\n\r\n";

    private static readonly Expected[] PipelinedHeads =
    [
        new("http://a.example/one", ["X-A"]),
        new("http://a.example/two", null),
        new("http://a.example/three", null),
        new("http://a.example/four", ["X-B", "X-C"]),
    ];

    [Fact]
    public async Task FollowsPipelinedRequestsAcrossEverySplit()
    {
        for (var split = 1; split < Pipelined.Length; split++)
        {
            await using var harness = new Harness();
            await harness.WriteAsync(Pipelined[..split]);
            await harness.WriteAsync(Pipelined[split..]);

            await harness.ShouldReport(Pipelined, PipelinedHeads, $"split at {split}");
        }
    }

    [Fact]
    public async Task FollowsRequestsWrittenOneByteAtATime()
    {
        await using var harness = new Harness();
        foreach (var c in Pipelined)
        {
            await harness.WriteAsync(c.ToString());
        }

        await harness.ShouldReport(Pipelined, PipelinedHeads, "byte at a time");
    }

    [Fact]
    public async Task OnlyReportsOptionsForTheMatchingTarget()
    {
        await using var harness = new Harness();
        await harness.WriteAsync("GET /a HTTP/1.1\r\nConnection: X-A, close\r\n\r\n");
        await harness.ConsumeAsync(43);

        harness.Observer.TryGetConnectionOptions("/a", out var options).ShouldBeTrue();
        options.ShouldBe(["X-A"]);
        harness.Observer.TryGetConnectionOptions("/b", out options).ShouldBeFalse();
        options.ShouldBeNull();
    }

    [Fact]
    public async Task ResumesAfterARejectedUpgrade()
    {
        const string first = "GET /ws HTTP/1.1\r\nConnection: Upgrade, X-U\r\nUpgrade: websocket\r\n\r\n";
        const string second = "GET /next HTTP/1.1\r\nConnection: close, X-N\r\n\r\n";
        await using var harness = new Harness();
        await harness.WriteAsync(first + second);

        await harness.ConsumeAsync(first.Length);
        harness.Observer.IsAwaitingUpgradeOutcome.ShouldBeTrue();
        harness.Observer.TryGetConnectionOptions("/ws", out var options).ShouldBeTrue();
        options.ShouldBe(["X-U"]);
        harness.Observer.ResumeAfterRejectedUpgrade();
        await harness.ConsumeAsync(first.Length + second.Length);

        harness.Observer.IsAwaitingUpgradeOutcome.ShouldBeFalse();
        harness.Observer.TryGetConnectionOptions("/next", out options).ShouldBeTrue();
        options.ShouldBe(["X-N"]);
    }

    [Fact]
    public async Task StopsAfterAnAcceptedUpgrade()
    {
        const string head = "GET /ws HTTP/1.1\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n";
        const string upgraded = "GET /not-http HTTP/1.1\r\nConnection: close, X-N\r\n\r\n";
        await using var harness = new Harness();
        await harness.WriteAsync(head + upgraded);

        await harness.ConsumeAsync(head.Length + upgraded.Length);

        harness.Observer.TryGetConnectionOptions("/not-http", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nContent-Length: 1x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\nx")]
    [InlineData("GET / HTTP/1.1\r\nTransfer-Encoding: gzip\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n")]
    [InlineData("GET / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n1\r\nxy\r\n")]
    [InlineData("GET /" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa HTTP/1.1\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nX\r\n\r\n")]
    [InlineData("CONNECT a.example:443 HTTP/1.1\r\n\r\n")]
    public async Task UnknownWhenTheStreamCannotBeFollowed(string input)
    {
        await using var harness = new Harness(maxLineLength: 64);
        await harness.WriteAsync(input);

        await harness.ConsumeAsync(input.Length);

        harness.Observer.TryGetConnectionOptions("/", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task UnknownUntilAHeadIsConsumed()
    {
        await using var harness = new Harness();
        await harness.WriteAsync("GET / HTTP/1.1\r\n\r\n");

        harness.Observer.TryGetConnectionOptions("/", out _).ShouldBeFalse();
        await harness.ConsumeAsync(18);
        harness.Observer.TryGetConnectionOptions("/", out var options).ShouldBeTrue();
        options.ShouldBeNull();
    }

    private sealed record Expected(string Target, string[]? Options);

    /// <summary>Drives the observer like Kestrel: read, examine nothing, and consume each head before using it.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Pipe _pipe = new();
        private long _consumed;

        public Harness(int maxLineLength = 8192) => Observer = new Http1RequestHeadObserver(_pipe.Reader, maxLineLength);

        public Http1RequestHeadObserver Observer { get; }

        public async Task WriteAsync(string text)
        {
            await _pipe.Writer.WriteAsync(Encoding.Latin1.GetBytes(text), TestContext.Current.CancellationToken);
            var result = await Observer.ReadAsync(TestContext.Current.CancellationToken);
            Observer.AdvanceTo(result.Buffer.Start);
        }

        public async Task ConsumeAsync(long offset)
        {
            var result = await Observer.ReadAsync(TestContext.Current.CancellationToken);
            Observer.AdvanceTo(result.Buffer.GetPosition(offset - _consumed));
            _consumed = offset;
        }

        public async Task ShouldReport(string input, Expected[] heads, string because)
        {
            foreach (var head in heads)
            {
                var requestLine = input.IndexOf($" {head.Target} HTTP/1.1\r\n", StringComparison.Ordinal);
                await ConsumeAsync(input.IndexOf("\r\n\r\n", requestLine, StringComparison.Ordinal) + 4);
                Observer.TryGetConnectionOptions(head.Target, out var options).ShouldBeTrue(because);
                options.ShouldBe(head.Options, because);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _pipe.Writer.CompleteAsync();
            await Observer.CompleteAsync();
        }
    }
}
