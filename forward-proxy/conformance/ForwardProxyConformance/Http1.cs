// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DamianH.Http.ForwardProxy.Conformance;

/// <summary>A parsed HTTP/1 request. Strings are Latin-1 so each char is one wire byte.</summary>
internal sealed record ParsedRequest(
    string Method,
    string Target,
    string Version,
    IReadOnlyList<KeyValuePair<string, string>> Fields,
    string Body)
{
    public required string RawStartLine { get; init; }

    /// <summary>
    /// The method and field names are tokens and field values contain no CR, LF or NUL (RFC 9110 §5.1, §5.5, §9.1).
    /// A message violating these may be interpreted differently by a downstream parser.
    /// </summary>
    public bool IsWellFormed =>
        Http1.IsToken(Method)
        && Fields.All(f => Http1.IsToken(f.Key) && f.Value.AsSpan().IndexOfAny(Http1.ForbiddenValueChars) < 0);

    public IReadOnlyList<string> Values(string name) =>
        [.. Fields.Where(field => string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase)).Select(field => field.Value)];
}

internal sealed record ParsedResponse(int Status, IReadOnlyList<KeyValuePair<string, string>> Fields, string Body)
{
    public string? Field(string name) =>
        Fields.FirstOrDefault(field => string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>
/// Permissive HTTP/1 parsing of what the origin and client observe. Parsing is deliberately lenient so that
/// malformed forwarded bytes are still visible to fixture checks; strictness is applied by the evaluator.
/// </summary>
internal static partial class Http1
{
    [GeneratedRegex("""\A[0-9A-Fa-f]+(?:[ \t]*;[ \t]*[!#$%&'*+.^_`|~0-9A-Za-z-]+(?:[ \t]*=[ \t]*(?:[!#$%&'*+.^_`|~0-9A-Za-z-]+|"(?:[\x09\x20\x21\x23-\x5b\x5d-\xff]|\\[\x09\x20-\x7e\x80-\xff])*"))?)*\z""")]
    private static partial Regex ChunkSizeLine();

    public static readonly Encoding Latin1 = Encoding.Latin1;

    private static readonly SearchValues<char> TokenChars =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    internal static readonly SearchValues<char> ForbiddenValueChars = SearchValues.Create("\r\n\0");

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    public static bool IsToken(string value) => value.Length > 0 && !value.AsSpan().ContainsAnyExcept(TokenChars);

    /// <summary>Parses consecutive requests; returns the bytes that did not parse as a request.</summary>
    public static (IReadOnlyList<ParsedRequest> Requests, string Leftover) ParseRequests(string raw)
    {
        var requests = new List<ParsedRequest>();
        while (raw.Length > 0 && TryParseRequest(raw, out var request, out var rest))
        {
            requests.Add(request);
            raw = rest;
        }
        return (requests, raw);
    }

    public static bool TryParseRequest(string raw, out ParsedRequest request, out string rest)
    {
        request = null!;
        rest = raw;
        if (!TryReadLine(raw, 0, out var line, out var position))
        {
            return false;
        }

        // Any run of SP/HTAB separates parts, so an empty request-target ("GET  HTTP/1.1") parses with
        // "HTTP/1.1" as the target and no version, and is then caught by field checks.
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var rawStartLine = raw[..position];
        string version;
        if (parts.Length == 2)
        {
            version = "";
        }
        else if (parts.Length == 3 && parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            version = parts[2]["HTTP/".Length..];
        }
        else
        {
            return false;
        }

        if (!TryParseFields(raw, ref position, out var fields)
            || !TryParseBody(raw, ref position, fields, isResponse: false, status: 0, out var body))
        {
            return false;
        }

        request = new ParsedRequest(parts[0], parts[1], version, fields, body) { RawStartLine = rawStartLine };
        rest = raw[position..];
        return true;
    }

    public static bool TryParseResponse(string raw, out ParsedResponse response, out string rest)
    {
        response = null!;
        rest = raw;
        if (!TryReadLine(raw, 0, out var line, out var position))
        {
            return false;
        }

        var parts = line.Split(' ', 3);
        if (parts.Length < 2
            || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || parts[1].Length != 3
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
        {
            return false;
        }

        if (!TryParseFields(raw, ref position, out var fields)
            || !TryParseBody(raw, ref position, fields, isResponse: true, status, out var body))
        {
            return false;
        }

        response = new ParsedResponse(status, fields, body);
        rest = raw[position..];
        return true;
    }

    /// <summary>Reads to LF, removing one trailing CR. A bare CR elsewhere stays in the line.</summary>
    private static bool TryReadLine(string raw, int start, out string line, out int next)
    {
        var lf = raw.IndexOf('\n', start);
        if (lf < 0)
        {
            line = "";
            next = start;
            return false;
        }
        var end = lf > start && raw[lf - 1] == '\r' ? lf - 1 : lf;
        line = raw[start..end];
        next = lf + 1;
        return true;
    }

    private static bool TryParseFields(string raw, ref int position, out List<KeyValuePair<string, string>> fields)
    {
        fields = [];
        while (true)
        {
            if (!TryReadLine(raw, position, out var line, out var next))
            {
                return false;
            }
            position = next;
            if (line.Length == 0)
            {
                return true;
            }

            // The name is everything before the first colon, so whitespace or other invalid name
            // characters are kept and later fail IsWellFormed.
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return false;
            }
            fields.Add(new(line[..colon], line[(colon + 1)..].Trim([' ', '\t'])));
        }
    }

    private static bool TryParseBody(
        string raw,
        ref int position,
        List<KeyValuePair<string, string>> fields,
        bool isResponse,
        int status,
        out string body)
    {
        body = "";
        if (isResponse && (status is >= 100 and < 200 or 204 or 304))
        {
            return true;
        }

        var chunked = false;
        long? contentLength = null;
        foreach (var (name, value) in fields)
        {
            if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                var codings = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                chunked = codings.Length > 0 && string.Equals(codings[^1], "chunked", StringComparison.OrdinalIgnoreCase);
            }
            else if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                {
                    return false;
                }
                contentLength = length;
            }
        }

        if (chunked)
        {
            return TryParseChunked(raw, ref position, out body);
        }
        if (contentLength is { } declared)
        {
            if (declared > raw.Length - position)
            {
                return false;
            }
            body = raw.Substring(position, (int)declared);
            position += (int)declared;
            return true;
        }
        if (isResponse)
        {
            body = raw[position..];
            position = raw.Length;
        }
        return true;
    }

    private static bool TryParseChunked(string raw, ref int position, out string body)
    {
        var builder = new StringBuilder();
        body = "";
        while (true)
        {
            if (!TryReadLine(raw, position, out var line, out var next))
            {
                return false;
            }
            var digits = line.AsSpan()[..Math.Max(0, line.AsSpan().IndexOfAnyExcept(HexDigits) is var i and >= 0 ? i : line.Length)];
            if (!ChunkSizeLine().IsMatch(line)
                || digits.IsEmpty
                || !long.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size)
                || size < 0
                || size > raw.Length - next)
            {
                return false;
            }
            position = next;
            if (size == 0)
            {
                // Trailer section, terminated by an empty line.
                if (!TryParseFields(raw, ref position, out _))
                {
                    return false;
                }
                body = builder.ToString();
                return true;
            }
            if (raw.Length - position < size + 2 || raw[(int)(position + size)] != '\r' || raw[(int)(position + size + 1)] != '\n')
            {
                return false;
            }
            builder.Append(raw, position, (int)size);
            position += (int)size + 2;
        }
    }
}
