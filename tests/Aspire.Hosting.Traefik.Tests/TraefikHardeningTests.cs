// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikHardeningTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task RunModeLogLevelAndAccessLogs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLogLevel(TraefikLogLevel.Debug)
            .WithAccessLogs(logs =>
            {
                logs.Format = TraefikAccessLogFormat.Json;
                logs.StatusCodes.Add("400-599");
                logs.StatusCodes.Add("302");
                logs.RetryAttempts = true;
                logs.MinDuration = TimeSpan.FromMilliseconds(250);
                logs.KeepHeaders.Add("User-Agent");
            });

        var args = await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource);

        await Verify(args);
    }

    [Fact]
    public async Task PublishWithDockerSocketProxy()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var traefik = builder.AddTraefik("traefik")
            .WithDockerSocketProxy()
            .WithAccessLogs();

        builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com");

        using var app = builder.Build();
        app.Run();

        await Verify(File.ReadAllText(Path.Combine(workspace.Path, "docker-compose.yaml")), "yaml");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WithDockerSocketPathMountsThatSocket(bool socketProxy, bool socketProxyFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik");
        if (socketProxy && socketProxyFirst)
        {
            traefik.WithDockerSocketProxy();
        }

        traefik.WithDockerSocketPath("/run/user/1000/docker.sock");
        if (socketProxy && !socketProxyFirst)
        {
            traefik.WithDockerSocketProxy();
        }

        IResource mounted = socketProxy ? traefik.Resource.DockerSocketProxy! : traefik.Resource;
        Assert.Collection(
            mounted.Annotations.OfType<ContainerMountAnnotation>(),
            mount => Assert.Equal(("/run/user/1000/docker.sock", "/var/run/docker.sock", true), (mount.Source, mount.Target, mount.IsReadOnly)));

        if (socketProxy)
        {
            Assert.Empty(traefik.Resource.Annotations.OfType<ContainerMountAnnotation>());
        }
    }

    [Fact]
    public async Task DockerSocketProxyIsNotPublishedWithExternalTraefik()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        // PublishAsExternal after WithDockerSocketProxy, as when an AppHost switches it by configuration.
        var traefik = builder.AddTraefik("traefik")
            .WithDockerSocketProxy()
            .PublishAsExternal("traefik-public");

        builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com");

        using var app = builder.Build();
        app.Run();

        await Verify(File.ReadAllText(Path.Combine(workspace.Path, "docker-compose.yaml")), "yaml");
    }

    [Fact]
    public void DockerSocketProxyImageIsPinnedOnlyWhileItIsTheDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik")
            .WithDockerSocketProxy();
        var mirrored = builder.AddTraefik("mirrored")
            .WithDockerSocketProxy(proxy => proxy
                .WithImageRegistry("registry.example.com")
                .WithImage("mirror/docker-socket-proxy", "v0.5.0"));

        Assert.True(traefik.Resource.DockerSocketProxy!.TryGetContainerImageName(out var pinned));
        Assert.Equal("docker.io/tecnativa/docker-socket-proxy@sha256:1f5038b54f06c3e18422902cf00ba21803d1c97805aae032e5e6673d532d3459", pinned);

        Assert.True(mirrored.Resource.DockerSocketProxy!.TryGetContainerImageName(out var mirror));
        Assert.Equal("registry.example.com/mirror/docker-socket-proxy:v0.5.0", mirror);
    }

    [Fact]
    public void InvalidAccessLogOptionsThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var statusCode = Assert.Throws<ArgumentException>(() => traefik.WithAccessLogs(logs => logs.StatusCodes.Add("5xx")));
        Assert.Equal("The status code filter '5xx' is invalid. Use a code or a range, for example '404' or '500-599'. (Parameter 'configure')", statusCode.Message);

        var header = Assert.Throws<ArgumentException>(() => traefik.WithAccessLogs(logs => logs.KeepHeaders.Add("X.Forwarded")));
        Assert.Equal("The header name 'X.Forwarded' is invalid. (Parameter 'configure')", header.Message);
    }
}
