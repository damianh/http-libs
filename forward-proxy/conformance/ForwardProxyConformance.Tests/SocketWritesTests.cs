// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Sockets;

namespace DamianH.Http.ForwardProxy.Conformance;

public class SocketWritesTests
{
    [Fact]
    public async Task Sends_entire_payload_with_a_small_send_buffer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { SendBufferSize = 1024 };
        var cancellationToken = TestContext.Current.CancellationToken;
        await sender.ConnectAsync((IPEndPoint)listener.LocalEndpoint, cancellationToken);
        using var receiver = await listener.AcceptSocketAsync(cancellationToken);
        var payload = new byte[2 * 1024 * 1024];
        new Random(42).NextBytes(payload);
        var sending = Task.Run(() =>
        {
            SocketWrites.SendAll(sender, payload);
            sender.Shutdown(SocketShutdown.Send);
        }, cancellationToken);
        using var received = new MemoryStream();
        await using var stream = new NetworkStream(receiver, ownsSocket: false);
        await stream.CopyToAsync(received, cancellationToken);
        await sending;
        received.ToArray().ShouldBe(payload);
    }
}
