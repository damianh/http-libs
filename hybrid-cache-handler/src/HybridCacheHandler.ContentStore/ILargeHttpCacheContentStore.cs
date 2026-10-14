// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

/// <summary>
/// Identifies an optional content store used for large responses.
/// </summary>
public interface ILargeHttpCacheContentStore : IHttpCacheContentStore;
