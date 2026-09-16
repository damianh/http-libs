---
title: Azure Blob HTTP cache content store
sidebarLabel: Azure Blob
description: Azure Blob registration, block upload limits, stream ownership, lifecycle retention, and opt-in tests.
order: 2
section: HTTP cache content stores
---

# Azure Blob HTTP cache content store

`DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob` targets `net10.0`,
`netstandard2.0`, and `net472` (.NET Framework 4.7.2+) and uses the
official `Azure.Storage.Blobs` SDK (12.29.2). It depends on the independent
ContentStore package, not the HTTP handler or HybridCache. Release tags use
`cache-azureblob-v`. NativeAOT/trimming compatibility has **not** been validated.

[NuGet](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob/) |
[Source](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler/src/HttpHybridCacheHandler.ContentStore.AzureBlob)

## Registration

Install this package alongside `DamianH.HttpHybridCacheHandler`. Run in the
application project directory:

```powershell
dotnet add package DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob
```

The application supplies an existing container client, including credentials,
retry policy and endpoint configuration. This sample also requires `Azure.Identity`:

```csharp
using Azure.Identity;
using Azure.Storage.Blobs;
using DamianH.HttpHybridCacheHandler;
using DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
var container = new BlobContainerClient(
    new Uri("https://myaccount.blob.core.windows.net/http-cache"),
    new DefaultAzureCredential());
services.AddSingleton(container);
services.AddHttpHybridCacheAzureBlobContentStore(
    options => options.Namespace = "production/my-service");

// Configure the HTTP handler and its large-body offload settings separately.
// Or use the independent content-store contract directly:
using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<ILargeHttpCacheContentStore>();
using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("example body"));
await store.WriteAsync("opaque-logical-key", input, input.Length, null, CancellationToken.None);
using var body = await store.OpenReadAsync("opaque-logical-key", CancellationToken.None);
if (body is not null)
    await body.CopyToAsync(Stream.Null);
```

Register only one large-content backend per handler setup. This adapter neither
creates containers nor changes lifecycle rules, and does not dispose the supplied
client or input streams. Each successful read returns a separately owned response
stream; dispose it, including after failed or abandoned consumption. See
[handler offload settings](/docs/hybrid-cache-handler/content-stores/overview/).

## Storage semantics and limits

Names are `<Namespace>/<SHA256 UTF-8 logical key>`; raw keys/URLs never become paths.
Keep the namespace stable, isolated from other applications, and compatible with
Azure blob naming rules. The default is `http-cache`. Changing it causes cold misses.
Tags are advisory and ignored, not Azure index tags or provider-side invalidation.
Bodies, including internally gzipped representations, use `application/octet-stream`
without an HTTP Content-Encoding. Freshness remains an HTTP handler concern.

Writes require a completed, readable, seekable stream whose remaining bytes exactly
match the supplied stored-byte length. They use sequential blocks of at most 4 MiB
and at most one rented block buffer per active upload (plus SDK transport overhead).
The limit is 50,000 blocks / 209,715,200,000 bytes (195.3125 GiB) per body.
Nothing is committed until all bytes have been read successfully. Empty bodies
commit an empty block list. Concurrent writers have independent block IDs; the
last successful complete commit wins. A failed write preserves an existing
committed body. Cancellation or a lost commit response can leave a **complete**
committed object whose success was not acknowledged; no partial body is published.
Recognized Azure upload failures are wrapped in `HttpCacheContentStoreException`.

Only Azure's `404 BlobNotFound` means a miss or an idempotent remove. Missing
containers, authorization failures, throttling and other errors propagate. Mid-read
errors propagate from the returned stream; the adapter cannot retry origin after
bytes have been delivered. Removal targets one exact blob, not a prefix; snapshots,
leases and retention restrictions can prevent removal and are not bypassed.

## Operator-owned retention

Provision a private container and grant only the access needed by the application.
For the registration example's `http-cache` container and `production/my-service`
namespace, the following account-level Azure lifecycle policy deletes current
bodies older than seven days since modification, plus previous versions and
snapshots older than seven days since their creation:

