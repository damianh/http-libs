// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Strict parser for an HTTP/1.x CONNECT request head (RFC 9112).</summary>
internal static class ConnectRequestParser
{
    public const int MaxHeaderCount = 100;

    private static ReadOnlySpan<byte> Prefix => "CONNECT "u8;

    private static ReadOnlySpan<byte> HeadTerminator => "\r\n\r\n"u8;

    public enum Detection
    {
        NeedMoreData,
        Connect,
        NotConnect,
    }

    public enum ParseStatus
    {
        Incomplete,
        Complete,
        Invalid,
    }

    public sealed record ConnectRequest(string Target, string Protocol, IReadOnlyList<KeyValuePair<string, string>> Headers);

    /// <summary>Determines whether the connection starts with a CONNECT request line.</summary>
    public static Detection Detect(in ReadOnlySequence<byte> buffer)
    {
        var length = (int)Math.Min(buffer.Length, Prefix.Length);
        Span<byte> start = stackalloc byte[Prefix.Length];
        buffer.Slice(0, length).CopyTo(start);
        if (!start[..length].SequenceEqual(Prefix[..length]))
        {
            return Detection.NotConnect;
        }

        return length == Prefix.Length ? Detection.Connect : Detection.NeedMoreData;
    }

    /// <summary>Parses a complete request head from <paramref name="buffer"/>.</summary>
    /// <param name="buffer">Buffered connection bytes, starting at the request line.</param>
    /// <param name="maxHeadSize">Maximum request head size, including the terminating empty line.</param>
    /// <param name="request">The parsed request when <see cref="ParseStatus.Complete"/>.</param>
    /// <param name="consumed">The position after the head when <see cref="ParseStatus.Complete"/>.</param>
    /// <param name="errorStatusCode">The status code to respond with when <see cref="ParseStatus.Invalid"/>.</param>
    public static ParseStatus TryParse(
        in ReadOnlySequence<byte> buffer,
        int maxHeadSize,
        out ConnectRequest? request,
        out SequencePosition consumed,
        out int errorStatusCode)
    {
        request = null;
        consumed = buffer.Start;
        errorStatusCode = 0;

        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> head, HeadTerminator, advancePastDelimiter: true))
        {
            if (buffer.Length > maxHeadSize)
            {
                errorStatusCode = StatusCodes.Status431RequestHeaderFieldsTooLarge;
                return ParseStatus.Invalid;
            }

            return ParseStatus.Incomplete;
        }

        if (head.Length + HeadTerminator.Length > maxHeadSize)
        {
            errorStatusCode = StatusCodes.Status431RequestHeaderFieldsTooLarge;
            return ParseStatus.Invalid;
        }

        var status = Parse(head.ToArray(), out request, out errorStatusCode);
        if (status == ParseStatus.Complete)
        {
            consumed = reader.Position;
        }

        return status;
    }

    private static ParseStatus Parse(ReadOnlySpan<byte> head, out ConnectRequest? request, out int errorStatusCode)
    {
        request = null;
        errorStatusCode = StatusCodes.Status400BadRequest;

        NextLine(head, out var line, out var rest);
        if (!TryParseRequestLine(line, out var target, out var protocol))
        {
            return ParseStatus.Invalid;
        }

        var headers = new List<KeyValuePair<string, string>>();
        var hasMore = rest.Length > 0;
        while (hasMore)
        {
            hasMore = NextLine(rest, out line, out rest);
            if (headers.Count == MaxHeaderCount)
            {
                errorStatusCode = StatusCodes.Status431RequestHeaderFieldsTooLarge;
                return ParseStatus.Invalid;
            }

            if (!TryParseHeader(line, out var name, out var value))
            {
                return ParseStatus.Invalid;
            }

            if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase) && value != "0"))
            {
                return ParseStatus.Invalid;
            }

            headers.Add(new(name, value));
        }

        request = new ConnectRequest(target, protocol, headers);
        errorStatusCode = 0;
        return ParseStatus.Complete;
    }

    /// <summary>Splits off the next CRLF-terminated line. Returns false when no CRLF follows (last line).</summary>
    private static bool NextLine(ReadOnlySpan<byte> input, out ReadOnlySpan<byte> line, out ReadOnlySpan<byte> rest)
    {
        var index = input.IndexOf("\r\n"u8);
        if (index < 0)
        {
            line = input;
            rest = default;
            return false;
        }

        line = input[..index];
        rest = input[(index + 2)..];
        return true;
    }

    private static bool TryParseRequestLine(ReadOnlySpan<byte> line, out string target, out string protocol)
    {
        target = "";
        protocol = "";
        if (!line.StartsWith(Prefix))
        {
            return false;
        }

        line = line[Prefix.Length..];
        var space = line.IndexOf((byte)' ');
        if (space <= 0)
        {
            return false;
        }

        var targetBytes = line[..space];
        var versionBytes = line[(space + 1)..];
        foreach (var b in targetBytes)
        {
            if (b is <= 0x20 or >= 0x7f)
            {
                return false;
            }
        }

        if (versionBytes.SequenceEqual("HTTP/1.1"u8))
        {
            protocol = "HTTP/1.1";
        }
        else if (versionBytes.SequenceEqual("HTTP/1.0"u8))
        {
            protocol = "HTTP/1.0";
        }
        else
        {
            return false;
        }

        target = Encoding.ASCII.GetString(targetBytes);
        return true;
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> line, out string name, out string value)
    {
        name = "";
        value = "";
        var colon = line.IndexOf((byte)':');
        if (colon <= 0)
        {
            return false;
        }

        // Rejects obs-fold (leading whitespace) and whitespace before the colon, as both are request-smuggling vectors.
        var nameBytes = line[..colon];
        foreach (var b in nameBytes)
        {
            if (!IsTokenChar(b))
            {
                return false;
            }
        }

        var valueBytes = line[(colon + 1)..].Trim(" \t"u8);
        foreach (var b in valueBytes)
        {
            if (b is (< 0x20 and not (byte)'\t') or 0x7f)
            {
                return false;
            }
        }

        name = Encoding.ASCII.GetString(nameBytes);
        value = Encoding.Latin1.GetString(valueBytes);
        return true;
    }

    private static bool IsTokenChar(byte b) =>
        b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
            or (byte)'!' or (byte)'#' or (byte)'$' or (byte)'%' or (byte)'&' or (byte)'\'' or (byte)'*' or (byte)'+'
            or (byte)'-' or (byte)'.' or (byte)'^' or (byte)'_' or (byte)'`' or (byte)'|' or (byte)'~';
}
