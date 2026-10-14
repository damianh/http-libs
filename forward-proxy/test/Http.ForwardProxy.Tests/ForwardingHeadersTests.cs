// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http;
using DamianH.Http.ForwardProxy.Internal.Forwarding;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class ForwardingHeadersTests
{
    [Fact]
    public void CopiesSingleValuedHeadersWithoutBoxingTheirValues()
    {
        var context = new DefaultHttpContext();
        for (var i = 0; i < 10; i++)
        {
            context.Request.Headers[$"X-Value-{i}"] = "value";
        }

        var copied = Measure(() =>
        {
            using var message = new HttpRequestMessage();
            ForwardingHeaders.CopyRequestHeaders(context.Request, message);
        });
        var scalar = Measure(() =>
        {
            using var message = new HttpRequestMessage();
            foreach (var header in context.Request.Headers)
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value[0]);
            }
        });

        copied.ShouldBeLessThanOrEqualTo(scalar);
    }

    [Theory]
    [InlineData(HttpStatusCode.Continue)]
    [InlineData(HttpStatusCode.EarlyHints)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.ResetContent)]
    public void RemovesZeroContentLengthFromBodylessResponses(HttpStatusCode statusCode)
    {
        using var source = new HttpResponseMessage(statusCode) { Content = new ByteArrayContent([]) };
        source.Content.Headers.ContentLength = 0;
        var destination = new DefaultHttpContext().Response;

        ForwardingHeaders.CopyResponseHeaders(source, destination);

        destination.Headers.ContainsKey("Content-Length").ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    public void KeepsZeroContentLengthOnResponsesThatCanHaveABody(HttpStatusCode statusCode)
    {
        using var source = new HttpResponseMessage(statusCode) { Content = new ByteArrayContent([]) };
        source.Content.Headers.ContentLength = 0;
        var destination = new DefaultHttpContext().Response;

        ForwardingHeaders.CopyResponseHeaders(source, destination);

        destination.Headers.ContentLength.ShouldBe(0);
    }

    private static long Measure(Action action)
    {
        for (var i = 0; i < 1000; i++)
        {
            action();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            action();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / 5000;
    }
}
