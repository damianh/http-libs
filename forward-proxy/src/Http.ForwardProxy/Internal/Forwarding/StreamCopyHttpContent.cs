// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.
//
// Adapted from YARP v2.3.0 (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/StreamCopyHttpContent.cs).
// Copyright (c) .NET Foundation and Contributors. Licensed under the MIT License; see THIRD-PARTY-NOTICES.txt.

using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Http;

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

/// <summary>Streams the incoming request body to the destination and reports how the copy ended.</summary>
internal sealed class StreamCopyHttpContent(HttpContext context, ActivityCancellationTokenSource activityToken) : HttpContent
{
    private readonly TaskCompletionSource<(StreamCopyResult Result, Exception? Error)> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    public Task<(StreamCopyResult Result, Exception? Error)> ConsumptionTask => _tcs.Task;

    public bool Started => Volatile.Read(ref _started) == 1;

    public bool InProgress => Started && !ConsumptionTask.IsCompleted;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? transportContext, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("Stream was already consumed.");
        }

        CancellationTokenSource? linkedCts = null;
        if (activityToken.Token != cancellationToken)
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(activityToken.Token, cancellationToken);
            cancellationToken = linkedCts.Token;
        }

        try
        {
            try
            {
                // Flush the request headers before waiting on the client body (e.g. Expect: 100-continue).
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var canceled = ex is OperationCanceledException || activityToken.CancelledByLinkedToken;
                _tcs.TrySetResult((canceled ? StreamCopyResult.Canceled : StreamCopyResult.OutputError, ex));
                return;
            }

            var (result, error) = await StreamCopier.CopyAsync(context.Request.Body, stream, Headers.ContentLength ?? StreamCopier.UnknownLength, activityToken, autoFlush: false, cancellationToken).ConfigureAwait(false);
            _tcs.TrySetResult((result, error));

            // Fail the outgoing request so HttpClient doesn't send a truncated body as complete.
            if (result == StreamCopyResult.InputError)
            {
                throw new IOException("An error occurred when reading the request body from the client.", error);
            }

            if (result == StreamCopyResult.Canceled)
            {
                throw new OperationCanceledException("The request body copy was canceled.", error);
            }
        }
        finally
        {
            linkedCts?.Dispose();
        }
    }

    protected override Task<Stream> CreateContentReadStreamAsync() => throw new NotSupportedException();

    protected override bool TryComputeLength(out long length)
    {
        // Content-Length, when present, comes from the copied request headers.
        length = -1;
        return false;
    }
}
