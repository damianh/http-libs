// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace DamianH.Http.ForwardProxy.Conformance;

/// <summary>Replays fixture payloads through the proxy over raw sockets and evaluates what comes back.</summary>
internal sealed class FixtureDriver(IPEndPoint proxy, string origin, string echoServer)
{
    /// <summary>Wait for the first response byte; forwarding includes the echo origin's 100 ms read window.</summary>
    public TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>After the first byte, read until the connection is idle this long or closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public SortedDictionary<string, FixtureResult> Run(IEnumerable<Fixture> fixtures)
    {
        var results = new ConcurrentDictionary<string, FixtureResult>();
        Parallel.ForEach(fixtures, new ParallelOptions { MaxDegreeOfParallelism = 8 }, fixture =>
        {
            FixtureResult result;
            try
            {
                result = FixtureEvaluator.Evaluate(fixture, Exchange(fixture), origin, echoServer);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                result = new FixtureResult(Outcomes.Error, fixture.Expect, fixture.Reference, [$"{ex.GetType().Name}: {ex.Message}"]);
            }
            results[fixture.Id] = result;
        });
        return new SortedDictionary<string, FixtureResult>(results, StringComparer.Ordinal);
    }

    private string Exchange(Fixture fixture)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        socket.Connect(proxy);
        using var received = new MemoryStream();
        var buffer = new byte[64 * 1024];
        for (var index = 0; index < fixture.Payload.Count; index++)
        {
            try
            {
                SocketWrites.SendAll(socket, Http1.Latin1.GetBytes(FixtureEvaluator.Substitute(fixture.Payload[index], origin)));
            }
            catch (SocketException)
            {
                break;
            }

            var last = index == fixture.Payload.Count - 1;
            socket.ReceiveTimeout = (int)(last ? FirstByteTimeout : IdleTimeout).TotalMilliseconds;
            int read;
            try
            {
                read = socket.Receive(buffer);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (SocketException)
            {
                break;
            }
            if (read == 0)
            {
                break;
            }
            received.Write(buffer, 0, read);
            socket.ReceiveTimeout = (int)IdleTimeout.TotalMilliseconds;
            SocketReads.ReadUntilIdle(socket, received, buffer);
        }
        return Http1.Latin1.GetString(received.ToArray());
    }
}

internal static class SocketWrites
{
    public static void SendAll(Socket socket, ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var sent = socket.Send(bytes);
            if (sent == 0)
            {
                throw new IOException("Socket closed before all bytes were sent.");
            }
            bytes = bytes[sent..];
        }
    }
}

internal static class SocketReads
{
    /// <summary>Reads until the socket's receive timeout expires with no data, or the peer closes or resets.</summary>
    public static void ReadUntilIdle(Socket socket, Stream destination, byte[] buffer)
    {
        while (true)
        {
            int read;
            try
            {
                read = socket.Receive(buffer);
            }
            catch (SocketException)
            {
                return;
            }
            if (read == 0)
            {
                return;
            }
            destination.Write(buffer, 0, read);
        }
    }
}
