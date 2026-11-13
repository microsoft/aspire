// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.ApplicationModel;

[Trait("Partition", "4")]
public class EndpointHostHelpersTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("LocalHost", true)]
    [InlineData("LoCaLhOsT", true)]
    [InlineData("app.localhost", false)]
    [InlineData("api.localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("example.com", false)]
    [InlineData("notlocalhost", false)]
    [InlineData("localhostx", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalhost_VariousInputs_ReturnsExpectedResult(string? host, bool expected)
    {
        // Act
        var result = EndpointHostHelpers.IsLocalhost(host);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("LocalHost", true)]
    [InlineData("LoCaLhOsT", true)]
    [InlineData("app.localhost", false)]
    [InlineData("api.localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("example.com", false)]
    [InlineData("notlocalhost", false)]
    [InlineData("localhostx", false)]
    public void IsLocalhost_VariousUriInputs_ReturnsExpectedResult(string? host, bool expected)
    {
        // Act
        var result = EndpointHostHelpers.IsLocalhost(new Uri($"http://{host}:12345"));

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("localhost", false)]
    [InlineData("app.localhost", true)]
    [InlineData("api.localhost", true)]
    [InlineData("my-service.localhost", true)]
    [InlineData("APP.LOCALHOST", true)]
    [InlineData("Api.LocalHost", true)]
    [InlineData("my-service.LOCALHOST", true)]
    [InlineData("a.b.c.localhost", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("example.com", false)]
    [InlineData("localhost.example.com", false)]
    [InlineData("notlocalhost", false)]
    [InlineData("localhostx", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalhostTld_VariousInputs_ReturnsExpectedResult(string? host, bool expected)
    {
        // Act
        var result = EndpointHostHelpers.IsLocalhostTld(host);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("localhost", false)]
    [InlineData("app.localhost", true)]
    [InlineData("api.localhost", true)]
    [InlineData("my-service.localhost", true)]
    [InlineData("APP.LOCALHOST", true)]
    [InlineData("Api.LocalHost", true)]
    [InlineData("my-service.LOCALHOST", true)]
    [InlineData("a.b.c.localhost", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("example.com", false)]
    [InlineData("localhost.example.com", false)]
    [InlineData("notlocalhost", false)]
    [InlineData("localhostx", false)]
    public void IsLocalhostTld_VariousUriInputs_ReturnsExpectedResult(string? host, bool expected)
    {
        // Act
        var result = EndpointHostHelpers.IsLocalhostTld(new Uri($"http://{host}:12345"));

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("localhost", false)]
    [InlineData("dev.localhost", false)]
    [InlineData("app.dev.localhost", true)]
    [InlineData("api.dev.localhost", true)]
    [InlineData("my-service.dev.localhost", true)]
    [InlineData("APP.DEV.LOCALHOST", true)]
    [InlineData("Api.Dev.LocalHost", true)]
    [InlineData("my-service.DEV.LOCALHOST", true)]
    [InlineData("a.b.c.dev.localhost", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("example.com", false)]
    [InlineData("localhost.example.com", false)]
    [InlineData("notlocalhost", false)]
    [InlineData("localhostx", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDevLocalhostTld_VariousInputs_ReturnsExpectedResult(string? host, bool expected)
    {
        // Act
        var result = EndpointHostHelpers.IsDevLocalhostTld(host);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("localhost", false)]
    [InlineData("dev.localhost", false)]
    [InlineData("app.dev.localhost", true)]
    [InlineData("api.dev.localhost", true)]
    [InlineData("my-service.dev.localhost", true)]
    [InlineData("APP.DEV.LOCALHOST", true)]
    [InlineData("Api.Dev.LocalHost", true)]
    [InlineData("my-service.DEV.LOCALHOST", true)]
    [InlineData("a.b.c.dev.localhost", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("example.com", false)]
    [InlineData("localhost.example.com", false)]
    [InlineData("notlocalhost", false)]
    [InlineData("localhostx", false)]
    public void IsDevLocalhostTld_VariousUriInputs_ReturnsExpectedResult(string? host, bool expected)
    {
        // Act
        var result = EndpointHostHelpers.IsDevLocalhostTld(new Uri($"http://{host}:12345"));

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("LocalHost", true)]
    [InlineData("LoCaLhOsT", true)]
    [InlineData("app.localhost", true)]
    [InlineData("api.localhost", true)]
    [InlineData("my-service.localhost", true)]
    [InlineData("APP.LOCALHOST", true)]
    [InlineData("Api.LocalHost", true)]
    [InlineData("my-service.LOCALHOST", true)]
    [InlineData("a.b.c.localhost", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("example.com", false)]
    [InlineData("localhost.example.com", false)]
    [InlineData("notlocalhost", false)]
    [InlineData("localhostx", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalhostOrLocalhostTld_VariousInputs_ReturnsExpectedResult(string? host, bool expected)
    {
        // Act
        var result = EndpointHostHelpers.IsLocalhostOrLocalhostTld(host);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("LocalHost", true)]
    [InlineData("LoCaLhOsT", true)]
    [InlineData("app.localhost", true)]
    [InlineData("api.localhost", true)]
    [InlineData("my-service.localhost", true)]
    [InlineData("APP.LOCALHOST", true)]
    [InlineData("Api.LocalHost", true)]
    [InlineData("my-service.LOCALHOST", true)]
    [InlineData("a.b.c.localhost", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("example.com", false)]
    [InlineData("localhost.example.com", false)]
    [InlineData("notlocalhost", false)]
    [InlineData("localhostx", false)]
    public void IsLocalhostOrLocalhostTld_VariousUriInputs_ReturnsExpectedResult(string? host, bool expected)
    {
        // Act
        var result = EndpointHostHelpers.IsLocalhostOrLocalhostTld(new Uri($"http://{host}:12345"));

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("dashboard.testing.localhost", "http://dashboard.testing.localhost:17454")]
    [InlineData("app.dev.localhost", "http://app.dev.localhost:17454")]
    [InlineData("localhost", "http://localhost:17454")]
    [InlineData("example.com", "http://localhost:17454")]
    public async Task GetUrlWithTargetHostAsync_UsesTargetHostOnlyForLocalhostTld(string targetHost, string expectedUrl)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container1", "image")
                               .WithHttpEndpoint(name: "primary", targetPort: 10005)
                               .WithEndpoint("primary", ep =>
                               {
                                   ep.TargetHost = targetHost;

                                   // DCP allocates "localhost" even when a localhost TLD is configured, because
                                   // "localhost" is the address the service actually binds to. The TLD hostname
                                   // only ever exists in the URL presented back to the user.
                                   ep.AllocatedEndpoint = new AllocatedEndpoint(ep, "localhost", 17454);
                               });

        var url = await EndpointHostHelpers.GetUrlWithTargetHostAsync(container.GetEndpoint("primary"))
            .AsTask()
            .DefaultTimeout();

        Assert.Equal(expectedUrl, url);
    }

    [Fact]
    public async Task GetUrlWithTargetHostAsync_KeepsAddressResolvedForANonLocalNetworkContext()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container1", "image")
                               .WithHttpEndpoint(name: "primary", targetPort: 10005)
                               .WithEndpoint("primary", ep =>
                               {
                                   ep.TargetHost = "app.dev.localhost";
                                   ep.AllocatedEndpoint = new AllocatedEndpoint(ep, "localhost", 17454);
                                   ep.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(
                                       KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
                                       new AllocatedEndpoint(ep, "container1.dev.internal", 10005, EndpointBindingMode.SingleAddress, networkId: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
                               });

        var containerNetworkEndpoint = new EndpointReference(
            container.Resource,
            container.GetEndpoint("primary").EndpointAnnotation,
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork);

        var url = await EndpointHostHelpers.GetUrlWithTargetHostAsync(containerNetworkEndpoint)
            .AsTask()
            .DefaultTimeout();

        // A localhost TLD only ever resolves on the host loopback, so it must not be substituted into an
        // address that was resolved for the container network - the container would be sent to its own loopback.
        Assert.Equal("http://container1.dev.internal:10005", url);
    }

    [Fact]
    public async Task GetUrlWithTargetHostAsync_ThrowsForNullEndpoint()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await EndpointHostHelpers.GetUrlWithTargetHostAsync(null!));
    }
}
