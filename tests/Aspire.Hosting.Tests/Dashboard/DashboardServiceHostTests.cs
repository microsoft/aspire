// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.Dashboard;

namespace Aspire.Hosting.Tests.Dashboard;

[Trait("Partition", "3")]
public class DashboardServiceHostTests
{
    [Theory]
    [InlineData(null, false, "https")]
    [InlineData(null, true, "http")]
    [InlineData("https://localhost:5001", false, "https")]
    [InlineData("http://localhost:5000", true, "http")]
    [InlineData("http://localhost:5000", false, "http")] // Explicit URI scheme wins regardless of allowUnsecuredTransport. Should never happen because of validation.
    public void ResolveScheme_ReturnsExpectedScheme(string? uriString, bool allowUnsecuredTransport, string expectedScheme)
    {
        var uri = uriString is not null ? new Uri(uriString) : null;

        var scheme = DashboardServiceHost.ResolveScheme(configuredUri: uri, allowUnsecuredTransport: allowUnsecuredTransport);

        Assert.Equal(expectedScheme, scheme);
    }

    [Fact]
    public void ResolveEndpoint_NullUri_BindsToLoopbackPort0()
    {
        var result = DashboardServiceHost.ResolveEndpoint(configuredUri: null, randomizePorts: false, allowUnsecuredTransport: false);

        Assert.Equal(IPAddress.Loopback, result.BindAddress);
        Assert.Equal(0, result.Port);
        Assert.False(result.UseListenLocalhost);
        Assert.Equal("https", result.Scheme);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000", false, "127.0.0.1", 5000, false)]
    [InlineData("https://127.0.0.1:5001", false, "127.0.0.1", 5001, false)]
    [InlineData("http://[::1]:5000", false, "::1", 5000, false)]
    [InlineData("https://[::1]:0", false, "::1", 0, false)]
    public void ResolveEndpoint_IpLoopback_BindsToExactAddress(string uriString, bool randomizePorts, string expectedHost, int expectedPort, bool expectedUseLocalhost)
    {
        var uri = new Uri(uriString);

        var result = DashboardServiceHost.ResolveEndpoint(uri, randomizePorts, allowUnsecuredTransport: false);

        Assert.Equal(IPAddress.Parse(expectedHost), result.BindAddress);
        Assert.Equal(expectedPort, result.Port);
        Assert.Equal(expectedUseLocalhost, result.UseListenLocalhost);
    }

    [Theory]
    [InlineData("http://localhost:5000", false, 5000, true)]
    [InlineData("http://localhost:5000", true, 0, false)] // randomizePorts overrides to port 0, falls back from ListenLocalhost
    [InlineData("http://localhost:0", false, 0, false)] // port 0 already dynamic, falls back from ListenLocalhost
    [InlineData("http://localhost:0", true, 0, false)] // port 0 + randomize, still dynamic
    public void ResolveEndpoint_Localhost_BindsCorrectly(string uriString, bool randomizePorts, int expectedPort, bool expectedUseLocalhost)
    {
        var uri = new Uri(uriString);

        var result = DashboardServiceHost.ResolveEndpoint(uri, randomizePorts, allowUnsecuredTransport: true);

        Assert.Equal(expectedPort, result.Port);
        Assert.Equal(expectedUseLocalhost, result.UseListenLocalhost);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000", true, 0)]
    [InlineData("http://[::1]:5000", true, 0)]
    [InlineData("http://127.0.0.1:0", true, 0)] // port 0 stays 0
    [InlineData("http://[::1]:0", true, 0)] // port 0 stays 0
    [InlineData("http://127.0.0.1:5000", false, 5000)] // randomize off, port preserved
    [InlineData("http://[::1]:5000", false, 5000)]
    public void ResolveEndpoint_RandomizePorts_OverridesNonZeroPort(string uriString, bool randomizePorts, int expectedPort)
    {
        var uri = new Uri(uriString);

        var result = DashboardServiceHost.ResolveEndpoint(uri, randomizePorts, allowUnsecuredTransport: true);

        Assert.Equal(expectedPort, result.Port);
    }

    [Fact]
    public void ResolveEndpoint_NonLoopbackAddress_Throws()
    {
        var uri = new Uri("http://192.168.1.1:5000");

        Assert.Throws<ArgumentException>(() => DashboardServiceHost.ResolveEndpoint(uri, randomizePorts: false, allowUnsecuredTransport: true));
    }
}
