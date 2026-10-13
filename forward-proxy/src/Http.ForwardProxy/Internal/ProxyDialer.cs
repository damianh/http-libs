// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>
/// Resolves and connects to destinations. Every resolved address is checked against <see cref="DestinationPolicy"/>
/// and the socket connects to that exact checked address, so DNS rebinding cannot bypass the IP rules.
/// </summary>
internal sealed class ProxyDialer(DestinationPolicy policy, IOptions<ForwardProxyOptions> options, ILogger<ProxyDialer> logger)
{
    private readonly TimeSpan _connectTimeout = options.Value.ConnectTimeout;

    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/>, or throws <see cref="ProxyErrorException"/>.</summary>
    public async Task<Socket> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);

        var addresses = await ResolveAsync(host, timeout.Token, cancellationToken).ConfigureAwait(false);
        var allowed = Array.FindAll(addresses, policy.IsAddressAllowed);
        if (allowed.Length == 0)
        {
            Log.DestinationAddressProhibited(logger, host, port);
            throw new ProxyErrorException(new(StatusCodes.Status403Forbidden, ProxyErrorTypes.DestinationIpProhibited, "Destination address is prohibited."));
        }

        SocketException? lastError = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new ProxyErrorException(new(StatusCodes.Status504GatewayTimeout, ProxyErrorTypes.ConnectionTimeout, "Timed out connecting to destination."));
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new ProxyErrorException(lastError?.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => new(StatusCodes.Status502BadGateway, ProxyErrorTypes.ConnectionRefused, "Destination refused the connection."),
            SocketError.HostUnreachable or SocketError.NetworkUnreachable => new(StatusCodes.Status502BadGateway, ProxyErrorTypes.DestinationIpUnroutable, "Destination is unreachable."),
            SocketError.TimedOut => new(StatusCodes.Status504GatewayTimeout, ProxyErrorTypes.ConnectionTimeout, "Timed out connecting to destination."),
            _ => new(StatusCodes.Status502BadGateway, ProxyErrorTypes.DestinationUnavailable, "Destination is unavailable."),
        });
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken timeoutToken, CancellationToken requestToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, timeoutToken).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                throw new ProxyErrorException(new(StatusCodes.Status502BadGateway, ProxyErrorTypes.DnsError, "Destination host has no addresses."));
            }

            return addresses;
        }
        catch (SocketException)
        {
            throw new ProxyErrorException(new(StatusCodes.Status502BadGateway, ProxyErrorTypes.DnsError, "Destination host could not be resolved."));
        }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
        {
            throw new ProxyErrorException(new(StatusCodes.Status504GatewayTimeout, ProxyErrorTypes.DnsTimeout, "Timed out resolving destination host."));
        }
    }
}
