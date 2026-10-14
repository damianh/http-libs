// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using EstInterception.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class EstInterceptionConfigurationTests
{
    [Theory]
    [InlineData("Interception:AllowedHosts")]
    [InlineData("Interception:Hosts")]
    public void ReadsScalarHostLists(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = "example.org, HTTPBIN.org, ,example.org" }).Build();

        var hosts = configuration.GetInterceptionHosts(key);

        hosts.Count.ShouldBe(2);
        hosts.Contains("EXAMPLE.org").ShouldBeTrue();
        hosts.Contains("httpbin.org").ShouldBeTrue();
    }

    [Theory]
    [InlineData("Interception:AllowedHosts")]
    [InlineData("Interception:Hosts")]
    public void ReadsIndexedHostLists(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"{key}:0"] = " example.org ", [$"{key}:1"] = "HTTPBIN.org" }).Build();

        var hosts = configuration.GetInterceptionHosts(key);

        hosts.Count.ShouldBe(2);
        hosts.Contains("example.org").ShouldBeTrue();
        hosts.Contains("httpbin.org").ShouldBeTrue();
    }

    [Fact]
    public void DefaultsOnlyWhenHostConfigurationIsMissing()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Interception:Hosts"] = "" }).Build();

        configuration.GetInterceptionHosts("Interception:AllowedHosts").ShouldBe(["httpbin.org"]);
        configuration.GetInterceptionHosts("Interception:Hosts").ShouldBeEmpty();
    }
}
