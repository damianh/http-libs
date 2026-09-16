// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

#if NETSTANDARD2_0 || NETFRAMEWORK
namespace DamianH.HttpHybridCacheHandler;

internal static class HttpContentCompatibility
{
    public static Stream ReadAsStream(this HttpContent content, Ct ct) =>
        Task.Run(() => content.ReadAsStreamAsync(ct), ct).GetAwaiter().GetResult();

    public static async Task<Stream> ReadAsStreamAsync(this HttpContent content, Ct ct)
    {
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(content.Dispose);
        try
        {
            var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                stream.Dispose();
                ct.ThrowIfCancellationRequested();
            }
            return stream;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    public static async Task<byte[]> ReadAsByteArrayAsync(this HttpContent content, Ct ct)
    {
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(content.Dispose);
        try
        {
            var bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return bytes;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    public static async Task CopyToAsync(this HttpContent content, Stream destination, Ct ct)
    {
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await stream.CopyToAsync(destination, 64 * 1024, ct).ConfigureAwait(false);
    }
}
#endif
