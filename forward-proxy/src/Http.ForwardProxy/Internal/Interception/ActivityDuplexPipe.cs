// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers;
using System.IO.Pipelines;

namespace DamianH.Http.ForwardProxy.Internal.Interception;

/// <summary>Wraps the client pipes of an intercepted tunnel to count bytes and track the last activity.</summary>
internal sealed class ActivityDuplexPipe : IDuplexPipe
{
    private long _bytesRead;
    private long _bytesWritten;
    private long _lastActivity = Environment.TickCount64;

    public ActivityDuplexPipe(PipeReader input, PipeWriter output)
    {
        Input = new Reader(this, input);
        Output = new Writer(this, output);
    }

    public PipeReader Input { get; }

    public PipeWriter Output { get; }

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public long LastActivity => Volatile.Read(ref _lastActivity);

    private void OnRead(long bytes)
    {
        Interlocked.Add(ref _bytesRead, bytes);
        Volatile.Write(ref _lastActivity, Environment.TickCount64);
    }

    private void OnWritten(long bytes)
    {
        Interlocked.Add(ref _bytesWritten, bytes);
        Volatile.Write(ref _lastActivity, Environment.TickCount64);
    }

    private sealed class Reader(ActivityDuplexPipe owner, PipeReader inner) : PipeReader
    {
        private ReadOnlySequence<byte> _buffer;

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
            _buffer = result.Buffer;
            return result;
        }

        public override bool TryRead(out ReadResult result)
        {
            if (!inner.TryRead(out result))
            {
                return false;
            }

            _buffer = result.Buffer;
            return true;
        }

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            var bytes = _buffer.Slice(_buffer.Start, consumed).Length;
            _buffer = default;
            if (bytes > 0)
            {
                owner.OnRead(bytes);
            }

            inner.AdvanceTo(consumed, examined);
        }

        public override void CancelPendingRead() => inner.CancelPendingRead();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }

    private sealed class Writer(ActivityDuplexPipe owner, PipeWriter inner) : PipeWriter
    {
        public override void Advance(int bytes)
        {
            inner.Advance(bytes);
            if (bytes > 0)
            {
                owner.OnWritten(bytes);
            }
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }
}
