// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Parses and normalizes request-target authorities (<c>host:port</c>, <c>[v6]:port</c>).</summary>
internal static class AuthorityParser
{
    private const int MaxHostLength = 253;

    /// <summary>Parses an authority-form target. The port is required.</summary>
    public static bool TryParse(string authority, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrEmpty(authority))
        {
            return false;
        }

        string rawHost;
        string rawPort;
        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']');
            if (close < 0 || close + 1 >= authority.Length || authority[close + 1] != ':')
            {
                return false;
            }

            rawHost = authority[1..close];
            rawPort = authority[(close + 2)..];
            if (!IPAddress.TryParse(rawHost, out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return false;
            }

            host = v6.ToString();
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon <= 0 || authority.IndexOf(':') != colon)
            {
                return false;
            }

            rawHost = authority[..colon];
            rawPort = authority[(colon + 1)..];
            if (!TryNormalizeHost(rawHost, out host))
            {
                return false;
            }
        }

        return TryParsePort(rawPort, out port);
    }

    /// <summary>
    /// Validates and normalizes a DNS host name or IPv4 literal: ASCII letters, digits, '-', '_' and '.', non-empty
    /// labels, lower-cased, trailing dot removed.
    /// </summary>
    public static bool TryNormalizeHost(string rawHost, out string host)
    {
        host = "";
        var value = rawHost.EndsWith('.') ? rawHost[..^1] : rawHost;
        if (value.Length is 0 or > MaxHostLength)
        {
            return false;
        }

        var labelLength = 0;
        foreach (var c in value)
        {
            if (c == '.')
            {
                if (labelLength == 0)
                {
                    return false;
                }

                labelLength = 0;
                continue;
            }

            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || ++labelLength > 63)
            {
                return false;
            }
        }

        if (labelLength == 0)
        {
            return false;
        }

        host = value.ToLowerInvariant();
        return true;
    }

    private static bool TryParsePort(string value, out int port)
    {
        port = 0;
        if (value.Length is 0 or > 5)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            port = (port * 10) + (c - '0');
        }

        return port is > 0 and <= 65535;
    }
}
