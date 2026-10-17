// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.
//
// Adapted from YARP v2.3.0 (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/HttpForwarder.cs).
// Copyright (c) .NET Foundation and Contributors. Licensed under the MIT License; see THIRD-PARTY-NOTICES.txt.
//
// Reduced to what a forward proxy needs: HTTP/1.1 to the destination, header transforms through
// IForwardProxyRequestTransform, WebSocket upgrades only, and failures reported as Proxy-Status errors.

using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

/// <summary>Forwards a request to its <c>http://</c> or (intercepted) <c>https://</c> destination.</summary>
internal sealed class HttpForwarder(UpstreamHttpClientPool clients, ForwardProxyOptions options) : IDisposable
{
    private const string WebSocket = "websocket";

    private static readonly ProxyError DestinationUnavailable = new(StatusCodes.Status502BadGateway, ProxyErrorTypes.DestinationUnavailable, "Destination is unavailable.");
    private static readonly ProxyError DestinationTimeout = new(StatusCodes.Status504GatewayTimeout, ProxyErrorTypes.ConnectionTimeout, "Destination timed out.");
    private static readonly ProxyError ResponseIncomplete = new(StatusCodes.Status502BadGateway, ProxyErrorTypes.HttpResponseIncomplete, "Destination response was incomplete.");
    private static readonly ProxyError ClientAborted = new(StatusCodes.Status400BadRequest, ProxyErrorTypes.HttpRequestError, "Client aborted the request.");
    private static readonly ProxyError TlsProtocolError = new(StatusCodes.Status502BadGateway, ProxyErrorTypes.TlsProtocolError, "TLS handshake with the destination failed.");

    public void Dispose() => clients.Dispose();

