// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DamianH.Http.HybridCacheHandler;

public sealed class StreamingFillTests : IDisposable
{
    private const string Url = "https://streaming.example.test/resource";
    private readonly Ct _ct = TestContext.Current.CancellationToken;
    private readonly string _directory = Path.Combine(Environment.CurrentDirectory, ".streaming-test-spools", Guid.NewGuid().ToString("N"));

    private HttpHybridCacheHandlerFixture Fixture(HttpMessageHandler handler, Store store,
        Action<HttpHybridCacheHandlerOptions>? configure = null) =>
        new(handler, options =>
        {
            options.LargeContentThreshold = 16;
            options.CompressionThreshold = 0;
            options.SpoolMemoryThreshold = 32;
            options.SpoolDirectory = _directory;
            configure?.Invoke(options);
        }, largeContentStore: store);

    private static HttpResponseMessage Response(Stream stream, long? length = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Content.Headers.ContentLength = length;
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
        return response;
    }

    [Fact]
    public async Task Headers_and_initial_bytes_arrive_before_EOF_and_publication()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 128));
        using var source = new GatedStream(bytes);
        var store = new Store();
        var handler = new Origin(_ => Response(source));
        await using var fixture = Fixture(handler, store);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        source.Reads.ShouldBe(0);
        store.Writes.ShouldBe(0);
        var stream = await response.Content.ReadAsStreamAsync(_ct);
        var first = new byte[8];
        (await stream.ReadAsync(first, _ct)).ShouldBe(8);
        first.ShouldBe(bytes[..8]);
        using var before = await OnlyIfCached(client);
        before.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        source.Release();
        using var remainder = new MemoryStream();
        await stream.CopyToAsync(remainder, _ct);
        remainder.ToArray().ShouldBe(bytes[8..]);
        store.Writes.ShouldBe(1);
        using var after = await OnlyIfCached(client);
        (await after.Content.ReadAsByteArrayAsync(_ct)).ShouldBe(bytes);
        handler.Calls.ShouldBe(1);
        AssertNoSpools();
    }

    [Fact]
    public async Task Upload_is_awaited_at_EOF_before_metadata_is_visible()
    {
        var store = new Store { UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var handler = new Origin(_ => Response(new MemoryStream(new byte[128])));
        await using var fixture = Fixture(handler, store);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        var copy = response.Content.CopyToAsync(Stream.Null, _ct);
        await store.UploadStarted.Task.WaitAsync(_ct);
        copy.IsCompleted.ShouldBeFalse();
        using var before = await OnlyIfCached(client);
        before.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        store.UploadGate.SetResult();
        await copy;
        using var after = await OnlyIfCached(client);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertNoSpools();
    }

    [Fact]
    public async Task Known_length_final_read_validates_EOF_and_awaits_publication()
    {
        var payload = Encoding.UTF8.GetBytes(new string('k', 128));
        using var source = new GatedStream(payload, released: true);
        var store = new Store { UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var fixture = Fixture(new Origin(_ => Response(source, payload.Length)), store);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        source.Reads.ShouldBe(0);
        var stream = await response.Content.ReadAsStreamAsync(_ct);
        var received = new byte[payload.Length];
        (await stream.ReadAsync(received.AsMemory(0, 8), _ct)).ShouldBe(8);
        store.Writes.ShouldBe(0);

        var finalRead = stream.ReadAsync(received.AsMemory(8), _ct).AsTask();
        await store.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        finalRead.IsCompleted.ShouldBeFalse();
        source.Reads.ShouldBe(3); // Initial bytes, remaining bytes, nonempty EOF probe.
        using var before = await OnlyIfCached(client);
        before.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        store.UploadGate.SetResult();
        (await finalRead).ShouldBe(payload.Length - 8);
        received.ShouldBe(payload);

        // No additional consumer read is needed to make the completed body reusable.
        using var cached = await OnlyIfCached(client);
        (await cached.Content.ReadAsByteArrayAsync(_ct)).ShouldBe(payload);
        (await stream.ReadAsync(new byte[1], _ct)).ShouldBe(0);
        source.Reads.ShouldBe(3);
        store.Writes.ShouldBe(1);
        AssertNoSpools();
    }

    [Fact]
    public async Task Known_length_EOF_probe_rejects_unannounced_extra_bytes()
    {
        using var source = new GatedStream(new byte[129], released: true);
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(source, 128)), store);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        var stream = await response.Content.ReadAsStreamAsync(_ct);
        (await stream.ReadAsync(new byte[8], _ct)).ShouldBe(8);
        await Should.ThrowAsync<IOException>(() => stream.ReadAsync(new byte[120], _ct).AsTask());
        store.Writes.ShouldBe(0);
        using var cached = await OnlyIfCached(client);
        cached.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        AssertNoSpools();
    }

    [Fact]
    public async Task Cold_HEAD_with_nonzero_representation_length_does_not_require_a_body()
    {
        var store = new Store();
        var handler = new Origin(request => Response(
            new MemoryStream(request.Method == HttpMethod.Head ? [] : new byte[128]), 128));
        await using var fixture = Fixture(handler, store);
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, Url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _ct);
        response.Content.Headers.ContentLength.ShouldBe(128);
        var stream = await response.Content.ReadAsStreamAsync(_ct);
        (await stream.ReadAsync(new byte[128], _ct)).ShouldBe(0);
        store.Writes.ShouldBe(0);
        using var before = await OnlyIfCached(client);
        before.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        (await client.GetByteArrayAsync(Url, _ct)).Length.ShouldBe(128);
        store.Writes.ShouldBe(1);
        handler.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(204, false)]
    [InlineData(404, true)]
    public async Task Empty_responses_publish_before_headers_without_consumer_reads(int status, bool declareLength)
    {
        using var source = new GatedStream([], released: true);
        var handler = new Origin(_ =>
        {
            var response = Response(source, declareLength ? 0 : null);
            response.StatusCode = (HttpStatusCode)status;
            return response;
        });
        await using var fixture = Fixture(handler, new Store(), options => options.LargeContentThreshold = 1);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);

        // Do not access the first response's Content: proxies need not read bodyless responses.
        source.Reads.ShouldBe(1);
        using var cached = await OnlyIfCached(client);
        cached.StatusCode.ShouldBe((HttpStatusCode)status);
        (await cached.Content.ReadAsByteArrayAsync(_ct)).ShouldBeEmpty();
        handler.Calls.ShouldBe(1);
        AssertNoSpools();
    }

    [Fact]
    public async Task Declared_zero_length_with_actual_bytes_fails_probe_without_publication_or_retry()
    {
        using var source = new GatedStream(new byte[128], released: true);
        var handler = new Origin(_ => Response(source, 0));
        await using var fixture = Fixture(handler, new Store());
        using var client = fixture.CreateClient();
        await Should.ThrowAsync<IOException>(() => client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct));
        source.Reads.ShouldBe(1);
        source.Disposed.ShouldBeTrue();
        using var cached = await OnlyIfCached(client);
        cached.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        handler.Calls.ShouldBe(1);
        AssertNoSpools();
    }

    [Fact]
    public async Task Cancelling_zero_length_probe_releases_origin_and_publishes_nothing()
    {
        using var source = new GatedStream([], firstChunk: 0);
        var handler = new Origin(_ => Response(source, 0));
        await using var fixture = Fixture(handler, new Store());
        using var client = fixture.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var sending = client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(sending);
        source.Disposed.ShouldBeTrue();
        using var cached = await OnlyIfCached(client);
        cached.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        AssertNoSpools();
    }

    [Fact]
    public async Task Early_disposal_does_not_drain_or_publish()
    {
        using var source = new GatedStream(new byte[128], firstChunk: 64);
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(source)), store);
        using var client = fixture.CreateClient();
        var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        var stream = await response.Content.ReadAsStreamAsync(_ct);
        (await stream.ReadAsync(new byte[64], _ct)).ShouldBe(64);
        response.Dispose();
        source.Reads.ShouldBe(1);
        source.Disposed.ShouldBeTrue();
        store.Writes.ShouldBe(0);
        AssertNoSpools();
    }

    [Fact]
    public async Task Cancellation_during_read_releases_spool_and_publishes_nothing()
    {
        using var source = new GatedStream(new byte[128], firstChunk: 64);
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(source)), store);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        var stream = await response.Content.ReadAsStreamAsync(_ct);
        (await stream.ReadAsync(new byte[64], _ct)).ShouldBe(64);
        using var cancellation = new CancellationTokenSource();
        var read = stream.ReadAsync(new byte[64], cancellation.Token).AsTask();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(read);
        store.Writes.ShouldBe(0);
        AssertNoSpools();
    }

    [Fact]
    public async Task Disposal_during_upload_cancels_publication()
    {
        var store = new Store { UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var fixture = Fixture(new Origin(_ => Response(new MemoryStream(new byte[128]))), store);
        using var client = fixture.CreateClient();
        var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        var copy = response.Content.CopyToAsync(Stream.Null, _ct);
        await store.UploadStarted.Task.WaitAsync(_ct);
        response.Dispose();
        await Should.ThrowAsync<OperationCanceledException>(copy);
        using var cached = await OnlyIfCached(client);
        cached.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        AssertNoSpools();
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(1024, 0)]
    [InlineData(64, 32)]
    public async Task Exhausted_disk_budget_bypasses_cache_without_truncating(long bytes, int count)
    {
        var payload = Encoding.UTF8.GetBytes(new string('q', 256 * 1024));
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(new GatedStream(payload, released: true))), store,
            options => { options.MaxSpoolDiskBytes = bytes; options.MaxConcurrentDiskSpools = count; });
        using var client = fixture.CreateClient();
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
        store.Writes.ShouldBe(0);
        AssertNoSpools();
    }

    [Fact]
    public async Task Unknown_length_over_cache_limit_returns_entire_origin_body()
    {
        var payload = Encoding.UTF8.GetBytes(new string('q', 400 * 1024));
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(new GatedStream(payload, released: true))), store,
            options => options.MaxCacheableContentSize = 100);
        using var client = fixture.CreateClient();
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
        store.Writes.ShouldBe(0);
        AssertNoSpools();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Size_exceeded_metric_counts_once_with_snapshotted_tags_and_complete_origin(
        bool knownLength, bool exhaustDiskFirst)
    {
        var payload = Encoding.UTF8.GetBytes(new string('m', 400 * 1024));
        var host = $"metric-overflow-{Guid.NewGuid():N}.example.test";
        var measurements = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        long count = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "DamianH.Http.HybridCacheHandler" &&
                    instrument.Name == HttpHybridCacheHandler.CacheSizeExceededCounterKey)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            var snapshot = tags.ToArray();
            if (snapshot.Any(tag => tag.Key == "server.address" && Equals(tag.Value, host)))
            {
                Interlocked.Add(ref count, measurement);
                measurements.Enqueue(snapshot);
            }
        });
        listener.Start();

        var store = new Store();
        await using var fixture = Fixture(
            new Origin(_ => Response(new GatedStream(payload, firstChunk: 64, released: true),
                knownLength ? payload.Length : null)), store, options =>
            {
                options.MaxCacheableContentSize = 100;
                if (exhaustDiskFirst)
                {
                    options.MaxSpoolDiskBytes = 0;
                }
            });
        using var client = fixture.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/body");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _ct);
        Interlocked.Read(ref count).ShouldBe(knownLength ? 1 : 0);
        request.Method = HttpMethod.Post;
        request.RequestUri = new Uri("http://mutated.invalid/ignored");
        request.Dispose();

        var stream = await response.Content.ReadAsStreamAsync(_ct);
        using var body = new MemoryStream();
        await stream.CopyToAsync(body, _ct);
        (await stream.ReadAsync(new byte[1], _ct)).ShouldBe(0);
        body.ToArray().ShouldBe(payload);
        Interlocked.Read(ref count).ShouldBe(1);
        var metric = measurements.Single().ToDictionary(tag => tag.Key, tag => tag.Value);
        metric["http.request.method"].ShouldBe("GET");
        metric["url.scheme"].ShouldBe("https");
        metric["server.address"].ShouldBe(host);
        metric["server.port"].ShouldBe(443);
        store.Writes.ShouldBe(0);
        AssertNoSpools();
    }

    [Fact]
    public async Task Known_oversized_response_returns_headers_without_reading_or_staging()
    {
        using var source = new GatedStream(new byte[128]);
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(source, 128)), store,
            options => options.MaxCacheableContentSize = 64);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        source.Reads.ShouldBe(0);
        source.Release();
        (await response.Content.ReadAsByteArrayAsync(_ct)).Length.ShouldBe(128);
        store.Writes.ShouldBe(0);
        AssertNoSpools();
    }

    [Fact]
    public async Task Disk_reservations_are_aggregate_and_released_after_disposal()
    {
        using var slow = new GatedStream(new byte[128], firstChunk: 64);
        var calls = 0;
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(Interlocked.Increment(ref calls) == 1
            ? slow : new MemoryStream(new byte[128]))), store, options => options.MaxConcurrentDiskSpools = 1);
        using var client = fixture.CreateClient();
        var first = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        var stream = await first.Content.ReadAsStreamAsync(_ct);
        (await stream.ReadAsync(new byte[64], _ct)).ShouldBe(64);
        Directory.GetDirectories(_directory).Length.ShouldBe(1);
        (await client.GetByteArrayAsync(Url + "?second", _ct)).Length.ShouldBe(128);
        store.Writes.ShouldBe(0);
        first.Dispose();
        (await client.GetByteArrayAsync(Url + "?third", _ct)).Length.ShouldBe(128);
        store.Writes.ShouldBe(1);
        AssertNoSpools();
    }

    [Fact]
    public async Task Compression_spool_budget_failure_preserves_origin()
    {
        var payload = new byte[1024];
        new Random(17).NextBytes(payload);
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(new MemoryStream(payload))), store, options =>
        {
            options.CompressionThreshold = 1;
            options.MaxConcurrentDiskSpools = 1;
        });
        using var client = fixture.CreateClient();
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
        store.Writes.ShouldBe(0);
        AssertNoSpools();
    }

    [Fact]
    public async Task Staging_IO_failure_preserves_origin_and_releases_reservation()
    {
        Directory.CreateDirectory(_directory);
        var blockedPath = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blockedPath, "", _ct);
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(new MemoryStream(new byte[128]))), store,
            options => options.SpoolDirectory = blockedPath);
        using var client = fixture.CreateClient();
        (await client.GetByteArrayAsync(Url, _ct)).Length.ShouldBe(128);
        store.Writes.ShouldBe(0);
        File.Delete(blockedPath);
        AssertNoSpools();
    }

    [Fact]
    public async Task Orphan_cleanup_preserves_live_process_spools()
    {
        Directory.CreateDirectory(_directory);
        var orphan = Path.Combine(_directory, "httpcache-spool-orphan");
        var live = Path.Combine(_directory, "httpcache-spool-live");
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(orphan, "body"), "abandoned", _ct);
        await File.WriteAllTextAsync(Path.Combine(orphan, "lease"), "", _ct);
        await File.WriteAllTextAsync(Path.Combine(live, "body"), "live", _ct);
        using (var lease = new FileStream(Path.Combine(live, "lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var store = new Store();
            await using var fixture = Fixture(new Origin(_ => Response(new MemoryStream(new byte[128]))), store);
            using var client = fixture.CreateClient();
            (await client.GetByteArrayAsync(Url, _ct)).Length.ShouldBe(128);
            Directory.Exists(orphan).ShouldBeFalse();
            File.Exists(Path.Combine(live, "body")).ShouldBeTrue();
            store.Writes.ShouldBe(1);
        }
        Directory.Delete(live, recursive: true);
        AssertNoSpools();
    }

    [Fact]
    public async Task Truncated_known_length_propagates_and_never_publishes()
    {
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(new GatedStream(new byte[64], released: true), 128)), store);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        var stream = await response.Content.ReadAsStreamAsync(_ct);
        await Should.ThrowAsync<IOException>(() => stream.CopyToAsync(Stream.Null, _ct));
        store.Writes.ShouldBe(0);
        AssertNoSpools();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_size_routing_uses_actual_original_length_with_or_without_compression(bool compress)
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 128 * 1024));
        var store = new Store();
        var handler = new Origin(_ => Response(new GatedStream(payload, released: true)));
        await using var fixture = Fixture(handler, store, options =>
        {
            options.LargeContentThreshold = 64 * 1024;
            options.CompressionThreshold = compress ? 1 : 0;
        });
        using var client = fixture.CreateClient();
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
        store.Writes.ShouldBe(1);
        handler.Calls.ShouldBe(1);
        store.LastLength.ShouldBe(compress ? store.Entries.Single().Value.LongLength : payload.LongLength);
        if (compress)
        {
            store.LastLength.ShouldBeLessThan(4096);
        }
        AssertNoSpools();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public async Task Empty_small_and_exact_threshold_bodies_are_cached(int length)
    {
        var payload = new byte[length];
        var store = new Store();
        var handler = new Origin(_ => Response(new GatedStream(payload, released: true)));
        await using var fixture = Fixture(handler, store);
        using var client = fixture.CreateClient();
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
        handler.Calls.ShouldBe(1);
        store.Writes.ShouldBe(length >= 16 ? 1 : 0);
    }

    [Fact]
    public async Task Expected_upload_failure_preserves_origin_but_does_not_publish()
    {
        var payload = new byte[128];
        var store = new Store { Failure = new IOException("offline") };
        await using var fixture = Fixture(new Origin(_ => Response(new MemoryStream(payload))), store);
        using var client = fixture.CreateClient();
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
        using var cached = await OnlyIfCached(client);
        cached.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        AssertNoSpools();
    }

    [Fact]
    public async Task Unexpected_upload_failure_is_not_silently_hidden()
    {
        var store = new Store { Failure = new InvalidOperationException("programming error") };
        await using var fixture = Fixture(new Origin(_ => Response(new MemoryStream(new byte[128]))), store);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        var stream = await response.Content.ReadAsStreamAsync(_ct);
        await Should.ThrowAsync<InvalidOperationException>(() => stream.CopyToAsync(Stream.Null, _ct));
        AssertNoSpools();
    }

    [Fact]
    public async Task Origin_read_failure_propagates_without_storing_partial_bytes()
    {
        using var source = new GatedStream(new byte[128], firstChunk: 64) { ReadFailure = new IOException("origin fault") };
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(source)), store);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        var stream = await response.Content.ReadAsStreamAsync(_ct);
        (await stream.ReadAsync(new byte[64], _ct)).ShouldBe(64);
        source.Release();
        await Should.ThrowAsync<IOException>(() => stream.CopyToAsync(Stream.Null, _ct));
        store.Writes.ShouldBe(0);
        AssertNoSpools();
    }

    [Fact]
    public async Task Origin_header_failure_is_not_retried_as_a_cache_failure()
    {
        var handler = new Origin(_ => throw new IOException("origin header failure"));
        await using var fixture = Fixture(handler, new Store());
        using var client = fixture.CreateClient();
        await Should.ThrowAsync<IOException>(() => client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct));
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Sync_span_and_array_reads_also_complete_the_fill()
    {
        var payload = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        var store = new Store();
        await using var fixture = Fixture(new Origin(_ => Response(new MemoryStream(payload))), store);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        var stream = response.Content.ReadAsStream(_ct);
        var bytes = new byte[128];
        stream.Read(bytes.AsSpan(0, 32)).ShouldBe(32);
        stream.Read(bytes, 32, 96).ShouldBe(96);
        stream.ReadByte().ShouldBe(-1);
        bytes.ShouldBe(payload);
        store.Writes.ShouldBe(1);
        AssertNoSpools();
    }

    [Fact]
    public async Task Unsafe_invalidation_prevents_slow_fill_resurrection()
    {
        using var source = new GatedStream(new byte[128]);
        var store = new Store();
        var handler = new Origin(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : Response(source));
        await using var fixture = Fixture(handler, store);
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        using var mutation = await client.PostAsync(Url, null, _ct);
        source.Release();
        await response.Content.CopyToAsync(Stream.Null, _ct);
        using var cached = await OnlyIfCached(client);
        cached.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        AssertNoSpools();
    }

    [Fact]
    public async Task Unrelated_URI_invalidation_cannot_abandon_a_slow_fill()
    {
        // Deliberately collide in the former striped epoch implementation.
        var formerStripe = (uint)StringComparer.Ordinal.GetHashCode($"httpcache:uri:{Url}") % 256;
        var unrelatedUrl = Enumerable.Range(1, 100000)
            .Select(i => $"{Url}?unrelated={i}")
            .First(uri => (uint)StringComparer.Ordinal.GetHashCode($"httpcache:uri:{uri}") % 256 == formerStripe);
        using var source = new GatedStream(new byte[128]);
        var handler = new Origin(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : Response(source));
        await using var fixture = Fixture(handler, new Store());
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        using var mutation = await client.PostAsync(unrelatedUrl, null, _ct);
        source.Release();
        await response.Content.CopyToAsync(Stream.Null, _ct);
        using var cached = await OnlyIfCached(client);
        cached.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cached.Content.ReadAsByteArrayAsync(_ct)).Length.ShouldBe(128);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Newer_completed_fill_cannot_be_replaced_by_older_slow_fill()
    {
        var old = Encoding.UTF8.GetBytes(new string('o', 128));
        var fresh = Encoding.UTF8.GetBytes(new string('n', 128));
        using var slow = new GatedStream(old);
        var calls = 0;
        var handler = new Origin(_ => Response(Interlocked.Increment(ref calls) == 1 ? slow : new MemoryStream(fresh)));
        var store = new Store();
        await using var fixture = Fixture(handler, store);
        using var client = fixture.CreateClient();
        using var first = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, _ct);
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(fresh);
        slow.Release();
        (await first.Content.ReadAsByteArrayAsync(_ct)).ShouldBe(old);
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(fresh);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Independent_slow_variants_merge_without_losing_newer_variant()
    {
        using var slow = new GatedStream(Encoding.UTF8.GetBytes(new string('e', 128)));
        var handler = new Origin(request =>
        {
            var response = Response(request.Headers.AcceptLanguage.ToString() == "en"
                ? slow : new MemoryStream(Encoding.UTF8.GetBytes(new string('f', 128))));
            response.Headers.Vary.Add("Accept-Language");
            return response;
        });
        await using var fixture = Fixture(handler, new Store());
        using var client = fixture.CreateClient();
        using var english = new HttpRequestMessage(HttpMethod.Get, Url);
        english.Headers.AcceptLanguage.ParseAdd("en");
        using var first = await client.SendAsync(english, HttpCompletionOption.ResponseHeadersRead, _ct);
        using var french = new HttpRequestMessage(HttpMethod.Get, Url);
        french.Headers.AcceptLanguage.ParseAdd("fr");
        using var second = await client.SendAsync(french, _ct);
        (await second.Content.ReadAsStringAsync(_ct)).ShouldBe(new string('f', 128));
        slow.Release();
        (await first.Content.ReadAsStringAsync(_ct)).ShouldBe(new string('e', 128));
        foreach (var language in new[] { "en", "fr" })
        {
            using var lookup = new HttpRequestMessage(HttpMethod.Get, Url);
            lookup.Headers.AcceptLanguage.ParseAdd(language);
            using var cached = await client.SendAsync(lookup, _ct);
            (await cached.Content.ReadAsStringAsync(_ct)).ShouldBe(new string(language == "en" ? 'e' : 'f', 128));
        }
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Caller_mutation_and_request_disposal_do_not_change_snapshot()
    {
        var store = new Store();
        var handler = new Origin(_ =>
        {
            var response = Response(new MemoryStream(new byte[128]));
            response.Headers.TryAddWithoutValidation("X-Snapshot", "original");
            response.Headers.Vary.Add("Accept-Language");
            return response;
        });
        await using var fixture = Fixture(handler, store);
        using var client = fixture.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _ct);
        request.Headers.Remove("Accept-Language");
        request.Headers.TryAddWithoutValidation("Accept-Language", "de");
        request.Dispose();
        response.Headers.Remove("X-Snapshot");
        response.Headers.TryAddWithoutValidation("X-Snapshot", "mutated");
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        await response.Content.CopyToAsync(Stream.Null, _ct);
        using var lookup = new HttpRequestMessage(HttpMethod.Get, Url);
        lookup.Headers.TryAddWithoutValidation("Accept-Language", "en");
        using var cached = await client.SendAsync(lookup, _ct);
        cached.Headers.GetValues("X-Snapshot").Single().ShouldBe("original");
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Background_revalidation_drains_and_publishes_replacement()
    {
        var calls = 0;
        using var replacement = new GatedStream(Encoding.UTF8.GetBytes(new string('n', 128)));
        var store = new Store();
        var handler = new Origin(_ =>
        {
            var response = Response(Interlocked.Increment(ref calls) == 1
                ? new MemoryStream(Encoding.UTF8.GetBytes(new string('o', 128))) : replacement);
            response.Headers.CacheControl = null;
            response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1, stale-while-revalidate=60");
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        });
        await using var fixture = Fixture(handler, store);
        using var client = fixture.CreateClient();
        (await client.GetStringAsync(Url, _ct)).ShouldBe(new string('o', 128));
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        (await client.GetStringAsync(Url, _ct)).ShouldBe(new string('o', 128));
        await replacement.Waiting.Task.WaitAsync(_ct);
        replacement.Release();
        await store.SecondUpload.Task.WaitAsync(_ct);
        // Upload completion precedes metadata; wait for the response/spool ownership to end.
        await replacement.Disposal.Task.WaitAsync(_ct);
        (await client.GetStringAsync(Url, _ct)).ShouldBe(new string('n', 128));
        handler.Calls.ShouldBe(2);
        AssertNoSpools();
    }

    [Fact]
    public async Task Invalidation_during_background_fill_prevents_republication()
    {
        using var replacement = new GatedStream(new byte[128]);
        var calls = 0;
        var handler = new Origin(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            var response = Response(Interlocked.Increment(ref calls) == 1
                ? new MemoryStream(new byte[128]) : replacement);
            response.Headers.CacheControl = null;
            response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1, stale-while-revalidate=60");
            return response;
        });
        await using var fixture = Fixture(handler, new Store());
        using var client = fixture.CreateClient();
        await client.GetByteArrayAsync(Url, _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        await client.GetByteArrayAsync(Url, _ct);
        await replacement.Waiting.Task.WaitAsync(_ct);
        using var mutation = await client.PostAsync(Url, null, _ct);
        replacement.Release();
        await replacement.Disposal.Task.WaitAsync(_ct);
        using var cached = await OnlyIfCached(client);
        cached.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        AssertNoSpools();
    }

    [Fact]
    public async Task Handler_disposal_cancels_background_read_and_releases_disk_spool()
    {
        using var replacement = new GatedStream(new byte[128], firstChunk: 64);
        var calls = 0;
        var origin = new Origin(_ =>
        {
            var response = Response(Interlocked.Increment(ref calls) == 1
                ? new MemoryStream(new byte[128]) : replacement);
            response.Headers.CacheControl = null;
            response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1, stale-while-revalidate=60");
            return response;
        });
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var store = new Store();
        using var handler = new HttpHybridCacheHandler(origin, new InspectableHybridCache(), time, null,
            new HttpHybridCacheHandlerOptions
            {
                LargeContentThreshold = 16,
                CompressionThreshold = 0,
                SpoolMemoryThreshold = 32,
                SpoolDirectory = _directory
            }, Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpHybridCacheHandler>.Instance, store);
        using var client = new HttpClient(handler, disposeHandler: false);
        await client.GetByteArrayAsync(Url, _ct);
        time.Advance(TimeSpan.FromSeconds(2));
        await client.GetByteArrayAsync(Url, _ct);
        await replacement.Waiting.Task.WaitAsync(_ct);
        handler.Dispose();
        await replacement.Disposal.Task.WaitAsync(_ct);
        store.Writes.ShouldBe(1);
        AssertNoSpools();
    }

    [Fact]
    public async Task Not_modified_revalidation_keeps_external_body_and_blocks_older_fill()
    {
        var old = Encoding.UTF8.GetBytes(new string('o', 128));
        var newer = Encoding.UTF8.GetBytes(new string('n', 128));
        using var slow = new GatedStream(newer);
        var calls = 0;
        var handler = new Origin(_ =>
        {
            var call = Interlocked.Increment(ref calls);
            var response = call == 3
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : Response(call == 1 ? new MemoryStream(old) : slow);
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
            return response;
        });
        var store = new Store();
        await using var fixture = Fixture(handler, store);
        using var client = fixture.CreateClient();
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(old);
        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, Url);
        firstRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var first = await client.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead, _ct);
        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, Url);
        secondRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var second = await client.SendAsync(secondRequest, _ct);
        (await second.Content.ReadAsByteArrayAsync(_ct)).ShouldBe(old);
        store.Writes.ShouldBe(1);
        slow.Release();
        (await first.Content.ReadAsByteArrayAsync(_ct)).ShouldBe(newer);
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(old);
        handler.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task Streaming_cached_body_supports_HEAD_and_single_ranges()
    {
        var payload = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        var handler = new Origin(request => request.Method == HttpMethod.Head
            ? Response(new MemoryStream(), payload.Length)
            : Response(new MemoryStream(payload)));
        var store = new Store();
        await using var fixture = Fixture(handler, store);
        using var client = fixture.CreateClient();
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, Url);
        rangeRequest.Headers.Range = new RangeHeaderValue(10, 19);
        using var range = await client.SendAsync(rangeRequest, _ct);
        range.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await range.Content.ReadAsByteArrayAsync(_ct)).ShouldBe(payload[10..20]);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, Url);
        using var head = await client.SendAsync(headRequest, _ct);
        head.Content.Headers.ContentLength.ShouldBe(payload.Length);
        store.Writes.ShouldBe(1);
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compressed_unknown_length_body_replays_original_length_after_HEAD(bool external)
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 4096));
        var handler = new Origin(request => Response(
            new GatedStream(request.Method == HttpMethod.Head ? [] : payload, released: true)));
        var store = new Store();
        await using var fixture = Fixture(handler, store, options =>
        {
            options.CompressionThreshold = 1;
            options.LargeContentThreshold = external ? 16 : 0;
        });
        using var client = fixture.CreateClient();
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);

        using var request = new HttpRequestMessage(HttpMethod.Head, Url);
        using var head = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _ct);
        head.Content.Headers.ContentLength.ShouldBe(payload.LongLength);
        (await head.Content.ReadAsByteArrayAsync(_ct)).ShouldBeEmpty();
        (await client.GetByteArrayAsync(Url, _ct)).ShouldBe(payload);
        handler.Calls.ShouldBe(2);
        if (external)
        {
            store.LastLength.ShouldBeLessThan(payload.LongLength);
        }
    }

    [Theory]
    [InlineData(-1, 1024, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1024, -1)]
    public void Negative_spool_options_are_rejected(int memory, long bytes, int count)
    {
        var options = new HttpHybridCacheHandlerOptions
        {
            SpoolMemoryThreshold = memory, MaxSpoolDiskBytes = bytes, MaxConcurrentDiskSpools = count
        };
        Should.Throw<ArgumentOutOfRangeException>(options.ValidateSpooling);
    }

    private async Task<HttpResponseMessage> OnlyIfCached(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.CacheControl = new CacheControlHeaderValue { OnlyIfCached = true };
        return await client.SendAsync(request, _ct);
    }

    private void AssertNoSpools()
    {
        if (Directory.Exists(_directory))
        {
            Directory.GetFileSystemEntries(_directory).ShouldBeEmpty();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Origin(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Ct cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(response(request));
        }
    }

    private sealed class Store : ILargeHttpCacheContentStore
    {
        public ConcurrentDictionary<string, byte[]> Entries { get; } = new();
        public int Writes;
        public long LastLength;
        public Exception? Failure;
        public TaskCompletionSource? UploadGate;
        public TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondUpload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WriteAsync(string contentKey, Stream content, long contentLength, IEnumerable<string>? tags, Ct ct)
        {
            Interlocked.Increment(ref Writes);
            content.CanRead.ShouldBeTrue();
            content.CanSeek.ShouldBeTrue();
            content.Position.ShouldBe(0);
            content.Length.ShouldBe(contentLength);
            LastLength = contentLength;
            UploadStarted.TrySetResult();
            if (UploadGate != null)
            {
                await UploadGate.Task.WaitAsync(ct);
            }
            if (Failure != null)
            {
                throw Failure;
            }
            var bytes = new byte[checked((int)contentLength)];
            await content.ReadExactlyAsync(bytes, ct);
            Entries[contentKey] = bytes;
            if (Writes >= 2)
            {
                SecondUpload.TrySetResult();
            }
        }

        public ValueTask<Stream?> OpenReadAsync(string contentKey, Ct ct) =>
            ValueTask.FromResult(Entries.TryGetValue(contentKey, out var bytes) ? (Stream)new MemoryStream(bytes) : null);
        public ValueTask RemoveAsync(string contentKey, Ct ct)
        {
            Entries.TryRemove(contentKey, out _);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedStream(byte[] bytes, int firstChunk = 8, bool released = false) : Stream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        public int Reads;
        public bool Disposed;
        public Exception? ReadFailure;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate.TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, Ct ct = default)
        {
            Interlocked.Increment(ref Reads);
            if (_position >= firstChunk)
            {
                Waiting.TrySetResult();
                if (!released)
                {
                    await _gate.Task.WaitAsync(ct);
                }
                if (ReadFailure != null)
                {
                    throw ReadFailure;
                }
            }
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            if (_position == 0)
            {
                count = Math.Min(firstChunk, count);
            }
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            Disposal.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
