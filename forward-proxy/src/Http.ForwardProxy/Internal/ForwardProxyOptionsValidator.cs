// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;

namespace DamianH.Http.ForwardProxy.Internal;

internal sealed class ForwardProxyOptionsValidator : IValidateOptions<ForwardProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ForwardProxyOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrEmpty(options.ProxyName) || !char.IsAsciiLetter(options.ProxyName[0]) || !options.ProxyName.All(IsTokenChar))
        {
            failures.Add($"{nameof(ForwardProxyOptions.ProxyName)} must be an HTTP token that starts with a letter: letters, digits and any of !#$%&'*+-.^_`|~.");
        }

        if (options.AuthorizationPolicy is not null && options.AuthorizationPolicyName is not null)
        {
            failures.Add($"Set only one of {nameof(ForwardProxyOptions.AuthorizationPolicy)} and {nameof(ForwardProxyOptions.AuthorizationPolicyName)}.");
        }

        CheckTimeout(failures, options.ConnectTimeout, nameof(ForwardProxyOptions.ConnectTimeout));
        CheckTimeout(failures, options.TunnelIdleTimeout, nameof(ForwardProxyOptions.TunnelIdleTimeout));
        CheckTimeout(failures, options.HttpActivityTimeout, nameof(ForwardProxyOptions.HttpActivityTimeout));
        CheckTimeout(failures, options.ConnectRequestHeadersTimeout, nameof(ForwardProxyOptions.ConnectRequestHeadersTimeout));

        if (options.MaxConnectRequestHeadersSize < 64)
        {
            failures.Add($"{nameof(ForwardProxyOptions.MaxConnectRequestHeadersSize)} must be at least 64.");
        }

        if (options.MaxConcurrentTunnels is <= 0)
        {
            failures.Add($"{nameof(ForwardProxyOptions.MaxConcurrentTunnels)} must be positive or null.");
        }

        CheckPorts(failures, options.AllowedConnectPorts, nameof(ForwardProxyOptions.AllowedConnectPorts));
        CheckPorts(failures, options.AllowedHttpPorts, nameof(ForwardProxyOptions.AllowedHttpPorts));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // RFC 9110 tchar. A token starting with a letter is also a valid RFC 8941 sf-token, as Proxy-Status requires.
    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    // Timeouts are converted to int milliseconds (activity timers, SocketsHttpHandler.ConnectTimeout).
    private static void CheckTimeout(List<string> failures, TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
        {
            failures.Add($"{name} must be positive and at most {int.MaxValue} milliseconds (about 24.8 days).");
        }
    }

    private static void CheckPorts(List<string> failures, ISet<int>? ports, string name)
    {
        if (ports?.Any(p => p is < 1 or > 65535) == true)
        {
            failures.Add($"{name} must contain only ports between 1 and 65535.");
        }
    }
}
