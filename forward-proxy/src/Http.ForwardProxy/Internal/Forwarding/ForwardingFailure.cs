// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

/// <summary>Why forwarding failed. <see cref="Error"/> is written to the response if it has not started.</summary>
internal sealed record ForwardingFailure(string Stage, ProxyError Error, Exception Exception);
