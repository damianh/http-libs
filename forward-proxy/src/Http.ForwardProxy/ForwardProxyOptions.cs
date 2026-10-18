// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using Microsoft.AspNetCore.Authorization;

namespace DamianH.Http.ForwardProxy;

/// <summary>Configures the forward proxy. Defaults are secure for an internet egress proxy.</summary>
public sealed class ForwardProxyOptions
{
    /// <summary>
    /// The proxy's name, used as the received-by pseudonym in <c>Via</c> and as the proxy identifier in
    /// <c>Proxy-Status</c>. Must be an HTTP token (RFC 9110 <c>tchar</c> characters) that starts with an ASCII letter, so
    /// it is also a valid structured-field token; <c>:</c> and <c>/</c> are not allowed. Defaults to <c>forward-proxy</c>.
    /// </summary>
    public string ProxyName { get; set; } = "forward-proxy";

    /// <summary>
    /// Name of an ASP.NET Core authorization policy every proxy request must satisfy. The policy is evaluated with the
    /// <see cref="ForwardProxyDestination"/> as its resource. When neither this nor <see cref="AuthorizationPolicy"/>
    /// is set, the proxy accepts anonymous clients. Mutually exclusive with <see cref="AuthorizationPolicy"/>; setting both
    /// fails options validation.
    /// </summary>
    public string? AuthorizationPolicyName { get; set; }

    /// <summary>
    /// An authorization policy every proxy request must satisfy. Mutually exclusive with
    /// <see cref="AuthorizationPolicyName"/>; setting both fails options validation.
    /// </summary>
    public AuthorizationPolicy? AuthorizationPolicy { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default), authentication challenges that produce <c>401</c> and
    /// <c>WWW-Authenticate</c> are translated to <c>407</c> and <c>Proxy-Authenticate</c>, so origin-style
    /// authentication handlers can authenticate proxy clients.
    /// </summary>
    public bool TranslateChallenges { get; set; } = true;

    /// <summary>Destination ports allowed for <c>CONNECT</c> tunnels. Defaults to 443. <see langword="null"/> allows any port.</summary>
    public ISet<int>? AllowedConnectPorts { get; set; } = new HashSet<int> { 443 };

    /// <summary>Destination ports allowed for absolute-form <c>http://</c> requests. Defaults to 80. <see langword="null"/> allows any port.</summary>
    public ISet<int>? AllowedHttpPorts { get; set; } = new HashSet<int> { 80 };

    /// <summary>
    /// Host patterns the proxy may reach. Empty (default) allows any host not otherwise denied. Patterns are an exact
    /// host name or IP literal (<c>api.example.com</c>), a subdomain wildcard (<c>*.example.com</c>, which does not
    /// match <c>example.com</c>), or <c>*</c>.
    /// </summary>
    public IList<string> AllowedHosts { get; } = [];

    /// <summary>Host patterns the proxy must not reach. Evaluated before <see cref="AllowedHosts"/>.</summary>
    public IList<string> DeniedHosts { get; } = [];

    /// <summary>
    /// Networks the proxy may connect to even when <see cref="BlockNonPublicAddresses"/> would block them, for example
    /// an internal service range. Evaluated after <see cref="DeniedNetworks"/>.
    /// </summary>
    public IList<IPNetwork> AllowedNetworks { get; } = [];

    /// <summary>Networks the proxy must never connect to. Evaluated first, against every resolved address.</summary>
    public IList<IPNetwork> DeniedNetworks { get; } = [];

    /// <summary>
    /// When <see langword="true"/> (default), the proxy refuses to connect to loopback, private, link-local
    /// (including cloud metadata endpoints), shared, documentation, multicast and other non-public addresses.
    /// Addresses are checked after DNS resolution, immediately before connecting.
    /// </summary>
    public bool BlockNonPublicAddresses { get; set; } = true;

    /// <summary>
    /// Time allowed for an interception or upstream client-certificate selector to decide, and for destination DNS
    /// resolution and connection attempts.
    /// Each phase is bounded by this timeout. Defaults to 10 seconds. Must be positive and at most
    /// <see cref="int.MaxValue"/> milliseconds (about 24.8 days).
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A tunnel (including an upgraded WebSocket connection) with no traffic in either direction for this long is
    /// closed. Defaults to 5 minutes. Must be positive and at most <see cref="int.MaxValue"/> milliseconds (about 24.8 days).
    /// </summary>
    public TimeSpan TunnelIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum time without progress (sending the request, receiving response headers or body data) when forwarding
    /// <c>http://</c> requests. Defaults to 100 seconds. Must be positive and at most <see cref="int.MaxValue"/>
    /// milliseconds (about 24.8 days).
    /// </summary>
    public TimeSpan HttpActivityTimeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Time allowed for a client to send an HTTP/1.1 <c>CONNECT</c> request head. Defaults to 30 seconds. Must be
    /// positive and at most <see cref="int.MaxValue"/> milliseconds (about 24.8 days).
    /// </summary>
    public TimeSpan ConnectRequestHeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum size, in bytes, of an HTTP/1.1 <c>CONNECT</c> request head. Defaults to 16 KiB.</summary>
    public int MaxConnectRequestHeadersSize { get; set; } = 16 * 1024;

    /// <summary>Maximum concurrent tunnels across all protocols. Defaults to 10,000. <see langword="null"/> means unlimited.</summary>
    public int? MaxConcurrentTunnels { get; set; } = 10_000;

    /// <summary>When <see langword="true"/> (default), forwarded HTTP requests and responses get a <c>Via</c> entry.</summary>
    public bool AddViaHeader { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> (default), a forwarded HTTP request whose <c>Via</c> already has an entry received by
    /// <see cref="ProxyName"/> is a forwarding loop: it is rejected with <c>502</c> and a <c>Proxy-Status</c>
    /// <c>http_request_denied</c> error without contacting the destination. <c>CONNECT</c> requests are not checked.
    /// </summary>
    public bool RejectForwardingLoops { get; set; } = true;

    /// <summary>When <see langword="true"/> (default), proxy-generated errors include an RFC 9209 <c>Proxy-Status</c> header.</summary>
    public bool AddProxyStatusHeader { get; set; } = true;
}
