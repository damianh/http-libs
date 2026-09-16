---
title: HTTP Signatures verification policy
sidebarLabel: Verification policy
description: Separate cryptographic validity from application acceptance and opt-in replay protection.
order: 4
section: HTTP Signatures
---

# HTTP Signatures verification policy

`HttpMessageVerifier.Verify` and `VerifyAsync` perform protocol and cryptographic
verification. `IsValid` does not mean application age, replay, tag, or
required-component policy has accepted the message.

## Verification Policy

Use `VerifyAndValidateAsync` when successful cryptographic verification must
also satisfy explicit application requirements:

```csharp
var policy = new VerificationPolicy
{
    RequiredComponents =
    [
        ComponentIdentifier.Method,
        ComponentIdentifier.Authority,
        ComponentIdentifier.Field("content-digest"),
    ],
    RequireCreated = true,
    MaximumAge = TimeSpan.FromMinutes(5),
    ValidateExpiration = true,
    RequiredTag = "my-app",
    TimeProvider = TimeProvider.System,
};

VerificationAcceptanceResult acceptance =
    await verifier.VerifyAndValidateAsync(
        "sig1", context, verificationCredentials, policy);

if (!acceptance.IsAccepted)
{
    Console.WriteLine(acceptance.ErrorMessage);
}
```

## Replay protection

Replay protection is opt-in through `INonceStore`. Its `TryUseAsync` operation
must atomically claim a nonce across every server sharing a replay scope. A
separate cache read followed by a write is not sufficient. When a nonce store
is configured, policy must enforce a finite acceptance window using
`MaximumAge` or `RequireExpires`; `ValidateExpiration` alone is not
sufficient, because it only validates an expires value when one is present
and does not guarantee one was signed. The claim is retained through the
resulting deadline, including clock skew. Storage adapters, including any
HybridCache adapter, are application concerns and are not included in this
package.

Policy acceptance does not replace authorization or body-digest verification;
see [security boundaries](/docs/signatures/security/).
