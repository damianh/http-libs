// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

#if NETSTANDARD2_0 || NETFRAMEWORK
using System.Buffers;
using System.Runtime.InteropServices;

namespace DamianH.HttpHybridCacheHandler;

internal static class StreamCompatibility
{
    private const int BufferSize = 64 * 1024;

    public static int Read(this Stream stream, Span<byte> destination)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(destination.Length, BufferSize));
        try
        {
            var count = stream.Read(buffer, 0, Math.Min(destination.Length, buffer.Length));
            buffer.AsSpan(0, count).CopyTo(destination);
            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask<int> ReadAsync(this Stream stream, Memory<byte> destination, CancellationToken ct = default)
    {
        if (MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out var segment))
        {
            return await stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, ct).ConfigureAwait(false);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(destination.Length, BufferSize));
        try
        {
            var count = await stream.ReadAsync(buffer, 0, Math.Min(destination.Length, buffer.Length), ct).ConfigureAwait(false);
            buffer.AsMemory(0, count).CopyTo(destination);
            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static void Write(this Stream stream, ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            stream.Write(Array.Empty<byte>(), 0, 0);
            return;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(source.Length, BufferSize));
        try
        {
            while (!source.IsEmpty)
            {
                var count = Math.Min(source.Length, buffer.Length);
                source.Slice(0, count).CopyTo(buffer);
                stream.Write(buffer, 0, count);
                source = source.Slice(count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteAsync(this Stream stream, ReadOnlyMemory<byte> source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (MemoryMarshal.TryGetArray(source, out var segment))
        {
            await stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, ct).ConfigureAwait(false);
            return;
        }

        if (source.IsEmpty)
        {
            await stream.WriteAsync(Array.Empty<byte>(), 0, 0, ct).ConfigureAwait(false);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(source.Length, BufferSize));
        try
        {
            while (!source.IsEmpty)
            {
                var count = Math.Min(source.Length, buffer.Length);
                source.Slice(0, count).CopyTo(buffer);
                await stream.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false);
                source = source.Slice(count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask ReadExactlyAsync(this Stream stream, Memory<byte> destination, CancellationToken ct = default)
    {
        while (!destination.IsEmpty)
        {
            var count = await stream.ReadAsync(destination, ct).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The stream ended before the requested bytes were read.");
            }
            destination = destination.Slice(count);
        }
    }
}
#endif
