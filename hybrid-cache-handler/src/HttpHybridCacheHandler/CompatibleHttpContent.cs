// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;

namespace DamianH.HttpHybridCacheHandler;

internal abstract class CompatibleHttpContent : HttpContent
{
#if NETSTANDARD2_0 || NETFRAMEWORK
    protected virtual Stream CreateContentReadStream(Ct ct) =>
        Task.Run(() => CreateContentReadStreamAsync(ct), ct).GetAwaiter().GetResult();

    protected virtual Task<Stream> CreateContentReadStreamAsync(Ct ct)
    {
        ct.ThrowIfCancellationRequested();
        return CreateContentReadStreamAsync();
    }

    protected virtual Task SerializeToStreamAsync(Stream stream, TransportContext? context, Ct ct)
    {
        ct.ThrowIfCancellationRequested();
        return SerializeToStreamAsync(stream, context);
    }
#endif
}
