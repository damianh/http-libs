// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Sockets;

namespace DamianH.Http.ForwardProxy;

internal static class SocketExtensions
{
    /// <summary>Sends all of <paramref name="buffer"/>; a single <see cref="Socket.SendAsync(ReadOnlyMemory{byte}, CancellationToken)"/> may send less.</summary>
    public static async Task SendAllAsync(this Socket socket, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            var sent = await socket.SendAsync(buffer, cancellationToken);
            buffer = buffer[sent..];
        }
    }
}
