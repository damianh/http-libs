// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Sockets;

namespace DamianH.Http.ForwardProxy;

/// <summary>A TCP server that echoes bytes and writes <c>BYE</c> when the client half-closes.</summary>
internal sealed class EchoServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public EchoServer()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                _ = EchoAsync(socket);
            }
        }
        catch (Exception)
        {
        }
    }

    private static async Task EchoAsync(Socket socket)
    {
        using (socket)
        {
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = await socket.ReceiveAsync(buffer)) > 0)
                {
                    await socket.SendAllAsync(buffer.AsMemory(0, read));
                }

                await socket.SendAllAsync("BYE"u8.ToArray());
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
        }
    }
}
