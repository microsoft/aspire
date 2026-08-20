// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class SecurityOptionsBindingTests
{
    [Fact]
    public void BindCertificateAuthenticationOptions_PreservesScalarOverrides()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Options:ClaimsIssuer"] = "dashboard-certificates",
            ["Options:ForwardAuthenticate"] = "authenticate",
            ["Options:ForwardChallenge"] = "challenge",
            ["Options:ForwardDefault"] = "default",
            ["Options:ForwardForbid"] = "forbid",
            ["Options:ForwardSignIn"] = "sign-in",
            ["Options:ForwardSignOut"] = "sign-out"
        });
        var options = new CertificateAuthenticationOptions();

        DashboardWebApplication.BindCertificateAuthenticationOptions(
            configuration.GetSection("Options"),
            options);

        Assert.Equal("dashboard-certificates", options.ClaimsIssuer);
        Assert.Equal("authenticate", options.ForwardAuthenticate);
        Assert.Equal("challenge", options.ForwardChallenge);
        Assert.Equal("default", options.ForwardDefault);
        Assert.Equal("forbid", options.ForwardForbid);
        Assert.Equal("sign-in", options.ForwardSignIn);
        Assert.Equal("sign-out", options.ForwardSignOut);
    }

    [Fact]
    public void BindSslClientAuthenticationOptions_PreservesTargetHost()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Options:TargetHost"] = "resources.internal"
        });
        var options = new SslClientAuthenticationOptions();

        DashboardClient.BindSslClientAuthenticationOptions(
            configuration.GetSection("Options"),
            options);

        Assert.Equal("resources.internal", options.TargetHost);
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
