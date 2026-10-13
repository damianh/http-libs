// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;

namespace DamianH.Http.ForwardProxy.Internal;

internal sealed class ForwardProxyOptionsValidator : IValidateOptions<ForwardProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ForwardProxyOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrEmpty(options.ProxyName) || !char.IsAsciiLetter(options.ProxyName[0]) || !options.ProxyName.All(IsNameChar))
        {
            failures.Add($"{nameof(ForwardProxyOptions.ProxyName)} must start with a letter and contain only letters, digits and '-', '.', '_', '~', ':' or '/'.");
        }

        if (options.AuthorizationPolicy is not null && options.AuthorizationPolicyName is not null)
        {
            failures.Add($"Set only one of {nameof(ForwardProxyOptions.AuthorizationPolicy)} and {nameof(ForwardProxyOptions.AuthorizationPolicyName)}.");
        }

        CheckPositive(failures, options.ConnectTimeout, nameof(ForwardProxyOptions.ConnectTimeout));
        CheckPositive(failures, options.TunnelIdleTimeout, nameof(ForwardProxyOptions.TunnelIdleTimeout));
        CheckPositive(failures, options.HttpActivityTimeout, nameof(ForwardProxyOptions.HttpActivityTimeout));
        CheckPositive(failures, options.ConnectRequestHeadersTimeout, nameof(ForwardProxyOptions.ConnectRequestHeadersTimeout));

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

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or ':' or '/';

    private static void CheckPositive(List<string> failures, TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            failures.Add($"{name} must be positive.");
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
