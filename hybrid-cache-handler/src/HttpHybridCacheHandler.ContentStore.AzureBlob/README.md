# Azure Blob HTTP cache content store

`DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob` targets `net10.0`,
`netstandard2.0`, and `net472` using the official Azure SDK.
It depends on the independent ContentStore package, not
the handler or HybridCache; release tags use `cache-azureblob-v`.

**[Full Azure Blob guide](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/azure-blob/)**

Install alongside the handler from your application project directory:

```powershell
dotnet add package DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob
```

Given an application-configured `BlobContainerClient`:

```csharp
using DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob;

services.AddSingleton(container);
services.AddHttpHybridCacheAzureBlobContentStore(
    options => options.Namespace = "production/my-service");
```

Configure the handler's large-body offload separately and use only one backend.
The application supplies credentials, retries, endpoints, and an existing private
container; the adapter does not provision resources or apply lifecycle rules.
It does not dispose supplied clients or input streams. Dispose each returned stream.

Writes require seekable input and an exact remaining length; complete block-list
commits publish bodies, never partial uploads. Only `404 BlobNotFound` is a miss;
other failures propagate. NativeAOT/trimming is not validated.

Operators own lifecycle retention, versions/snapshots, and uncommitted-block costs.
Retention is not HTTP expiry; never eagerly delete a shared body on metadata
invalidation. The full guide preserves upload limits, lifecycle policy safety,
and explicit opt-in integration-test instructions. Mock tests do not prove real
Azure interoperability.
