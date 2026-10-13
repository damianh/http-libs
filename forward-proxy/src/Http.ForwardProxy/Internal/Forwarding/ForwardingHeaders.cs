// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.
//
// Adapted from YARP v2.3.0 (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/RequestUtilities.cs
// and src/ReverseProxy/Forwarder/HttpTransformer.cs).
// Copyright (c) .NET Foundation and Contributors. Licensed under the MIT License; see THIRD-PARTY-NOTICES.txt.

using System.Collections.Frozen;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

/// <summary>Copies end-to-end headers between the client and destination messages.</summary>
internal static class ForwardingHeaders
{
    // Hop-by-hop and proxy-specific headers that must not be forwarded (RFC 9110 §7.6.1).
    private static readonly FrozenSet<string> HeadersToExclude = new[]
    {
        HeaderNames.Connection,
        HeaderNames.TransferEncoding,
        HeaderNames.KeepAlive,
        HeaderNames.Upgrade,
        HeaderNames.ProxyConnection,
        HeaderNames.ProxyAuthenticate,
        "Proxy-Authentication-Info",
        HeaderNames.ProxyAuthorization,
        "Proxy-Features",
        "Proxy-Instruction",
        "Security-Scheme",
        "ALPN",
        "Close",
        "HTTP2-Settings",
        HeaderNames.UpgradeInsecureRequests,
        HeaderNames.TE,
        HeaderNames.AltSvc,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Headers that HttpClient only accepts on HttpContent.
    private static readonly FrozenSet<string> ContentHeaders = new[]
    {
        HeaderNames.Allow,
        HeaderNames.ContentDisposition,
        HeaderNames.ContentEncoding,
        HeaderNames.ContentLanguage,
        HeaderNames.ContentLength,
        HeaderNames.ContentLocation,
        HeaderNames.ContentMD5,
        HeaderNames.ContentRange,
        HeaderNames.ContentType,
        HeaderNames.Expires,
        HeaderNames.LastModified,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
    {
        var connectionOptions = source.Headers.GetCommaSeparatedValues(HeaderNames.Connection);

        // Kestrel drops options from a Connection header that also lists close, keep-alive or upgrade.
        string[]? rawConnectionOptions = null;
        if (source.HttpContext.Features.Get<IHttp1ConnectionOptionsFeature>() is { } observed)
        {
            observed.TryGetConnectionOptions(source.HttpContext.Features.Get<IHttpRequestFeature>()!.RawTarget, out rawConnectionOptions);
        }

        foreach (var (name, value) in source.Headers)
        {
            // Host is derived from the target URI (RFC 9112 §3.2.2); pseudo-headers aren't real headers.
            if (HeadersToExclude.Contains(name)
                || name.StartsWith(':')
                || string.Equals(name, HeaderNames.Host, StringComparison.OrdinalIgnoreCase)
                || ContainsToken(connectionOptions, name)
                || (rawConnectionOptions is not null && ContainsToken(rawConnectionOptions, name)))
            {
                continue;
            }

            AddHeader(destination, name, value);
        }

        // A message with both Transfer-Encoding and Content-Length may be a smuggling attempt; the sender must
        // remove Content-Length before forwarding (RFC 9112 §6.3).
        if (source.Headers.ContainsKey(HeaderNames.TransferEncoding) && source.Headers.ContainsKey(HeaderNames.ContentLength))
        {
            destination.Content?.Headers.Remove(HeaderNames.ContentLength);
        }
    }

    public static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        var connectionOptions = TryGetValues(source.Headers, HeaderNames.Connection, out var connection)
            ? connection.SelectMany(v => v!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray()
            : [];
        CopyResponseHeaders(source.Headers, destination.Headers, connectionOptions);
        CopyResponseHeaders(source.Content.Headers, destination.Headers, connectionOptions);

        if (source.Headers.NonValidated.Contains(HeaderNames.TransferEncoding) && source.Content.Headers.NonValidated.Contains(HeaderNames.ContentLength))
        {
            destination.Headers.Remove(HeaderNames.ContentLength);
        }

        // Bodyless responses must not carry a Content-Length: 0 synthesized by HttpClient.
        if (IsBodylessStatusCode(source.StatusCode)
            && source.Content.Headers.NonValidated.TryGetValues(HeaderNames.ContentLength, out var contentLength)
            && contentLength.ToString() == "0")
        {
            destination.Headers.Remove(HeaderNames.ContentLength);
        }
    }

    public static bool TryGetValues(HttpHeaders headers, string name, out StringValues values)
    {
        if (headers.NonValidated.TryGetValues(name, out var headerValues))
        {
            values = headerValues.Count <= 1 ? new StringValues(headerValues.ToString()) : new StringValues([.. headerValues]);
            return true;
        }

        values = default;
        return false;
    }

    private static void CopyResponseHeaders(HttpHeaders source, IHeaderDictionary destination, string[] connectionOptions)
    {
        foreach (var header in source.NonValidated)
        {
            if (HeadersToExclude.Contains(header.Key) || ContainsToken(connectionOptions, header.Key))
            {
                continue;
            }

            // Append to prior values; IHeaderDictionary.Append skips empty values.
            TryGetValues(source, header.Key, out var values);
            destination[header.Key] = StringValues.Concat(destination[header.Key], values);
        }
    }

    // HttpClient sends the union of request and content headers, so each header must be in exactly one of them.
    private static void AddHeader(HttpRequestMessage request, string name, StringValues value)
    {
        foreach (var item in value)
        {
            if (item is not null && item.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                return;
            }
        }

        if (TryAdd(request.Headers, name, value))
        {
            return;
        }

        if (request.Content is null && ContentHeaders.Contains(name))
        {
            request.Content = new EmptyHttpContent();
        }

        if (request.Content is not null)
        {
            TryAdd(request.Content.Headers, name, value);
        }
    }

    // The string overload avoids boxing StringValues for the common single-valued header.
    private static bool TryAdd(HttpHeaders headers, string name, StringValues value) =>
        value.Count == 1
            ? headers.TryAddWithoutValidation(name, value[0])
            : headers.TryAddWithoutValidation(name, (IEnumerable<string>)value!);

    private static bool ContainsToken(string[] tokens, string name)
    {
        foreach (var token in tokens)
        {
            if (string.Equals(token, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBodylessStatusCode(HttpStatusCode statusCode) =>
        statusCode is >= HttpStatusCode.Continue and < HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.ResetContent;
}
