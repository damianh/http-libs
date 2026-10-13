// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.IO.Pipelines;
using System.Net.Sockets;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Relays bytes between a client pipe pair and an upstream socket.</summary>
internal static class TunnelPump
{
    private const int ReadBufferSize = 16 * 1024;

    public readonly record struct Result(long BytesUpstream, long BytesDownstream, bool IdleTimedOut);

    /// <summary>
    /// Runs until the upstream closes, the client aborts, or the tunnel is idle for <paramref name="idleTimeout"/>.
    /// A client half-close is propagated upstream and the tunnel keeps relaying the upstream response.
    /// </summary>
    public static async Task<Result> RunAsync(PipeReader clientInput, PipeWriter clientOutput, Socket upstream, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new State();
        var idleMs = (long)idleTimeout.TotalMilliseconds;
        var period = TimeSpan.FromMilliseconds(Math.Clamp(idleMs / 4, 10, 1000));
        await using var timer = new Timer(_ =>
        {
            if (Environment.TickCount64 - Volatile.Read(ref state.LastActivity) > idleMs)
            {
                state.IdleTimedOut = true;
                cts.Cancel();
            }
        }, null, period, period);

        await using var stream = new NetworkStream(upstream, ownsSocket: false);
        var upTask = ClientToUpstreamAsync(clientInput, stream, upstream, state, cts.Token);
        var downTask = UpstreamToClientAsync(stream, clientOutput, state, cts.Token);

        var first = await Task.WhenAny(upTask, downTask).ConfigureAwait(false);
        if (first == downTask || !await upTask.ConfigureAwait(false))
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(upTask, downTask).ConfigureAwait(false);
        return new(Interlocked.Read(ref state.BytesUpstream), Interlocked.Read(ref state.BytesDownstream), state.IdleTimedOut);
    }

    /// <returns><see langword="true"/> when the client completed gracefully (half-close).</returns>
    private static async Task<bool> ClientToUpstreamAsync(PipeReader input, NetworkStream stream, Socket upstream, State state, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                foreach (var segment in buffer)
                {
                    await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                }

                Interlocked.Add(ref state.BytesUpstream, buffer.Length);
                Volatile.Write(ref state.LastActivity, Environment.TickCount64);
                input.AdvanceTo(buffer.End);
                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }

            upstream.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (Exception ex) when (IsTunnelTermination(ex))
        {
            return false;
        }
    }

    private static async Task UpstreamToClientAsync(NetworkStream stream, PipeWriter output, State state, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var memory = output.GetMemory(ReadBufferSize);
                var read = await stream.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                output.Advance(read);
                Interlocked.Add(ref state.BytesDownstream, read);
                Volatile.Write(ref state.LastActivity, Environment.TickCount64);
                var flush = await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (IsTunnelTermination(ex))
        {
        }
    }

    private static bool IsTunnelTermination(Exception ex) =>
        ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidOperationException;

    private sealed class State
    {
        public long LastActivity = Environment.TickCount64;
        public long BytesUpstream;
        public long BytesDownstream;
        public volatile bool IdleTimedOut;
    }
}
