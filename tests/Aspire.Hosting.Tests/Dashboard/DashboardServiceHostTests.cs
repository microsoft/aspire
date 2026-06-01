// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
}
