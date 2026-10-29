// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Dashboard;

[Trait("Partition", "3")]
public class DashboardServiceHostTests
{
    [Fact]
    public void AllocatePersistentResourceServicePort_ExcludesFixedAndPersistedModelEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddExecutable("fixed", "unused", ".")
            .WithEndpoint(port: 10000, targetPort: 10002, isProxied: true);
#pragma warning disable ASPIREPERSISTENCE001 // Exercise the persistent endpoint reservation path.
        builder.AddExecutable("persistent", "unused", ".")
            .WithEndpoint(name: "http", isProxied: false)
            .WithPersistentLifetime();
#pragma warning restore ASPIREPERSISTENCE001
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resources:persistent:http:port"] = "10001"
        }).Build();
        using var allocator = new ProxylessEndpointPortAllocator(10000, 10003, 0, 1, static (_, _) => true);

        var port = DashboardServiceHost.AllocatePersistentResourceServicePort(
            new DistributedApplicationModel(builder.Resources), new DcpOptions(), configuration, allocator,
            NullLogger<DashboardServiceHost>.Instance, new MockUserSecretsManager());

        Assert.Equal(10003, port);
    }

    [Fact]
    public void AllocatePersistentResourceServicePort_RequiresSuccessfulPersistence()
    {
        var model = new DistributedApplicationModel(new ResourceCollection());
        using var allocator = new ProxylessEndpointPortAllocator(10000, 10000, 0, 1, static (_, _) => true);
        var secrets = new MockUserSecretsManager(canSetSecret: false);

        var error = Assert.Throws<DistributedApplicationException>(() => DashboardServiceHost.AllocatePersistentResourceServicePort(
            model, new DcpOptions(), new ConfigurationBuilder().Build(), allocator,
            NullLogger<DashboardServiceHost>.Instance, secrets));

        Assert.Contains("resource service port", error.Message);
    }

    [Fact]
    public void AllocatePersistentResourceServicePort_DoesNotResolveModelEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
#pragma warning disable ASPIREPERSISTENCE001 // Exercise implicit proxy policy during port planning.
        var resource = builder.AddExecutable("persistent", "unused", ".")
            .WithEndpoint(targetPort: 10000)
            .WithPersistentLifetime();
#pragma warning restore ASPIREPERSISTENCE001
        var endpoint = resource.Resource.Annotations.OfType<EndpointAnnotation>().Single();
        Assert.True(endpoint.IsProxied);
        using var allocator = new ProxylessEndpointPortAllocator(10000, 10001, 0, 1, static (_, _) => true);
        var secrets = new MockUserSecretsManager();

        var port = DashboardServiceHost.AllocatePersistentResourceServicePort(
            new DistributedApplicationModel(builder.Resources), new DcpOptions(), new ConfigurationBuilder().Build(), allocator,
            NullLogger<DashboardServiceHost>.Instance, secrets);

        Assert.Equal(10001, port);
        Assert.True(endpoint.IsProxied);
        Assert.Equal("10001", secrets.Secrets["AppHost:ResourceService:Port"]);
    }

    [Fact]
    public async Task DashboardRunState_CancelledWaitDoesNotCancelOtherResources()
    {
        var state = new DashboardRunState();
        using var cts = new CancellationTokenSource();
        var cancelled = state.WaitForAttachmentAsync(cts.Token);
        var waiting = state.WaitForAttachmentAsync(CancellationToken.None);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(waiting.IsCompleted);
        state.MarkAttached();
        await waiting.DefaultTimeout();
    }

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