    /// <summary>Forwards the request.</summary>
    /// <param name="context">The client request.</param>
    /// <param name="target">The absolute destination URI.</param>
    /// <param name="destination">The destination, passed to request transforms.</param>
    /// <param name="startTunnel">
    /// For a WebSocket upgrade (<see cref="IsWebSocketUpgrade"/>), starts tunnel telemetry once the destination accepts
    /// the upgrade; <see langword="null"/> for other requests.
    /// </param>
    /// <param name="intercepted">Whether this request is inside a TLS-intercepted tunnel.</param>
    /// <remarks>An exception from a request transform propagates, so it is reported as a proxy internal error.</remarks>
    public async Task<ForwardingFailure?> SendAsync(HttpContext context, Uri target, ForwardProxyDestination destination, Func<TunnelTelemetry>? startTunnel, bool intercepted)
    {
        var activity = ActivityCancellationTokenSource.Rent(options.HttpActivityTimeout, context.RequestAborted);
        try
        {
            var isUpgrade = startTunnel is not null;
            var transforms = context.RequestServices.GetServices<IForwardProxyRequestTransform>().ToArray();
            StreamCopyHttpContent? requestContent = null;
            HttpRequestMessage request;
            try
            {
                request = CreateRequest(context, target, isUpgrade, activity, out requestContent);
            }
            catch (Exception ex)
            {
                return await HandleRequestFailureAsync(context, requestContent, ex, activity).ConfigureAwait(false);
            }

            using var outgoingRequest = request;
            UpstreamHttpClientPool.Lease lease;
            try
            {
                foreach (var transform in transforms)
                {
                    await transform.TransformRequestAsync(context, destination, request, context.RequestAborted).ConfigureAwait(false);
                    if (request.RequestUri is not { IsAbsoluteUri: true } transformedUri
                        || !string.Equals(transformedUri.AbsoluteUri, target.AbsoluteUri, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Request transforms must not change the destination URI.");
                    }

                    if (request.Headers.NonValidated.TryGetValues(HeaderNames.Host, out var hosts)
                        && (hosts.Count != 1
                            || !AuthorityParser.TryParse(hosts.ToString(), target.Scheme == Uri.UriSchemeHttps ? 443 : 80, out var transformedHost, out var transformedPort)
                            || transformedHost != destination.Host || transformedPort != destination.Port))
                    {
                        throw new InvalidOperationException("Request transforms must not change the destination authority.");
                    }
                }

                lease = clients.Acquire(await SelectCertificateAsync(context, destination, intercepted).ConfigureAwait(false));
            }
            catch (ProxyErrorException ex)
            {
                await CancelRequestBodyAsync(requestContent, activity).ConfigureAwait(false);
                return new("ClientCertificate", ex.Error, ex);
            }
            catch
            {
                await CancelRequestBodyAsync(requestContent, activity).ConfigureAwait(false);
                throw;
            }

            using var clientLease = lease;
            HttpResponseMessage response;
            try
            {
                response = await lease.Client.SendAsync(request, activity.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await HandleRequestFailureAsync(context, requestContent, ex, activity).ConfigureAwait(false);
            }

            using (response)
            {
                activity.ResetTimeout();
                try
                {
                    foreach (var transform in transforms)
                    {
                        await transform.TransformResponseAsync(context, destination, response, context.RequestAborted).ConfigureAwait(false);
                    }
                }
                catch
                {
                    await CancelRequestBodyAsync(requestContent, activity).ConfigureAwait(false);
                    throw;
                }

                try
                {
                    CopyResponseHead(context, response);
                }
                catch (Exception ex)
                {
                    await CancelRequestBodyAsync(requestContent, activity).ConfigureAwait(false);
                    ClearResponse(context);
                    return new("ResponseHeaders", ResponseIncomplete, ex);
                }

                if (isUpgrade && response.StatusCode == HttpStatusCode.SwitchingProtocols)
                {
                    return await UpgradeAsync(context, response, activity, startTunnel!).ConfigureAwait(false);
                }

                // An event stream (such as MCP's Streamable HTTP) can be quiet for long periods, like a tunnel.
                if (IsEventStream(response))
                {
                    activity.SetActivityTimeout(options.TunnelIdleTimeout);
                }

                (StreamCopyResult Result, Exception? Error, long Bytes) responseCopy;
                var responseStream = await response.Content.ReadAsStreamAsync(activity.Token).ConfigureAwait(false);
                await using (responseStream.ConfigureAwait(false))
                {
                    responseCopy = await StreamCopier.CopyAsync(responseStream, context.Response.Body, StreamCopier.UnknownLength, activity, autoFlush: false, activity.Token).ConfigureAwait(false);
                }

                if (responseCopy.Result != StreamCopyResult.Success)
                {
                    await CancelRequestBodyAsync(requestContent, activity).ConfigureAwait(false);
                    if (!ClearResponse(context))
                    {
                        context.Abort();
                    }

                    var stage = responseCopy.Result == StreamCopyResult.InputError ? "ResponseBodyDestination" : "ResponseBodyClient";
                    return new(stage, ResponseIncomplete, responseCopy.Error!);
                }

                if (requestContent is { Started: true })
                {
                    var (result, error) = await requestContent.ConsumptionTask.ConfigureAwait(false);
                    if (result != StreamCopyResult.Success)
                    {
                        return new("RequestBody", DestinationUnavailable, error!);
                    }
                }

                return null;
            }
        }
        finally
        {
            activity.Return();
        }
    }

    private async ValueTask<X509Certificate2?> SelectCertificateAsync(HttpContext context, ForwardProxyDestination destination, bool intercepted)
    {
        if (!intercepted || context.RequestServices.GetService<IForwardProxyUpstreamClientCertificateSelector>() is not { } selector)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(options.ConnectTimeout);
        try
        {
            var certificate = await selector.SelectAsync(context, destination, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return certificate;
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            throw new ProxyErrorException(new(StatusCodes.Status504GatewayTimeout, ProxyErrorTypes.ConnectionTimeout,
                "Timed out selecting the upstream client certificate."));
        }
    }

    private HttpRequestMessage CreateRequest(HttpContext context, Uri target, bool isUpgrade, ActivityCancellationTokenSource activity, out StreamCopyHttpContent? requestContent)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = isUpgrade ? HttpVersionPolicy.RequestVersionExact : HttpVersionPolicy.RequestVersionOrLower,
        };
        requestContent = HasBody(context.Request) ? new StreamCopyHttpContent(context, activity) : null;
        request.Content = requestContent;
        ForwardingHeaders.CopyRequestHeaders(context.Request, request);
        if (isUpgrade)
        {
            request.Headers.TryAddWithoutValidation(HeaderNames.Connection, HeaderNames.Upgrade);
            request.Headers.TryAddWithoutValidation(HeaderNames.Upgrade, context.Request.Headers.Upgrade.ToString());
        }

        if (options.AddViaHeader)
        {
            request.Headers.TryAddWithoutValidation(HeaderNames.Via, Via(context.Request.Protocol));
        }

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        return request;
    }

    private void CopyResponseHead(HttpContext context, HttpResponseMessage response)
    {
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.ReasonPhrase != ReasonPhrases.GetReasonPhrase((int)response.StatusCode))
        {
            context.Features.Get<IHttpResponseFeature>()!.ReasonPhrase = response.ReasonPhrase;
        }

        ForwardingHeaders.CopyResponseHeaders(response, context.Response);
        if (options.AddViaHeader)
        {
            context.Response.Headers.Append(HeaderNames.Via, Via($"HTTP/{response.Version}"));
        }
    }

