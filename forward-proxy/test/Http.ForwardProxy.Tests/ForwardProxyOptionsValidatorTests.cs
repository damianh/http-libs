// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using DamianH.Http.ForwardProxy.Internal;
using Microsoft.AspNetCore.Authorization;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class ForwardProxyOptionsValidatorTests
{
    [Fact]
    public void DefaultsAreValid() => Validate(new ForwardProxyOptions()).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("egress")]
    [InlineData("proxy-1.example")]
    [InlineData("a!b")]
    [InlineData("a#$%&'*+-.^_`|~9")]
    public void AcceptsHttpTokenNames(string name) =>
        Validate(new ForwardProxyOptions { ProxyName = name }).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("proxy.example:8080")]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a,b")]
    [InlineData("1proxy")]
    [InlineData("*proxy")]
    [InlineData("pr\u00f6xy")]
    public void RejectsNamesThatAreNotHttpTokensStartingWithALetter(string name)
    {
        var result = Validate(new ForwardProxyOptions { ProxyName = name });
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(nameof(ForwardProxyOptions.ProxyName));
    }

    [Fact]
    public void RejectsBothAuthorizationPolicyAndName()
    {
        var result = Validate(new ForwardProxyOptions
        {
            AuthorizationPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(),
            AuthorizationPolicyName = "proxy",
        });
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(nameof(ForwardProxyOptions.AuthorizationPolicyName));
    }

    public static TheoryData<string> TimeoutNames =>
    [
        nameof(ForwardProxyOptions.ConnectTimeout),
        nameof(ForwardProxyOptions.TunnelIdleTimeout),
        nameof(ForwardProxyOptions.HttpActivityTimeout),
        nameof(ForwardProxyOptions.ConnectRequestHeadersTimeout),
    ];

    [Theory]
    [MemberData(nameof(TimeoutNames))]
    public void AcceptsTimeoutsUpToIntMaxValueMilliseconds(string timeout)
    {
        Validate(WithTimeout(timeout, TimeSpan.FromDays(1))).Succeeded.ShouldBeTrue();
        Validate(WithTimeout(timeout, TimeSpan.FromMilliseconds(int.MaxValue))).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(TimeoutNames))]
    public void RejectsOutOfRangeTimeouts(string timeout)
    {
        foreach (var value in new[] { TimeSpan.FromDays(30), TimeSpan.FromMilliseconds(int.MaxValue + 1L), TimeSpan.MaxValue, TimeSpan.Zero, Timeout.InfiniteTimeSpan })
        {
            var result = Validate(WithTimeout(timeout, value));
            result.Failed.ShouldBeTrue($"{timeout} = {value}");
            result.FailureMessage.ShouldContain(timeout);
        }
    }

    private static ForwardProxyOptions WithTimeout(string name, TimeSpan value)
    {
        var options = new ForwardProxyOptions();
        typeof(ForwardProxyOptions).GetProperty(name)!.SetValue(options, value);
        return options;
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(ForwardProxyOptions options) =>
        new ForwardProxyOptionsValidator().Validate(null, options);
}
