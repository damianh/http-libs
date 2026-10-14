// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DamianH.Http.ForwardProxy;

namespace ForwardProxySample;

/// <summary>Intercepts the configured hosts only.</summary>
internal sealed class HostListSelector(IReadOnlySet<string> hosts) : IForwardProxyInterceptionSelector
{
    public ValueTask<bool> ShouldInterceptAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken) =>
        ValueTask.FromResult(hosts.Contains(destination.Host));
}

/// <summary>
/// Issues destination certificates from a CA created at startup. Demo only: a real deployment
/// loads a long-lived CA from a secret store or HSM so that clients can trust it permanently.
/// </summary>
internal sealed class EphemeralCaCertificateProvider : IForwardProxyCertificateProvider, IDisposable
{
    private readonly X509Certificate2 _ca;
    private readonly ConcurrentDictionary<string, Lazy<SslStreamCertificateContext>> _cache = new(StringComparer.Ordinal);

    public EphemeralCaCertificateProvider()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ForwardProxySample Interception CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        _ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    public string CaCertificatePem => _ca.ExportCertificatePem();

    public ValueTask<SslStreamCertificateContext?> GetCertificateAsync(ForwardProxyDestination destination, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SslStreamCertificateContext?>(_cache.GetOrAdd(destination.Host, host => new(() => Issue(host))).Value);

    public void Dispose() => _ca.Dispose();

    private SslStreamCertificateContext Issue(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var address))
        {
            san.AddIpAddress(address);
        }
        else
        {
            san.AddDnsName(host);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_ca, true, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var certificate = request.Create(_ca, DateTimeOffset.UtcNow.AddMinutes(-5), _ca.NotAfter.AddMinutes(-1), serial);
        using var withKey = certificate.CopyWithPrivateKey(key);

        // Round-trip through PKCS#12 so the private key is usable by SslStream on every platform.
        var leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
        return SslStreamCertificateContext.Create(leaf, additionalCertificates: null, offline: true);
    }
}
