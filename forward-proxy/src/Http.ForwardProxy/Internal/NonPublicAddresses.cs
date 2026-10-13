// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Sockets;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Address ranges that must not be reachable from an internet egress proxy by default.</summary>
internal static class NonPublicAddresses
{
    private static readonly IPNetwork[] V4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),        // "this" network
        IPNetwork.Parse("10.0.0.0/8"),       // private
        IPNetwork.Parse("100.64.0.0/10"),    // shared address space (CGNAT)
        IPNetwork.Parse("127.0.0.0/8"),      // loopback
        IPNetwork.Parse("169.254.0.0/16"),   // link-local, including cloud metadata 169.254.169.254
        IPNetwork.Parse("172.16.0.0/12"),    // private
        IPNetwork.Parse("192.0.0.0/24"),     // IETF protocol assignments
        IPNetwork.Parse("192.0.2.0/24"),     // documentation
        IPNetwork.Parse("192.88.99.0/24"),   // deprecated 6to4 relay anycast
        IPNetwork.Parse("192.168.0.0/16"),   // private
        IPNetwork.Parse("198.18.0.0/15"),    // benchmarking
        IPNetwork.Parse("198.51.100.0/24"),  // documentation
        IPNetwork.Parse("203.0.113.0/24"),   // documentation
        IPNetwork.Parse("224.0.0.0/4"),      // multicast
        IPNetwork.Parse("240.0.0.0/4"),      // reserved, including broadcast
    ];

    private static readonly IPNetwork[] V6 =
    [
        IPNetwork.Parse("::/96"),            // unspecified, loopback and deprecated IPv4-compatible
        IPNetwork.Parse("100::/64"),         // discard-only
        IPNetwork.Parse("2001::/23"),        // IETF protocol assignments, including Teredo
        IPNetwork.Parse("2001:db8::/32"),    // documentation
        IPNetwork.Parse("3fff::/20"),        // documentation
        IPNetwork.Parse("5f00::/16"),        // segment routing SIDs
        IPNetwork.Parse("fc00::/7"),         // unique local
        IPNetwork.Parse("fe80::/10"),        // link-local
        IPNetwork.Parse("fec0::/10"),        // deprecated site-local
        IPNetwork.Parse("ff00::/8"),         // multicast
    ];

    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork Nat64Local = IPNetwork.Parse("64:ff9b:1::/48");
    private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");

    public static bool IsNonPublic(IPAddress address)
    {
        address = Canonicalize(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return Contains(V4, address);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return true;
        }

        if (Nat64Local.Contains(address))
        {
            return true;
        }

        // Translation prefixes embed an IPv4 address; judge them by the embedded address.
        if (Nat64.Contains(address) || SixToFour.Contains(address))
        {
            return IsNonPublic(GetEmbeddedIPv4(address));
        }

        return Contains(V6, address);
    }

    /// <summary>Maps IPv4-mapped IPv6 to IPv4 and drops IPv6 scope IDs, so policy sees the real destination.</summary>
    public static IPAddress Canonicalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            return new IPAddress(address.GetAddressBytes());
        }

        return address;
    }

    internal static IPAddress GetEmbeddedIPv4(IPAddress v6)
    {
        var bytes = v6.GetAddressBytes();
        return SixToFour.Contains(v6)
            ? new IPAddress(bytes.AsSpan(2, 4))
            : new IPAddress(bytes.AsSpan(12, 4));
    }

    private static bool Contains(IPNetwork[] networks, IPAddress address)
    {
        foreach (var network in networks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}
