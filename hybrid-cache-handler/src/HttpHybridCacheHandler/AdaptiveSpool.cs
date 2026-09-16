// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;

namespace DamianH.HttpHybridCacheHandler;

internal sealed class SpoolBudgetExceededException() : IOException("The HTTP cache staging budget was exhausted.");

/// <summary>A completed spool is seekable and caller-owned, as required by content stores.</summary>
internal sealed class AdaptiveSpool(HttpHybridCacheHandlerOptions options, Action<Exception> log) : CompatibleStream
{
    private static readonly object BudgetLock = new();
    private static long _diskBytes;
    private static int _diskSpools;
    private static readonly HashSet<string> ScannedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private Stream _stream = new MemoryStream();
    private FileStream? _lease;
    private string? _directory;
    private long _reservedBytes;
    private bool _diskReserved;
    private bool _disposed;

    public string FinishHash() => Hashing.ToHex(_hash.GetHashAndReset());
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;
    public override long Length => _stream.Length;
    public override long Position { get => _stream.Position; set => _stream.Position = value; }
    public override void Flush() => _stream.Flush();
    public override Task FlushAsync(Ct ct) => _stream.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _stream.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, Ct ct = default) => _stream.ReadAsync(buffer, ct);

    private void Reserve(int count)
    {
        Guard.NotDisposed(_disposed, this);
        if (!_diskReserved && Length + count <= options.SpoolMemoryThreshold)
        {
            // MemoryStream's geometric growth must not exceed the configured ceiling.
            var memory = (MemoryStream)_stream;
            if (memory.Capacity < Length + count)
            {
                memory.Capacity = (int)Math.Min(options.SpoolMemoryThreshold, Math.Max(Length + count, memory.Capacity * 2L));
            }
            return;
        }
        lock (BudgetLock)
        {
            var addedBytes = _diskReserved ? count : Length + count;
            if ((!_diskReserved && _diskSpools >= options.MaxConcurrentDiskSpools) ||
                addedBytes > options.MaxSpoolDiskBytes - _diskBytes)
            {
                throw new SpoolBudgetExceededException();
            }
            _diskBytes += addedBytes;
            _reservedBytes += addedBytes;
            if (!_diskReserved)
            {
                _diskSpools++;
                _diskReserved = true;
            }
        }
        if (_stream is MemoryStream)
        {
            Spill();
        }
    }

    private void Spill()
    {
        var root = Path.GetFullPath(options.SpoolDirectory ?? Path.GetTempPath());
        Directory.CreateDirectory(root);
        lock (BudgetLock)
        {
            if (ScannedRoots.Add(root))
            {
                foreach (var directory in Directory.EnumerateDirectories(root, "httpcache-spool-*"))
                {
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                        // Never remove files from a directory held by a live process.
                        using (new FileStream(Path.Combine(directory, "lease"), FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None))
                        {
                            File.Delete(Path.Combine(directory, "body"));
                        }
                        File.Delete(Path.Combine(directory, "lease"));
                        Directory.Delete(directory);
                    }
                    catch (Exception ex) when (HttpHybridCacheHandler.IsExpectedCacheFailure(ex))
                    {
                        log(ex);
                    }
                }
            }
        }
        var directoryPath = Path.Combine(root, $"httpcache-spool-{Guid.NewGuid():N}");
        PrivateDirectory.Create(directoryPath);
        _directory = directoryPath;
        _lease = new FileStream(Path.Combine(_directory, "lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var disk = new FileStream(Path.Combine(_directory, "body"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        try
        {
            _stream.Position = 0;
            _stream.CopyTo(disk);
        }
        catch
        {
            disk.Dispose();
            throw;
        }
        _stream.Dispose();
        _stream = disk;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Reserve(buffer.Length);
        _stream.Write(buffer);
        AppendHash(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, Ct ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Reserve(buffer.Length);
        await _stream.WriteAsync(buffer, ct);
        AppendHash(buffer.Span);
    }

    private void AppendHash(ReadOnlySpan<byte> buffer)
    {
#if NET10_0_OR_GREATER
        _hash.AppendData(buffer);
#else
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length, 64 * 1024));
        try
        {
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(buffer.Length, rented.Length);
                buffer.Slice(0, count).CopyTo(rented);
                _hash.AppendData(rented, 0, count);
                buffer = buffer.Slice(count);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
#endif
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, Ct ct) =>
        _stream.ReadAsync(buffer, offset, count, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, Ct ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                try
                {
                    _stream.Dispose();
                }
                finally
                {
                    _hash.Dispose();
                    _lease?.Dispose();
                    if (_directory != null)
                    {
                        File.Delete(Path.Combine(_directory, "body"));
                        File.Delete(Path.Combine(_directory, "lease"));
                        Directory.Delete(_directory);
                    }
                }
            }
            catch (Exception ex) when (HttpHybridCacheHandler.IsExpectedCacheFailure(ex))
            {
                log(ex);
            }
            finally
            {
                lock (BudgetLock)
                {
                    _diskBytes -= _reservedBytes;
                    if (_diskReserved)
                    {
                        _diskSpools--;
                    }
                }
            }
        }
        base.Dispose(disposing);
    }
}
