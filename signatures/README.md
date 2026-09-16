# DamianH.Http.HttpSignatures

RFC 9421 HTTP Message Signatures for signing and verifying HTTP messages.
Depends on `DamianH.Http.StructuredFieldValues` (included transitively).

## Installation

Run in the consuming project directory:

```bash
dotnet add package DamianH.Http.HttpSignatures
```

## Quick start

Given securely provisioned `keyBytes` and a `context` implementing
[`IHttpMessageContext`](https://damianh.github.io/http-libs/docs/signatures/api-reference/#ihttpmessagecontext)
for the received message (including its signature headers):

```csharp
using DamianH.Http.HttpSignatures;
using DamianH.Http.HttpSignatures.Algorithms;
using DamianH.Http.HttpSignatures.Keys;

var key = new HmacSharedVerificationKey("my-key-id", keyBytes);
var credentials = new VerificationCredentials(key, new HmacSha256SignatureAlgorithm());
var verifier = new HttpMessageVerifier();
var result = verifier.Verify("sig1", context, credentials);
if (!result.IsValid)
{
    Console.WriteLine(result.ErrorMessage);
}
```

`IsValid` means protocol and cryptographic validity, **not** application
acceptance or authorization. Use `VerifyAndValidateAsync` with explicit
application policy. Replay protection is opt-in: `INonceStore` must claim nonces
atomically across the replay scope, with a finite acceptance window. Signing
`Content-Digest` does not verify the body digest. Cryptographic objects remain
caller-owned. Ed25519 is a stub that throws `PlatformNotSupportedException`.

## Documentation

- [Signing quick start and supported algorithms](https://damianh.github.io/http-libs/docs/signatures/overview/)
- [API reference and message adapters](https://damianh.github.io/http-libs/docs/signatures/api-reference/)
- [Credentials, keys, and runtime resolution](https://damianh.github.io/http-libs/docs/signatures/credentials/)
- [Verification policy and replay protection](https://damianh.github.io/http-libs/docs/signatures/verification-policy/)
- [Security boundaries](https://damianh.github.io/http-libs/docs/signatures/security/)
