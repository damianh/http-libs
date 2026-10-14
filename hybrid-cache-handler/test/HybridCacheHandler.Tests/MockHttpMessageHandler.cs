// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage? _response;
    private readonly Func<HttpResponseMessage>? _responseFactory;
    private readonly Func<Task<HttpResponseMessage>>? _asyncResponseFactory;
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _requestResponseFactory;
    private int _requestCount;

    public int RequestCount => _requestCount;
    public HttpRequestMessage? LastRequest { get; private set; }

    public MockHttpMessageHandler(HttpResponseMessage? response = null)
        => _response = response;

    public MockHttpMessageHandler(Func<HttpResponseMessage> responseFactory)
        => _responseFactory = responseFactory;

    public MockHttpMessageHandler(Func<Task<HttpResponseMessage>> asyncResponseFactory)
        => _asyncResponseFactory = asyncResponseFactory;

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> requestResponseFactory)
        => _requestResponseFactory = requestResponseFactory;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        Ct ct)
    {
        Interlocked.Increment(ref _requestCount);
        LastRequest = request;

        HttpResponseMessage response;
        if (_requestResponseFactory != null)
        {
            response = await _requestResponseFactory(request);
        }
        else if (_asyncResponseFactory != null)
        {
            response = await _asyncResponseFactory();
        }
        else
        {
            response = _responseFactory?.Invoke() ?? _response ?? new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("default response")
            };
        }

        // Clone response for each request to avoid disposal issues
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var clonedResponse = new HttpResponseMessage(response.StatusCode)
        {
            Content = new ByteArrayContent(bytes),
            ReasonPhrase = response.ReasonPhrase,
            RequestMessage = request,
            Version = response.Version
        };

        foreach (var header in response.Headers)
        {
            clonedResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content headers
        foreach (var header in response.Content.Headers)
        {
            clonedResponse.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clonedResponse;
    }
}
