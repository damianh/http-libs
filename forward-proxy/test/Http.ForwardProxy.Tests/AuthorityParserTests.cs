// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using DamianH.Http.ForwardProxy.Internal;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class AuthorityParserTests
{
    [Theory]
    [InlineData("example.com:443", "example.com", 443)]
    [InlineData("Example.COM.:8443", "example.com", 8443)]
    [InlineData("my_host-1.internal:1", "my_host-1.internal", 1)]
    [InlineData("192.0.2.1:65535", "192.0.2.1", 65535)]
    [InlineData("[2001:db8::1]:443", "2001:db8::1", 443)]
    [InlineData("[::FFFF:192.0.2.1]:443", "::ffff:192.0.2.1", 443)]
    public void ParsesValidAuthorities(string authority, string expectedHost, int expectedPort)
    {
        AuthorityParser.TryParse(authority, out var host, out var port).ShouldBeTrue();
        host.ShouldBe(expectedHost);
        port.ShouldBe(expectedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("example.com:")]
    [InlineData(":443")]
    [InlineData("example.com:0")]
    [InlineData("example.com:65536")]
    [InlineData("example.com:+443")]
    [InlineData("example.com:443 ")]
    [InlineData("2001:db8::1:443")]
    [InlineData("[2001:db8::1]")]
    [InlineData("[not-an-ip]:443")]
    [InlineData("user@example.com:443")]
    [InlineData("exa mple.com:443")]
    [InlineData("example..com:443")]
    [InlineData("http://example.com:443")]
    [InlineData("example.com/path:443")]
    public void RejectsInvalidAuthorities(string authority) =>
        AuthorityParser.TryParse(authority, out _, out _).ShouldBeFalse();

    [Fact]
    public void RejectsOverlongLabelsAndNames()
    {
        AuthorityParser.TryParse(new string('a', 64) + ".com:443", out _, out _).ShouldBeFalse();
        AuthorityParser.TryParse(new string('a', 63) + ".com:443", out _, out _).ShouldBeTrue();
        var longName = string.Join('.', Enumerable.Repeat(new string('a', 50), 6));
        AuthorityParser.TryParse(longName + ":443", out _, out _).ShouldBeFalse();
    }
}
