// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http;

internal sealed class FixtureTransportHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HttpMessageInvoker> _transports = new();
    private bool _disposed;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var scope = Guid.Empty;
        var segments = request.RequestUri!.AbsolutePath.Split('/');
        if (segments.Length >= 3 && segments[1] is "config" or "test" or "state")
            Guid.TryParse(segments[2], out scope);

        HttpMessageInvoker transport;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FixtureTransportHandler));
            if (!_transports.TryGetValue(scope, out transport!))
            {
                if (_transports.Count >= 1024) throw new InvalidOperationException("Too many conformance fixture connection pools.");
                // Some fixtures deliberately misstate Content-Length. HttpWebRequest can otherwise reuse
                // the contaminated connection for an unrelated fixture and fail parsing its status line.
                transport = new HttpMessageInvoker(new HttpClientHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.None,
                    UseCookies = false
                });
                _transports.Add(scope, transport);
            }
        }
        return transport.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (var transport in _transports.Values) transport.Dispose();
                _transports.Clear();
            }
        }
        base.Dispose(disposing);
    }
}
