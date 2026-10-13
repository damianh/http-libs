// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Protocol-independent proxy request handling.</summary>
internal sealed class ForwardProxyHandler
{
    private readonly ForwardProxyOptions _options;
    private readonly DestinationPolicy _policy;
    private readonly ProxyDialer _dialer;
    private readonly ProxyAuthorizer _authorizer;
    private readonly IForwardProxyDestinationFilter[] _filters;
    private readonly ForwardProxyMetrics _metrics;
    private readonly TunnelLimiter _limiter;
    private readonly IHttpForwarder _forwarder;
    private readonly HttpMessageInvoker _httpClient;
    private readonly ForwarderRequestConfig _requestConfig;
    private readonly ILogger _logger;

    public ForwardProxyHandler(
        IOptions<ForwardProxyOptions> options,
        DestinationPolicy policy,
        ProxyDialer dialer,
        ProxyAuthorizer authorizer,
        IEnumerable<IForwardProxyDestinationFilter> filters,
        ForwardProxyMetrics metrics,
        IHttpForwarder forwarder,
        ILogger<ForwardProxyHandler> logger)
    {
        _options = options.Value;
        _policy = policy;
        _dialer = dialer;
        _authorizer = authorizer;
        _filters = [.. filters];
        _metrics = metrics;
        _limiter = new TunnelLimiter(_options.MaxConcurrentTunnels);
        _forwarder = forwarder;
        _logger = logger;
        _httpClient = CreateHttpClient(dialer, _options);
        _requestConfig = new ForwarderRequestConfig
        {
            ActivityTimeout = _options.HttpActivityTimeout,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            AllowResponseBuffering = false,
        };
        if (_authorizer.IsAnonymous)
        {
            Log.AnonymousProxy(logger);
        }
    }

    public ForwardProxyOptions Options => _options;

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
            Reject(context, destination, new(StatusCodes.Status503ServiceUnavailable, ProxyErrorTypes.ConnectionLimitReached, "Too many concurrent tunnels."), Outcomes.Error);
            return null;
        }

        try
        {
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

    /// <summary>Authorizes and forwards an absolute-form request through YARP.</summary>
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

        _metrics.Request(ForwardProxyRequestKind.Http, Outcomes.Allowed);
        var transformer = new ForwardProxyTransformer(target, _options);
        var error = await _forwarder.SendAsync(context, $"{target.Scheme}://{target.Authority}", _httpClient, _requestConfig, transformer).ConfigureAwait(false);
        if (error == ForwarderError.None)
        {
            return;
        }

        var exception = context.Features.Get<IForwarderErrorFeature>()?.Exception;
        Log.ForwardFailed(_logger, exception, context.Request.Method, host, target.Port, error.ToString());
        if (context.Response.HasStarted)
        {
            return;
        }

        var proxyError = FindProxyError(exception) ?? error switch
        {
            ForwarderError.RequestTimedOut => new(StatusCodes.Status504GatewayTimeout, ProxyErrorTypes.ConnectionTimeout, "Destination timed out."),
            ForwarderError.Request => new(StatusCodes.Status502BadGateway, ProxyErrorTypes.DestinationUnavailable, "Destination is unavailable."),
            ForwarderError.ResponseHeaders => new(StatusCodes.Status502BadGateway, ProxyErrorTypes.HttpResponseIncomplete, "Destination response was incomplete."),
            _ => new ProxyError(context.Response.StatusCode >= 400 ? context.Response.StatusCode : StatusCodes.Status502BadGateway, ProxyErrorTypes.ProxyInternalError, "Proxy error."),
        };
        SetError(context, proxyError);
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

        foreach (var filter in _filters)
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

    private static HttpMessageInvoker CreateHttpClient(ProxyDialer dialer, ForwardProxyOptions options) =>
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
        }, disposeHandler: true);

    /// <summary>An open upstream connection. Must be run or disposed exactly once.</summary>
    internal sealed class Tunnel(ForwardProxyHandler owner, ForwardProxyDestination destination, Socket socket) : IDisposable
    {
        private int _disposed;

        public async Task RunAsync(PipeReader clientInput, PipeWriter clientOutput, CancellationToken cancellationToken)
        {
            using var activity = ForwardProxyMetrics.ActivitySource.StartActivity("ForwardProxy.Tunnel", ActivityKind.Internal);
            activity?.SetTag("server.address", destination.Host);
            activity?.SetTag("server.port", destination.Port);
            owner._metrics.TunnelOpened();
            var started = Stopwatch.GetTimestamp();
            var result = default(TunnelPump.Result);
            try
            {
                result = await TunnelPump.RunAsync(clientInput, clientOutput, socket, owner._options.TunnelIdleTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                owner._metrics.TunnelClosed(elapsed.TotalSeconds, result.BytesUpstream, result.BytesDownstream);
                if (result.IdleTimedOut)
                {
                    Log.TunnelIdleTimeout(owner._logger, destination.Host, destination.Port);
                }

                Log.TunnelClosed(owner._logger, destination.Host, destination.Port, elapsed.TotalMilliseconds, result.BytesUpstream, result.BytesDownstream);
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                socket.Dispose();
                owner._limiter.Release();
            }
        }
    }
}