    private async Task<ForwardingFailure?> UpgradeAsync(HttpContext context, HttpResponseMessage response, ActivityCancellationTokenSource requestActivity, Func<TunnelTelemetry> startTunnel)
    {
        if (ForwardingHeaders.TryGetValues(response.Headers, HeaderNames.Upgrade, out var upgrade))
        {
            context.Response.Headers.Upgrade = upgrade;
        }

        var destinationStream = await response.Content.ReadAsStreamAsync(requestActivity.Token).ConfigureAwait(false);
        await using var _ = destinationStream.ConfigureAwait(false);
        Stream clientStream;
        try
        {
            clientStream = await context.Features.Get<IHttpUpgradeFeature>()!.UpgradeAsync().ConfigureAwait(false);
            context.Features.Get<IHttpRequestTimeoutFeature>()?.DisableTimeout();
        }
        catch (Exception ex)
        {
            return new("UpgradeClient", DestinationUnavailable, ex);
        }

        await using var __ = clientStream.ConfigureAwait(false);

        // An upgraded connection is a tunnel: apply the tunnel idle timeout rather than the HTTP activity timeout.
        var telemetry = startTunnel();
        var activity = ActivityCancellationTokenSource.Rent(options.TunnelIdleTimeout, context.RequestAborted);
        var requestTask = StreamCopier.CopyAsync(clientStream, destinationStream, StreamCopier.UnknownLength, activity, autoFlush: false, activity.Token).AsTask();
        var responseTask = StreamCopier.CopyAsync(destinationStream, clientStream, StreamCopier.UnknownLength, activity, autoFlush: false, activity.Token).AsTask();
        var cancelled = false;
        try
        {
            var firstTask = await Task.WhenAny(requestTask, responseTask).ConfigureAwait(false);
            var requestFinishedFirst = firstTask == requestTask;
            var secondTask = requestFinishedFirst ? responseTask : requestTask;
            var (firstResult, firstException, _) = await firstTask.ConfigureAwait(false);
            if (firstResult != StreamCopyResult.Success)
            {
                cancelled = !activity.IsCancellationRequested;
                activity.Cancel();
                await secondTask.ConfigureAwait(false);
                return Failure(requestFinishedFirst, firstResult, firstException!);
            }

            // When the destination closes first, stop reading from the client.
            var cancelReads = !requestFinishedFirst && !secondTask.IsCompleted;
            if (cancelReads)
            {
                cancelled = true;
                activity.Cancel();
            }

            var (secondResult, secondException, _) = await secondTask.ConfigureAwait(false);
            return !cancelReads && secondResult != StreamCopyResult.Success ? Failure(!requestFinishedFirst, secondResult, secondException!) : null;
        }
        finally
        {
            // Both copies have completed here; CopyAsync reports failures instead of throwing.
            var idleTimedOut = !cancelled && activity.IsCancellationRequested && !activity.CancelledByLinkedToken;
            telemetry.Complete(BytesCopied(requestTask), BytesCopied(responseTask), idleTimedOut);
            activity.Return();
        }

        static long BytesCopied(Task<(StreamCopyResult Result, Exception? Error, long Bytes)> task) =>
            task.IsCompletedSuccessfully ? task.Result.Bytes : 0;

        static ForwardingFailure? Failure(bool request, StreamCopyResult result, Exception exception) => result switch
        {
            StreamCopyResult.Canceled => null,
            StreamCopyResult.InputError => new(request ? "UpgradeRequestClient" : "UpgradeResponseDestination", DestinationUnavailable, exception),
            _ => new(request ? "UpgradeRequestDestination" : "UpgradeResponseClient", DestinationUnavailable, exception),
        };
    }

