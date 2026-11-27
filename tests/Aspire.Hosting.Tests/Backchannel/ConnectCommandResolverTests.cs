// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONNECT001 // Connect command APIs are experimental.

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Backchannel;

public class ConnectCommandResolverTests
{
    [Fact]
    public async Task ResolvesContainerCommandWithRuntimeContainerNameAndForwardedArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("db", "image")
            .WithEndpoint(targetPort: 5432, name: "tcp")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new(e, "localhost", 15432));
        container.WithConnectCommand(
            "client",
            ConnectCommandLocation.Container,
            ReferenceExpression.Create($"PASSWORD=\"$DB_PASSWORD\" exec client --port {container.GetEndpoint("tcp").Property(EndpointProperty.TargetPort)} --label 'it''s'"),
            append: "<query>",
            note: "A note.");

        var connect = await ResolveAsync(container.Resource, "db-abcdefgh", "podman");

        Assert.NotNull(connect);
        // Single quotes in the script are closed, escaped, and reopened for the host shell.
        Assert.Equal("""podman exec -i db-abcdefgh sh -c 'PASSWORD="$DB_PASSWORD" exec client --port 5432 --label '\''it'\'''\''s'\'' "$@"' sh""", connect.Command);
        Assert.Equal(ResourceSnapshotConnect.RunsInContainer, connect.Runs);
        Assert.Equal("client", connect.Tool);
        Assert.Equal("<query>", connect.Append);
        Assert.Equal("A note.", connect.Note);
    }

    [Fact]
    public async Task ResolvesHostCommandWithPublishedPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("nats", "nats")
            .WithEndpoint(targetPort: 4222, name: "tcp")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new(e, "localhost", 14222));
        container.WithConnectCommand(
            "nats",
            ConnectCommandLocation.Host,
            ReferenceExpression.Create($"nats --server nats://localhost:{container.GetEndpoint("tcp").Property(EndpointProperty.Port)}"));

        var connect = await ResolveAsync(container.Resource, "nats-abcdefgh", "docker");

        Assert.NotNull(connect);
        Assert.Equal("nats --server nats://localhost:14222", connect.Command);
        Assert.Equal(ResourceSnapshotConnect.RunsOnHost, connect.Runs);
        Assert.Null(connect.Append);
        Assert.Null(connect.Note);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OmitsCommandThatReferencesParameters(bool secret)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", "p@ssw0rd", secret: secret);
        var container = builder.AddContainer("db", "image")
            .WithConnectCommand("client", ConnectCommandLocation.Container, ReferenceExpression.Create($"client --password {password}"));

        Assert.Null(await ResolveAsync(container.Resource, "db-abcdefgh", "docker"));
    }

    [Fact]
    public async Task OmitsCommandThatReferencesParametersInConditionalBranch()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", "p@ssw0rd", secret: true);
        var container = builder.AddContainer("db", "image")
            .WithEndpoint(targetPort: 5432, name: "tcp")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new(e, "localhost", 15432));
        var tlsEnabled = container.GetEndpoint("tcp").Property(EndpointProperty.TlsEnabled);
        container.WithConnectCommand(
            "client",
            ConnectCommandLocation.Container,
            ReferenceExpression.CreateConditional(tlsEnabled, bool.TrueString, ReferenceExpression.Create($"client --password {password}"), ReferenceExpression.Create($"client")));

        Assert.Null(await ResolveAsync(container.Resource, "db-abcdefgh", "docker"));
    }

    [Fact]
    public async Task OmitsCommandUntilEndpointIsAllocated()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("nats", "nats").WithEndpoint(targetPort: 4222, name: "tcp");
        container.WithConnectCommand(
            "nats",
            ConnectCommandLocation.Host,
            ReferenceExpression.Create($"nats --server nats://localhost:{container.GetEndpoint("tcp").Property(EndpointProperty.Port)}"));

        // Resolving an unallocated endpoint would wait for allocation, so the command must be omitted promptly.
        Assert.Null(await ResolveAsync(container.Resource, "nats-abcdefgh", "docker").DefaultTimeout());

        container.WithEndpoint("tcp", e => e.AllocatedEndpoint = new(e, "localhost", 14222));

        var connect = await ResolveAsync(container.Resource, "nats-abcdefgh", "docker");
        Assert.Equal("nats --server nats://localhost:14222", connect?.Command);
    }

    [Fact]
    public async Task OmitsCommandWhenEndpointAllocationFaulted()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("nats", "nats")
            .WithEndpoint(targetPort: 4222, name: "tcp")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = null);
        container.WithConnectCommand(
            "nats",
            ConnectCommandLocation.Host,
            ReferenceExpression.Create($"nats --server nats://localhost:{container.GetEndpoint("tcp").Property(EndpointProperty.Port)}"));

        Assert.Null(await ResolveAsync(container.Resource, "nats-abcdefgh", "docker").DefaultTimeout());
    }

    [Fact]
    public async Task OmitsContainerCommandForResourceThatIsNotAContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("tool", "tool", ".")
            .WithConnectCommand("tool", ConnectCommandLocation.Container, ReferenceExpression.Create($"exec tool"));

        Assert.Null(await ResolveAsync(executable.Resource, "tool-abcdefgh", "docker"));
    }

    [Fact]
    public async Task OmitsContainerCommandWhenContainerRuntimeCannotBeResolved()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("db", "image")
            .WithConnectCommand("client", ConnectCommandLocation.Container, ReferenceExpression.Create($"exec client"));

        var connect = await ConnectCommandResolver.ResolveAsync(
            container.Resource,
            "db-abcdefgh",
            _ => Task.FromException<string>(new InvalidOperationException("No service for type 'IContainerRuntime' has been registered.")),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Null(connect);
    }

    [Fact]
    public async Task OmitsCommandThatReferencesAnotherResourcesEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var other = builder.AddContainer("other", "image")
            .WithEndpoint(targetPort: 4222, name: "tcp")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new(e, "localhost", 14222));
        var container = builder.AddContainer("client", "image")
            .WithConnectCommand(
                "nats",
                ConnectCommandLocation.Host,
                ReferenceExpression.Create($"nats --server nats://localhost:{other.GetEndpoint("tcp").Property(EndpointProperty.Port)}"));

        // Only the owner's snapshots re-resolve the command, so another resource's endpoint could go stale.
        Assert.Null(await ResolveAsync(container.Resource, "client-abcdefgh", "docker"));
    }

    [Fact]
    public async Task OmitsCommandThatReferencesUndefinedEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("db", "image");
        container.WithConnectCommand(
            "client",
            ConnectCommandLocation.Host,
            ReferenceExpression.Create($"client --port {container.GetEndpoint("missing").Property(EndpointProperty.Port)}"));

        Assert.Null(await ResolveAsync(container.Resource, "db-abcdefgh", "docker"));
    }

    [Fact]
    public async Task OmitsCommandThatReferencesTargetPortThatIsNotFixed()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("db", "image")
            .WithEndpoint(name: "tcp")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new(e, "localhost", 15432, targetPortExpression: """{{- portForServing "db-abcdefgh" -}}"""));
        container.WithConnectCommand(
            "client",
            ConnectCommandLocation.Container,
            ReferenceExpression.Create($"exec client --port {container.GetEndpoint("tcp").Property(EndpointProperty.TargetPort)}"));

        Assert.Null(await ResolveAsync(container.Resource, "db-abcdefgh", "docker"));
    }

    [Theory]
    [InlineData(EndpointProperty.BrowserOrigins)]
    [InlineData(EndpointProperty.PrimaryBrowserOrigin)]
    public async Task OmitsCommandThatReferencesBrowserOrigins(EndpointProperty property)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("web", "image")
            .WithHttpEndpoint(targetPort: 80, name: "http")
            .WithEndpoint("http", e => e.AllocatedEndpoint = new(e, "localhost", 18080));
        container.WithConnectCommand(
            "curl",
            ConnectCommandLocation.Host,
            ReferenceExpression.Create($"curl {container.GetEndpoint("http").Property(property)}"));

        // The browser URLs snapshot is never set here, so resolving the property would wait indefinitely.
        Assert.Null(await ResolveAsync(container.Resource, "web-abcdefgh", "docker").DefaultTimeout());
    }

    [Theory]
    [InlineData(ConnectCommandLocation.Host)]
    [InlineData(ConnectCommandLocation.Container)]
    public async Task OmitsCommandThatReferencesEndpointOnAnotherNetwork(ConnectCommandLocation location)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("nats", "nats")
            .WithEndpoint(targetPort: 4222, name: "tcp")
            .WithEndpoint("tcp", e =>
            {
                e.AllocatedEndpoint = new(e, "localhost", 14222);
                e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(
                    KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
                    new(e, "nats.dev.internal", 4222, EndpointBindingMode.SingleAddress, networkId: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            });
        var endpoint = container.GetEndpoint("tcp", KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
        container.WithConnectCommand(
            "nats",
            location,
            ReferenceExpression.Create($"nats --server nats://{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}"));

        // The container-network address and port are not reachable from the host.
        Assert.Null(await ResolveAsync(container.Resource, "nats-abcdefgh", "docker").DefaultTimeout());
    }

    [Fact]
    public async Task RetriesContainerRuntimeDetectionCancelledByAnotherCaller()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("db", "image")
            .WithConnectCommand("client", ConnectCommandLocation.Container, ReferenceExpression.Create($"exec client"));
        var attempts = 0;

        var connect = await ConnectCommandResolver.ResolveAsync(
            container.Resource,
            "db-abcdefgh",
            _ => ++attempts == 1 ? Task.FromCanceled<string>(new CancellationToken(canceled: true)) : Task.FromResult("docker"),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal("docker exec -i db-abcdefgh sh -c 'exec client \"$@\"' sh", connect?.Command);
    }

    [Fact]
    public async Task OmitsContainerCommandWhenRuntimeDetectionIsRepeatedlyCancelledByAnotherCaller()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("db", "image")
            .WithConnectCommand("client", ConnectCommandLocation.Container, ReferenceExpression.Create($"exec client"));
        var attempts = 0;

        var connect = await ConnectCommandResolver.ResolveAsync(
            container.Resource,
            "db-abcdefgh",
            _ =>
            {
                attempts++;
                return Task.FromCanceled<string>(new CancellationToken(canceled: true));
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Null(connect);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task PropagatesCallerCancellationDuringContainerRuntimeDetection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("db", "image")
            .WithConnectCommand("client", ConnectCommandLocation.Container, ReferenceExpression.Create($"exec client"));
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectCommandResolver.ResolveAsync(
            container.Resource,
            "db-abcdefgh",
            ct =>
            {
                attempts++;
                cts.Cancel();
                return Task.FromCanceled<string>(ct);
            },
            NullLogger.Instance,
            cts.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ReturnsNullWithoutAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("db", "image");

        Assert.Null(await ResolveAsync(container.Resource, "db-abcdefgh", "docker"));
    }

    [Fact]
    public void WithConnectCommandReplacesPreviousCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("db", "image")
            .WithConnectCommand("first", ConnectCommandLocation.Container, ReferenceExpression.Create($"exec first"))
            .WithConnectCommand("second", ConnectCommandLocation.Host, ReferenceExpression.Create($"second"));

        var annotation = Assert.Single(container.Resource.Annotations.OfType<ConnectCommandAnnotation>());
        Assert.Equal("second", annotation.Tool);
        Assert.Equal(ConnectCommandLocation.Host, annotation.Location);
    }

    private static Task<ResourceSnapshotConnect?> ResolveAsync(IResource resource, string resourceId, string runtime)
        => ConnectCommandResolver.ResolveAsync(
            resource,
            resourceId,
            _ => Task.FromResult(runtime),
            NullLogger.Instance,
            CancellationToken.None);
}
