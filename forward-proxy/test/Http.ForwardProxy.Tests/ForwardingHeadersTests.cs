// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

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
