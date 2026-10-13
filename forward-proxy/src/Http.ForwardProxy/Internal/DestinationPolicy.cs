// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using Microsoft.Extensions.Options;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Built-in host, port and IP address rules.</summary>
internal sealed class DestinationPolicy(IOptions<ForwardProxyOptions> options)
{
    private readonly ForwardProxyOptions _options = options.Value;

    /// <summary>Checks host and port rules. Returns <see langword="null"/> when allowed.</summary>
    public ProxyError? CheckDestination(ForwardProxyDestination destination)
    {
        var ports = destination.Kind == ForwardProxyRequestKind.Connect ? _options.AllowedConnectPorts : _options.AllowedHttpPorts;
        if (ports is not null && !ports.Contains(destination.Port))
        {
            return ProxyError.Denied($"Port {destination.Port} is not allowed.");
        }

        if (MatchesAny(_options.DeniedHosts, destination.Host))
        {
            return ProxyError.Denied("Host is denied.");
        }

        if (_options.AllowedHosts.Count > 0 && !MatchesAny(_options.AllowedHosts, destination.Host))
        {
            return ProxyError.Denied("Host is not allowed.");
        }

        // IP literals fail fast here; the dialer re-checks every address it connects to.
        if (IPAddress.TryParse(destination.Host, out var literal) && !IsAddressAllowed(literal))
        {
            return new ProxyError(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, ProxyErrorTypes.DestinationIpProhibited, "Destination address is prohibited.");
        }

        return null;
    }

    /// <summary>Checks a resolved address. Called for every address immediately before connecting.</summary>
    public bool IsAddressAllowed(IPAddress address)
    {
        address = NonPublicAddresses.Canonicalize(address);
        foreach (var network in _options.DeniedNetworks)
        {
            if (network.Contains(address))
            {
                return false;
            }
        }

        foreach (var network in _options.AllowedNetworks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return !_options.BlockNonPublicAddresses || !NonPublicAddresses.IsNonPublic(address);
    }

    internal static bool MatchesAny(IList<string> patterns, string host)
    {
        foreach (var pattern in patterns)
        {
            if (Matches(pattern, host))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool Matches(string pattern, string host)
    {
        if (pattern == "*")
        {
            return true;
        }

        var normalized = pattern.TrimEnd('.');
        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = normalized[1..];
            return host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        if (normalized.StartsWith('[') && normalized.EndsWith(']'))
        {
            normalized = normalized[1..^1];
        }

        if (IPAddress.TryParse(normalized, out var patternAddress) && IPAddress.TryParse(host, out var hostAddress))
        {
            return NonPublicAddresses.Canonicalize(patternAddress).Equals(NonPublicAddresses.Canonicalize(hostAddress));
        }

        return string.Equals(normalized, host, StringComparison.OrdinalIgnoreCase);
    }
}
