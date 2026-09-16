---
title: HTTP Signatures security boundaries
sidebarLabel: Security boundaries
description: Body-digest verification, authorization, complete message context, and nonce storage responsibilities.
order: 5
section: HTTP Signatures
---

# HTTP Signatures security boundaries

- Signing a `Content-Digest` field protects that field's value; it does not
  compare the digest to the message body. Applications must perform body-digest
  verification separately.
- Signature verification and policy acceptance do not replace authorization.
- HTTP framework adapters must provide complete request/response context,
  including trailers, before signing or verification.
- This package defines the atomic nonce-store contract but does not provide a
  production distributed implementation.

Use [verification policy](/docs/signatures/verification-policy/) to enforce
application acceptance requirements, including an explicit finite window for
opt-in replay protection. Bind keys to [trusted credentials](/docs/signatures/credentials/)
rather than letting an incoming `alg` select the algorithm. Follow the
[message-context contract](/docs/signatures/api-reference/#ihttpmessagecontext)
to keep headers and trailers separate.
