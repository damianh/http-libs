// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.ForwardProxy;

internal sealed record RawResponse(int StatusCode, string StatusLine, ILookup<string, string> Headers, string Body)
{
    public string Header(string name) => string.Join(", ", Headers[name]);
}
