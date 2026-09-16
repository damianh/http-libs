# YARP caching proxy sample

Demonstrates YARP direct forwarding with `HttpHybridCacheHandler` in shared mode.
It sets a ten-minute fallback and 50 MB maximum cacheable size.

**[Full sample guide and limitations](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/samples/yarp-caching-proxy/)**

From this directory, explicitly bind the documented port:

```powershell
dotnet run --no-launch-profile -- --urls http://localhost:5000
```

Or from the repository root:

```powershell
dotnet run --project hybrid-cache-handler\samples\YarpCachingProxySample\YarpCachingProxySample.csproj --no-launch-profile -- --urls http://localhost:5000
```

From another terminal:

```powershell
curl http://localhost:5000/api/repos/dotnet/runtime
```

This is not a production-ready gateway. The current direct-forwarding code does
not apply `appsettings.json`'s `/api` removal transform, so this URL does not
guarantee the intended GitHub resource or cache hits. The sample also does not
explicitly enable decompression. Read the full guide before adapting its routing,
transport, or authenticated shared-cache behavior.
