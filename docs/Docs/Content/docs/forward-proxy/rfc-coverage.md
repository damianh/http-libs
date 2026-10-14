---
title: Forward proxy RFC coverage
sidebarLabel: RFC coverage
description: Which HTTP RFC requirements the forward proxy tests cover.
order: 7
section: Forward Proxy
---

# RFC coverage

This page maps proxy-relevant requirements in the HTTP RFCs to the tests in
`forward-proxy/test/Http.ForwardProxy.Tests` that check them. Tests are named
`Class.Method`. It covers the proxy's own behavior; framing and parsing that
Kestrel or `SocketsHttpHandler` enforce for every request are not listed unless
the proxy relies on them for a proxy-specific rule. Update this page when adding
or renaming a protocol test.

## RFC 9110: HTTP semantics

| Section | Requirement | Tests |
|---|---|---|
| §7.6.1 | Remove headers named in `Connection` (requests and responses) | `HttpForwardingTests.StripsHeadersNamedInConnection`, `StripsConnectionOptionsWhenCloseIsAlsoPresent`, `StripsMixedConnectionOptionsAcrossKeepAliveRequests`, `StripsMixedConnectionOptionsAfterARejectedUpgrade`; `Http1RequestHeadObserverTests.*` |
| §7.6.1 | Do not forward fixed hop-by-hop fields (`Proxy-Connection`, `Keep-Alive`, `TE`, `HTTP2-Settings`) | `HttpForwardingTests.DoesNotForwardHopByHopRequestHeaders`, `HttpForwardingTests.ForwardsAbsoluteFormRequests` |
| §7.6.2 | `Max-Forwards` on `TRACE`/`OPTIONS`: decrement a positive value; at `0` answer without forwarding (`OPTIONS` `200`, `TRACE` `405`, never echoed); forward invalid values unchanged; other methods untouched | `HttpForwardingTests.DecrementsMaxForwardsOnForwardedTraceAndOptions`, `AnswersOptionsWithZeroMaxForwardsWithoutContactingTheOrigin`, `RefusesTraceWithZeroMaxForwardsWithoutEchoingOrContactingTheOrigin`, `ForwardsInvalidMaxForwardsUnchanged`, `DoesNotApplyMaxForwardsToOtherMethods` |
| §7.6.3 | Add `Via` with the received protocol version, append to existing values, on requests and responses | `HttpForwardingTests.ForwardsAbsoluteFormRequests`, `AppendsToReceivedViaHeaders`, `ForwardsWebSocketUpgrades`, `UsesTheConfiguredProxyNameInVia` |
| §7.6.3 | Reject a request whose `Via` shows it already passed through this proxy (loop); forward other proxies' entries | `HttpForwardingTests.RejectsForwardingLoopsWithoutContactingTheOrigin`, `ForwardsWhenViaNamesOtherProxies`, `ForwardsLoopsWhenDetectionIsDisabled`; `ViaHeaderTests.*` |
| §7.8 | Forward `Upgrade` (WebSocket only) and relay after `101` | `HttpForwardingTests.ForwardsWebSocketUpgrades`, `StripsMixedConnectionOptionsAfterARejectedUpgrade`; `TlsInterceptionTests.RelaysWebSocketsInsideTheTunnel` |
| §8.6 | No `Content-Length` on `1xx` or `204` responses: the `Content-Length: 0` that `HttpClient` adds to `1xx`, `204` and `205` responses is not relayed (Kestrel frames `205` itself, RFC 9110 §15.3.6); other statuses keep it | `ForwardingHeadersTests.RemovesZeroContentLengthFromBodylessResponses`, `KeepsZeroContentLengthOnResponsesThatCanHaveABody`; end to end: `HttpForwardingTests.DoesNotAddContentLengthToNoContentResponses` |
| §10.1.1 | Forward `Expect: 100-continue`; send the request body after the interim `100` | `HttpForwardingTests.ForwardsExpectContinue` |
| §6.6.2 | Do not advertise trailer fields that are not forwarded | `HttpForwardingTests.DoesNotAdvertiseRequestTrailersItDrops`, `DoesNotAdvertiseResponseTrailersItDrops` |
| §9.3.6 | `CONNECT` uses authority-form with a port; reject content; `2xx` switches to a tunnel; restrict ports | `ConnectTunnelTests.TunnelsBytesIncludingEarlyDataAndPropagatesHalfClose`, `RejectsMalformedRequests`, `BlocksDisallowedPorts`; `ConnectRequestParserTests.RejectsMalformedRequests`, `AllowsZeroContentLength`; `AuthorityParserTests.*`; `DestinationPolicyTests.DefaultPortsAllow443ForConnectAnd80ForHttp` |
| §9.3.6 | Tunnel closes when either side closes | `ConnectTunnelTests.TunnelsBytesIncludingEarlyDataAndPropagatesHalfClose`, `ClosesIdleTunnels` |
| §11.7.1, §15.5.8 | Challenge with `407` and `Proxy-Authenticate` | `AuthenticationTests.ChallengesConnectWithoutCredentials`, `UnauthenticatedHttpRequestsAreChallenged`, `TranslatesOriginStyleChallenges` |
| §11.7.2 | Consume `Proxy-Authorization`; do not forward it | `HttpForwardingTests.ForwardsAbsoluteFormRequests`, `AuthenticationTests.HttpClientAuthenticatesForConnectAndHttp` |
| §15.5.20 | `421` when a request in an intercepted tunnel targets another host | `TlsInterceptionTests.RejectsRequestsForAnotherHost` |
| §15.6.2 | `501` for a `CONNECT` the proxy cannot tunnel: HTTP/3, or HTTP/1.1 after another request on the connection | `ForwardProxyMiddlewareTests.RejectsHttp3ConnectAsNotImplemented`, `ConnectTunnelTests.ConnectAfterAnotherRequestIsNotImplemented` |
| §15.6.3 | `502` when the destination fails | `HttpForwardingTests.ReportsUnreachableOrigins`, `ReportsInvalidOriginResponsesAsIncomplete`, `ReportsResetOriginConnectionsAsUnavailable`; `ConnectTunnelTests.ReportsRefusedConnections`, `ReportsUnresolvableHosts`, `ReportsUnroutableDestinations` |
| §15.6.4 | `503` when the proxy is at capacity | `ConnectTunnelTests.LimitsConcurrentTunnels`, `HttpForwardingTests.WebSocketUpgradesRespectTheTunnelLimit` |
| §15.6.5 | `504` when the destination does not respond in time | `HttpForwardingTests.ReportsOriginsThatDoNotRespondAsTimedOut`, `TlsInterceptionTests.CancelsAnInterceptionSelectorAfterConnectTimeout` |

