// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DamianH.Http.ForwardProxy;

/// <summary>Raw socket helpers for sending exact proxy requests.</summary>
internal static class RawProxy
{
    public static async Task<Socket> ConnectAsync(int port)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        return socket;
    }

    public static async Task SendAsync(Socket socket, string text) =>
        await socket.SendAllAsync(Encoding.Latin1.GetBytes(text), TestContext.Current.CancellationToken);

    /// <summary>Reads a response head (through the blank line) and returns it with any body bytes read so far.</summary>
    public static async Task<RawResponse> ReadResponseAsync(Socket socket)
    {
        var received = new List<byte>();
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var text = Encoding.Latin1.GetString(received.ToArray());
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                var lines = text[..end].Split("\r\n");
                var headers = lines.Skip(1).Select(l => l.Split(':', 2)).ToLookup(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                return new RawResponse(int.Parse(lines[0].Split(' ')[1]), lines[0], headers, text[(end + 4)..]);
            }

            var read = await socket.ReceiveAsync(buffer, timeout.Token);
            if (read == 0)
            {
                throw new IOException($"Connection closed before the response head was complete: {text}");
            }

            received.AddRange(buffer.AsSpan(0, read));
        }
    }

    /// <summary>Reads until the peer closes the connection.</summary>
    public static async Task<string> ReadToEndAsync(Socket socket, string prefix = "")
    {
        var builder = new StringBuilder(prefix);
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        int read;
        while ((read = await socket.ReceiveAsync(buffer, timeout.Token)) > 0)
        {
            builder.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads the rest of <paramref name="response"/>'s body as framed by Content-Length or chunked
    /// transfer coding (without trailers), leaving the connection positioned at the next response.
    /// </summary>
    public static async Task<string> ReadBodyAsync(Socket socket, RawResponse response)
    {
        if (response.Header("Transfer-Encoding").Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            var raw = response.Body;
            while (!raw.EndsWith("0\r\n\r\n", StringComparison.Ordinal))
            {
                raw = await ReadExactlyAsync(socket, raw.Length + 1, raw);
            }

            var body = new StringBuilder();
            var position = 0;
            while (true)
            {
                var lineEnd = raw.IndexOf("\r\n", position, StringComparison.Ordinal);
                var size = Convert.ToInt32(raw[position..lineEnd].Split(';')[0], 16);
                if (size == 0)
                {
                    return body.ToString();
                }

                body.Append(raw, lineEnd + 2, size);
                position = lineEnd + 2 + size + 2;
            }
        }

        var length = int.Parse(response.Header("Content-Length"), CultureInfo.InvariantCulture);
        return await ReadExactlyAsync(socket, length, response.Body);
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes.</summary>
    public static async Task<string> ReadExactlyAsync(Socket socket, int count, string prefix = "")
    {
        var builder = new StringBuilder(prefix);
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (builder.Length < count)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(0, Math.Min(buffer.Length, count - builder.Length)), timeout.Token);
            if (read == 0)
            {
                throw new IOException($"Connection closed after {builder.Length} of {count} bytes: {builder}");
            }

            builder.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }
}
