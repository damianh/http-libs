// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DamianH.Http.ForwardProxy.Conformance;

/// <summary>An origin that replies <c>200</c> with the exact bytes it received as the body.</summary>
internal interface IEchoOrigin : IAsyncDisposable
{
    int Port { get; }

    /// <summary>The <c>Server</c> field value that marks an echo response.</summary>
    string ServerHeader { get; }

    string Description { get; }
}

/// <summary>
/// In-process echo origin. Each read lasts until 100 ms pass without input; the bytes read are echoed back,
/// and the connection closes after an idle read. This matches HTTP Garden's <c>echo_server.py</c>.
/// </summary>
internal sealed class EchoOrigin : IEchoOrigin
{
    public const string Server = "conformance-echo";
    private static readonly TimeSpan ReadWindow = TimeSpan.FromMilliseconds(100);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;

    private EchoOrigin(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    public int Port { get; }

    public string ServerHeader => Server;

    public string Description => "built-in echo origin";

    public static EchoOrigin Start(int port = 0) => new(port);

    private async Task AcceptAsync()
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            // Blocking reads with a short timeout keep the read window independent of thread-pool load.
            new Thread(() => Echo(socket)) { IsBackground = true, Name = "echo-origin" }.Start();
        }
    }

    private static void Echo(Socket socket)
    {
        using (socket)
        {
            socket.NoDelay = true;
            socket.ReceiveTimeout = (int)ReadWindow.TotalMilliseconds;
            var buffer = new byte[64 * 1024];
            var received = Read(socket, buffer);
            while (true)
            {
                var head = Http1.Latin1.GetBytes(
                    $"HTTP/1.1 200 OK\r\nServer: {Server}\r\nContent-Length: {received.Length}\r\n\r\n");
                try
                {
                    socket.Send([.. head, .. received]);
                }
                catch (SocketException)
                {
                    return;
                }
                received = Read(socket, buffer);
                if (received.Length == 0)
                {
                    return;
                }
            }
        }
    }

    private static byte[] Read(Socket socket, byte[] buffer)
    {
        using var received = new MemoryStream();
        SocketReads.ReadUntilIdle(socket, received, buffer);
        return received.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stopping.Dispose();
    }
}

/// <summary>
/// HTTP Garden's <c>tools/echo_server.py</c> at a pinned commit, run as an independent third-party origin.
/// Garden is GPL-3.0, so it is cloned into the ignored <c>.http-garden</c> directory at run time, never vendored.
/// </summary>
internal sealed class GardenEchoOrigin : IEchoOrigin
{
    public const string Repository = "https://github.com/narfindustries/http-garden.git";
    public const string Commit = "b417e806c1b15e8ea0b9312f81a91fbdcbc7a83e";
    public const string Server = "echo-python";

    private readonly Process _process;

    private GardenEchoOrigin(Process process, int port)
    {
        _process = process;
        Port = port;
    }

    public int Port { get; }

    public string ServerHeader => Server;

    public string Description => $"HTTP Garden {Commit} echo_server.py";

    public static async Task<GardenEchoOrigin> StartAsync(
        string checkout, string python, int port, string logPath, CancellationToken cancellationToken)
    {
        await EnsureCheckoutAsync(checkout, cancellationToken);
        if (port == 0)
        {
            port = FreePort();
        }

        var start = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = checkout,
        };
        // -u: the echo server prints each received preamble; unbuffered output keeps the log useful.
        foreach (var argument in new[] { "-u", Path.Combine(checkout, "tools", "echo_server.py"), "127.0.0.1", port.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {python}.");
        var log = new StreamWriter(logPath) { AutoFlush = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (log) { log.WriteLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (log) { log.WriteLine(e.Data); } } };
        process.Exited += (_, _) => { lock (log) { log.Dispose(); } };
        process.EnableRaisingEvents = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var origin = new GardenEchoOrigin(process, port);
        try
        {
            await WaitForListenerAsync(process, port, logPath, cancellationToken);
        }
        catch
        {
            await origin.DisposeAsync();
            throw;
        }
        return origin;
    }

    private static async Task EnsureCheckoutAsync(string checkout, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(checkout, ".git")))
        {
            await ProcessRunner.RunAsync("git", ["clone", "--quiet", Repository, checkout], cancellationToken);
        }
        var head = await ProcessRunner.RunAsync("git", ["-C", checkout, "rev-parse", "HEAD"], cancellationToken);
        if (head != Commit)
        {
            await ProcessRunner.RunAsync("git", ["-C", checkout, "fetch", "--quiet", "origin", Commit], cancellationToken);
            await ProcessRunner.RunAsync("git", ["-C", checkout, "checkout", "--quiet", Commit], cancellationToken);
        }
        await ProcessRunner.RunAsync("git", ["-C", checkout, "diff", "--exit-code", "--quiet", Commit, "--", "tools"], cancellationToken);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForListenerAsync(Process process, int port, string logPath, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (true)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"HTTP Garden echo origin exited with code {process.ExitCode}; see {logPath}.");
            }
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
    }
}

internal static class ProcessRunner
{
    /// <summary>Runs a process to completion and returns its trimmed standard output.</summary>
    public static async Task<string> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {string.Join(' ', start.ArgumentList)} exited with code {process.ExitCode}.");
        }
        return output.Trim();
    }
}
