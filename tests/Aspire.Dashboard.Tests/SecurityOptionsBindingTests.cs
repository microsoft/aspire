// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
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
            ["Options:AllowedCertificateTypes"] = "All",
            ["Options:ChainTrustValidationMode"] = "CustomRootTrust",
            ["Options:RevocationFlag"] = "EndCertificateOnly",
            ["Options:RevocationMode"] = "NoCheck",
            ["Options:ValidateCertificateUse"] = "false",
            ["Options:ValidateValidityPeriod"] = "false",
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

        Assert.Equal(CertificateTypes.All, options.AllowedCertificateTypes);
        Assert.Equal(X509ChainTrustMode.CustomRootTrust, options.ChainTrustValidationMode);
        Assert.Equal(X509RevocationFlag.EndCertificateOnly, options.RevocationFlag);
        Assert.Equal(X509RevocationMode.NoCheck, options.RevocationMode);
        Assert.False(options.ValidateCertificateUse);
        Assert.False(options.ValidateValidityPeriod);
        Assert.Equal("dashboard-certificates", options.ClaimsIssuer);
        Assert.Equal("authenticate", options.ForwardAuthenticate);
        Assert.Equal("challenge", options.ForwardChallenge);
        Assert.Equal("default", options.ForwardDefault);
        Assert.Equal("forbid", options.ForwardForbid);
        Assert.Equal("sign-in", options.ForwardSignIn);
        Assert.Equal("sign-out", options.ForwardSignOut);
    }

    [Fact]
    public void BindSslClientAuthenticationOptions_PreservesScalarOverrides()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Options:AllowRenegotiation"] = "false",
            ["Options:AllowTlsResume"] = "false",
            ["Options:CertificateRevocationCheckMode"] = "Online",
            ["Options:EnabledSslProtocols"] = "Tls12",
            ["Options:EncryptionPolicy"] = "AllowNoEncryption",
            ["Options:TargetHost"] = "resources.internal"
        });
        var options = new SslClientAuthenticationOptions();

        DashboardClient.BindSslClientAuthenticationOptions(
            configuration.GetSection("Options"),
            options);

        Assert.False(options.AllowRenegotiation);
        Assert.False(options.AllowTlsResume);
        Assert.Equal(X509RevocationMode.Online, options.CertificateRevocationCheckMode);
        Assert.Equal(SslProtocols.Tls12, options.EnabledSslProtocols);
#pragma warning disable SYSLIB0040 // This insecure test-only value verifies that binding overrides the secure default.
        Assert.Equal(EncryptionPolicy.AllowNoEncryption, options.EncryptionPolicy);
#pragma warning restore SYSLIB0040
        Assert.Equal("resources.internal", options.TargetHost);
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
