// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class WebSocketOriginValidatorTests
{
    [Theory]
    [InlineData("https", "dashboard.example.com", "https://dashboard.example.com", true)]
    [InlineData("https", "dashboard.example.com:8443", "https://dashboard.example.com:8443", true)]
    [InlineData("https", "dashboard.example.com", "https://dashboard.example.com:443", true)]
    [InlineData("https", "dashboard.example.com:443", "https://dashboard.example.com", true)]
    [InlineData("http", "dashboard.example.com:80", "http://dashboard.example.com", true)]
    [InlineData("HTTPS", "Dashboard.Example.Com", "https://dashboard.example.com", true)]
    [InlineData("http", "localhost:5101", "http://localhost:5101", true)]
    [InlineData("https", "dashboard.example.com", "http://dashboard.example.com", false)]
    [InlineData("https", "dashboard.example.com", "https://evil.example.com", false)]
    [InlineData("https", "dashboard.example.com:8443", "https://dashboard.example.com:8444", false)]
    [InlineData("http", "localhost:5101", "http://localhost:5100", false)]
    [InlineData("https", "dashboard.example.com", "not-a-uri", false)]
    [InlineData("https", "dashboard.example.com", "/relative", false)]
    [InlineData("https", "dashboard.example.com", null, false)]
    [InlineData("https", null, "https://dashboard.example.com", false)]
    public void IsSameOrigin_MatchesRequestSchemeAndHost(string scheme, string? host, string? origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;

        if (host is not null)
        {
            context.Request.Host = HostString.FromUriComponent(host);
        }

        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        var result = WebSocketOriginValidator.IsSameOrigin(context, out _);

        Assert.Equal(expected, result);
    }
}