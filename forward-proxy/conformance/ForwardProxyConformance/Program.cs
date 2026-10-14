// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Net;
using System.Text.Json;
using DamianH.Http.ForwardProxy.Conformance;

const string Usage = """
    Usage: dotnet run --project forward-proxy/conformance/ForwardProxyConformance -c Release -- [options]

      --test-id <id>       Run one fixture (diagnostic; does not gate).
      --update             Rewrite expected-results.json from a full run.
      --garden-origin      Use the pinned HTTP Garden echo_server.py as the origin (needs Git and Python 3.12+).
      --python <path>      Python for --garden-origin (default: $PYTHON, else python3, or python on Windows).
      --serve              Keep the proxy and origin running until Ctrl+C, e.g. for the HTTP Garden REPL.
      --origin-port <n>    Origin port (default: automatic).
      --proxy-port <n>     Proxy port (default: automatic).

    Exit codes: 0 = no regressions, 1 = regressions, missing fixtures or run failure, 2 = usage error.
    """;

string? testId = null;
bool update = false, garden = false, serve = false;
int originPort = 0, proxyPort = 0;
var python = Environment.GetEnvironmentVariable("PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3");
try
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--test-id": testId = Value(ref i); break;
            case "--update": update = true; break;
            case "--garden-origin": garden = true; break;
            case "--python": python = Value(ref i); break;
            case "--serve": serve = true; break;
            case "--origin-port": originPort = Port(ref i); break;
            case "--proxy-port": proxyPort = Port(ref i); break;
            case "-h" or "--help": Console.WriteLine(Usage); return 0;
            default: throw new ArgumentException($"Unknown option {args[i]}.");
        }
    }
    if (update && testId is not null)
    {
        throw new ArgumentException("Only full runs may update the baseline.");
    }
    if (originPort != 0 && originPort == proxyPort)
    {
        throw new ArgumentException("Origin and proxy ports must differ.");
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}

var directory = ConformancePaths.Directory;
var key = (garden ? "garden-" : "") + (testId is null ? "full" : $"diagnostic-{string.Concat(testId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'))}");
var resultsPath = Path.Combine(directory, $"results-{key}.json");
var baselinePath = Path.Combine(directory, "expected-results.json");
File.Delete(resultsPath);

var fixtures = FixtureCorpus.Load(Path.Combine(directory, "fixtures.json")).Fixtures;
if (testId is not null)
{
    fixtures = [.. fixtures.Where(f => f.Id == testId)];
    if (fixtures.Count == 0)
    {
        Console.Error.WriteLine($"Unknown fixture {testId}.");
        return 2;
    }
}

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

// Blocking socket reads in the driver must not starve Kestrel of thread-pool threads.
ThreadPool.SetMinThreads(64, 64);

try
{
    await using IEchoOrigin origin = garden
        ? await GardenEchoOrigin.StartAsync(Path.Combine(directory, ".http-garden"), python, originPort, Path.Combine(directory, $"{key}-origin.log"), stopping.Token)
        : EchoOrigin.Start(originPort);
    await using var proxy = await ConformanceProxy.StartAsync(proxyPort, Path.Combine(directory, $"{key}-proxy.log"), stopping.Token);
    var proxyEndPoint = new IPEndPoint(IPAddress.Loopback, ConformanceProxy.Port(proxy));
    var originAuthority = $"127.0.0.1:{origin.Port}";
    Console.WriteLine($"Origin: {origin.Description} on {originAuthority}. Forward proxy on {proxyEndPoint}.");

    if (serve)
    {
        Console.WriteLine($"""
            Serving until Ctrl+C. To use the HTTP Garden REPL, add to .http-garden/external-services.yml:

            dotnet_forward_proxy:
              x-props:
                role: transducer
                address: 127.0.0.1
                port: {proxyEndPoint.Port}

            Payloads must use absolute-form, e.g. GET http://{originAuthority}/ HTTP/1.1
            """);
        await Task.Delay(Timeout.Infinite, stopping.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        return 0;
    }

    var results = new FixtureDriver(proxyEndPoint, originAuthority, origin.ServerHeader).Run(fixtures);
    File.WriteAllText(resultsPath, JsonSerializer.Serialize(results, ConformanceJson.Options) + "\n");
    Console.WriteLine($"Ran {results.Count} fixtures: {results.Values.Count(r => r.Outcome == Outcomes.Pass)} passing. Results: {resultsPath}");

    if (testId is not null)
    {
        var result = results[testId];
        Console.WriteLine($"{testId}: {result.Outcome}{(result.Reasons.Count > 0 ? ": " + string.Join("; ", result.Reasons) : "")}");
        Console.WriteLine("Single-fixture runs do not gate.");
        return 0;
    }
    if (update)
    {
        ResultComparer.WriteBaseline(results, baselinePath);
        Console.WriteLine($"Baseline written to {baselinePath}. Review the diff before committing.");
        return 0;
    }
    return ResultComparer.Compare(results, ResultComparer.LoadBaseline(baselinePath), Console.Out);
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    return 130;
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

string Value(ref int i) =>
    i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} requires a value.");

int Port(ref int i) =>
    int.TryParse(Value(ref i), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port <= 65535
        ? port
        : throw new ArgumentException("Ports must be 0 (automatic) or 1..65535.");

namespace DamianH.Http.ForwardProxy.Conformance
{
    internal static class ConformancePaths
    {
        /// <summary>The <c>forward-proxy/conformance</c> source directory, recorded at build time.</summary>
        public static string Directory { get; } =
            typeof(ConformancePaths).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>()
                .Single(a => a.Key == "ConformanceDirectory").Value!;
    }
}
