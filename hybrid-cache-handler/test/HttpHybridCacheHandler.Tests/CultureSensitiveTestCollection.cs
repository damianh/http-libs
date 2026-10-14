// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CultureSensitiveTestCollection
{
    public const string Name = "Culture-sensitive tests";
}
