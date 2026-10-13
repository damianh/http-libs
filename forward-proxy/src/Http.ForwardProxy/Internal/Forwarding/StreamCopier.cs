// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.
//
// Adapted from YARP v2.3.0 (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/StreamCopier.cs).
// Copyright (c) .NET Foundation and Contributors. Licensed under the MIT License; see THIRD-PARTY-NOTICES.txt.

using System.Buffers;

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

/// <summary>Copies streams, capturing errors and resetting the activity timeout on progress.</summary>
internal static class StreamCopier
{
    // See https://github.com/dotnet/yarp/pull/330#issuecomment-758851852.
    private const int DefaultBufferSize = 65536;

    public const long UnknownLength = -1;

    public static async ValueTask<(StreamCopyResult Result, Exception? Error, long Bytes)> CopyAsync(Stream input, Stream output, long promisedContentLength, ActivityCancellationTokenSource activityToken, bool autoFlush, CancellationToken cancellation)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        var read = 0;
        long contentLength = 0;
        try
        {
            while (true)
            {
                read = 0;

                // A zero-byte read defers renting a buffer until data is available.
                var zeroByteReadTask = input.ReadAsync(Memory<byte>.Empty, cancellation);
                if (zeroByteReadTask.IsCompletedSuccessfully)
                {
                    _ = zeroByteReadTask.Result;
                }
                else
                {
                    var bufferToReturn = buffer;
                    buffer = null;
                    ArrayPool<byte>.Shared.Return(bufferToReturn);
                    await zeroByteReadTask.ConfigureAwait(false);
                    buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
                }

                read = await input.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false);
                contentLength += read;
                if (promisedContentLength != UnknownLength && contentLength > promisedContentLength)
                {
                    return (StreamCopyResult.InputError, new InvalidOperationException("More bytes received than the specified Content-Length."), contentLength);
                }

                activityToken.ResetTimeout();
                if (read == 0)
                {
                    return promisedContentLength == UnknownLength || contentLength == promisedContentLength
                        ? (StreamCopyResult.Success, null, contentLength)
                        : (StreamCopyResult.InputError, new InvalidOperationException($"Sent {contentLength} content bytes, but Content-Length promised {promisedContentLength}."), contentLength);
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                if (autoFlush)
                {
                    await output.FlushAsync(cancellation).ConfigureAwait(false);
                }

                activityToken.ResetTimeout();
            }
        }
        catch (Exception ex)
        {
            if (activityToken.CancelledByLinkedToken)
            {
                return (StreamCopyResult.Canceled, ex, contentLength);
            }

            // If the activity timeout fired while reading or writing, blame the sender or receiver.
            return (read == 0 ? StreamCopyResult.InputError : StreamCopyResult.OutputError, ex, contentLength);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
