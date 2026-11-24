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
        Assert.Equal("""podman exec db-abcdefgh sh -c 'PASSWORD="$DB_PASSWORD" exec client --port 5432 --label '\''it'\'''\''s'\'' "$@"' sh""", connect.Command);
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
    public async Task OmitsContainerCommandForResourceThatIsNotAContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("tool", "tool", ".")
            .WithConnectCommand("tool", ConnectCommandLocation.Container, ReferenceExpression.Create($"exec tool"));

        Assert.Null(await ResolveAsync(executable.Resource, "tool-abcdefgh", "docker"));
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
