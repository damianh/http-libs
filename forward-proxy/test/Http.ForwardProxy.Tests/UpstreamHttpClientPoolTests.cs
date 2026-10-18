// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DamianH.Http.ForwardProxy.Internal;
using DamianH.Http.ForwardProxy.Internal.Forwarding;
using DamianH.Http.ForwardProxy.Internal.Interception;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class UpstreamHttpClientPoolTests
{
    [Fact]
    public async Task SameDerSharesOneEntryUnderConcurrentAcquisition()
    {
        using var certificate = TestCertificateAuthority.IssueClient();
        var factory = new ClientFactory();
        using var pool = new UpstreamHttpClientPool(factory.Create, new());
        var leases = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            using var wrapper = new X509Certificate2(certificate);
            return pool.Acquire(wrapper);
        })));
        try
        {
            factory.Clients.Count.ShouldBe(2);
            leases.Select(l => l.Client).Distinct().Count().ShouldBe(1);
            factory.Clients[1].Certificate.ShouldNotBeSameAs(certificate);
            factory.Clients[1].Certificate!.GetCertHashString(HashAlgorithmName.SHA256)
                .ShouldBe(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public void CapacityProtectsActiveEntriesAndExcludesDefaultPool()
    {
        using var a = TestCertificateAuthority.IssueClient();
        using var b = TestCertificateAuthority.IssueClient();
        var factory = new ClientFactory();
        using var pool = new UpstreamHttpClientPool(factory.Create, new() { MaxUpstreamClientCertificatePools = 1 });
        using var defaultLease = pool.Acquire(null);
        var aLease = pool.Acquire(a);
        using var anotherA = pool.Acquire(a);

        var failure = Should.Throw<ProxyErrorException>(() => pool.Acquire(b));
        failure.Error.StatusCode.ShouldBe(503);
        failure.Error.ErrorType.ShouldBe(ProxyErrorTypes.ConnectionLimitReached);
        aLease.Dispose();
        aLease.Dispose();
        Should.Throw<ProxyErrorException>(() => pool.Acquire(b));
        anotherA.Dispose();

        using var bLease = pool.Acquire(b);
        factory.Clients[1].Handler.Disposed.ShouldBeTrue();
        factory.Clients[0].Handler.Disposed.ShouldBeFalse();
        bLease.Client.ShouldNotBeSameAs(defaultLease.Client);
        a.HasPrivateKey.ShouldBeTrue();
    }

    [Fact]
    public void IdleCleanupRunsWithoutTrafficAndDoesNotInterruptActiveLeases()
    {
        using var a = TestCertificateAuthority.IssueClient();
        using var b = TestCertificateAuthority.IssueClient();
        var time = new ManualTimeProvider();
        var factory = new ClientFactory();
        using var pool = new UpstreamHttpClientPool(factory.Create, new()
        {
            UpstreamClientCertificatePoolIdleTimeout = TimeSpan.FromMinutes(2),
        }, time);
        using var active = pool.Acquire(a);
        pool.Acquire(b).Dispose();

        time.Advance(TimeSpan.FromMinutes(2));

        factory.Clients[1].Handler.Disposed.ShouldBeFalse();
        factory.Clients[2].Handler.Disposed.ShouldBeTrue();
        Should.Throw<CryptographicException>(() => factory.Clients[2].Certificate!.GetCertHash());
        b.HasPrivateKey.ShouldBeTrue();

        active.Dispose();
        time.Advance(TimeSpan.FromSeconds(119));
        factory.Clients[1].Handler.Disposed.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(1));
        factory.Clients[1].Handler.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void CapacityEvictsOldestIdleEntry()
    {
        using var a = TestCertificateAuthority.IssueClient();
        using var b = TestCertificateAuthority.IssueClient();
        using var c = TestCertificateAuthority.IssueClient();
        var time = new ManualTimeProvider();
        var factory = new ClientFactory();
        using var pool = new UpstreamHttpClientPool(factory.Create, new() { MaxUpstreamClientCertificatePools = 2 }, time);
        pool.Acquire(a).Dispose();
        time.Advance(TimeSpan.FromSeconds(1));
        pool.Acquire(b).Dispose();
        using var lease = pool.Acquire(c);

        factory.Clients[1].Handler.Disposed.ShouldBeTrue();
        factory.Clients[2].Handler.Disposed.ShouldBeFalse();
    }

    [Fact]
    public void PoolOwnsIndependentPrivateKeyAndDisposesOnlyItsCopies()
    {
        var certificate = TestCertificateAuthority.IssueClient();
        using var callerCopy = new X509Certificate2(certificate);
        var factory = new ClientFactory();
        var time = new ManualTimeProvider();
        var pool = new UpstreamHttpClientPool(factory.Create, new(), time);
        var lease = pool.Acquire(certificate);
        certificate.Dispose();
        var owned = factory.Clients[1].Certificate!;
        using (var key = owned.GetECDsaPrivateKey())
        {
            key.ShouldNotBeNull();
            var data = new byte[] { 1, 2, 3 };
            var signature = key.SignData(data, HashAlgorithmName.SHA256);
            using var publicKey = callerCopy.GetECDsaPublicKey();
            publicKey!.VerifyData(data, signature, HashAlgorithmName.SHA256).ShouldBeTrue();
        }

        pool.Dispose();
        pool.Dispose();
        lease.Dispose();
        time.TimerDisposed.ShouldBeTrue();
        factory.Clients.ShouldAllBe(c => c.Handler.Disposed);
        Should.Throw<CryptographicException>(() => owned.GetCertHash());
        callerCopy.HasPrivateKey.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => pool.Acquire(null));
    }

    [Fact]
    public void InvalidSelectionCannotReuseCachedIdentity()
    {
        using var valid = TestCertificateAuthority.IssueClient();
        using var publicOnly = X509CertificateLoader.LoadCertificate(valid.RawData);
        using var expired = TestCertificateAuthority.IssueClient(DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
        using var future = TestCertificateAuthority.IssueClient(DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2));
        var factory = new ClientFactory();
        using var pool = new UpstreamHttpClientPool(factory.Create, new());
        pool.Acquire(valid).Dispose();

        Should.Throw<InvalidOperationException>(() => pool.Acquire(publicOnly));
        Should.Throw<InvalidOperationException>(() => pool.Acquire(expired));
        Should.Throw<InvalidOperationException>(() => pool.Acquire(future));
        var disposed = new X509Certificate2(valid);
        disposed.Dispose();
        Should.Throw<CryptographicException>(() => pool.Acquire(disposed));
        factory.Clients.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentCleanupCannotDisposeAnAcquiredClient()
    {
        using var certificate = TestCertificateAuthority.IssueClient();
        var time = new ManualTimeProvider();
        var factory = new ClientFactory();
        using var pool = new UpstreamHttpClientPool(factory.Create, new()
        {
            UpstreamClientCertificatePoolIdleTimeout = TimeSpan.FromMilliseconds(1),
        }, time);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var index = 0; index < 100; index++)
            {
                using var lease = pool.Acquire(certificate);
                time.Advance(TimeSpan.FromMilliseconds(1));
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
                using var response = await lease.Client.SendAsync(request, TestContext.Current.CancellationToken);
                response.IsSuccessStatusCode.ShouldBeTrue();
            }
        })));
    }

    [Fact]
    public void FailedClientCreationDisposesItsCertificateCopy()
    {
        using var certificate = TestCertificateAuthority.IssueClient();
        X509Certificate2? copied = null;
        using var pool = new UpstreamHttpClientPool(c =>
        {
            if (c is not null)
            {
                copied = c;
                throw new InvalidOperationException("factory failed");
            }

            return new HttpMessageInvoker(new TrackingHandler());
        }, new());

        Should.Throw<InvalidOperationException>(() => pool.Acquire(certificate));
        copied.ShouldNotBeNull();
        Should.Throw<CryptographicException>(() => copied.GetCertHash());
        certificate.HasPrivateKey.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 120)]
    [InlineData(-1, 120)]
    [InlineData(128, 0)]
    [InlineData(128, -1)]
    [InlineData(128, int.MaxValue)]
    public void ValidatesPoolOptions(int capacity, int idleSeconds)
    {
        var options = new ForwardProxyTlsInterceptionOptions
        {
            MaxUpstreamClientCertificatePools = capacity,
            UpstreamClientCertificatePoolIdleTimeout = TimeSpan.FromSeconds(idleSeconds),
        };
        new TlsInterceptionOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    private sealed class ClientFactory
    {
        public List<(X509Certificate2? Certificate, TrackingHandler Handler)> Clients { get; } = [];

        public HttpMessageInvoker Create(X509Certificate2? certificate)
        {
            var handler = new TrackingHandler();
            Clients.Add((certificate, handler));
            return new HttpMessageInvoker(handler);
        }
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private long _ticks;
        private TimerCallback? _callback;
        private object? _state;
        public bool TimerDisposed { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => _start + TimeSpan.FromTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new ManualTimer(this);
        }

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _ticks, elapsed.Ticks);
            if (!TimerDisposed)
            {
                _callback?.Invoke(_state);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => !owner.TimerDisposed;
            public void Dispose() => owner.TimerDisposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
