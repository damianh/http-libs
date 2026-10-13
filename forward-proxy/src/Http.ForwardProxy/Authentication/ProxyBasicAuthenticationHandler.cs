// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DamianH.Http.ForwardProxy.Authentication;

/// <summary>
/// RFC 7617 Basic authentication for proxy clients: reads <c>Proxy-Authorization</c> and challenges with
/// <c>407</c> and <c>Proxy-Authenticate</c>.
/// </summary>
public sealed class ProxyBasicAuthenticationHandler(
    IOptionsMonitor<ProxyBasicAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ProxyBasicAuthenticationOptions>(options, logger, encoder)
{
    private const string BasicPrefix = "Basic ";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers[HeaderNames.ProxyAuthorization];
        if (header.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (header.Count > 1)
        {
            return AuthenticateResult.Fail("Multiple Proxy-Authorization headers.");
        }

        var value = header[0]!;
        if (!value.StartsWith(BasicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(Convert.FromBase64String(value[BasicPrefix.Length..].Trim()));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        var userName = decoded[..separator];
        var password = decoded[(separator + 1)..];
        if (!await Options.ValidateCredentials!(Context, userName, password).ConfigureAwait(false))
        {
            return AuthenticateResult.Fail("Invalid proxy credentials.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userName, ClaimValueTypes.String, ClaimsIssuer), new Claim(ClaimTypes.Name, userName, ClaimValueTypes.String, ClaimsIssuer)],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status407ProxyAuthenticationRequired;
        Response.Headers.Append(HeaderNames.ProxyAuthenticate, $"Basic realm=\"{Options.Realm}\", charset=\"UTF-8\"");
        return Task.CompletedTask;
    }
}
