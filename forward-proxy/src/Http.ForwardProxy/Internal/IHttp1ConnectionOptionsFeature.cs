// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>The original <c>Connection</c> options of the current HTTP/1.x request, as received on the wire.</summary>
internal interface IHttp1ConnectionOptionsFeature
{
    /// <summary>
    /// Gets the connection options of the current request other than <c>close</c>, <c>keep-alive</c> and
    /// <c>upgrade</c>. Kestrel replaces a <c>Connection</c> header holding one of those with just that option, which
    /// would otherwise forward the headers the client listed as hop-by-hop.
    /// </summary>
    /// <param name="rawTarget">The current request's raw target, used to confirm the observed head is this request's.</param>
    /// <param name="options">The options, or <see langword="null"/> when there are none.</param>
    /// <returns><see langword="false"/> when the options are unknown.</returns>
    bool TryGetConnectionOptions(string rawTarget, out string[]? options);

    /// <summary>Whether the current request asked to upgrade and observation is paused until its outcome is known.</summary>
    bool IsAwaitingUpgradeOutcome { get; }

    /// <summary>Resumes observation after the current upgrade request was answered without switching protocols.</summary>
    void ResumeAfterRejectedUpgrade();
}
