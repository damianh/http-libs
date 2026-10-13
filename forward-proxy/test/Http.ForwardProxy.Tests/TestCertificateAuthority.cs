// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DamianH.Http.ForwardProxy;

/// <summary>A test CA that issues interception certificates, and validation that trusts only that CA.</summary>
internal static class TestCertificateAuthority
{
    public static readonly X509Certificate2 Root = CreateRoot();

    private static readonly ConcurrentDictionary<string, SslStreamCertificateContext> Leaves = new();

    public static SslStreamCertificateContext GetCertificate(string host) =>
        Leaves.GetOrAdd(host, static h => SslStreamCertificateContext.Create(Issue(h), additionalCertificates: null, offline: true));

    /// <summary>Validates a server certificate against <see cref="Root"/> only.</summary>
    public static bool IsIssuedByRoot(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
    }

    private static X509Certificate2 CreateRoot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Forward Proxy Test CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    }

    private static X509Certificate2 Issue(string host)
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
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var certificate = request.Create(Root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), serial);
        using var withKey = certificate.CopyWithPrivateKey(key);

        // Round-trip through PFX so SslStream can use the key on every platform.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }
}
