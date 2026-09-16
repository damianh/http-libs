// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.HttpHybridCacheHandler;

internal abstract class CompatibleStream : Stream
#if NETSTANDARD2_0 || NETFRAMEWORK
    , IAsyncDisposable
#endif
{
#if NETSTANDARD2_0 || NETFRAMEWORK
    public virtual int Read(Span<byte> buffer) => StreamCompatibility.Read(this, buffer);
    public virtual ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        StreamCompatibility.ReadAsync(this, buffer, cancellationToken);
    public virtual void Write(ReadOnlySpan<byte> buffer) => StreamCompatibility.Write(this, buffer);
    public virtual ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        StreamCompatibility.WriteAsync(this, buffer, cancellationToken);
    public virtual ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
#endif
}
