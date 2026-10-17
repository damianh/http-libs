// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DamianH.Http.ForwardProxy.Internal.Interception;

internal sealed class TlsInterceptionOptionsValidator : IValidateOptions<ForwardProxyTlsInterceptionOptions>
{
    public ValidateOptionsResult Validate(string? name, ForwardProxyTlsInterceptionOptions options)
    {
        var failures = new List<string>();
        if (options.ClientProtocols is not (HttpProtocols.Http1 or HttpProtocols.Http2 or HttpProtocols.Http1AndHttp2))
        {
            failures.Add($"{nameof(ForwardProxyTlsInterceptionOptions.ClientProtocols)} must be Http1, Http2 or Http1AndHttp2.");
        }

        if (options.HandshakeTimeout <= TimeSpan.Zero || options.HandshakeTimeout.TotalMilliseconds > int.MaxValue)
        {
            failures.Add($"{nameof(ForwardProxyTlsInterceptionOptions.HandshakeTimeout)} must be positive and at most {int.MaxValue} milliseconds (about 24.8 days).");
        }

        if (options.MaxUpstreamClientCertificatePools <= 0)
        {
            failures.Add($"{nameof(ForwardProxyTlsInterceptionOptions.MaxUpstreamClientCertificatePools)} must be positive.");
        }

        if (options.UpstreamClientCertificatePoolIdleTimeout <= TimeSpan.Zero || options.UpstreamClientCertificatePoolIdleTimeout.TotalMilliseconds > int.MaxValue)
        {
            failures.Add($"{nameof(ForwardProxyTlsInterceptionOptions.UpstreamClientCertificatePoolIdleTimeout)} must be positive and at most {int.MaxValue} milliseconds.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