[RFC 7617](https://www.rfc-editor.org/rfc/rfc7617.html) Basic credentials with
`charset="UTF-8"` are covered by `AuthenticationTests.ChallengesConnectWithoutCredentials`,
`RejectsWrongCredentials` and `HttpClientAuthenticatesForConnectAndHttp`.

## RFC 9112: HTTP/1.1

| Section | Requirement | Tests |
|---|---|---|
| §3.2.2 | Accept absolute-form; send origin-form with the path and query unchanged | `HttpForwardingTests.ForwardsAbsoluteFormRequests`, `PreservesTheOriginalPathAndQueryOnTheWire` |
| §3.2.2 (with RFC 9110 §7.2) | Use the absolute-form authority, not the received `Host`, as the destination | `HttpForwardingTests.AbsoluteTargetsOverrideTheReceivedHost`, `HttpForwardingTests.ForwardsAbsoluteFormRequests` |
| §3.2.2 | Only `http://` is forwarded; absolute-form `https://` is rejected | `HttpForwardingTests.RejectsAbsoluteFormHttpsTargets` |
| §3.2.3 | `CONNECT` target is authority-form | `ConnectTunnelTests.RejectsMalformedRequests`, `AuthorityParserTests.RejectsInvalidAuthorities` |
| §2.2, §5.1, §5.2 | Reject bare CR/LF, whitespace before `:`, and `obs-fold` in a `CONNECT` head | `ConnectRequestParserTests.RejectsMalformedRequests`, `ConnectTunnelTests.RejectsMalformedRequests` |
| §2.3 | Accept `HTTP/1.0` and `HTTP/1.1`; reject other versions on `CONNECT` | `ConnectRequestParserTests.ParsesRequestWithoutHeadersAndHttp10`, `RejectsMalformedRequests`; `HttpForwardingTests.AppendsToReceivedViaHeaders` |
| §6.1, §7.1 | Forward chunked request bodies | `HttpForwardingTests.ForwardsChunkedRequestBodies` |
| §6.3 | Do not forward `Content-Length` together with `Transfer-Encoding` on requests | `HttpForwardingTests.DoesNotForwardContentLengthWithTransferEncoding` (Kestrel already drops the request field, so the test restores it in middleware to exercise the proxy's own check) |
| §6.3 | Do not forward `Content-Length` together with `Transfer-Encoding` on responses | `HttpForwardingTests.DoesNotForwardResponseContentLengthWithTransferEncoding` |
| §9.3 | Pipelined requests on a keep-alive connection | `HttpForwardingTests.StripsMixedConnectionOptionsAcrossKeepAliveRequests`, `Http1RequestHeadObserverTests.FollowsPipelinedRequestsAcrossEverySplit` |
| §9.6 | Close the connection after an error response to `CONNECT` | `ConnectTunnelTests.BlocksLoopbackByDefault`, `RejectsMalformedRequests`; `AuthenticationTests.ChallengesConnectWithoutCredentials` |

## RFC 9113 §8.5: HTTP/2 `CONNECT`

| Requirement | Tests |
|---|---|
| `CONNECT` with `:authority` opens a tunnel; `200` then `DATA` frames carry the bytes | `Http2ConnectTests.TunnelsOverHttp2`, `TlsInterceptionTests.InterceptsHttp2Connect` |
| A denied tunnel gets a final non-`2xx` response | `Http2ConnectTests.ReportsDeniedTunnelsOverHttp2` |
| A TCP error on the destination connection (for example, a reset) resets the stream with `CONNECT_ERROR` | `Http2ConnectTests.ResetsTheStreamWithConnectErrorWhenTheDestinationResets` |
| An aborted stream closes the destination connection with a TCP `RST` | `Http2ConnectTests.ResetsTheDestinationConnectionWhenTheStreamIsAborted` |
| HTTP/1.1 `CONNECT` still works on the same TLS listener | `Http2ConnectTests.Http1ConnectStillWorksOnTlsListener` |
| [RFC 8441](https://www.rfc-editor.org/rfc/rfc8441.html) extended `CONNECT` is passed to the application, not tunneled | `Http2ConnectTests.PassesExtendedConnectToTheApplication` |
| Extended `CONNECT` inside an intercepted tunnel is rejected with `400` | `TlsInterceptionTests.RejectsExtendedConnectInsideTheTunnel` |

The classic `CONNECT` tests use `curl --proxy-http2` and are skipped when that
curl is not available.

## RFC 9209: `Proxy-Status`

| Error type | Tests |
|---|---|
| `destination_ip_prohibited` | `ConnectTunnelTests.BlocksLoopbackByDefault`, `HttpForwardingTests.DoesNotRouteAbsoluteFormRequestsToTheProxyApplication`, `Http2ConnectTests.ReportsDeniedTunnelsOverHttp2`, `DestinationPolicyTests.NonPublicIpLiteralsFailFast` |
| `http_request_denied` | `ConnectTunnelTests.BlocksDisallowedPorts`, `CustomFiltersCanDenyDestinations`; `HttpForwardingTests.RejectsDisallowedHttpPorts`, `RejectsForwardingLoopsWithoutContactingTheOrigin`; `TlsInterceptionTests.RejectsRequestsForAnotherHost` |
| `http_request_error` | `HttpForwardingTests.RejectsAbsoluteFormHttpsTargets`, `TlsInterceptionTests.RejectsExtendedConnectInsideTheTunnel` |
| `dns_error` | `ConnectTunnelTests.ReportsUnresolvableHosts`, `ProxyDialerTests.ReportsNamesWithoutAddressesAsDnsError` |
| `dns_timeout` | `ProxyDialerTests.ReportsSlowNameResolutionAsDnsTimeout` |
| `destination_ip_unroutable` | `ConnectTunnelTests.ReportsUnroutableDestinations` (Linux only) |
| `destination_unavailable` | `HttpForwardingTests.ReportsResetOriginConnectionsAsUnavailable`; `CONNECT` dialer mapping: `ProxyDialerTests.MapsConnectFailuresToProxyStatusErrors` |
| `connection_refused` | `ConnectTunnelTests.ReportsRefusedConnections`, `HttpForwardingTests.ReportsUnreachableOrigins` |
| `connection_limit_reached` | `ConnectTunnelTests.LimitsConcurrentTunnels` |
| `connection_timeout` | `HttpForwardingTests.ReportsOriginsThatDoNotRespondAsTimedOut` |
| `http_response_incomplete` | `HttpForwardingTests.ReportsInvalidOriginResponsesAsIncomplete` |
| `tls_certificate_error` | `TlsInterceptionTests.RejectsUntrustedDestinationCertificates` |
| `tls_protocol_error` | `TlsInterceptionTests.ReportsDestinationTlsFailures` |
| `proxy_internal_error` | `AuthenticationTests.MissingNamedPolicyIsAProxyInternalError`, `TlsInterceptionTests.FailingTransformIsAProxyError` |

The proxy name in each value is checked by `ConnectTunnelTests.BlocksLoopbackByDefault`.

## Gaps

None known. A few rules are checked below the HTTP level because they cannot be
produced or observed end to end:

- `dns_timeout` and the `CONNECT` dialer's socket-error mapping use
  `ProxyDialerTests`, which replaces the internal resolver or calls the mapping
  directly; a slow DNS server or arbitrary socket errors cannot be produced
  reliably.
- `Content-Length` removal on bodyless responses uses `ForwardingHeadersTests`,
  because Kestrel also normalizes `Content-Length` on `204` and `205` responses
  and hides the proxy's own check.
- HTTP/3 `CONNECT` uses `ForwardProxyMiddlewareTests`, because Kestrel needs
  `libmsquic` to listen for HTTP/3 and the test environments do not have it.
