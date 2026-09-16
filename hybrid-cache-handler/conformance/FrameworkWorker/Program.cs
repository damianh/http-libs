// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.IO.Pipes;
using System.Diagnostics;
using System.Net.Http;
using Conformance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

if (args.Length != 4) throw new ArgumentException("Usage: FrameworkWorker <pipe-name> <file-system> <content-root> <parent-pid>");
using var parent = Process.GetProcessById(int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
_ = Task.Run(() =>
{
    parent.WaitForExit();
    Environment.Exit(0);
});
var provenance = CacheRuntime.Verify("net472");
Console.WriteLine(provenance);
Console.WriteLine("NOTE: HybridCache 10.8.0 does not support/test net472 upstream; this harness deliberately exercises the real legacy DLL.");
var services = new ServiceCollection();
services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));
var fileSystem = bool.Parse(args[1]);
CacheRuntime.AddServices(services, fileSystem, args[2]);
using var provider = services.BuildServiceProvider();
using var transport = new FixtureTransportHandler();
using var handler = CacheRuntime.CreateHandler(transport, provider, fileSystem);
using var invoker = new HttpMessageInvoker(handler, false);
using var capacity = new SemaphoreSlim(32);
var active = new HashSet<Task>();

while (true)
{
    await capacity.WaitAsync();
    var pipe = new NamedPipeServerStream(args[0], PipeDirection.InOut, 32, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);
    try
    {
        await pipe.WaitForConnectionAsync();
    }
    catch (IOException ex) when ((ex.HResult & 0xffff) is 109 or 232 or 233)
    {
        Console.Error.WriteLine("Request cancelled before pipe accept: " + ex.Message);
        pipe.Dispose();
        capacity.Release();
        continue;
    }
    catch
    {
        pipe.Dispose();
        capacity.Release();
        throw;
    }
    var task = Serve(pipe);
    active.Add(task);
    // Completed tasks are observed here; a live worker cannot hide failed IPC behind HTTP error responses.
    foreach (var completed in active.Where(t => t.IsCompleted).ToArray())
    {
        await completed;
        active.Remove(completed);
    }
}

async Task Serve(NamedPipeServerStream pipe)
{
    using (pipe)
    using (var disconnected = new CancellationTokenSource())
    {
        Task? monitor = null;
        try
        {
            var frame = await WireProtocol.ReadFrame(pipe, disconnected.Token);
            if (frame[0] == WireProtocol.Hello)
            {
                if (WireProtocol.DecodeHello(frame) != "conformance-ipc-v1")
                    throw new InvalidDataException("Unsupported IPC protocol.");
                await WireProtocol.WriteFrame(pipe, WireProtocol.EncodeHello(provenance), disconnected.Token);
                return;
            }
            using var request = WireProtocol.DecodeRequest(frame);
            monitor = WatchDisconnect(pipe, disconnected);
            byte[] encoded;
            try
            {
                using var response = await invoker.SendAsync(request, disconnected.Token);
                encoded = await WireProtocol.EncodeResponse(response, disconnected.Token);
            }
            catch (Exception ex) when (!disconnected.IsCancellationRequested &&
                                       ex is HttpRequestException or IOException)
            {
                Console.Error.WriteLine($"Origin transport error for {request.Method} {request.RequestUri}: {ex.Message}");
                encoded = WireProtocol.EncodeOriginError(ex.ToString());
            }
            await WireProtocol.WriteFrame(pipe, encoded, disconnected.Token);
        }
        catch (EndOfStreamException)
        {
            Console.Error.WriteLine("Request cancelled: frontend disconnected while sending a frame.");
        }
        catch (Exception ex) when (disconnected.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException or IOException or ObjectDisposedException)
        {
            Console.Error.WriteLine("Request cancelled: frontend disconnected.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL conformance worker error: " + ex);
            Environment.Exit(1);
            throw;
        }
        finally
        {
            pipe.Dispose();
            if (monitor is not null) await monitor;
            capacity.Release();
        }
    }
}

static async Task WatchDisconnect(Stream pipe, CancellationTokenSource disconnected)
{
    try
    {
        var bytes = new byte[1];
        if (await pipe.ReadAsync(bytes, 0, 1) != 0)
            throw new InvalidDataException("Unexpected data after IPC request.");
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
        // Disposing the pipe after a completed response also ends this read.
    }
    finally
    {
        disconnected.Cancel();
    }
}
