// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EstInterception.ServiceDefaults;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using OpenCertServer.Ca.Server;
using OpenCertServer.Ca.Utils.Ca;
using OpenCertServer.Est.Server;
using OpenCertServer.Est.Server.Handlers;
using OpenCertServer.Est.Server.Response;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var allowedHosts = (builder.Configuration.GetSection("Interception:AllowedHosts").Get<string[]>() ?? ["httpbin.org"])
    .Select(static host => host.ToLowerInvariant())
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var caCertificatePath = builder.Configuration["Interception:CaCertificatePath"]
    ?? Path.Combine(Path.GetTempPath(), "http-libs-est-interception-ca.pem");

builder.Services
    .AddInMemoryCertificateStore()
    .AddSelfSignedCertificateAuthority(
        new X500DistinguishedName("CN=HTTP Libs Interception Sample CA"),
        certificateValidity: TimeSpan.FromDays(90))
    .AddEstServer<UnavailableCsrTemplateLoader>();

builder.Services.AddSingleton<IValidateCertificateRequests>(new ProxyHostValidation(allowedHosts));

builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, SampleEstBasicAuthenticationHandler>("SampleEstBasic", _ => { });

var enrollPolicy = new AuthorizationPolicyBuilder("SampleEstBasic")
    .RequireAuthenticatedUser()
    .Build();
builder.Services.AddAuthorization();
X509Certificate2? serverCertificate = null;
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(options =>
    options.ServerCertificateSelector = (_, _) => serverCertificate));

var app = builder.Build();

var ca = app.Services.GetRequiredService<ICertificateAuthority>();
var roots = await ca.GetRootCertificates("ecdsa");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(caCertificatePath))!);
await File.WriteAllTextAsync(caCertificatePath, string.Join(Environment.NewLine, roots.Select(static root => root.ExportCertificatePem())));
app.Logger.LogInformation("Interception CA certificate written to {Path}", caCertificatePath);

var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256);
var serverSan = new SubjectAlternativeNameBuilder();
serverSan.AddDnsName("localhost");
serverSan.AddDnsName("ca-host");
serverRequest.CertificateExtensions.Add(serverSan.Build());
serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
var serverResult = await ca.SignCertificateRequest(serverRequest, "ecdsa");
if (serverResult is not SignCertificateResponse.Success serverSuccess)
{
    var message = serverResult is SignCertificateResponse.Error error ? string.Join(Environment.NewLine, error.Errors) : "Unknown CA error.";
    throw new InvalidOperationException($"Could not issue the EST server certificate: {message}");
}

serverCertificate = serverSuccess.Certificate.CopyWithPrivateKey(serverKey);

app.UseRouting().UseAuthentication().UseAuthorization();
app.MapEstServer(enrollPolicy: enrollPolicy);
app.MapGet("/healthz", () => Results.Ok("ok"));
app.Run();

internal sealed class UnavailableCsrTemplateLoader : ICsrTemplateLoader
{
    public Task<CsrAttributesResponse> GetTemplate(string? profileName = null, ClaimsPrincipal? user = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(CsrAttributesResponse.Unavailable());
}

internal sealed class ProxyHostValidation(IReadOnlySet<string> allowedHosts) : IValidateCertificateRequests
{
    public Task<string?> Validate(
        CertificateRequest request,
        string? profile,
        ClaimsIdentity? requestor,
        X509Certificate2? reenrollingFrom = null,
        CancellationToken cancellationToken = default)
    {
        var sanExtension = request.CertificateExtensions.SingleOrDefault(static extension => extension.Oid?.Value == "2.5.29.17");
        if (sanExtension is null)
        {
            return Task.FromResult<string?>("A subject alternative name is required.");
        }

        var san = new X509SubjectAlternativeNameExtension(sanExtension.RawData, sanExtension.Critical);
        var names = san.EnumerateDnsNames()
            .Concat(san.EnumerateIPAddresses().Select(static address => address.ToString()))
            .ToArray();
        var serverCertificateRequest = requestor is null
            && names.Length == 2
            && names.Contains("localhost", StringComparer.OrdinalIgnoreCase)
            && names.Contains("ca-host", StringComparer.OrdinalIgnoreCase);
        if (requestor?.Name != "proxy" && !serverCertificateRequest)
        {
            return Task.FromResult<string?>("Only the proxy enrollment identity may request certificates.");
        }

        if (!serverCertificateRequest && (names.Length == 0 || names.Any(host => !allowedHosts.Contains(host))))
        {
            return Task.FromResult<string?>("The requested subject alternative name is not allowed.");
        }

        var commonName = request.SubjectName.EnumerateRelativeDistinguishedNames()
            .Where(static name => name.GetSingleElementType().Value == "2.5.4.3")
            .Select(static name => name.GetSingleElementValue())
            .SingleOrDefault();
        if (commonName is null
            || (serverCertificateRequest
                ? !string.Equals(commonName, "localhost", StringComparison.OrdinalIgnoreCase)
                : names.Any(name => !string.Equals(name, commonName, StringComparison.OrdinalIgnoreCase))))
        {
            return Task.FromResult<string?>("The common name must match every subject alternative name.");
        }

        return Task.FromResult<string?>(null);
    }
}

internal sealed class SampleEstBasicAuthenticationHandler(
    Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expectedPassword = configuration["Est:Password"] ?? "change-me";
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"proxy:{expectedPassword}"));
        var supplied = Request.Headers.Authorization.ToString();
        if (!supplied.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(supplied[6..]),
                Encoding.ASCII.GetBytes(expected)))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid EST sample credentials."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "proxy")], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
