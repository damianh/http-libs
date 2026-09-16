---
title: HTTP Signatures overview
sidebarLabel: Overview
description: Install HTTP Message Signatures, sign and verify a message, and check algorithm support.
order: 1
section: HTTP Signatures
---

# HTTP Signatures overview

`DamianH.Http.HttpSignatures` provides RFC 9421 HTTP Message Signatures for
signing and verifying HTTP messages.

> **Depends on** [`DamianH.Http.StructuredFieldValues`](/docs/structured-field-values/overview/) (pulled in automatically as a transitive dependency).

## Installation

Run in the consuming project directory:

```bash
dotnet add package DamianH.Http.HttpSignatures
```

Repository builds reference the local Structured Field Values project, so both
libraries use the same object model. NuGet packaging records that project's
computed version as a dependency. For coordinated releases, publish the matching
Structured Field Values package before HttpSignatures, from the same source
commit.

## Quick Start

The following example shows a complete sign-then-verify round-trip using HMAC-SHA256 (symmetric — the same key is used for both operations):

> The hard-coded secret is illustrative, not a production key. Supply securely
> provisioned key material in an application. `context` adapts your message through
> [`IHttpMessageContext`](/docs/signatures/api-reference/#ihttpmessagecontext);
> verification must see the message with its signature headers attached.
> `IsValid` is protocol and cryptographic verification, **not** application
> acceptance or authorization. Use [verification policy](/docs/signatures/verification-policy/)
> to enforce application requirements and read the [security boundaries](/docs/signatures/security/).

```csharp
using DamianH.Http.HttpSignatures;
using DamianH.Http.HttpSignatures.Algorithms;
using DamianH.Http.HttpSignatures.Keys;

// --- Signing ---

var signingKey = new HmacSharedKey("my-key-id", Encoding.UTF8.GetBytes("super-secret"));
var algorithm = new HmacSha256SignatureAlgorithm();
var signingCredentials = new SigningCredentials(signingKey, algorithm);
var signer = new HttpMessageSigner();

var parameters = new SignatureParameters([
    ComponentIdentifier.Method,
    ComponentIdentifier.Authority,
    ComponentIdentifier.Path,
    ComponentIdentifier.Field("content-type"),
])
{
    Created = DateTimeOffset.UtcNow,
    KeyId = signingKey.KeyId,
    Algorithm = algorithm.AlgorithmName,
};

// context adapts your HTTP message (see IHttpMessageContext)
SignatureResult result = signer.Sign("sig1", context, parameters, signingCredentials);

// Add the headers to the outgoing request
request.Headers.Add("Signature-Input", result.SignatureInputHeaderValue);
request.Headers.Add("Signature", result.SignatureHeaderValue);

// --- Verification ---

var verificationKey = signingKey.AsVerificationKey();
var verificationCredentials = new VerificationCredentials(verificationKey, algorithm);
var verifier = new HttpMessageVerifier();

VerificationResult verification = verifier.Verify("sig1", context, verificationCredentials);

if (!verification.IsValid)
{
    Console.WriteLine($"Signature invalid: {verification.ErrorMessage}");
}
```

The example uses `Encoding` from `System.Text`.

## Supported Algorithms

| Class | Algorithm Name | RFC Section | Key Types | Status |
|-------|---------------|-------------|-----------|--------|
| `HmacSha256SignatureAlgorithm` | `hmac-sha256` | §3.3.3 | `HmacSharedKey` / `HmacSharedVerificationKey` | ✅ Supported |
| `EcdsaP256Sha256SignatureAlgorithm` | `ecdsa-p256-sha256` | §3.3.4 | `EcdsaSigningKey` / `EcdsaVerificationKey` | ✅ Supported |
| `EcdsaP384Sha384SignatureAlgorithm` | `ecdsa-p384-sha384` | §3.3.5 | `EcdsaSigningKey` / `EcdsaVerificationKey` | ✅ Supported |
| `RsaPssSha512SignatureAlgorithm` | `rsa-pss-sha512` | §3.3.1 | `RsaSigningKey` / `RsaVerificationKey` | ✅ Supported |
| `RsaPkcs1Sha256SignatureAlgorithm` | `rsa-v1_5-sha256` | §3.3.2 | `RsaSigningKey` / `RsaVerificationKey` | ✅ Supported |
| `Ed25519SignatureAlgorithm` | `ed25519` | §3.3.6 | `Ed25519SigningKey` / `Ed25519VerificationKey` | ⚠️ Stub — throws `PlatformNotSupportedException` (awaiting .NET runtime support) |

See [credentials and key types](/docs/signatures/credentials/) for trusted
algorithm binding, ownership, and runtime resolution, and the
[API reference](/docs/signatures/api-reference/) for message and field adapters.
