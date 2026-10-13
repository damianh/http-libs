// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.
//
// Adapted from YARP v2.3.0 (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/ReverseProxyPropagator.cs).
// Copyright (c) .NET Foundation and Contributors. Licensed under the MIT License; see THIRD-PARTY-NOTICES.txt.

using System.Diagnostics;
using System.Net.Http;

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

/// <summary>Removes trace context headers copied from the client before injecting the current activity's.</summary>
internal sealed class ReverseProxyPropagator(DistributedContextPropagator innerPropagator) : DistributedContextPropagator
{
    private readonly string[] _headersToRemove = [.. innerPropagator.Fields];

    public override IReadOnlyCollection<string> Fields => innerPropagator.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (carrier is HttpRequestMessage message)
        {
            foreach (var header in _headersToRemove)
            {
                message.Headers.Remove(header);
            }
        }

        innerPropagator.Inject(activity, carrier, setter);
    }

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
        innerPropagator.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) =>
        innerPropagator.ExtractBaggage(carrier, getter);
}
