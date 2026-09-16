---
title: HTTP Signatures credentials and keys
sidebarLabel: Credentials and keys
description: Bind keys to trusted algorithms and resolve verification credentials at runtime.
order: 3
section: HTTP Signatures
---

# HTTP Signatures credentials and keys

## Credentials

`SigningCredentials` and `VerificationCredentials` bind key material to exactly
one trusted algorithm and validate compatibility when constructed. A signed
`alg` parameter is optional; when present, it must match the trusted algorithm.
A signed `keyid`, when present, must match the trusted credential identity.

ECDSA credentials validate the actual P-256 or P-384 curve, and ECDSA
verification enforces the RFC fixed signature size.

## Key Types

### Signing Keys

| Class | Constructor | Algorithm |
|-------|-------------|-----------|
| `HmacSharedKey` | `(string keyId, byte[] keyBytes)` | `hmac-sha256` |
| `EcdsaSigningKey` | `(string keyId, ECDsa ecdsa)` | `ecdsa-p256-sha256`, `ecdsa-p384-sha384` |
| `RsaSigningKey` | `(string keyId, RSA rsa)` | `rsa-pss-sha512`, `rsa-v1_5-sha256` |
| `Ed25519SigningKey` | `(string keyId, byte[] privateKeyBytes)` | `ed25519` ⚠️ stub |

### Verification Keys

| Class | Constructor | Notes |
|-------|-------------|-------|
| `HmacSharedVerificationKey` | `(string keyId, byte[] keyBytes)` | Obtain via `HmacSharedKey.AsVerificationKey()` |
| `EcdsaVerificationKey` | `(string keyId, ECDsa ecdsa)` | Public key sufficient |
| `RsaVerificationKey` | `(string keyId, RSA rsa)` | Public key sufficient |
| `Ed25519VerificationKey` | `(string keyId, byte[] publicKeyBytes)` | `ed25519` ⚠️ stub |

All key types carry a `KeyId`. Cryptographic objects remain caller-owned and
are never disposed by this library.

See [supported algorithms](/docs/signatures/overview/#supported-algorithms)
for implementation status, including the unsupported Ed25519 stub.

## Runtime Credential Resolution

For server-side verification, resolve a trusted key and its one allowed
algorithm together:

```csharp
public sealed class MyCredentialsResolver : IVerificationCredentialsResolver
{
    public async ValueTask<VerificationCredentials?> ResolveAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        VerificationKey? key = await _store.FindAsync(keyId, cancellationToken);
        return key is null
            ? null
            : new VerificationCredentials(key, new HmacSha256SignatureAlgorithm());
    }
}

var verifier = new HttpMessageVerifier();
var result = await verifier.VerifyAsync(
    "sig1", context, new MyCredentialsResolver());
```

`keyid` is required for runtime resolution. The incoming `alg` value does not
select an algorithm; it is checked for agreement with the trusted credential
when present. The message and signature base are snapshotted before the
resolver is awaited.
