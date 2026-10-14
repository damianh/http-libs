// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;

namespace EstInterception.ServiceDefaults;

public static class InterceptionConfiguration
{
    public static IReadOnlySet<string> GetInterceptionHosts(this IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        var hosts = section.Value is { } value
            ? value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : section.Get<string[]>() ?? ["httpbin.org"];
        return hosts.Select(static host => host.Trim())
            .Where(static host => host.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