```json
{
  "rules": [
    {
      "name": "HttpCacheProductionMyServiceRetention",
      "enabled": true,
      "type": "Lifecycle",
      "definition": {
        "filters": {
          "blobTypes": ["blockBlob"],
          "prefixMatch": ["http-cache/production/my-service/"]
        },
        "actions": {
          "baseBlob": {
            "delete": { "daysAfterModificationGreaterThan": 7 }
          },
          "version": {
            "delete": { "daysAfterCreationGreaterThan": 7 }
          },
          "snapshot": {
            "delete": { "daysAfterCreationGreaterThan": 7 }
          }
        }
      }
    }
  ]
}
```

Review and merge this rule into your existing account policy through Azure Portal
or management tooling; do not replace unrelated rules blindly. **This example has
not been deployed.** The adapter never applies it. `prefixMatch` is case-sensitive,
includes the container name, and has a trailing slash to isolate this namespace;
do not omit the filter or it can target unrelated account data. Choose retention
appropriate for your workloads. See the official
[policy structure](https://learn.microsoft.com/en-us/azure/storage/blobs/lifecycle-management-policy-structure).

Previous-version age is measured from creation, not from when a version became
noncurrent. Azure will not delete a current version until its previous versions
and snapshots have been deleted, so retain all three actions where applicable.
Lifecycle processing is asynchronous, not a precise seven-day deletion deadline.
Soft delete can retain deleted data and cost; immutable retention may prohibit
deletion. Account features, leases and service limitations still apply.

Azure block uploads do not have S3-style multipart upload IDs or a lifecycle
`AbortIncompleteMultipartUpload` action. As described by
[Put Block](https://learn.microsoft.com/en-us/rest/api/storageservices/put-block),
Azure automatically garbage-collects uncommitted blocks after a week without a
successful `Put Block` or `Put Block List` on that blob. A block-list commit also
discards uncommitted blocks omitted from the list. Interrupted uploads can retain
blocks until then, and activity on the same blob can postpone expiry. No abort or
prefix deletion is attempted by this adapter: deleting that blob could remove a
previous complete body or another writer's work. High same-key write concurrency
can exhaust the uncommitted-block limit or cause a competing commit to fail when
its staged blocks are discarded; these failures remain observable.

Retention is not HTTP expiry. Deduplication/revalidation may not rewrite blobs;
lifecycle deletion can remove a still-referenced body, causing a normal cache miss.
Do not eagerly remove shared bodies when invalidating one URI's metadata.

Tests exercise the real SDK through a supported fake HTTP transport without cloud
credentials. Real Azure service interoperability, lifecycle execution, account
policies, and NativeAOT deployment require separate opt-in deployment validation.

### Opt-in Azure integration test

The test project includes an integration test that is **skipped unless both**
`HTTP_CACHE_AZURE_TEST_CONNECTION_STRING` and `HTTP_CACHE_AZURE_TEST_CONTAINER`
are nonempty. Setting both explicitly opts into network requests and storage costs.
Supply the connection string through your local environment or a secret manager,
never source control. The container must already exist in a dedicated test account
or other explicitly approved isolated test resource with read/write/delete access.
The test does not provision resources, discover credentials, or modify account policies.

From the repository root, after configuring those environment variables:

```powershell
dotnet test --project hybrid-cache-handler\test\HttpHybridCacheHandler.ContentStore.AzureBlob.Tests\HttpHybridCacheHandler.ContentStore.AzureBlob.Tests.csproj
```

Each run uses a unique `http-cache-integration/<guid>` namespace and checks exact
empty and multi-block body roundtrips, missing reads, and idempotent removal.
Cleanup attempts only that run's two exact object keys, including on failure;
it never lists or recursively deletes a prefix or container. Cloud/authentication
and cleanup errors fail the test rather than being silently ignored or skipped.
An interrupted process can leave its own objects or uncommitted blocks behind:
the resource owner remains responsible for retention, versions, soft-deleted data,
and eventual cleanup/costs. Use a dedicated lifecycle prefix if desired.

Running the default mock tests is not proof of Azure interoperability. The opt-in
test validates basic data operations only, not lifecycle execution, every account
policy, or NativeAOT. With either variable absent, it makes no cloud API calls.
