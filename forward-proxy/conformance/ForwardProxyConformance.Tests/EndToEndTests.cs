// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;

namespace DamianH.Http.ForwardProxy.Conformance;

/// <summary>Runs the whole corpus through the in-process proxy and built-in echo origin and gates it against the baseline.</summary>
public class EndToEndTests
{
    [Fact]
    public async Task Corpus_has_no_regressions_against_the_baseline()
    {
        ThreadPool.SetMinThreads(64, 64);
        var cancellationToken = TestContext.Current.CancellationToken;
        var log = Path.GetTempFileName();
        try
        {
            await using var origin = EchoOrigin.Start();
            await using var proxy = await ConformanceProxy.StartAsync(0, log, cancellationToken);
            var fixtures = FixtureCorpus.Load(Path.Combine(ConformancePaths.Directory, "fixtures.json")).Fixtures;

            var results = new FixtureDriver(new IPEndPoint(IPAddress.Loopback, ConformanceProxy.Port(proxy)), $"127.0.0.1:{origin.Port}", origin.ServerHeader)
                .Run(fixtures);

            using var output = new StringWriter();
            var code = ResultComparer.Compare(results, ResultComparer.LoadBaseline(Path.Combine(ConformancePaths.Directory, "expected-results.json")), output);
            TestContext.Current.TestOutputHelper?.WriteLine(output.ToString());
            code.ShouldBe(0, output.ToString());
        }
        finally
        {
            File.Delete(log);
        }
    }

    [Fact]
    public async Task Disposing_the_proxy_releases_its_log_file()
    {
        var log = Path.GetTempFileName();
        try
        {
            var proxy = await ConformanceProxy.StartAsync(0, log, TestContext.Current.CancellationToken);
            await proxy.DisposeAsync();

            using var exclusive = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            File.Delete(log);
        }
    }
}
