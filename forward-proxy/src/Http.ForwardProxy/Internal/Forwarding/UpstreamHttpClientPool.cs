// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

/// <summary>Owns immutable transports, partitioned by upstream TLS client identity.</summary>
internal sealed class UpstreamHttpClientPool : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<X509Certificate2?, HttpMessageInvoker> _createClient;
    private readonly HttpMessageInvoker _defaultClient;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly int _capacity;
    private readonly TimeSpan _idleTimeout;
    private bool _disposed;

    public UpstreamHttpClientPool(
        Func<X509Certificate2?, HttpMessageInvoker> createClient,
        ForwardProxyTlsInterceptionOptions options,
        TimeProvider? timeProvider = null)
    {
        _createClient = createClient;
        _capacity = options.MaxUpstreamClientCertificatePools;
        _idleTimeout = options.UpstreamClientCertificatePoolIdleTimeout;
        _time = timeProvider ?? TimeProvider.System;
        _defaultClient = createClient(null);
        var period = TimeSpan.FromMilliseconds(Math.Clamp(_idleTimeout.TotalMilliseconds / 4, 1, 30_000));
        _timer = _time.CreateTimer(_ => RemoveExpired(), null, period, period);
    }

    public Lease Acquire(X509Certificate2? certificate)
    {
        string? key = null;
        if (certificate is not null)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (!certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
            {
                throw new InvalidOperationException("The upstream client certificate must be currently valid and have a private key.");
            }

            key = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }

        Entry? retired = null;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (key is null)
                {
                    return new Lease(this, _defaultClient, null);
                }

                if (_entries.TryGetValue(key, out var entry))
                {
                    entry.Users++;
                    return new Lease(this, entry.Client, entry);
                }

                if (_entries.Count >= _capacity)
                {
                    var timestamp = _time.GetTimestamp();
                    retired = _entries.Values.Where(e => e.Users == 0)
                        .MaxBy(e => _time.GetElapsedTime(e.IdleSince, timestamp));
                    if (retired is null)
                    {
                        throw new ProxyErrorException(new(StatusCodes.Status503ServiceUnavailable,
                            ProxyErrorTypes.ConnectionLimitReached, "All upstream client-certificate pools are active."));
                    }

                    _entries.Remove(retired.Key);
                }

                // Duplicate the native certificate/key handles, not the key material; non-exportable keys work too.
                var ownedCertificate = new X509Certificate2(certificate!);
                try
                {
                    entry = new Entry(key, ownedCertificate, _createClient(ownedCertificate));
                }
                catch
                {
                    ownedCertificate.Dispose();
                    throw;
                }

                _entries.Add(key, entry);
                return new Lease(this, entry.Client, entry);
            }
        }
        finally
        {
            retired?.Dispose();
        }
    }

    private void Release(Entry? entry)
    {
        if (entry is null)
        {
            return;
        }

        lock (_gate)
        {
            if (--entry.Users == 0)
            {
                entry.IdleSince = _time.GetTimestamp();
            }
        }
    }

    private void RemoveExpired()
    {
        List<Entry> retired = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _time.GetTimestamp();
            foreach (var entry in _entries.Values)
            {
                if (entry.Users == 0 && _time.GetElapsedTime(entry.IdleSince, now) >= _idleTimeout)
                {
                    retired.Add(entry);
                }
            }

            foreach (var entry in retired)
            {
                _entries.Remove(entry.Key);
            }
        }

        foreach (var entry in retired)
        {
            entry.Dispose();
        }
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }

        _timer.Dispose();
        _defaultClient.Dispose();
        foreach (var entry in entries)
        {
            entry.Dispose();
        }
    }

    internal sealed class Entry(string key, X509Certificate2 certificate, HttpMessageInvoker client) : IDisposable
    {
        public string Key { get; } = key;
        public HttpMessageInvoker Client { get; } = client;
        public int Users { get; set; } = 1;
        public long IdleSince { get; set; }

        public void Dispose()
        {
            Client.Dispose();
            certificate.Dispose();
        }
    }

    internal sealed class Lease(UpstreamHttpClientPool owner, HttpMessageInvoker client, Entry? entry) : IDisposable
    {
        private UpstreamHttpClientPool? _owner = owner;

        public HttpMessageInvoker Client { get; } = client;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(entry);
    }
}
