// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace DamianH.HttpHybridCacheHandler;

public class CompatibilityPrimitiveTests
{
    private static bool UsesLegacyAssets =>
        typeof(HttpHybridCacheHandler).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName
            != ".NETCoreApp,Version=v10.0";

    [Fact]
    public void Header_snapshot_has_explicit_target_specific_representation()
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=60, must-revalidate");
        response.Headers.TryAddWithoutValidation("ETag", "W/ \"v1\"");
        response.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "a=1; Expires=Tue, 01 Jan 2030 00:00:00 GMT", "b=2" });
        response.Headers.TryAddWithoutValidation("X-Custom", new[] { "  A, B  ", "C" });
        var snapshot = HeaderCompatibility.Read(response.Headers).ToDictionary(header => header.Key, header => header.Value);

        snapshot["Cache-Control"].ShouldBe(new[] { UsesLegacyAssets ? "public, must-revalidate, max-age=60" : "public, max-age=60, must-revalidate" });
        snapshot["ETag"].ShouldBe(new[] { UsesLegacyAssets ? "W/\"v1\"" : "W/ \"v1\"" });
        snapshot["Set-Cookie"].ShouldBe(new[] { "a=1; Expires=Tue, 01 Jan 2030 00:00:00 GMT", "b=2" });
        snapshot["X-Custom"].ShouldBe(new[] { "  A, B  ", "C" });
    }

    [Fact]
    public async Task Non_array_memory_stream_operations_preserve_bytes_and_bound_legacy_copies()
    {
        using var memory = new OwnedMemory(200_000);
        var expected = Enumerable.Range(0, 200_000).Select(value => (byte)value).ToArray();
        expected.AsMemory().CopyTo(memory.Memory);
        using var stream = new ArrayStream();
        await stream.WriteAsync(memory.Memory, TestContext.Current.CancellationToken);
        stream.Bytes.ShouldBe(expected);
        if (UsesLegacyAssets)
        {
            stream.MaximumWrite.ShouldBeInRange(1, 64 * 1024);
        }

        stream.Position = 0;
        memory.Memory.Span.Clear();
        var offset = 0;
        while (offset < expected.Length)
        {
            var read = await stream.ReadAsync(memory.Memory.Slice(offset), TestContext.Current.CancellationToken);
            read.ShouldBeGreaterThan(0);
            offset += read;
        }
        memory.Memory.ToArray().ShouldBe(expected);
    }

    [Fact]
    public async Task Cancelled_non_array_write_does_not_write_any_bytes()
    {
        using var memory = new OwnedMemory(1024);
        using var stream = new ArrayStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(
            () => stream.WriteAsync(memory.Memory, cancellation.Token).AsTask());
        stream.Bytes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Spool_hash_and_payload_survive_disk_spill_and_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "httpcache-compat-" + Guid.NewGuid().ToString("N"));
        var expected = Encoding.UTF8.GetBytes("streamed content across a spill");
        var failures = new List<Exception>();
        try
        {
            using (var spool = new AdaptiveSpool(new HttpHybridCacheHandlerOptions
            {
                SpoolDirectory = root,
                SpoolMemoryThreshold = 4
            }, failures.Add))
            {
                await spool.WriteAsync(expected.AsMemory(0, 3), TestContext.Current.CancellationToken);
                await spool.WriteAsync(expected.AsMemory(3), TestContext.Current.CancellationToken);
                Directory.GetDirectories(root).ShouldHaveSingleItem();
#if NET10_0_OR_GREATER
                if (!OperatingSystem.IsWindows())
                {
                    var mode = File.GetUnixFileMode(Directory.GetDirectories(root).Single());
                    mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
#endif
                using var algorithm = SHA256.Create();
                var hash = algorithm.ComputeHash(expected);
                spool.FinishHash().ShouldBe(BitConverter.ToString(hash).Replace("-", ""));
                Hashing.ToHex(hash, true).ShouldBe(BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant());
                Hashing.Sha256(expected).ShouldBe(hash);
                spool.Position = 0;
                using var copy = new MemoryStream();
                await spool.CopyToAsync(copy, 4096, TestContext.Current.CancellationToken);
                copy.ToArray().ShouldBe(expected);
            }
            Directory.GetFileSystemEntries(root).ShouldBeEmpty();
            failures.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class OwnedMemory(int length) : MemoryManager<byte>
    {
        private readonly byte[] _bytes = new byte[length];
        public override Span<byte> GetSpan() => _bytes;
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private sealed class ArrayStream : CompatibleStream
    {
        private readonly MemoryStream _stream = new();
        public int MaximumWrite { get; private set; }
        public byte[] Bytes => _stream.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => _stream.Position = value; }
        public override void Flush() => _stream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count)
        {
            MaximumWrite = Math.Max(MaximumWrite, count);
            _stream.Write(buffer, offset, count);
        }
        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
        public override void SetLength(long value) => _stream.SetLength(value);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
