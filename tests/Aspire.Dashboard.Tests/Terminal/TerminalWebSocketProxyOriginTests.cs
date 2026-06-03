// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Terminal;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aspire.Dashboard.Tests.Terminal;

public class TerminalWebSocketProxyOriginTests
{
    [Theory]
    // Plain match.
    [InlineData("https", "dashboard.example.com", "https://dashboard.example.com", true)]
    // Explicit non-default port matches HostString that includes the port.
    [InlineData("https", "dashboard.example.com:8443", "https://dashboard.example.com:8443", true)]
    // Default port on origin (implicit :443) matches host that has no explicit port.
    [InlineData("https", "dashboard.example.com", "https://dashboard.example.com:443", true)]
    // Case-insensitive scheme + host.
    [InlineData("HTTPS", "Dashboard.Example.Com", "https://dashboard.example.com", true)]
    // Wrong scheme (http vs https) is rejected even when host matches.
    [InlineData("https", "dashboard.example.com", "http://dashboard.example.com", false)]
    // Different host is rejected (the CSWSH case).
    [InlineData("https", "dashboard.example.com", "https://evil.example.com", false)]
    // Same host, different port is rejected.
    [InlineData("https", "dashboard.example.com:8443", "https://dashboard.example.com:8444", false)]
    // Localhost ports must match exactly.
    [InlineData("http", "localhost:5101", "http://localhost:5101", true)]
    [InlineData("http", "localhost:5101", "http://localhost:5100", false)]
    public void IsAllowedOrigin_MatchesRequestSchemeAndHost(string scheme, string host, string origin, bool expected)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = HostString.FromUriComponent(host);
        ctx.Request.Headers.Origin = origin;

        var allowed = TerminalWebSocketProxy.IsAllowedOrigin(ctx, out var logged);

        Assert.Equal(expected, allowed);
        Assert.Equal(origin, logged);
    }

    [Fact]
    public void IsAllowedOrigin_MissingOrigin_Disallowed()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("dashboard.example.com");

        var allowed = TerminalWebSocketProxy.IsAllowedOrigin(ctx, out var logged);

        Assert.False(allowed);
        Assert.Equal("(none)", logged);
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("/relative")]
    [InlineData("https://")]
    public void IsAllowedOrigin_MalformedOrigin_Disallowed(string origin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("dashboard.example.com");
        ctx.Request.Headers.Origin = origin;

        var allowed = TerminalWebSocketProxy.IsAllowedOrigin(ctx, out _);

        Assert.False(allowed);
    }

    [Fact]
    public void IsAllowedOrigin_NullOrEmptyHost_Disallowed()
    {
        // Defensive: if Request.Host hasn't been populated (extremely unusual under
        // Kestrel, but possible in custom hosting setups) we should not accept any
        // cross-origin upgrade.
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString();
        ctx.Request.Headers.Origin = "https://dashboard.example.com";

        var allowed = TerminalWebSocketProxy.IsAllowedOrigin(ctx, out _);

        Assert.False(allowed);
    }
}
