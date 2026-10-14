// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DamianH.Http.ForwardProxy.Internal.Forwarding;
using DamianH.Http.ForwardProxy.Internal.Interception;
using Microsoft.Extensions.Options;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Protocol-independent proxy request handling.</summary>
internal sealed class ForwardProxyHandler : IDisposable
{
    private static readonly ProxyError TunnelLimitReached = new(StatusCodes.Status503ServiceUnavailable, ProxyErrorTypes.ConnectionLimitReached, "Too many concurrent tunnels.");

    private readonly ForwardProxyOptions _options;
    private readonly DestinationPolicy _policy;
    private readonly ProxyDialer _dialer;
    private readonly ProxyAuthorizer _authorizer;
    private readonly ForwardProxyMetrics _metrics;
    private readonly TunnelLimiter _limiter;
    private readonly HttpForwarder _forwarder;
    private readonly TlsInterceptor? _interceptor;
    private readonly ILogger _logger;

    public ForwardProxyHandler(
        IOptions<ForwardProxyOptions> options,
        DestinationPolicy policy,
        ProxyDialer dialer,
        ProxyAuthorizer authorizer,
        ForwardProxyMetrics metrics,
        IServiceProvider services,
        ILogger<ForwardProxyHandler> logger)
    {
        _options = options.Value;
        _policy = policy;
        _dialer = dialer;
        _authorizer = authorizer;
        _metrics = metrics;
        _limiter = new TunnelLimiter(_options.MaxConcurrentTunnels);
        _logger = logger;
        _interceptor = services.GetService<TlsInterceptor>();
        var destinationCertificateValidation = services.GetService<IOptions<ForwardProxyTlsInterceptionOptions>>()?.Value.DestinationCertificateValidation;
        _forwarder = new HttpForwarder(CreateHttpClient(dialer, _options, destinationCertificateValidation), _options);
        if (_authorizer.IsAnonymous)
        {
            Log.AnonymousProxy(logger);
        }
    }

    public ForwardProxyOptions Options => _options;

    /// <summary>Closes pooled origin connections held by the outbound HTTP client.</summary>
    public void Dispose() => _forwarder.Dispose();

    /// <summary>
    /// Authorizes and opens a tunnel to an authority-form <paramref name="target"/>. On failure the error is written
    /// to <paramref name="context"/>'s response (status and headers, not yet sent) and <see langword="null"/> is returned.
    /// </summary>
    public async Task<Tunnel?> OpenTunnelAsync(HttpContext context, string target)
    {
        if (!AuthorityParser.TryParse(target, out var host, out var port))
        {
            _metrics.Request(ForwardProxyRequestKind.Connect, Outcomes.Denied);
            SetError(context, ProxyError.BadTarget("Invalid CONNECT target."));
            return null;
        }

        var destination = new ForwardProxyDestination(host, port, ForwardProxyRequestKind.Connect);
        if (!await AdmitAsync(context, destination).ConfigureAwait(false))
        {
            return null;
        }

        if (!_limiter.TryAcquire())
        {
            Reject(context, destination, TunnelLimitReached, Outcomes.Error);
            return null;
        }

        try
        {
            if (_interceptor is not null
                && context.RequestServices.GetService<IForwardProxyInterceptionSelector>() is { } selector
                && await ShouldInterceptAsync(selector, context, destination).ConfigureAwait(false))
            {
                _metrics.Request(ForwardProxyRequestKind.Connect, Outcomes.Intercepted);
                Log.TunnelIntercepted(_logger, host, port);
                return new Tunnel(this, destination, new InterceptionFeature(destination, context.User));
            }

            var socket = await _dialer.ConnectAsync(host, port, context.RequestAborted).ConfigureAwait(false);
            _metrics.Request(ForwardProxyRequestKind.Connect, Outcomes.Allowed);
            Log.TunnelEstablished(_logger, host, port);
            return new Tunnel(this, destination, socket);
        }
        catch (ProxyErrorException ex)
        {
            _limiter.Release();
            Reject(context, destination, ex.Error, Outcomes.Error);
            return null;
        }
        catch
        {
            _limiter.Release();
            throw;
        }
    }

    private async ValueTask<bool> ShouldInterceptAsync(
        IForwardProxyInterceptionSelector selector,
        HttpContext context,
        ForwardProxyDestination destination)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(_options.ConnectTimeout);
        try
        {
            return await selector.ShouldInterceptAsync(context, destination, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            throw new ProxyErrorException(new(
                StatusCodes.Status504GatewayTimeout,
                ProxyErrorTypes.ConnectionTimeout,
                "Timed out selecting whether to intercept the tunnel."));
        }
    }

