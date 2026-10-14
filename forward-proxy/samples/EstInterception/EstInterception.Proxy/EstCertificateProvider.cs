// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DamianH.Http.ForwardProxy;

namespace EstInterception.Proxy;

internal sealed class HostListSelector(IReadOnlySet<string> hosts) : IForwardProxyInterceptionSelector
{
    public ValueTask<bool> ShouldInterceptAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken) =>
        ValueTask.FromResult(hosts.Contains(destination.Host));
}

internal sealed class EstCertificateProvider : IForwardProxyCertificateProvider, IDisposable
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(7);
    private readonly ConcurrentDictionary<string, Lazy<Task<CertificateEntry>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<X509Certificate2> _issuedCertificates = [];
    private readonly HttpClient _httpClient;
    private readonly Uri _estUri;
    private readonly AuthenticationHeaderValue _authorization;
    private readonly X509Certificate2 _caCertificate;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<EstCertificateProvider> _logger;

    public EstCertificateProvider(IConfiguration configuration, IHostApplicationLifetime lifetime, ILogger<EstCertificateProvider> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
        _estUri = new Uri(configuration["Est:Url"] ?? "https://localhost:5443", UriKind.Absolute);
        var username = configuration["Est:User"] ?? "proxy";
        var password = configuration["Est:Password"] ?? "change-me";
        _authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        var caPath = configuration["Est:CaCertificatePath"]
            ?? Path.Combine(Path.GetTempPath(), "http-libs-est-interception-ca.pem");
        _caCertificate = X509CertificateLoader.LoadCertificateFromFile(caPath);

        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null)
            {
                return false;
            }

            using var presentedCertificate = new X509Certificate2(certificate);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_caCertificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return !errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
                && chain.Build(presentedCertificate)
                && errors is (SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors);
        };
        _httpClient = new HttpClient(handler, disposeHandler: true);
    }

    public async ValueTask<SslStreamCertificateContext?> GetCertificateAsync(ForwardProxyDestination destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            var lazy = _cache.GetOrAdd(destination.Host, host => new Lazy<Task<CertificateEntry>>(
                () => IssueAsync(host, _lifetime.ApplicationStopping),
                LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                var entry = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (entry.ExpiresAt - DateTimeOffset.UtcNow > RenewalWindow)
                {
                    return entry.Context;
                }

                _cache.TryRemove(new KeyValuePair<string, Lazy<Task<CertificateEntry>>>(destination.Host, lazy));
            }
            catch
            {
                _cache.TryRemove(new KeyValuePair<string, Lazy<Task<CertificateEntry>>>(destination.Host, lazy));
                throw;
            }
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _caCertificate.Dispose();
        foreach (var certificate in _issuedCertificates)
        {
            certificate.Dispose();
        }
    }

    private async Task<CertificateEntry> IssueAsync(string host, CancellationToken cancellationToken)
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
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthenticationOid)], false));
        var csr = request.CreateSigningRequest();

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_estUri, "/.well-known/est/ecdsa/simpleenroll"));
        message.Headers.Authorization = _authorization;
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pkcs7-mime"));
        message.Content = new StringContent(Convert.ToBase64String(csr), Encoding.ASCII, "application/pkcs10");
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"EST enrollment for {host} failed with {(int)response.StatusCode}: {body}");
        }

        var cms = new SignedCms();
        cms.Decode(Convert.FromBase64String(body));
        var issued = cms.Certificates.Cast<X509Certificate2>()
            .SingleOrDefault(certificate => GetSubjectAlternativeNames(certificate).Any(name =>
                string.Equals(name, host, StringComparison.OrdinalIgnoreCase)));
        if (issued is null)
        {
            throw new InvalidDataException($"EST did not return a certificate for {host}.");
        }

        var leaf = issued.CopyWithPrivateKey(key);
        _issuedCertificates.Add(leaf);
        var intermediates = new X509Certificate2Collection { _caCertificate };
        var context = SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
        _logger.LogInformation("Enrolled an interception certificate for {Host}, expiring {ExpiresAt}.", host, leaf.NotAfter);
        return new CertificateEntry(context, new DateTimeOffset(leaf.NotAfter.ToUniversalTime()));
    }

    private static IEnumerable<string> GetSubjectAlternativeNames(X509Certificate2 certificate) =>
        certificate.Extensions
            .Where(static extension => extension.Oid?.Value == "2.5.29.17")
            .Select(static extension => new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical))
            .SelectMany(static extension => extension.EnumerateDnsNames()
                .Concat(extension.EnumerateIPAddresses().Select(static address => address.ToString())));

    private sealed record CertificateEntry(SslStreamCertificateContext Context, DateTimeOffset ExpiresAt);
}
