---
title: Forwarded Headers errors and diagnostics
sidebarLabel: Errors and diagnostics
description: Malformed input policies, header consumption, original values, and immutable forwarding diagnostics.
order: 4
section: Forwarded Headers
---

# Forwarded Headers errors and diagnostics

## Malformed input and trust boundaries

| Policy | Invalid whole-field syntax | Invalid/disallowed value in a considered hop |
|---|---|---|
| `Ignore` (default) | Continue with request properties and forwarding headers unchanged | Stop before that hop; apply any fully validated nearer hops |
| `Reject` | HTTP 400, no next middleware, no forwarding mutations | HTTP 400, no next middleware, no forwarding mutations, including staged nearer hops |

Enable strict handling with
`options.MalformedHeaderBehavior = MalformedHeaderBehavior.Reject;`.
Rejection does not echo the raw header in a diagnostic response body.
Malformed hops are never skipped in favor of more distant claims. A disallowed
host stops its whole hop, rather than falling back to a farther host.

Unknown extensions, missing optional values, unknown/obfuscated identities,
untrusted peers, and hop limits are not malformed input. Semantic values beyond
a trust or hop boundary are not validated. Whole-field syntax must still parse
before individual element boundaries can be consumed safely, so a syntax error
in a farther element can invalidate the field. Disabled parameter values are not
semantically validated, except `for`, which is needed for trust traversal.

## Consumption, originals, and diagnostics

Accepted elements are removed from the right of `Forwarded`; an unconsumed prefix
is retained without reordering parameters or discarding unknown extensions. The
header is removed when completely consumed. Enabled applied values preserve
their pre-forwarding originals in the configurable `X-Original-*` headers using
ASP.NET Core conventions. Inbound original-value headers are **not** trusted
provenance.

```csharp
var feature = context.Features.Get<IForwardedFeature>();
if (feature is not null)
{
    var physicalPeer = feature.OriginalRemoteIpAddress;
    var acceptedCount = feature.AcceptedHops.Count;
    var outcome = feature.StopReason;
    var rejected = feature.Rejected;
}
```

`IForwardedFeature` captures immutable original remote IP/port, scheme, host, and
PathBase independently of inbound headers. `AcceptedHops` is nearest-first and
contains only accepted elements (empty on rejection), **not** the entire raw
chain. Its unselected parameters remain metadata, not semantically validated
claims. The feature also marks processing so re-execution or repeated middleware
registration cannot consume another batch of hops. It is absent when processing
is disabled or the header is absent. Structured log events describe failures and
boundaries without logging full headers or identifying values by default.

See [deployment safety](/docs/forwarded-headers/deployment-safety/) before
exposing diagnostics or interpreting proxy claims as identity.