    private static async Task<ForwardingFailure> HandleRequestFailureAsync(HttpContext context, StreamCopyHttpContent? requestContent, Exception exception, ActivityCancellationTokenSource activity)
    {
        var triedRequestBody = requestContent?.ConsumptionTask.IsCompleted == true;
        await CancelRequestBodyAsync(requestContent, activity).ConfigureAwait(false);
        if (activity.CancelledByLinkedToken)
        {
            return new("RequestCanceled", ClientAborted, exception);
        }

        if (triedRequestBody)
        {
            var (result, bodyException) = requestContent!.ConsumptionTask.Result;
            var timedOut = activity.IsCancellationRequested;
            if (result == StreamCopyResult.InputError)
            {
                var status = timedOut ? StatusCodes.Status408RequestTimeout
                    : bodyException is BadHttpRequestException badRequest ? badRequest.StatusCode
                    : StatusCodes.Status400BadRequest;
                return new("RequestBodyClient", new(status, ProxyErrorTypes.HttpRequestError, "Request body could not be read."), new AggregateException(bodyException!, exception));
            }

            if (result == StreamCopyResult.OutputError)
            {
                return new("RequestBodyDestination", timedOut ? DestinationTimeout : DestinationUnavailable, new AggregateException(bodyException!, exception));
            }
        }

        return exception switch
        {
            OperationCanceledException => new("RequestTimedOut", DestinationTimeout, exception),
            HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded } => new("ResponseHeaders", ResponseIncomplete, exception),
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => new("Tls", TlsProtocolError, exception),
            _ => new("Request", DestinationUnavailable, exception),
        };
    }

    private static async Task CancelRequestBodyAsync(StreamCopyHttpContent? requestContent, ActivityCancellationTokenSource activity)
    {
        if (requestContent is { InProgress: true })
        {
            activity.Cancel();
            await requestContent.ConsumptionTask.ConfigureAwait(false);
        }
    }

    private static bool ClearResponse(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            return false;
        }

        context.Response.Clear();
        return true;
    }

    /// <summary>Whether the request asks to upgrade to a WebSocket and the server can upgrade it.</summary>
    public static bool IsWebSocketUpgrade(HttpContext context) =>
        context.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest == true
        && string.Equals(context.Request.Headers.Upgrade.ToString(), WebSocket, StringComparison.OrdinalIgnoreCase);

    private static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

    private static bool HasBody(HttpRequest request)
    {
        if (request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>() is { } detection)
        {
            return detection.CanHaveBody;
        }

        if (request.Headers.TransferEncoding.Count == 1 && string.Equals(request.Headers.TransferEncoding.ToString(), "chunked", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return request.ContentLength > 0;
    }

    private string Via(string protocol)
    {
        var version = protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ? protocol[5..] : protocol;
        version = version switch
        {
            "2.0" => "2",
            "3.0" => "3",
            _ => version,
        };
        return $"{version} {options.ProxyName}";
    }
}