    /// <summary>Authorizes and forwards an absolute-form <c>http://</c> request.</summary>
    public async Task ForwardHttpAsync(HttpContext context, Uri target)
    {
        if (!string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            _metrics.Request(ForwardProxyRequestKind.Http, Outcomes.Denied);
            SetError(context, ProxyError.BadTarget("Only http:// targets can be forwarded; use CONNECT for https://."));
            return;
        }

        if (target.UserInfo.Length > 0 || !AuthorityParser.TryNormalizeHost(target.IdnHost, out var host) && !TryNormalizeIPv6(target, out host))
        {
            _metrics.Request(ForwardProxyRequestKind.Http, Outcomes.Denied);
            SetError(context, ProxyError.BadTarget("Invalid request target."));
            return;
        }

        var destination = new ForwardProxyDestination(host, target.Port, ForwardProxyRequestKind.Http);
        if (!await AdmitAsync(context, destination).ConfigureAwait(false))
        {
            return;
        }

        await ForwardAsync(context, target, destination, intercepted: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Forwards a request received inside an intercepted tunnel to the tunnel's destination. The tunnel was authorized
    /// when it was opened; the destination's addresses are checked again when connecting.
    /// </summary>
    public Task ForwardInterceptedAsync(HttpContext context, ForwardProxyDestination destination, Uri target) =>
        ForwardAsync(context, target, destination, intercepted: true);

    /// <summary>Rejects a request received inside an intercepted tunnel.</summary>
    public void RejectIntercepted(HttpContext context, ForwardProxyDestination destination, ProxyError error)
    {
        _metrics.InterceptedRequest(Outcomes.Denied);
        Log.InterceptedRequestRejected(_logger, context.Request.Method, destination.Host, destination.Port, error.StatusCode, error.ErrorType, error.Reason);
        SetError(context, error);
    }

    private async Task ForwardAsync(HttpContext context, Uri target, ForwardProxyDestination destination, bool intercepted)
    {
        // An upgraded connection is a tunnel, so it counts against the same concurrency limit as CONNECT. Inside an
        // intercepted tunnel it is already counted.
        var isUpgrade = HttpForwarder.IsWebSocketUpgrade(context);
        var limited = isUpgrade && !intercepted;
        if (limited && !_limiter.TryAcquire())
        {
            Reject(context, destination, TunnelLimitReached, Outcomes.Error);
            return;
        }

        ForwardingFailure? failure;
        var tunnelStarted = false;
        try
        {
            failure = await _forwarder.SendAsync(context, target, destination, isUpgrade ? StartTunnel : null).ConfigureAwait(false);
        }
        finally
        {
            if (limited)
            {
                _limiter.Release();
            }
        }

        // Like CONNECT, an upgraded connection is counted when its tunnel opens; later relay failures are not errors.
        if (!tunnelStarted)
        {
            RecordRequest(failure is null ? Outcomes.Allowed : Outcomes.Error);
        }

        if (failure is null)
        {
            return;
        }

        Log.ForwardFailed(_logger, failure.Exception, context.Request.Method, destination.Host, destination.Port, failure.Stage);
        if (!context.Response.HasStarted)
        {
            SetError(context, FindProxyError(failure.Exception) ?? failure.Error);
        }

        TunnelTelemetry StartTunnel()
        {
            tunnelStarted = true;
            RecordRequest(Outcomes.Allowed);
            return new TunnelTelemetry(_metrics, _logger, destination, recordMetrics: !intercepted);
        }

        void RecordRequest(string outcome)
        {
            if (intercepted)
            {
                _metrics.InterceptedRequest(outcome);
            }
            else
            {
                _metrics.Request(ForwardProxyRequestKind.Http, outcome);
            }
        }
    }

    /// <summary>Writes a proxy error's status and <c>Proxy-Status</c> header to the response.</summary>
    public void SetError(HttpContext context, ProxyError error)
    {
        context.Response.StatusCode = error.StatusCode;
        if (_options.AddProxyStatusHeader)
        {
            context.Response.Headers["Proxy-Status"] = $"{_options.ProxyName}; error={error.ErrorType}";
        }
    }

    private async Task<bool> AdmitAsync(HttpContext context, ForwardProxyDestination destination)
    {
        if (!await _authorizer.AuthorizeAsync(context, destination).ConfigureAwait(false))
        {
            _metrics.Request(destination.Kind, Outcomes.Unauthorized);
            Log.RequestNotAuthorized(_logger, destination.Kind, destination.Host, destination.Port, context.Response.StatusCode);
            return false;
        }

        if (_policy.CheckDestination(destination) is { } denial)
        {
            Reject(context, destination, denial, Outcomes.Denied);
            return false;
        }

        // Resolved per request so filters can be singleton, scoped or transient.
        foreach (var filter in context.RequestServices.GetServices<IForwardProxyDestinationFilter>())
        {
            if (!await filter.IsAllowedAsync(context, destination, context.RequestAborted).ConfigureAwait(false))
            {
                Reject(context, destination, ProxyError.Denied("Destination denied by filter."), Outcomes.Denied);
                return false;
            }
        }

        return true;
    }

    private void Reject(HttpContext context, ForwardProxyDestination destination, ProxyError error, string outcome)
    {
        _metrics.Request(destination.Kind, outcome);
        Log.RequestRejected(_logger, destination.Kind, destination.Host, destination.Port, error.StatusCode, error.ErrorType, error.Reason);
        SetError(context, error);
    }

    private static bool TryNormalizeIPv6(Uri target, out string host)
    {
        host = "";
        if (target.HostNameType != UriHostNameType.IPv6 || !IPAddress.TryParse(target.DnsSafeHost, out var address))
        {
            return false;
        }

        host = address.ToString();
        return true;
    }

    private static ProxyError? FindProxyError(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProxyErrorException proxyError)
            {
                return proxyError.Error;
            }
        }

        return null;
    }

