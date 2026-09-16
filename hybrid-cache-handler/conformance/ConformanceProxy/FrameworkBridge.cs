// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.IO.Pipes;
using Conformance;

internal sealed class FrameworkBridge : HttpMessageHandler
{
    private readonly string _pipeName = "http-cache-conformance-" + Guid.NewGuid().ToString("N");
    private readonly Process _worker;
    private readonly Task _stdout;
    private readonly Task _stderr;
    private string? _failure;
    internal string Provenance { get; private set; } = "";
    internal int WorkerProcessId => _worker.Id;
    internal bool Healthy => _failure is null && !_worker.HasExited;

    internal FrameworkBridge(string workerPath, bool fileSystem, string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("net472 requires Windows.");
        var start = new ProcessStartInfo(Path.GetFullPath(workerPath))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(_pipeName);
        start.ArgumentList.Add(fileSystem.ToString());
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _worker = Process.Start(start) ?? throw new InvalidOperationException("Unable to start .NET Framework worker.");
        _stdout = Pump(_worker.StandardOutput, Console.Out);
        _stderr = Pump(_worker.StandardError, Console.Error);
    }

    internal async Task StartAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var pipe = await Connect(timeout.Token);
            await WireProtocol.WriteFrame(pipe, WireProtocol.EncodeHello("conformance-ipc-v1"), timeout.Token);
            Provenance = WireProtocol.DecodeHello(await WireProtocol.ReadFrame(pipe, timeout.Token));
            if (!Provenance.StartsWith("net472" + Environment.NewLine + ".NET Framework", StringComparison.Ordinal))
                throw new InvalidDataException("Worker did not report the real .NET Framework runtime.");
        }
        catch (Exception ex)
        {
            _failure = ex.ToString();
            throw;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Healthy) throw new InvalidOperationException("Framework worker failed: " + _failure);
        try
        {
            var encodedRequest = await WireProtocol.EncodeRequest(request, cancellationToken);
            using var pipe = await Connect(cancellationToken);
            using var registration = cancellationToken.Register(pipe.Dispose);
            await WireProtocol.WriteFrame(pipe, encodedRequest, cancellationToken);
            var frame = await WireProtocol.ReadFrame(pipe, cancellationToken);
            if (frame[0] == WireProtocol.OriginError) throw new OriginTransportException(WireProtocol.DecodeOriginError(frame));
            var response = WireProtocol.DecodeResponse(frame);
            response.RequestMessage = request;
            return response;
        }
        catch (OriginTransportException)
        {
            // Deliberate origin disconnect fixtures must reach YARP exactly like direct transport failures.
            throw;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException or IOException or ObjectDisposedException)
        {
            throw new OperationCanceledException("Conformance request cancelled.", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            _failure = ex.ToString();
            Console.Error.WriteLine("FATAL conformance bridge error: " + ex);
            throw;
        }
    }

    private sealed class OriginTransportException(string message) : HttpRequestException(message);

    private async Task<NamedPipeClientStream> Connect(CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(30_000, token);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static async Task Pump(StreamReader source, TextWriter destination)
    {
        string? line;
        while ((line = await source.ReadLineAsync()) is not null) await destination.WriteLineAsync(line);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!_worker.HasExited) _worker.Kill(entireProcessTree: true);
            _worker.WaitForExit();
            Task.WhenAll(_stdout, _stderr).GetAwaiter().GetResult();
            _worker.Dispose();
        }
        base.Dispose(disposing);
    }
}
