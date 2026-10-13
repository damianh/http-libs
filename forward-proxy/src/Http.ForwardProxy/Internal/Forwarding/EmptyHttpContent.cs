// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.
//
// Adapted from YARP v2.3.0 (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/EmptyHttpContent.cs).
// Copyright (c) .NET Foundation and Contributors. Licensed under the MIT License; see THIRD-PARTY-NOTICES.txt.

using System.Net;
using System.Net.Http;

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

/// <summary>A zero-length content used only to carry content headers.</summary>
internal sealed class EmptyHttpContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return true;
    }
}