    private static HttpMessageInvoker CreateHttpClient(ProxyDialer dialer, ForwardProxyOptions options, RemoteCertificateValidationCallback? destinationCertificateValidation) =>
        new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
            ConnectTimeout = options.ConnectTimeout,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = await dialer.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                // Only intercepted requests use https://. Throwing lets the failure be reported as tls_certificate_error.
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    (destinationCertificateValidation?.Invoke(sender, certificate, chain, errors) ?? errors == SslPolicyErrors.None)
                        ? true
                        : throw new ProxyErrorException(new(StatusCodes.Status502BadGateway, ProxyErrorTypes.TlsCertificateError, $"The destination's certificate is not trusted: {errors}.")),
            },
        }, disposeHandler: true);

    /// <summary>
    /// An open tunnel: an upstream connection, or an intercepted tunnel served by the interception endpoint. Must be run
    /// or disposed exactly once.
    /// </summary>
    internal sealed class Tunnel : IDisposable
    {
        private readonly ForwardProxyHandler _owner;
        private readonly ForwardProxyDestination _destination;
        private readonly Socket? _socket;
        private readonly InterceptionFeature? _interception;
        private int _disposed;

        public Tunnel(ForwardProxyHandler owner, ForwardProxyDestination destination, Socket socket)
        {
            _owner = owner;
            _destination = destination;
            _socket = socket;
        }

        public Tunnel(ForwardProxyHandler owner, ForwardProxyDestination destination, InterceptionFeature interception)
        {
            _owner = owner;
            _destination = destination;
            _interception = interception;
        }

        /// <summary>Relays or serves the tunnel until either side closes it or it is idle for the tunnel idle timeout.</summary>
        /// <param name="clientInput">Data from the client.</param>
        /// <param name="clientOutput">Data to the client.</param>
        /// <param name="cancellationToken">Ends a relayed tunnel.</param>
        /// <param name="context">The <c>CONNECT</c> request.</param>
        /// <param name="clientClosed">Signals that the client connection or stream carrying the tunnel has closed.</param>
        /// <param name="abortClient">Aborts the client connection or stream carrying the tunnel.</param>
        public async Task RunAsync(
            PipeReader clientInput,
            PipeWriter clientOutput,
            CancellationToken cancellationToken,
            HttpContext context,
            CancellationToken clientClosed,
            Action abortClient)
        {
            var telemetry = new TunnelTelemetry(_owner._metrics, _owner._logger, _destination);
            var result = default(TunnelPump.Result);
            try
            {
                if (_interception is not null)
                {
                    result = await _owner._interceptor!.RunAsync(
                        context.Connection.Id,
                        _interception,
                        clientInput,
                        clientOutput,
                        LocalEndPoint(context.Connection),
                        RemoteEndPoint(context.Connection),
                        abortClient,
                        clientClosed).ConfigureAwait(false);
                }
                else
                {
                    result = await TunnelPump.RunAsync(clientInput, clientOutput, _socket!, _owner._options.TunnelIdleTimeout, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                telemetry.Complete(result.BytesUpstream, result.BytesDownstream, result.IdleTimedOut);
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _socket?.Dispose();
                _owner._limiter.Release();
            }
        }

        private static IPEndPoint? LocalEndPoint(ConnectionInfo connection) =>
            connection.LocalIpAddress is { } address ? new(address, connection.LocalPort) : null;

        private static IPEndPoint? RemoteEndPoint(ConnectionInfo connection) =>
            connection.RemoteIpAddress is { } address ? new(address, connection.RemotePort) : null;
    }
}
