// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.
//
// Adapted from YARP v2.3.0 (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/StreamCopier.cs).
// Copyright (c) .NET Foundation and Contributors. Licensed under the MIT License; see THIRD-PARTY-NOTICES.txt.

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

internal enum StreamCopyResult
{
    Success,
    InputError,
    OutputError,
    Canceled,
}
