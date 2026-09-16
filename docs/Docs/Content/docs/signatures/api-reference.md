---
title: HTTP Signatures API reference
sidebarLabel: API reference
description: Signing, verification, covered components, message context, field type resolution, and results.
order: 2
section: HTTP Signatures
---

# HTTP Signatures API reference

## HttpMessageSigner

Signs an HTTP message, producing `Signature-Input` and `Signature` header values.

```csharp
public sealed class HttpMessageSigner
{
    public SignatureResult Sign(
        string label,
        IHttpMessageContext context,
        SignatureParameters parameters,
        SigningCredentials credentials,
        IStructuredFieldTypeResolver? fieldTypeResolver = null);
}
```

`fieldTypeResolver` declares the Structured Field type of HTTP fields, and is required to resolve `sf` and `key` components (see [IStructuredFieldTypeResolver](#istructuredfieldtyperesolver)). When omitted, every field's type is treated as unknown, so `sf`/`key` components fail explicitly instead of guessing the type from the field's value.

## HttpMessageVerifier

Performs protocol and cryptographic verification. `IsValid` does not mean that
application age, replay, tag, or required-component policy has accepted the message.

```csharp
public sealed class HttpMessageVerifier
{
    public VerificationResult Verify(
        string label,
        IHttpMessageContext context,
        VerificationCredentials credentials,
        IStructuredFieldTypeResolver? fieldTypeResolver = null);

    public ValueTask<VerificationResult> VerifyAsync(
        string label,
        IHttpMessageContext context,
        IVerificationCredentialsResolver credentialsResolver,
        IStructuredFieldTypeResolver? fieldTypeResolver = null,
        CancellationToken cancellationToken = default);
}
```

Expected input failures return a `VerificationResult` with a machine-readable
`FailureCode`. Cancellation and resolver, cryptographic-provider, or storage
infrastructure failures propagate to the caller.

See [credentials](/docs/signatures/credentials/) for trusted keys and runtime
resolution, and [verification policy](/docs/signatures/verification-policy/)
for `VerifyAndValidateAsync` and replay protection.

## SignatureParameters

Defines the covered components and metadata for a signature. Covered components determine which parts of the HTTP message are included in the signature base.

```csharp
var parameters = new SignatureParameters([
    ComponentIdentifier.Method,
    ComponentIdentifier.Authority,
    ComponentIdentifier.Path,
])
{
    Created  = DateTimeOffset.UtcNow,       // ;created=<unix timestamp>
    Expires  = DateTimeOffset.UtcNow.AddMinutes(5), // ;expires=<unix timestamp>
    KeyId    = "my-key-id",                 // ;keyid="..."
    Nonce    = Guid.NewGuid().ToString(),   // ;nonce="..."
    Algorithm = "hmac-sha256",              // ;alg="..."
    Tag      = "my-app",                    // ;tag="..."
};
```

All properties except `CoveredComponents` are optional. Metadata is signed, but
`Verify` does not impose application policy merely because metadata is present.
Configure a `VerificationPolicy` to enforce creation age, expiration, nonce,
tag, or required-component rules.

## ComponentIdentifier

Identifies a component of the HTTP message to include in the signature base.

**Derived components** (start with `@`):

| Static property/method | Component name | Applies to |
|------------------------|---------------|------------|
| `ComponentIdentifier.Method` | `@method` | Request |
| `ComponentIdentifier.Authority` | `@authority` | Request |
| `ComponentIdentifier.Scheme` | `@scheme` | Request |
| `ComponentIdentifier.Path` | `@path` | Request |
| `ComponentIdentifier.Query` | `@query` | Request |
| `ComponentIdentifier.TargetUri` | `@target-uri` | Request |
| `ComponentIdentifier.RequestTarget` | `@request-target` | Request |
| `ComponentIdentifier.Status` | `@status` | Response |
| `ComponentIdentifier.QueryParam("name")` | `@query-param;name="..."` | Request |

**HTTP field components:**

| Factory method | Description |
|----------------|-------------|
| `ComponentIdentifier.Field("content-type")` | Raw header field value |
| `ComponentIdentifier.FieldSf("content-digest")` | Strict SF-serialized header value |
| `ComponentIdentifier.FieldKey("priority", "u")` | Specific key from an SF Dictionary header |
| `ComponentIdentifier.FieldBs("signature")` | Binary-wrapped header field |

## IHttpMessageContext

Adapts a concrete HTTP message to the interface required by `HttpMessageSigner` and `HttpMessageVerifier`. You implement this for your specific HTTP framework.

```csharp
public interface IHttpMessageContext
{
    bool IsRequest { get; }
    string? Method { get; }
    string? Scheme { get; }
    string? Authority { get; }
    string? Path { get; }
    string? Query { get; }
    string? TargetUri { get; }
    string? RequestTarget { get; }
    int? StatusCode { get; }
    IReadOnlyList<string> GetHeaderValues(string fieldName);
    IReadOnlyList<string> GetTrailerValues(string fieldName);
    IHttpMessageContext? AssociatedRequest { get; }
}
```

Implementations provide only the raw, uncombined field values, in field-line order, for headers (`GetHeaderValues`) and trailers (`GetTrailerValues`); a missing trailer must never fall back to a header of the same name, and the two sections are never combined together (RFC 9421 §2.1.4). The `HttpMessageContextExtensions.GetHeaderValue`/`GetTrailerValue` extension methods build the ordinary combined, canonicalized value (trimming, obsolete line-fold unwrapping, and comma-space combination per RFC 9110 §5.2) on top of these raw values, so most callers never need to implement combination themselves.

## IStructuredFieldTypeResolver

Declares the Structured Field Values (RFC 8941/9651) type of an HTTP field, required to resolve `sf` and `key` components deterministically instead of guessing the type by trying each parser in turn.

```csharp
public enum StructuredFieldValueKind { Unknown, Item, List, Dictionary }

public interface IStructuredFieldTypeResolver
{
    StructuredFieldValueKind ResolveType(bool isRequest, string fieldName);
}
```

A ready-to-use `DictionaryStructuredFieldTypeResolver` is provided, backed by a case-insensitive `IReadOnlyDictionary<string, StructuredFieldValueKind>` map of field name to declared type. Pass an instance to `HttpMessageSigner.Sign` / `SignatureBaseBuilder.Build` / `SignatureBaseBuilder.BuildString` via the optional `fieldTypeResolver` parameter.

## SignatureResult

Returned by `HttpMessageSigner.Sign`. Contains the values to set on the outgoing HTTP message headers.

| Property | Type | Description |
|----------|------|-------------|
| `Label` | `string` | The signature label (e.g., `"sig1"`) |
| `SignatureInputHeaderValue` | `string` | The value to add to the `Signature-Input` header |
| `SignatureHeaderValue` | `string` | The value to add to the `Signature` header |
| `SignatureBytes` | `ReadOnlySpan<byte>` | The raw signature bytes (defensively copied on construction; call `.ToArray()` for an owned copy) |

## VerificationResult

Returned by `HttpMessageVerifier.Verify` / `VerifyAsync`.

| Property | Type | Description |
|----------|------|-------------|
| `IsValid` | `bool` | `true` if the signature was successfully verified |
| `Parameters` | `SignatureParameters?` | The parsed signature parameters if available |
| `ErrorMessage` | `string?` | Description of failure when `IsValid` is `false` |
