// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers;

namespace DamianH.HttpHybridCacheHandler;

/// <summary>
/// A buffer that stores data in segments to avoid Large Object Heap allocations.
/// Uses ArrayPool to rent segments and avoids allocating contiguous large arrays.
/// </summary>
internal sealed class SegmentedBuffer : IDisposable
{
    private const int SegmentSize = 81920; // 80KB - well below LOH threshold
    private readonly List<byte[]> _segments = [];
    private int _currentSegmentIndex;
    private int _currentSegmentPosition;
    private long _totalLength;
    private bool _disposed;

    public long Length => _totalLength;

    /// <summary>
    /// Writes data to the segmented buffer, renting new segments as needed.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        Guard.NotDisposed(_disposed, this);

        var remaining = data;
        while (remaining.Length > 0)
        {
            // Ensure we have a current segment
            if (_currentSegmentIndex >= _segments.Count)
            {
                var newSegment = ArrayPool<byte>.Shared.Rent(SegmentSize);
                _segments.Add(newSegment);
                _currentSegmentPosition = 0;
            }

            var currentSegment = _segments[_currentSegmentIndex];
            var availableInSegment = SegmentSize - _currentSegmentPosition;
            var toCopy = Math.Min(remaining.Length, availableInSegment);

            remaining.Slice(0, toCopy).CopyTo(currentSegment.AsSpan(_currentSegmentPosition));
            _currentSegmentPosition += toCopy;
            _totalLength += toCopy;
            remaining = remaining.Slice(toCopy);

            // Move to next segment if current is full
            if (_currentSegmentPosition >= SegmentSize)
            {
                _currentSegmentIndex++;
            }
        }
    }

    /// <summary>
    /// Copies all data from segments into a single contiguous array.
    /// This is only called when we need to cache the response.
    /// </summary>
    public byte[] ToArray()
    {
        Guard.NotDisposed(_disposed, this);

        if (_totalLength == 0)
        {
            return [];
        }

        var result = new byte[_totalLength];
        var offset = 0;

        for (var i = 0; i <= _currentSegmentIndex && i < _segments.Count; i++)
        {
            var segment = _segments[i];
            var lengthToCopy = i == _currentSegmentIndex
                ? _currentSegmentPosition
                : SegmentSize;

            segment.AsSpan(0, lengthToCopy).CopyTo(result.AsSpan(offset));
            offset += lengthToCopy;
        }

        return result;
    }

    /// <summary>
    /// Copies all data from segments to the destination stream.
    /// </summary>
    public async Task CopyToAsync(Stream destination, Ct cancellationToken)
    {
        Guard.NotDisposed(_disposed, this);

        for (var i = 0; i <= _currentSegmentIndex && i < _segments.Count; i++)
        {
            var segment = _segments[i];
            var lengthToCopy = i == _currentSegmentIndex
                ? _currentSegmentPosition
                : SegmentSize;

            await destination.WriteAsync(segment.AsMemory(0, lengthToCopy), cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var segment in _segments)
        {
            ArrayPool<byte>.Shared.Return(segment);
        }

        _segments.Clear();
        _disposed = true;
    }
}
