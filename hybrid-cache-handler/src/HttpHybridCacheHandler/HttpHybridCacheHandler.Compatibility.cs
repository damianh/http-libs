// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.HttpHybridCacheHandler;

public partial class HttpHybridCacheHandler
{
#if NETSTANDARD2_0 || NETFRAMEWORK
    private async Task<HttpResponseMessage> SendOriginAsync(HttpRequestMessage request, Ct ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        // Modern HttpResponseMessage provides empty content automatically; Framework does not.
        response.Content ??= new ByteArrayContent(Array.Empty<byte>());
        return response;
    }
#else
    private Task<HttpResponseMessage> SendOriginAsync(HttpRequestMessage request, Ct ct) =>
        base.SendAsync(request, ct);
#endif
}
