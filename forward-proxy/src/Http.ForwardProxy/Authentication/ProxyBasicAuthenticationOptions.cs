// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Authentication;

namespace DamianH.Http.ForwardProxy.Authentication;

/// <summary>Options for <see cref="ProxyBasicAuthenticationHandler"/>.</summary>
public sealed class ProxyBasicAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The realm sent in <c>Proxy-Authenticate</c>. Defaults to <c>proxy</c>.</summary>
    public string Realm { get; set; } = "proxy";

    /// <summary>
    /// Validates credentials. Required. Compare secrets in constant time, for example with
    /// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.
    /// </summary>
    public ProxyBasicCredentialValidator? ValidateCredentials { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        base.Validate();
        if (ValidateCredentials is null)
        {
            throw new InvalidOperationException($"{nameof(ProxyBasicAuthenticationOptions)}.{nameof(ValidateCredentials)} must be set.");
        }

        if (string.IsNullOrEmpty(Realm) || Realm.Any(c => c is '"' or '\\' || char.IsControl(c) || c > '\x7e'))
        {
            throw new InvalidOperationException($"{nameof(ProxyBasicAuthenticationOptions)}.{nameof(Realm)} must be non-empty printable ASCII without quotes or backslashes.");
        }
    }
}
