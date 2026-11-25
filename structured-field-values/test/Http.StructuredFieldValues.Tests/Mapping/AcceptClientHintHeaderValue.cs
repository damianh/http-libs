// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.StructuredFieldValues.Mapping;

/// <summary>
/// Represents an Accept-CH (Accept Client Hints) header value.
/// Plain POCO — no attributes, no partial class.
/// </summary>
public class AcceptClientHintHeaderValue
{
    /// <summary>Gets the requested client hint header names as tokens.</summary>
    public IReadOnlyList<string> Hints { get; init; } = [];
}
